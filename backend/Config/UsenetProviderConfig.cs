using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }

        public int Priority { get; set; }

        public int? PipeliningDepth { get; set; }

        // Optional user-friendly label shown in the UI in place of Host. Host is
        // still the real NNTP target and the stable key used for metrics/logs.
        public string? Nickname { get; set; }

        // null or 0 = no cap. Used by block-account holders to stop a paid block
        // from being drained beyond its purchased size.
        public long? ByteLimit { get; set; }

        // bytes added to the computed usage. Lets the user seed a starting value
        // when migrating from another client, or adjust drift against the
        // provider's own portal. Set to 0 after a reset.
        public long BytesUsedOffset { get; set; }

        // unix-ms cutoff: ProviderHourly rows older than this don't contribute to
        // the live counter. A reset bumps this to "now" so the gauge starts fresh
        // without losing the historical metrics rows underneath.
        public long BytesUsedResetAt { get; set; }
    }
}
