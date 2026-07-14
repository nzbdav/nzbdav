using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "0.0.0";

    private readonly Dictionary<string, string> _config = new();
    private readonly Dictionary<(string Name, Type Type), object?> _deserializedConfig = new();
    // Compiled exclude patterns are cached and rebuilt only when an exclude-related
    // config key changes (see UpdateValues / LoadConfig), rather than recompiled on
    // every search. Guarded by its own lock so searches and config writes don't
    // contend on _config.
    private readonly object _excludeLock = new();
    private IReadOnlyList<Regex>? _compiledExcludeCache;
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
        lock (_excludeLock) { _compiledExcludeCache = null; }
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
        string? rawValue;
        var cacheKey = (configName, typeof(T));

        lock (_config)
        {
            if (!_config.TryGetValue(configName, out var storedValue)) return default;
            rawValue = StringUtil.EmptyToNull(storedValue);
            if (rawValue == null) return default;

            if (_deserializedConfig.TryGetValue(cacheKey, out var cachedValue))
                return cachedValue is null ? default : (T)cachedValue;
        }

        // Deserialize outside the lock so large JSON payloads (providers, indexers)
        // do not block other config readers on the hot path.
        var value = JsonSerializer.Deserialize<T>(rawValue);

        lock (_config)
        {
            if (_deserializedConfig.TryGetValue(cacheKey, out var cachedValue))
                return cachedValue is null ? default : (T)cachedValue;

            if (_config.TryGetValue(configName, out var storedValue) &&
                StringUtil.EmptyToNull(storedValue) == rawValue)
            {
                _deserializedConfig[cacheKey] = value;
            }

            return value;
        }
    }

    public bool IsWardenHideDeadEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenHideDead));
        return v is null || v == "true";
    }

    public int GetWardenQuorum()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenQuorum));
        return v is not null && int.TryParse(v, out var n) && n >= 1 ? n : 2;
    }

    public int GetWardenMaxSourceEntries()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenMaxSourceEntries));
        return v is not null && int.TryParse(v, out var n) && n > 0 ? n : 2_000_000;
    }

    public bool IsWardenBackboneScopeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WardenBackboneScope));
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

        if (configItems.Any(x => x.ConfigName.StartsWith(ConfigKeys.SearchExcludePrefix, StringComparison.Ordinal)
                                 || x.ConfigName == ConfigKeys.PlayExcludePatterns))
        {
            lock (_excludeLock) { _compiledExcludeCache = null; }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    /// <summary>
    /// Validates incoming config values, failing fast for anything that would otherwise throw
    /// deep inside a request/background task at read time (non-numeric ints, non-boolean flags,
    /// malformed JSON). Empty values are treated as "unset" and always allowed, matching the
    /// getters' fallback-to-default behavior.
    /// </summary>
    public static void ValidateConfigItems(IEnumerable<ConfigItem> configItems)
    {
        foreach (var item in configItems)
        {
            var value = StringUtil.EmptyToNull(item.ConfigValue);
            if (value == null) continue;

            switch (item.ConfigName)
            {
                case ConfigKeys.UsenetMaxDownloadConnections:
                case ConfigKeys.UsenetMaxQueueConnections:
                case ConfigKeys.UsenetPipeliningDepth:
                case ConfigKeys.UsenetArticleBufferSize:
                case ConfigKeys.UsenetSegmentCacheMaxGb:
                case ConfigKeys.UsenetStreamingPriority:
                case ConfigKeys.WardenQuorum:
                case ConfigKeys.WardenMaxSourceEntries:
                case ConfigKeys.PlayTotalBudgetSeconds:
                case ConfigKeys.PlayHedgeDelaySeconds:
                case ConfigKeys.PlayMaxCandidates:
                case ConfigKeys.PlayMaxAttempts:
                case ConfigKeys.PlayVerifySampleCount:
                case ConfigKeys.PlayCandidateNegativeCacheMinutes:
                case ConfigKeys.GrabStallFailoverWindowSeconds:
                case ConfigKeys.GrabStallFailoverCeilingSeconds:
                case ConfigKeys.SearchExcludeSyncRefreshMinutes:
                case ConfigKeys.VariantsTolerancePct:
                case ConfigKeys.VariantsMaxPerGroup:
                case ConfigKeys.VariantsEvictionActiveGraceSeconds:
                case ConfigKeys.PreflightMaxAttempts:
                case ConfigKeys.PreflightTtlSeconds:
                case ConfigKeys.PreflightIndexerMaxWaitSeconds:
                case ConfigKeys.WatchtowerSizeFloorBytes:
                case ConfigKeys.WatchtowerSizeCeilingBytes:
                case ConfigKeys.WatchtowerMinGrabs:
                case ConfigKeys.WatchtowerShortlistDepth:
                case ConfigKeys.WatchtowerGrabCapPerResolve:
                case ConfigKeys.WatchtowerVerifySampleCount:
                case ConfigKeys.WatchtowerVerifyTimeoutSeconds:
                case ConfigKeys.WatchtowerActiveSetCap:
                case ConfigKeys.WatchtowerResolveConcurrency:
                case ConfigKeys.WatchtowerDailyResolveBudget:
                case ConfigKeys.WatchtowerSyncIntervalSeconds:
                case ConfigKeys.WatchtowerKeepfreshBaseSeconds:
                case ConfigKeys.WatchtowerKeepfreshMaxSeconds:
                case ConfigKeys.WatchtowerUnavailableRetrySeconds:
                case ConfigKeys.WatchtowerSeriesMaxEpisodes:
                case ConfigKeys.WatchtowerSeriesRecentCount:
                case ConfigKeys.WatchtowerSeasonBundleFallbackRecentCount:
                case ConfigKeys.WatchtowerSeasonBundleFallbackMaxEpisodes:
                case ConfigKeys.RepairHealthcheckConcurrency:
                case ConfigKeys.DatabaseHistoryRetentionDays:
                case ConfigKeys.DatabaseHealthcheckRetentionDays:
                case ConfigKeys.MaintenanceRemoveOrphanedScheduleTime:
                case ConfigKeys.BackupScheduleTime:
                case ConfigKeys.BackupRetentionCount:
                    RequireLong(item.ConfigName, value);
                    break;

                case ConfigKeys.ApiEnsureImportableVideo:
                case ConfigKeys.ApiIgnoreHistoryLimit:
                case ConfigKeys.ApiLazyRarParsing:
                case ConfigKeys.ApiNzbBackupEnabled:
                case ConfigKeys.WebdavShowHiddenFiles:
                case ConfigKeys.WebdavEnforceReadonly:
                case ConfigKeys.WebdavPreviewPar2Files:
                case ConfigKeys.UsenetMaxDownloadConnectionsPerStream:
                case ConfigKeys.UsenetPipeliningEnabled:
                case ConfigKeys.UsenetCascadeEnabled:
                case ConfigKeys.UsenetPipelinedBodyRequests:
                case ConfigKeys.UsenetSegmentCacheEnabled:
                case ConfigKeys.PlayWatchdogEnabled:
                case ConfigKeys.PlayPreferSubtitles:
                case ConfigKeys.GrabStallFailoverEnabled:
                case ConfigKeys.VariantsFallbackOnFailure:
                case ConfigKeys.WatchtowerEnabled:
                case ConfigKeys.WatchtowerAutoThroughput:
                case ConfigKeys.WatchtowerVerboseLogging:
                case ConfigKeys.WatchtowerSeasonBundles:
                case ConfigKeys.WatchtowerSeasonBundleFallback:
                case ConfigKeys.WardenHideDead:
                case ConfigKeys.WardenBackboneScope:
                case ConfigKeys.RepairEnable:
                case ConfigKeys.RcloneRcEnabled:
                case ConfigKeys.DbIsStartupVacuumEnabled:
                case ConfigKeys.MaintenanceRemoveOrphanedScheduleEnabled:
                case ConfigKeys.BackupScheduleEnabled:
                    RequireBool(item.ConfigName, value);
                    break;

                case ConfigKeys.UsenetProviders:
                    RequireJson<UsenetProviderConfig>(item.ConfigName, value);
                    break;

                case ConfigKeys.ArrInstances:
                    RequireJson<ArrConfig>(item.ConfigName, value);
                    break;

                case ConfigKeys.IndexersInstances:
                    RequireJson<IndexerConfig>(item.ConfigName, value);
                    break;

                case ConfigKeys.ProfilesInstances:
                    RequireJson<ProfileConfig>(item.ConfigName, value);
                    break;
            }
        }

        return;

        static void RequireLong(string key, string value)
        {
            if (!long.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be a whole number, but was '{value}'.");
        }

        static void RequireBool(string key, string value)
        {
            if (!bool.TryParse(value, out _))
                throw new ArgumentException($"Config value for '{key}' must be 'true' or 'false', but was '{value}'.");
        }

        static void RequireJson<T>(string key, string value)
        {
            try
            {
                JsonSerializer.Deserialize<T>(value);
            }
            catch (JsonException e)
            {
                throw new ArgumentException($"Config value for '{key}' is not valid JSON: {e.Message}");
            }
        }
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneMountDir))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiKey))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue(ConfigKeys.ApiStrmKey)
               ?? throw new InvalidOperationException($"The `{ConfigKeys.ApiStrmKey}` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiCategories))
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
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiManualCategory))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavUser))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavPass));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiEnsureImportableVideo));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavShowHiddenFiles));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MediaLibraryDir));
    }

    // The total connection budget used for webdav streaming. "0" or empty means
    // "auto": use the combined connection limit of the primary Pool providers
    // (backups excluded), recomputed as providers change. Any positive value is
    // a manual override.
    public int GetMaxDownloadConnections()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnections));
        if (configured is null || !int.TryParse(configured, out var value) || value <= 0)
            return GetUsenetProviderConfig().TotalPooledConnections;
        return value;
    }

    // When true, the max-download-connections budget is applied per playback
    // stream (each concurrent stream gets its own budget) rather than as a
    // single pool shared across all streams. The provider's connection limit
    // still caps the actual number of open connections.
    public bool IsMaxDownloadConnectionsPerStream()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnectionsPerStream));
        return v != null && bool.Parse(v);
    }

    // The per-stream streaming budget used when "per stream" mode is on. Each
    // stream is allowed a performance-preset fraction (Low/Medium/High/Max) of
    // the overall download-connection budget, at least 1.
    public int GetMaxDownloadConnectionsPerStreamCount()
    {
        var fraction = GetMaxDownloadConnectionsPerStreamFraction();
        return Math.Max(1, (int)Math.Round(GetMaxDownloadConnections() * fraction));
    }

    private double GetMaxDownloadConnectionsPerStreamFraction()
    {
        var preset = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxDownloadConnectionsPerStreamPreset));
        return preset?.ToLowerInvariant() switch
        {
            "low" => 0.25,
            "medium" => 0.5,
            "high" => 0.75,
            "max" => 1.0,
            _ => 0.75,
        };
    }

    public int GetMaxQueueConnections()
    {
        var pool = Math.Max(1, GetUsenetProviderConfig().TotalPooledConnections);
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetMaxQueueConnections));
        if (configured is null || !int.TryParse(configured, out var value))
            return pool;
        return Math.Clamp(value, 1, pool);
    }

    public bool IsPipeliningEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetPipeliningEnabled));
        return v != null && bool.Parse(v);
    }

    public bool IsCascadeEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetCascadeEnabled));
        return v != null && bool.Parse(v);
    }

    public int GetPipeliningDepth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetPipeliningDepth));
        if (configured is null || !int.TryParse(configured, out var value)) return 8;
        return Math.Clamp(value, 1, 64);
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetArticleBufferSize))
            ?? "40"
        );
    }

    public bool IsPipelinedBodyRequestsEnabled()
    {
        var configValue = StringUtil.EmptyToNull(
            GetConfigValue(ConfigKeys.UsenetPipelinedBodyRequests));
        return configValue == null || bool.Parse(configValue);
    }

    public bool IsSegmentCacheEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCacheEnabled));
        return v != null && bool.Parse(v);
    }

    public string GetSegmentCachePath()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCachePath))
               ?? "/config/segment-cache";
    }

    public long GetSegmentCacheMaxBytes()
    {
        var gb = long.Parse(StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetSegmentCacheMaxGb)) ?? "10");
        return Math.Max(1, gb) * 1024L * 1024L * 1024L;
    }

    // When true, RAR archives are mounted instantly by parsing only the first
    // volume at import; trailing volumes are resolved on first read. Falls
    // back to eager parsing for archives that don't fit the supported shape
    // (multi-file, solid, encrypted, or compressed).
    public bool IsLazyRarParsingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiLazyRarParsing));
        return v == null || bool.Parse(v);
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.UsenetStreamingPriority));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavEnforceReadonly));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue(ConfigKeys.ApiEnsureArticleExistenceCategories);
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPlaybackWatchdogEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayWatchdogEnabled));
        return v == null || bool.Parse(v);
    }

    public int GetPlayTotalBudgetSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayTotalBudgetSeconds));
        if (v == null) return 30;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 3, 180) : 30;
    }

    public int GetPlayHedgeDelaySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayHedgeDelaySeconds));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 30) : 3;
    }

    public int GetPlayMaxCandidates()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayMaxCandidates));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetPlayMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayMaxAttempts));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 10;
    }

    public string GetPlayVerifyMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayVerifyMode));
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
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayVerifySampleCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public TimeSpan GetPlayCandidateNegativeCacheTtl()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayCandidateNegativeCacheMinutes));
        if (v == null) return TimeSpan.FromMinutes(5);
        return int.TryParse(v, out var n) ? TimeSpan.FromMinutes(Math.Clamp(n, 1, 60 * 24)) : TimeSpan.FromMinutes(5);
    }

    public bool IsPlaySubtitlePreferenceEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PlayPreferSubtitles));
        return v == null || !bool.TryParse(v, out var enabled) || enabled;
    }

    public bool IsGrabStallFailoverEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverEnabled));
        return v == null || bool.Parse(v);
    }

    public int GetGrabStallFailoverWindowSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverWindowSeconds));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 60) : 2;
    }

    public int GetGrabStallFailoverCeilingSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.GrabStallFailoverCeilingSeconds));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 5, 120) : 5;
    }

    public const int DefaultExcludeSyncRefreshMinutes = 720;

    public IReadOnlyList<Regex> GetSearchExcludePatterns()
    {
        lock (_excludeLock)
        {
            return _compiledExcludeCache ??= BuildExcludePatterns();
        }
    }

    // Synced patterns (last-good cache, in configured-URL order) take precedence and are
    // emitted first; the manual textarea is appended after, with exact duplicates dropped
    // so a pattern present in both sources is compiled and evaluated only once.
    private IReadOnlyList<Regex> BuildExcludePatterns()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<Regex>();

        var cache = GetSearchExcludeSyncCache();
        foreach (var url in GetSearchExcludeSyncUrls())
        {
            if (!cache.Urls.TryGetValue(url, out var entry)) continue;
            foreach (var item in entry.Items)
                if (ExcludePatternParser.Parse(item) is { } p && seen.Add(p.Key))
                    compiled.Add(p.Regex);
        }

        var raw = GetConfigValue(ConfigKeys.SearchExcludePatterns);
        if (string.IsNullOrWhiteSpace(raw)) raw = GetConfigValue(ConfigKeys.PlayExcludePatterns);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (ExcludePatternParser.Parse(line) is { } p && seen.Add(p.Key))
                    compiled.Add(p.Regex);
        }

        return compiled;
    }

    /// <summary>Configured synced-exclude source URLs (http/https only, de-duplicated, in order).</summary>
    public IReadOnlyList<string> GetSearchExcludeSyncUrls()
    {
        var raw = GetConfigValue(ConfigKeys.SearchExcludeSyncUrls);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var urls = new List<string>();
        // Exact-match dedup: URL paths are case-sensitive, so two URLs that differ only
        // by path casing are distinct sources and must both be kept.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
            if (seen.Add(line)) urls.Add(line);
        }
        return urls;
    }

    public int GetSearchExcludeSyncRefreshMinutes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.SearchExcludeSyncRefreshMinutes));
        if (v == null) return DefaultExcludeSyncRefreshMinutes;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 15, 10080) : DefaultExcludeSyncRefreshMinutes;
    }

    public ExcludeSyncCache GetSearchExcludeSyncCache()
    {
        var raw = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.SearchExcludeSyncCache));
        if (raw == null) return new ExcludeSyncCache();
        try { return JsonSerializer.Deserialize<ExcludeSyncCache>(raw) ?? new ExcludeSyncCache(); }
        catch (JsonException) { return new ExcludeSyncCache(); }
    }

    public string GetVariantsMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsMode));
        return v switch
        {
            "smart" => "smart",
            "collect-all" => "collect-all",
            _ => "off",
        };
    }

    public int GetVariantsTolerancePct()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsTolerancePct));
        if (v == null) return 25;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 100) : 25;
    }

    public int GetVariantsMaxPerGroup()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsMaxPerGroup));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 3;
    }

    public string GetVariantsReplayStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsReplayStrategy));
        return v switch
        {
            "largest" => "largest",
            "smallest" => "smallest",
            _ => "closest-to-click",
        };
    }

    public bool IsVariantsFallbackOnFailureEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsFallbackOnFailure));
        return v == null || bool.Parse(v);
    }

    public string GetVariantsEvictionStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsEvictionStrategy));
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
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.VariantsEvictionActiveGraceSeconds));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 300) : 60;
    }

    public string GetPreflightMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightMode));
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
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightMaxAttempts));
        if (v == null) return 20;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 50) : 20;
    }

    public int GetPreflightTtlSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightTtlSeconds));
        if (v == null) return 120;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 10, 1800) : 120;
    }

    public int GetPreflightIndexerMaxWaitSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.PreflightIndexerMaxWaitSeconds));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 120) : 5;
    }


    public bool IsWatchtowerEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerEnabled));
        return v != null ? bool.Parse(v) : false;
    }

    public bool IsWatchtowerAutoThroughput()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerAutoThroughput));
        return v != null ? bool.Parse(v) : false;
    }

    public bool IsWatchtowerVerboseLoggingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerboseLogging));
        return v != null ? bool.Parse(v) : false;
    }

    public string GetWatchtowerProfileToken()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerProfileToken)) ?? "";
    }

    public string GetWatchtowerRanking()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerRanking));
        return v == "largest" ? "largest" : "watchdog";
    }

    public long GetWatchtowerSizeFloorBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSizeFloorBytes));
        if (v == null) return 524288000L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 524288000L;
    }

    public long GetWatchtowerSizeCeilingBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSizeCeilingBytes));
        if (v == null) return 0L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 0L;
    }

    public int GetWatchtowerMinGrabs()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerMinGrabs));
        if (v == null) return 0;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 0;
    }

    public int GetWatchtowerShortlistDepth()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerShortlistDepth));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 5) : 2;
    }

    public int GetWatchtowerGrabCapPerResolve()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerGrabCapPerResolve));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetWatchtowerVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerifySampleCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 20) : 3;
    }

    public int GetWatchtowerVerifyTimeoutSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerVerifyTimeoutSeconds));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 120) : 10;
    }

    public int GetWatchtowerActiveSetCap()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerActiveSetCap));
        if (v == null) return 100;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100000) : 100;
    }

    public int GetWatchtowerResolveConcurrency()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerResolveConcurrency));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 16) : 3;
    }

    public int GetWatchtowerDailyResolveBudget()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerDailyResolveBudget));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 60;
    }

    public int GetWatchtowerSyncIntervalSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSyncIntervalSeconds));
        if (v == null) return 3600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 60, 86400) : 3600;
    }

    public int GetWatchtowerKeepFreshBaseSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerKeepfreshBaseSeconds));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 300, 604800) : 21600;
    }

    public int GetWatchtowerKeepFreshMaxSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerKeepfreshMaxSeconds));
        if (v == null) return 604800;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 2592000) : 604800;
    }

    public int GetWatchtowerUnavailableRetrySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerUnavailableRetrySeconds));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 604800) : 21600;
    }

    public string GetWatchtowerSeriesScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesScope));
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
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesMaxEpisodes));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 1000) : 50;
    }

    public string GetWatchtowerSeriesCapKeep()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesCapKeep));
        return v == "oldest" ? "oldest" : "newest";
    }

    public int GetWatchtowerSeriesRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeriesRecentCount));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 3;
    }

    public bool IsWatchtowerSeasonBundlesEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundles));
        return v != null ? bool.Parse(v) : true;
    }

    public bool IsWatchtowerSeasonBundleFallbackEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallback));
        return v != null ? bool.Parse(v) : false;
    }

    public string GetWatchtowerSeasonBundleFallbackScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackScope));
        return v switch
        {
            "all" => "all",
            "recent" => "recent",
            _ => "latest-season",
        };
    }

    public int GetWatchtowerSeasonBundleFallbackRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackRecentCount));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 2;
    }

    public int GetWatchtowerSeasonBundleFallbackMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WatchtowerSeasonBundleFallbackMaxEpisodes));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 1000) : 50;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.WebdavPreviewPar2Files));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiIgnoreHistoryLimit));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// Server-side ceiling for SAB history <c>slots</c> returned in one response.
    /// Default 10_000 — far above a typical Arr import pass — removes the unbounded
    /// <c>int.MaxValue</c> worst case when ignore-history-limit is enabled.
    /// </summary>
    public int GetHistoryMaxPageSize()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiHistoryMaxPageSize));
        if (v == null) return 10_000;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100_000) : 10_000;
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairEnable));
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
            StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RepairHealthcheckConcurrency))
            ?? "50"
        );
        return Math.Clamp(configured, 1, Math.Max(1, poolSize));
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>(ConfigKeys.ArrInstances) ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>(ConfigKeys.UsenetProviders) ?? defaultValue;
    }

    public IndexerConfig GetIndexerConfig()
    {
        return GetConfigValue<IndexerConfig>(ConfigKeys.IndexersInstances) ?? new IndexerConfig();
    }

    public ProfileConfig GetProfileConfig()
    {
        return (GetConfigValue<ProfileConfig>(ConfigKeys.ProfilesInstances) ?? new ProfileConfig()).Normalized();
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue(ConfigKeys.ApiDuplicateNzbBehavior) ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv, *unpack.mkv, *.unpack.mp4";
        return (GetConfigValue(ConfigKeys.ApiDownloadFileBlocklist) ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue(ConfigKeys.ApiImportStrategy) ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue(ConfigKeys.ApiCompletedDownloadsDir) ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue(ConfigKeys.GeneralBaseUrl) ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.RcloneRcEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue(ConfigKeys.RcloneHost);
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue(ConfigKeys.RcloneUser);
    }

    public string? GetRclonePass()
    {
        return GetConfigValue(ConfigKeys.RclonePass);
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiUserAgent))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public string GetSearchUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiSearchUserAgent))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_SEARCH_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.DbIsStartupVacuumEnabled));
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
            ConfigKeys.DatabaseHistoryRetentionDays,
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
            ConfigKeys.DatabaseHealthcheckRetentionDays,
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
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiNzbBackupEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.ApiNzbBackupLocation));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MaintenanceRemoveOrphanedScheduleEnabled));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.MaintenanceRemoveOrphanedScheduleTime));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public bool IsDatabaseBackupScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupScheduleEnabled));
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public TimeSpan DatabaseBackupSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupScheduleTime));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public int GetDatabaseBackupRetentionCount()
    {
        var defaultValue = 5;
        var configValue = StringUtil.EmptyToNull(GetConfigValue(ConfigKeys.BackupRetentionCount));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var count) || count < 0) return defaultValue;
        return count;
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}
