using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "0.0.0";

    private readonly Dictionary<string, string> _config = new();
    private readonly Dictionary<(string Name, Type Type), object?> _deserializedConfig = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            _deserializedConfig.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        lock (_config)
        {
            if (!_config.TryGetValue(configName, out var storedValue)) return default;
            var rawValue = StringUtil.EmptyToNull(storedValue);
            if (rawValue == null) return default;

            var cacheKey = (configName, typeof(T));
            if (_deserializedConfig.TryGetValue(cacheKey, out var cachedValue))
                return cachedValue is null ? default : (T)cachedValue;

            var value = JsonSerializer.Deserialize<T>(rawValue);
            _deserializedConfig[cacheKey] = value;
            return value;
        }
    }

    public bool IsWardenHideDeadEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("warden.hide-dead"));
        return v is null || v == "true";
    }

    public int GetWardenQuorum()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("warden.quorum"));
        return v is not null && int.TryParse(v, out var n) && n >= 1 ? n : 2;
    }

    public int GetWardenMaxSourceEntries()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("warden.max-source-entries"));
        return v is not null && int.TryParse(v, out var n) && n > 0 ? n : 2_000_000;
    }

    public bool IsWardenBackboneScopeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("warden.backbone-scope"));
        return v is null || v == "true";
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
                foreach (var cacheKey in _deserializedConfig.Keys
                             .Where(key => key.Name == configItem.ConfigName)
                             .ToArray())
                {
                    _deserializedConfig.Remove(cacheKey);
                }
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public int GetMaxQueueConnections()
    {
        var pool = Math.Max(1, GetUsenetProviderConfig().TotalPooledConnections);
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.max-queue-connections"));
        if (configured is null || !int.TryParse(configured, out var value))
            return pool;
        return Math.Clamp(value, 1, pool);
    }

    public bool IsPipeliningEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("usenet.pipelining.enabled"));
        return v != null && bool.Parse(v);
    }

    public bool IsCascadeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("usenet.cascade.enabled"));
        return v != null && bool.Parse(v);
    }

    public int GetPipeliningDepth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.pipelining.depth"));
        if (configured is null || !int.TryParse(configured, out var value)) return 8;
        return Math.Clamp(value, 1, 64);
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    public bool IsPipelinedBodyRequestsEnabled()
    {
        var configValue = StringUtil.EmptyToNull(
            GetConfigValue("usenet.pipelined-body-requests"));
        return configValue == null || bool.Parse(configValue);
    }

    public bool IsSegmentCacheEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("usenet.segment-cache.enabled"));
        return v != null && bool.Parse(v);
    }

    public string GetSegmentCachePath()
    {
        return StringUtil.EmptyToNull(GetConfigValue("usenet.segment-cache.path"))
               ?? "/config/segment-cache";
    }

    public long GetSegmentCacheMaxBytes()
    {
        var gb = long.Parse(StringUtil.EmptyToNull(GetConfigValue("usenet.segment-cache.max-gb")) ?? "10");
        return Math.Max(1, gb) * 1024L * 1024L * 1024L;
    }

    // When true, RAR archives are mounted instantly by parsing only the first
    // volume at import; trailing volumes are resolved on first read. Falls
    // back to eager parsing for archives that don't fit the supported shape
    // (multi-file, solid, encrypted, or compressed).
    public bool IsLazyRarParsingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("api.lazy-rar-parsing"));
        return v == null || bool.Parse(v);
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPlaybackWatchdogEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.watchdog-enabled"));
        return v == null || bool.Parse(v);
    }

    public int GetPlayTotalBudgetSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.total-budget-seconds"));
        if (v == null) return 30;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 3, 180) : 30;
    }

    public int GetPlayHedgeDelaySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.hedge-delay-seconds"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 30) : 3;
    }

    public int GetPlayMaxCandidates()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-candidates"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetPlayMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-attempts"));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 10;
    }

    public string GetPlayVerifyMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.verify-mode"));
        return v switch
        {
            "body" => "body",
            "stat" => "stat",
            "none" => "none",
            _ => "none",
        };
    }

    public int GetPlayVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.verify-sample-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public TimeSpan GetPlayCandidateNegativeCacheTtl()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.candidate-negative-cache-minutes"));
        if (v == null) return TimeSpan.FromMinutes(5);
        return int.TryParse(v, out var n) ? TimeSpan.FromMinutes(Math.Clamp(n, 1, 60 * 24)) : TimeSpan.FromMinutes(5);
    }

    public bool IsGrabStallFailoverEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("grab.stall-failover-enabled"));
        return v == null || bool.Parse(v);
    }

    public int GetGrabStallFailoverWindowSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("grab.stall-failover-window-seconds"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 60) : 2;
    }

    public int GetGrabStallFailoverCeilingSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("grab.stall-failover-ceiling-seconds"));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 5, 120) : 5;
    }

    public IReadOnlyList<Regex> GetSearchExcludePatterns()
    {
        var raw = GetConfigValue("search.exclude-patterns");
        if (string.IsNullOrWhiteSpace(raw)) raw = GetConfigValue("play.exclude-patterns");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Regex>();

        var patterns = new List<Regex>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            try
            {
                patterns.Add(new Regex(
                    trimmed,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(250)));
            }
            catch (ArgumentException e)
            {
                Log.Warning("Skipping invalid search.exclude-patterns regex {Pattern}: {Message}", trimmed, e.Message);
            }
        }
        return patterns;
    }

    public string GetVariantsMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.mode"));
        return v switch
        {
            "smart" => "smart",
            "collect-all" => "collect-all",
            _ => "off",
        };
    }

    public int GetVariantsTolerancePct()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.tolerance-pct"));
        if (v == null) return 25;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 100) : 25;
    }

    public int GetVariantsMaxPerGroup()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.max-per-group"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 3;
    }

    public string GetVariantsReplayStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.replay-strategy"));
        return v switch
        {
            "largest" => "largest",
            "smallest" => "smallest",
            _ => "closest-to-click",
        };
    }

    public bool IsVariantsFallbackOnFailureEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.fallback-on-failure"));
        return v == null || bool.Parse(v);
    }

    public string GetVariantsEvictionStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.eviction-strategy"));
        return v switch
        {
            "largest-first" => "largest-first",
            "smallest-first" => "smallest-first",
            "never" => "never",
            _ => "lru",
        };
    }

    public int GetVariantsEvictionActiveGraceSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.eviction-active-grace-seconds"));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 300) : 60;
    }

    public string GetPreflightMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.mode"));
        return v switch
        {
            "light" => "light",
            "standard" => "standard",
            "full" => "full",
            _ => "off",
        };
    }

    public int GetPreflightMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.max-attempts"));
        if (v == null) return 20;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 50) : 20;
    }

    public int GetPreflightTtlSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.ttl-seconds"));
        if (v == null) return 120;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 10, 1800) : 120;
    }

    public int GetPreflightIndexerMaxWaitSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.indexer-max-wait-seconds"));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 120) : 5;
    }


    public bool IsWatchtowerEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.enabled"));
        return v != null ? bool.Parse(v) : false;
    }

    public bool IsWatchtowerAutoThroughput()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.auto-throughput"));
        return v != null ? bool.Parse(v) : false;
    }

    public bool IsWatchtowerVerboseLoggingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.verbose-logging"));
        return v != null ? bool.Parse(v) : false;
    }

    public string GetWatchtowerProfileToken()
    {
        return StringUtil.EmptyToNull(GetConfigValue("watchtower.profile-token")) ?? "";
    }

    public string GetWatchtowerRanking()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.ranking"));
        return v == "largest" ? "largest" : "watchdog";
    }

    public long GetWatchtowerSizeFloorBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.size-floor-bytes"));
        if (v == null) return 524288000L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 524288000L;
    }

    public long GetWatchtowerSizeCeilingBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.size-ceiling-bytes"));
        if (v == null) return 0L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 0L;
    }

    public int GetWatchtowerMinGrabs()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.min-grabs"));
        if (v == null) return 0;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 0;
    }

    public int GetWatchtowerShortlistDepth()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.shortlist-depth"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 5) : 2;
    }

    public int GetWatchtowerGrabCapPerResolve()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.grab-cap-per-resolve"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetWatchtowerVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.verify-sample-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 20) : 3;
    }

    public int GetWatchtowerVerifyTimeoutSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.verify-timeout-seconds"));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 120) : 10;
    }

    public int GetWatchtowerActiveSetCap()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.active-set-cap"));
        if (v == null) return 100;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100000) : 100;
    }

    public int GetWatchtowerResolveConcurrency()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.resolve-concurrency"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 16) : 3;
    }

    public int GetWatchtowerDailyResolveBudget()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.daily-resolve-budget"));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 60;
    }

    public int GetWatchtowerSyncIntervalSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.sync-interval-seconds"));
        if (v == null) return 3600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 60, 86400) : 3600;
    }

    public int GetWatchtowerKeepFreshBaseSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.keepfresh-base-seconds"));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 300, 604800) : 21600;
    }

    public int GetWatchtowerKeepFreshMaxSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.keepfresh-max-seconds"));
        if (v == null) return 604800;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 2592000) : 604800;
    }

    public int GetWatchtowerUnavailableRetrySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.unavailable-retry-seconds"));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 604800) : 21600;
    }

    public string GetWatchtowerSeriesScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-scope"));
        return NormalizeSeriesScope(v) ?? "latest-season";
    }

    public static string? NormalizeSeriesScope(string? value)
    {
        return StringUtil.EmptyToNull(value) switch
        {
            "latest-season" => "latest-season",
            "first-season" => "first-season",
            "all-aired" => "all-aired",
            "recent" => "recent",
            "off" => "off",
            _ => null,
        };
    }

    public int GetWatchtowerSeriesMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-max-episodes"));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 1000) : 50;
    }

    public string GetWatchtowerSeriesCapKeep()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-cap-keep"));
        return v == "oldest" ? "oldest" : "newest";
    }

    public int GetWatchtowerSeriesRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-recent-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 3;
    }

    public bool IsWatchtowerSeasonBundlesEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundles"));
        return v != null ? bool.Parse(v) : true;
    }

    public bool IsWatchtowerSeasonBundleFallbackEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback"));
        return v != null ? bool.Parse(v) : false;
    }

    public string GetWatchtowerSeasonBundleFallbackScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback-scope"));
        return v switch
        {
            "all" => "all",
            "recent" => "recent",
            _ => "latest-season",
        };
    }

    public int GetWatchtowerSeasonBundleFallbackRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback-recent-count"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 2;
    }

    public int GetWatchtowerSeasonBundleFallbackMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback-max-episodes"));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 1000) : 50;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    /// <summary>
    /// Max concurrent NNTP STAT connections for health checks.
    /// Capped at the configured provider pool size to avoid pool starvation.
    /// </summary>
    public int GetHealthCheckConcurrency()
    {
        var poolSize = GetUsenetProviderConfig().TotalPooledConnections;
        var configured = int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("repair.healthcheck-concurrency"))
            ?? "50"
        );
        return Math.Clamp(configured, 1, Math.Max(1, poolSize));
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public IndexerConfig GetIndexerConfig()
    {
        return GetConfigValue<IndexerConfig>("indexers.instances") ?? new IndexerConfig();
    }

    public ProfileConfig GetProfileConfig()
    {
        return (GetConfigValue<ProfileConfig>("profiles.instances") ?? new ProfileConfig()).Normalized();
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.rc-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public string GetSearchUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.search-user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_SEARCH_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("db.is-startup-vacuum-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Days to keep SAB history rows. 0 disables age-based pruning.
    /// Config key wins over DATABASE_HISTORY_RETENTION_DAYS; default is 90.
    /// Automatic retention never deletes mounted WebDAV content.
    /// </summary>
    public int GetHistoryRetentionDays()
    {
        return GetRetentionDaysSetting(
            "database.history-retention-days",
            "DATABASE_HISTORY_RETENTION_DAYS",
            defaultValue: 90);
    }

    /// <summary>
    /// Days to keep health-check result rows. 0 disables age-based pruning.
    /// Config key wins over DATABASE_HEALTHCHECK_RETENTION_DAYS; default is 30.
    /// </summary>
    public int GetHealthResultRetentionDays()
    {
        return GetRetentionDaysSetting(
            "database.healthcheck-retention-days",
            "DATABASE_HEALTHCHECK_RETENTION_DAYS",
            defaultValue: 30);
    }

    private int GetRetentionDaysSetting(string configKey, string environmentVariable, int defaultValue)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configKey))
                       ?? EnvironmentUtil.GetEnvironmentVariable(environmentVariable);
        return int.TryParse(rawValue, out var parsed) && parsed >= 0 ? parsed : defaultValue;
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}
