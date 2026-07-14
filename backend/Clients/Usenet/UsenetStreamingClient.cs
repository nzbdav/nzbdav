using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker)
        : base(CreateDownloadingNntpClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker))
    {
        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;

            // update the connection-pool according to the new config
            var newUsenetClient = CreateDownloadingNntpClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker);
            ReplaceUnderlyingClient(newUsenetClient);
        };
    }

    private static INntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker
    )
    {
        var multiProviderClient = CreateMultiProviderClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker);
        var downloadingClient = new DownloadingNntpClient(multiProviderClient, configManager);
        INntpClient inner = downloadingClient;
        if (configManager.IsSegmentCacheEnabled())
        {
            try
            {
                inner = new SegmentCacheNntpClient(
                    downloadingClient,
                    configManager.GetSegmentCachePath(),
                    configManager.GetSegmentCacheMaxBytes(),
                    usageTracker,
                    metricsWriter
                );
            }
            catch (Exception e)
            {
                Log.Warning(e, "Segment cache disabled: failed to initialise at {Path}.",
                    configManager.GetSegmentCachePath());
            }
        }

        // Always wrap with header caching so seek probes reuse immutable yEnc headers
        // even when the optional on-disk segment body cache is disabled.
        return new HeaderCachingNntpClient(inner);
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        // Seed the tracker from the persisted metrics rollup so the limit gate
        // is accurate before the first article fetch. Fire-and-forget — the
        // helper logs and swallows DB errors so a metrics outage can't keep
        // the streaming client from coming up. Limit enforcement degrades
        // gracefully to "uncapped until seed completes".
        _ = ProviderUsageHelper.SeedTrackerAsync(bytesTracker, providerConfig);

        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients, usageTracker, metricsWriter, bytesTracker,
            cascadeEnabled: configManager.IsCascadeEnabled);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged
        );
        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        // Ensure a metrics key even if startup backfill was skipped somehow.
        if (connectionDetails.ProviderId == Guid.Empty)
            connectionDetails.ProviderId = Guid.NewGuid();
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            circuitBreaker,
            connectionDetails.Host,
            connectionDetails.ByteLimit,
            connectionDetails.BytesUsedOffset,
            connectionDetails.Priority,
            connectionDetails.PipeliningDepth,
            connectionDetails.StorageGroup,
            UsenetProviderIdentity.MetricsKey(connectionDetails)
        );
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(maxConnections, connectionFactory);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}
