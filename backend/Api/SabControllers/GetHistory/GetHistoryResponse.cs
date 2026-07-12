using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryResponse : SabBaseResponse
{
    [JsonPropertyName("history")]
    public HistoryObject History { get; set; }

    public class HistoryObject
    {
        [JsonPropertyName("slots")]
        public List<HistorySlot> Slots { get; set; }

        [JsonPropertyName("noofslots")]
        public int TotalCount { get; set; }
    }

    public class HistorySlot
    {
        [JsonPropertyName("nzo_id")]
        public string NzoId { get; set; }

        [JsonPropertyName("nzb_name")]
        public string NzbName { get; set; }

        [JsonPropertyName("name")]
        public string JobName { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HistoryItem.DownloadStatusOption Status { get; set; }

        [JsonPropertyName("bytes")]
        public long SizeInBytes { get; set; }

        [JsonPropertyName("storage")]
        public string? DownloadPath { get; set; }

        [JsonPropertyName("download_time")]
        public int DownloadTimeSeconds { get; set; }

        [JsonPropertyName("completed")]
        public long Completed { get; set; }

        [JsonPropertyName("fail_message")]
        public string FailMessage { get; set; }

        [JsonPropertyName("nzb_blob_id")]
        public string? NzbBlobId { get; set; }

        [JsonPropertyName("indexer")]
        public string? Indexer { get; set; }

        [JsonPropertyName("providers")]
        public List<ProviderUsage>? Providers { get; set; }

        public static HistorySlot FromHistoryItem
        (
            HistoryItem historyItem,
            DavItem? downloadFolder,
            ConfigManager configManager,
            IReadOnlyDictionary<string, long>? providerUsage = null,
            IReadOnlyDictionary<string, string?>? nicknamesByHost = null
        )
        {
            return new HistorySlot()
            {
                NzoId = historyItem.Id.ToString(),
                NzbName = historyItem.FileName,
                JobName = historyItem.JobName,
                Category = historyItem.Category,
                Status = historyItem.DownloadStatus,
                SizeInBytes = historyItem.TotalSegmentBytes,
                DownloadPath = GetDownloadPath(historyItem, downloadFolder, configManager),
                DownloadTimeSeconds = historyItem.DownloadTimeSeconds,
                Completed = new DateTimeOffset(historyItem.CreatedAt).ToUnixTimeSeconds(),
                FailMessage = historyItem.FailMessage ?? "",
                NzbBlobId = historyItem.NzbBlobId?.ToString(),
                Indexer = historyItem.IndexerName,
                Providers = providerUsage is { Count: > 0 }
                    ? providerUsage
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new ProviderUsage
                        {
                            Host = kv.Key,
                            Nickname = nicknamesByHost is not null && nicknamesByHost.TryGetValue(kv.Key, out var n) ? n : null,
                            Segments = kv.Value,
                        })
                        .ToList()
                    : null,
            };
        }

        public class ProviderUsage
        {
            [JsonPropertyName("host")] public required string Host { get; init; }
            [JsonPropertyName("nickname")] public string? Nickname { get; init; }
            [JsonPropertyName("segments")] public required long Segments { get; init; }
        }

        private static string? GetDownloadPath
        (
            HistoryItem historyItem,
            DavItem? downloadFolder,
            ConfigManager configManager
        )
        {
            // return null for null download folder
            if (downloadFolder == null) return null;
            var importStrategy = configManager.GetImportStrategy();

            // return completed-downloads path
            if (importStrategy == "strm")
            {
                return Path.Join(new[]
                {
                    configManager.GetStrmCompletedDownloadDir(),
                    historyItem.Category,
                    downloadFolder.Name
                });
            }

            // return completed-symlinks path
            if (importStrategy == "symlinks")
            {
                return Path.Join(new[]
                {
                    configManager.GetRcloneMountDir(),
                    DavItem.SymlinkFolder.Name,
                    historyItem.Category,
                    downloadFolder.Name
                });
            }

            throw new Exception("Unknown import strategy");
        }
    }
}
