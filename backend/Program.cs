using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Serilog.Templates.Themes;

namespace NzbWebDAV;

class Program
{
    static async Task Main(string[] args)
    {
        // Update thread-pool
        var coreCount = Environment.ProcessorCount;
        var minThreads = Math.Max(coreCount * 2, 50); // 2x cores, minimum 50
        var maxThreads = Math.Max(coreCount * 50, 1000); // 50x cores, minimum 1000
        ThreadPool.SetMinThreads(minThreads, minThreads);
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = EnvironmentUtil.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        var bufferSize = (int)Math.Clamp(EnvironmentUtil.GetLongVariable("LOG_BUFFER_SIZE") ?? 2000, 100, 50000);
        var logBufferSink = new LogBufferSink(bufferSize);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", AtLeast(level, LogEventLevel.Warning))
            .MinimumLevel.Override("Microsoft", AtLeast(level, LogEventLevel.Information))
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", AtLeast(level, LogEventLevel.Warning))
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", AtLeast(level, LogEventLevel.Warning))
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", AtLeast(level, LogEventLevel.Warning))
            .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", AtLeast(level, LogEventLevel.Error))
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", AtLeast(level, LogEventLevel.Error))
            .WriteTo.Console(new ExpressionTemplate(
                "[{@t:HH:mm:ss} {@l:u3}] " +
                "{#if SourceContext is not null}" +
                "{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}: " +
                "{#end}{@m}\n{@x}",
                theme: TemplateTheme.Code))
            .WriteTo.Sink(logBufferSink)
            .CreateLogger();

        try
        {
            Log.Information(
                "Starting NzbDav {Version} with config at {ConfigPath}; minimum log level is {LogLevel}",
                ConfigManager.AppVersion,
                DavDatabaseContext.ConfigPath,
                level);

            // Block upgrades to version 0.6.x
            BlockUpgradesToV06X();

            // initialize database
            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database
                .ExecuteSqlRawAsync(
                    "PRAGMA journal_mode = WAL;",
                    SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            // run database migration, if necessary.
            if (args.Contains("--db-migration"))
            {
                await RunDatabaseMigrationsAsync(databaseContext, args).ConfigureAwait(false);
                return;
            }

            // The metrics database has its own schema and must also be current on
            // normal startup, where the operational migration runner is skipped.
            await using (var metricsBootstrap = new MetricsDbContext())
            {
                await metricsBootstrap.Database
                    .MigrateAsync(SigtermUtil.GetCancellationToken())
                    .ConfigureAwait(false);
            }

            // initialize the config-manager
            var configManager = new ConfigManager();
            await configManager.LoadConfig().ConfigureAwait(false);

            // initialize rclone client
            RcloneClient.Initialize(configManager);

            // initialize websocket-manager
            var websocketManager = new WebsocketManager();

            // initialize webapp
            var builder = WebApplication.CreateBuilder(args);
            var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
            builder.Host.UseSerilog();
            builder.Services.AddControllers();
            builder.Services.AddHealthChecks();
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedProto
                    | ForwardedHeaders.XForwardedHost;
                // Default: only trust the in-container frontend proxy (loopback).
                // Widen via TRUSTED_PROXY_CIDRS for split-container topologies.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
                ApplyTrustedProxyCidrs(options);
            });
            builder.Services
                .AddWebdavBasicAuthentication(configManager)
                .AddSingleton(configManager)
                .AddSingleton(websocketManager)
                .AddSingleton(logBufferSink)
                .AddSingleton<BenchmarkGate>()
                .AddHostedService<LogBroadcaster>()
                .AddSingleton<ActiveReadRegistry>()
                .AddSingleton<ProviderUsageTracker>(sp =>
                    new ProviderUsageTracker(sp.GetRequiredService<ActiveReadRegistry>()))
                .AddSingleton<QueueItemSourceTracker>()
                .AddSingleton<UsenetStreamingClient>()
                // LazyRarResolver takes INntpClient (for testability) but must
                // use the shared streaming client; wire it explicitly instead
                // of registering a container-wide INntpClient binding.
                .AddSingleton<LazyRarResolver>(sp => new LazyRarResolver(
                    sp.GetRequiredService<UsenetStreamingClient>(),
                    sp.GetRequiredService<ConfigManager>()))
                .AddSingleton<QueueManager>()
                .AddSingleton<NzbResolutionCache>()
                .AddSingleton<PreferredOrderStore>()
                .AddSingleton<NzbFetchCoalescer>()
                .AddSingleton<PlayResolutionCoalescer>()
                .AddSingleton<CandidateNegativeCache>()
                .AddSingleton<WardenStore>()
                .AddSingleton<WardenRemoteSourceService>()
                .AddHostedService(sp => sp.GetRequiredService<WardenRemoteSourceService>())
                .AddSingleton<WardenBackupService>()
                .AddHostedService(sp => sp.GetRequiredService<WardenBackupService>())
                .AddSingleton<SearchExcludeSyncService>()
                .AddHostedService(sp => sp.GetRequiredService<SearchExcludeSyncService>())
                .AddSingleton<PlaybackFastVerifier>()
                .AddSingleton<WatchdogLog>()
                .AddSingleton<PreflightCache>()
                .AddSingleton<PreflightSessionRegistry>()
                .AddSingleton<PreflightOrchestrator>()
                .AddSingleton<NewznabRateLimiter>()
                .AddSingleton<IndexerHitTracker>()
                .AddSingleton<TvdbIdResolver>()
                .AddSingleton<TmdbIdResolver>()
                .AddSingleton<AnimeListMappingResolver>()
                .AddSingleton<ExternalIdResolver>()
                .AddSingleton<ImdbTitleResolver>()
                .AddSingleton<SearchProfileService>()
                .AddSingleton<VariantResolver>()
                .AddSingleton<MetricsWriter>()
                .AddHostedService(sp => sp.GetRequiredService<MetricsWriter>())
                .AddSingleton<ProviderBytesTracker>()
                .AddHostedService<MetricsRollupService>()
                .AddHostedService<MetricsRetentionService>()
                .AddHostedService<SqliteMaintenanceService>()
                .AddSingleton<LiveStatsBroadcaster>()
                .AddHostedService(sp => sp.GetRequiredService<LiveStatsBroadcaster>())
                .AddHostedService<HealthCheckService>()
                .AddHostedService<HealthCheckRetentionService>()
                .AddHostedService<ArrMonitoringService>()
                .AddHostedService<BlobCleanupService>()
                .AddHostedService<NzbBlobCleanupService>()
                .AddHostedService<HistoryCleanupService>()
                .AddHostedService<HistoryRetentionService>()
                .AddHostedService<WatchdogPurgeService>()
                .AddHostedService<DavCleanupService>()
                .AddHostedService<UsenetFileToBlobstoreMigrationService>()
                .AddHostedService<RemoveOrphanedFilesSchedulerService>()
                .AddHostedService<ActiveReadsBroadcaster>()
                .AddSingleton<WatchtowerStore>()
                .AddSingleton<ListSourceEnumerator>()
                .AddSingleton<EpisodeEnumerator>()
                .AddHostedService<WatchtowerService>()
                .AddScoped<DavDatabaseContext>()
                .AddScoped<DavDatabaseClient>()
                .AddScoped<NzbWebDAV.Services.Benchmark.BenchmarkCorpusProvider>()
                .AddScoped<NzbWebDAV.Services.Benchmark.UsenetBenchmarkService>()
                .AddScoped<DatabaseStore>()
                .AddScoped<IStore, DatabaseStore>()
                .AddScoped<GetAndHeadHandlerPatch>()
                .AddScoped<SabApiController>()
                .AddNWebDav(opts =>
                {
                    opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                    opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                    opts.Filter = opts.GetFilter();
                    opts.RequireAuthentication = !WebApplicationAuthExtensions
                        .IsWebdavAuthDisabled();
                });

            // run
            var app = builder.Build();
            // Must run before anything that reads Scheme/Host/RemoteIpAddress.
            app.UseForwardedHeaders();
            _ = app.Services.GetRequiredService<QueueManager>();
            app.UseMiddleware<ExceptionMiddleware>();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            app.MapHealthChecks("/health");
            app.Map("/ws", websocketManager.HandleRoute);
            app.MapControllers();
            app.UseWebdavBasicAuthentication();
            app.UseNWebDav();
            app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "NzbDav terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static LogEventLevel AtLeast(LogEventLevel configured, LogEventLevel minimum)
    {
        return configured > minimum ? configured : minimum;
    }

    private static void ApplyTrustedProxyCidrs(ForwardedHeadersOptions options)
    {
        var raw = EnvironmentUtil.GetEnvironmentVariable("TRUSTED_PROXY_CIDRS");
        if (raw is null) return;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(part, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(part, out var proxyAddress))
            {
                options.KnownProxies.Add(proxyAddress);
            }
            else
            {
                Log.Warning("Ignoring invalid TRUSTED_PROXY_CIDRS entry: {Entry}", part);
            }
        }
    }

