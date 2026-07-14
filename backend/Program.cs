using System.Net;
using System.Text;
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); var (minThreads, maxThreads) = ThreadPoolUtil.ResolveLimits(
            Environment.ProcessorCount,
            EnvironmentUtil.GetLongVariable("THREADPOOL_MIN_THREADS"),
            EnvironmentUtil.GetLongVariable("THREADPOOL_MAX_THREADS"));
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);
        ThreadPool.SetMinThreads(minThreads, minThreads);

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
            Log.Information(
                "ThreadPool configured with minimum {MinThreads} and maximum {MaxThreads} worker and IOCP threads",
                minThreads,
                maxThreads);

            // Block upgrades to version 0.6.x
            BlockUpgradesToV06X();

            // run database migration / restore, if necessary.
            // Restore must run before opening the live DavDatabaseContext so pending
            // migrations are computed against the restored schema.
            if (args.Contains("--db-migration"))
            {
                await RunDatabaseMigrationsAsync(args).ConfigureAwait(false);
                return;
            }

            // initialize database
            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database
                .ExecuteSqlRawAsync(
                    "PRAGMA journal_mode = WAL;",
                    SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);

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

            // Assign stable ProviderIds (persisting if needed) and remap legacy
            // host-keyed metrics rows before the streaming client is built.
            await UsenetProviderIdentity
                .EnsureAsync(configManager, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);

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
                .AddSingleton<DatabaseBackupStore>()
                .AddSingleton<RestartService>()
                .AddHostedService<DatabaseBackupSchedulerService>()
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
        var databaseFileExists = File.Exists(DavDatabaseContext.DatabaseFilePath);
        IEnumerable<string> appliedMigrations = [];
        IEnumerable<string> pendingMigrations = [];
        if (databaseFileExists)
        {
            using var databaseContext = new DavDatabaseContext();
            appliedMigrations = databaseContext.Database.GetAppliedMigrations().ToList();
            pendingMigrations = databaseContext.Database.GetPendingMigrations().ToList();
        }

        if (!DatabaseStartupGuards.ShouldBlockUpgradeToV06X(
                databaseFileExists,
                appliedMigrations,
                pendingMigrations,
                EnvironmentUtil.GetEnvironmentVariable("UPGRADE")))
        {
            return;
        }

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

    private static async Task RunDatabaseMigrationsAsync(string[] args)
    {
        var ct = SigtermUtil.GetCancellationToken();
        var argIndex = args.ToList().IndexOf("--db-migration");
        var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
        var backupStore = new DatabaseBackupStore();
        backupStore.EnsureInitialized();
        var pendingRestore = backupStore.ReadPendingRestore();
        var hasPendingRestore = pendingRestore is not null
            && pendingRestore.StagedFiles.Count > 0
            && pendingRestore.StagedFiles.All(name =>
                File.Exists(Path.Combine(backupStore.RestoreStagingRoot, name)));
        if (pendingRestore is not null && !hasPendingRestore)
        {
            Log.Warning(
                "Discarding incomplete pending restore for backup {BackupId}",
                pendingRestore.BackupId);
            backupStore.ClearPendingRestore();
            backupStore.ClearRestoreStaging();
            pendingRestore = null;
        }

        // An explicit target (design-time tooling / tests) uses the simple,
        // single-call path. Progress tracking only covers the common upgrade
        // path where all pending migrations are applied.
        if (targetMigration is not null)
        {
            if (hasPendingRestore)
            {
                var progress = new MigrationProgress();
                progress.Initialize(DatabaseRestoreRunner.GetRestoreSteps(pendingRestore!));
                await using var statusServer = await MigrationStatusServer.StartAsync(progress, ct).ConfigureAwait(false);
                await DatabaseRestoreRunner.ApplyPendingRestoreAsync(progress, ct).ConfigureAwait(false);
                if (statusServer is not null)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database
                .ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct)
                .ConfigureAwait(false);
            Log.Information("Applying database migrations through {Target}", targetMigration);
            await databaseContext.Database.MigrateAsync(targetMigration, ct).ConfigureAwait(false);
            Log.Information("Database migrations completed");
            await using var metricsContext = new MetricsDbContext();
            await metricsContext.Database.MigrateAsync(ct).ConfigureAwait(false);
            await PerformDatabaseVacuumIfEnabled().ConfigureAwait(false);
            return;
        }

        // When a restore is pending we always show the status page, even if there
        // are no pending EF migrations after the swap.
        if (!hasPendingRestore)
        {
            await using var probeContext = new DavDatabaseContext();
            await probeContext.Database
                .ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct)
                .ConfigureAwait(false);
            var pendingProbe = (await probeContext.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var vacuumEnabledProbe = await IsDatabaseStartupVacuumEnabledAsync().ConfigureAwait(false);

            // Routine restarts with nothing to do: skip the status server and its
            // grace delay so Docker does not bind/unbind :8080 just to say "idle".
            if (MigrationProgress.IsIdleMaintenance(pendingProbe.Count, vacuumEnabledProbe))
            {
                Log.Information("No pending database migrations");
                await using var metricsContext = new MetricsDbContext();
                await metricsContext.Database.MigrateAsync(ct).ConfigureAwait(false);
                Log.Information("Database migrations completed");
                return;
            }
        }

        // Build the ordered list of maintenance steps: optional restore, then each
        // pending migration (computed AFTER restore), then metrics, then optional vacuum.
        var steps = new List<MigrationProgress.MigrationStep>();
        if (hasPendingRestore)
            steps.AddRange(DatabaseRestoreRunner.GetRestoreSteps(pendingRestore!));

        var progressFull = new MigrationProgress();
        // Restore steps are registered first; migration steps are appended after
        // the swap so GetPendingMigrations reflects the restored schema.
        progressFull.Initialize(steps);

        await using var statusServerFull = await MigrationStatusServer.StartAsync(progressFull, ct).ConfigureAwait(false);

        try
        {
            if (hasPendingRestore)
            {
                Log.Information("Applying staged database restore for backup {BackupId}", pendingRestore!.BackupId);
                await DatabaseRestoreRunner.ApplyPendingRestoreAsync(progressFull, ct).ConfigureAwait(false);
            }

            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database
                .ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct)
                .ConfigureAwait(false);

            var pending = (await databaseContext.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var vacuumEnabled = await IsDatabaseStartupVacuumEnabledAsync().ConfigureAwait(false);

            var remainingSteps = new List<MigrationProgress.MigrationStep>();
            foreach (var id in pending)
                remainingSteps.Add(new MigrationProgress.MigrationStep(id, MigrationProgress.FriendlyName(id), MigrationProgress.IsSlow(id)));
            remainingSteps.Add(new MigrationProgress.MigrationStep(MigrationProgress.MetricsStepId, "Metrics database", false));
            if (vacuumEnabled)
                remainingSteps.Add(new MigrationProgress.MigrationStep(MigrationProgress.VacuumStepId, "Optimizing database (vacuum)", true));

            // Re-initialize with restore steps (already completed) + remaining work so
            // the UI shows the full plan. Completed restore steps keep their status via
            // a fresh Initialize — instead append by re-init with all steps and mark
            // restore steps completed again.
            var allSteps = new List<MigrationProgress.MigrationStep>();
            if (hasPendingRestore)
                allSteps.AddRange(DatabaseRestoreRunner.GetRestoreSteps(pendingRestore!));
            allSteps.AddRange(remainingSteps);
            progressFull.Initialize(allSteps);
            if (hasPendingRestore)
            {
                foreach (var step in DatabaseRestoreRunner.GetRestoreSteps(pendingRestore!))
                {
                    progressFull.BeginStep(step.Id);
                    progressFull.CompleteStep(step.Id);
                }
            }

            if (pending.Count == 0)
                Log.Information("No pending database migrations");

            for (var i = 0; i < remainingSteps.Count; i++)
            {
                var step = remainingSteps[i];
                Log.Information(
                    "Database maintenance step {Index}/{Total}: {Name}",
                    i + 1, remainingSteps.Count, step.Name);
                progressFull.BeginStep(step.Id);

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

                progressFull.CompleteStep(step.Id);
            }

            progressFull.Complete();
            Log.Information("Database migrations completed");

            // Brief grace so the status page can render the final state before
            // this process exits and the port goes dark.
            if (statusServerFull is not null)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            progressFull.Fail(ex.Message);
            Log.Error(ex, "Database migration failed");

            // Keep the failure visible on the status page briefly before exiting.
            if (statusServerFull is not null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            throw;
        }
    }

    private static async Task<bool> IsDatabaseStartupVacuumEnabledAsync()
    {
        // Fresh / WAL-created empty databases have no ConfigItems table yet. Querying
        // it before migrations run is what broke brand-new installs after #269.
        await using var databaseContext = new DavDatabaseContext();
        if (!await DatabaseStartupGuards
                .ConfigItemsTableExistsAsync(databaseContext, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false))
        {
            return false;
        }

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