    private static void BlockUpgradesToV06X()
    {
        // If the database file doesn't exist.
        // Then this is a new installation.
        // Do nothing.
        if (!File.Exists(DavDatabaseContext.DatabaseFilePath)) return;

        // If there is no pending database migration,
        // Then the user has already upgraded.
        // Do nothing.
        using var databaseContext = new DavDatabaseContext();
        const string migration = "20260226053712_Add-NzbBlobId-And-NzbNames";
        var hasPendingMigration = databaseContext.Database.GetPendingMigrations().Contains(migration);
        if (!hasPendingMigration) return;

        // If the user has set the UPGRADE env variable,
        // Then they have acknowledged the upgrade message.
        // Do nothing.
        var upgradeEnv = EnvironmentUtil.GetEnvironmentVariable("UPGRADE");
        if (upgradeEnv == "0.6.0") return;

        // Otherwise, display the upgrade message, and exit.
        Log.Fatal(
            """
            Version 0.6.0 of nzbdav is NOT backwards compatible.
            You can upgrade, but you won't be able to downgrade.
            Make a backup of your entire /config directory prior to upgrading.
            The only way to downgrade back to a previous version is by restoring this backup.
            To acknowledge this message and continue upgrading, set the env variable UPGRADE=0.6.0
            """
        );
        Log.CloseAndFlush();
        Environment.Exit(1);
    }

    private static async Task RunDatabaseMigrationsAsync(DavDatabaseContext databaseContext, string[] args)
    {
        var ct = SigtermUtil.GetCancellationToken();
        var argIndex = args.ToList().IndexOf("--db-migration");
        var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;

        // An explicit target (design-time tooling / tests) uses the simple,
        // single-call path. Progress tracking only covers the common upgrade
        // path where all pending migrations are applied.
        if (targetMigration is not null)
        {
            Log.Information("Applying database migrations through {Target}", targetMigration);
            await databaseContext.Database.MigrateAsync(targetMigration, ct).ConfigureAwait(false);
            Log.Information("Database migrations completed");
            await using var metricsContext = new MetricsDbContext();
            await metricsContext.Database.MigrateAsync(ct).ConfigureAwait(false);
            await PerformDatabaseVacuumIfEnabled().ConfigureAwait(false);
            return;
        }

        var pending = (await databaseContext.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        var vacuumEnabled = await IsDatabaseStartupVacuumEnabledAsync().ConfigureAwait(false);

        // Routine restarts with nothing to do: skip the status server and its
        // grace delay so Docker does not bind/unbind :8080 just to say "idle".
        if (MigrationProgress.IsIdleMaintenance(pending.Count, vacuumEnabled))
        {
            Log.Information("No pending database migrations");
            await using var metricsContext = new MetricsDbContext();
            await metricsContext.Database.MigrateAsync(ct).ConfigureAwait(false);
            Log.Information("Database migrations completed");
            return;
        }

        // Build the ordered list of maintenance steps: each pending migration,
        // then the metrics database, then the optional vacuum.
        var steps = new List<MigrationProgress.MigrationStep>();
        foreach (var id in pending)
            steps.Add(new MigrationProgress.MigrationStep(id, MigrationProgress.FriendlyName(id), MigrationProgress.IsSlow(id)));
        steps.Add(new MigrationProgress.MigrationStep(MigrationProgress.MetricsStepId, "Metrics database", false));
        if (vacuumEnabled)
            steps.Add(new MigrationProgress.MigrationStep(MigrationProgress.VacuumStepId, "Optimizing database (vacuum)", true));

        var progress = new MigrationProgress();
        progress.Initialize(steps);

        // Serve live progress on the backend port for the duration of the migration.
        await using var statusServer = await MigrationStatusServer.StartAsync(progress, ct).ConfigureAwait(false);

        try
        {
            if (pending.Count == 0)
                Log.Information("No pending database migrations");

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                Log.Information(
                    "Database maintenance step {Index}/{Total}: {Name}",
                    i + 1, steps.Count, step.Name);
                progress.BeginStep(step.Id);

                if (step.Id == MigrationProgress.MetricsStepId)
                {
                    await using var metricsContext = new MetricsDbContext();
                    await metricsContext.Database.MigrateAsync(ct).ConfigureAwait(false);
                }
                else if (step.Id == MigrationProgress.VacuumStepId)
                {
                    await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;", ct).ConfigureAwait(false);
                }
                else
                {
                    await databaseContext.Database.MigrateAsync(step.Id, ct).ConfigureAwait(false);
                }

                progress.CompleteStep(step.Id);
            }

            progress.Complete();
            Log.Information("Database migrations completed");

            // Brief grace so the status page can render the final state before
            // this process exits and the port goes dark.
            if (statusServer is not null)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            progress.Fail(ex.Message);
            Log.Error(ex, "Database migration failed");

            // Keep the failure visible on the status page briefly before exiting.
            if (statusServer is not null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            throw;
        }
    }

    private static async Task<bool> IsDatabaseStartupVacuumEnabledAsync()
    {
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        return configManager.IsDatabaseStartupVacuumEnabled();
    }

    private static async Task PerformDatabaseVacuumIfEnabled()
    {
        if (await IsDatabaseStartupVacuumEnabledAsync().ConfigureAwait(false))
        {
            Log.Information("Performing database vacuum");
            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;");
            Log.Information("Database vacuum completed");
        }
    }
}
