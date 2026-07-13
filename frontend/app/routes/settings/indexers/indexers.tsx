import styles from "./indexers.module.css";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import { Button, Spinner, Textarea } from "~/components/ui";
import { isMaskedSecret } from "~/utils/config-mask";

type IndexersSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
    savedConfig?: Record<string, string>
};

interface ResultFilter {
    Enabled: boolean;
    SkipPassworded: boolean;
    MinGrabs: number;
    GrabsGraceHours: number;
    MaxAgeDaysWithoutGrabs: number;
    PreferDownloaded: boolean;
}

// Optimised baseline. Used both as the initial UI state when an indexer has no Filter
// yet AND as the comparison baseline that decides whether to omit the Filter object from
// the saved JSON (so users who never touch this section keep a clean config). The master
// toggle (`Enabled`) starts off — the rest are the values that take effect the moment a
// user flips it on, without them having to think about any sub-setting.
const OPTIMISED_DEFAULTS: ResultFilter = {
    Enabled: false,
    SkipPassworded: true,
    MinGrabs: 1,
    GrabsGraceHours: 6,
    MaxAgeDaysWithoutGrabs: 0,
    PreferDownloaded: true,
};

interface ConnectionDetails {
    Name: string;
    Url: string;
    ApiKey: string;
    Enabled: boolean;
    UserAgent?: string;
    SearchUserAgent?: string;
    RetrieveUserAgent?: string;
    MaxRequestsPerMinute?: number;
    EnableStrictMatching?: boolean;
    ProxyUrl?: string;
    TimeoutSeconds?: number;
    SearchResultLimit?: number;
    HitLimit?: number;
    DownloadLimit?: number;
    HitLimitResetTime?: number;
    ExtraMovieCategories?: string;
    ExtraTvCategories?: string;
    IgnoreCategoryFilter?: boolean;
    Filter?: ResultFilter;
}

interface IndexerConfig {
    ProxyUrl?: string;
    TimeoutSeconds?: number;
    SearchResultLimit?: number;
    Indexers: ConnectionDetails[];
}

// Hard fallback when neither the indexer nor the global override sets a timeout.
// Mirrors IndexerConfig.DefaultTimeoutSeconds in the backend.
const DEFAULT_TIMEOUT_SECONDS = 30;

// Hard fallback for results gathered per indexer per search; above this the indexer is paged.
// Mirrors IndexerConfig.DefaultSearchResultLimit in the backend.
const DEFAULT_SEARCH_RESULT_LIMIT = 100;

type PatternIssue = { line: number, pattern: string, error: string };

function validateExcludePatterns(raw: string): PatternIssue[] {
    const issues: PatternIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
        try {
            new RegExp(trimmed, "i");
        } catch (e: any) {
            issues.push({ line: i + 1, pattern: trimmed, error: e?.message ?? "invalid regex" });
        }
    }
    return issues;
}

type ExcludeSyncUrlStatus = {
    url: string,
    count: number,
    fetchedAt: number | null,
    lastChecked: number | null,
    error: string | null,
};

type SyncUrlIssue = { line: number, value: string, error: string };

function validateSyncUrls(raw: string): SyncUrlIssue[] {
    const issues: SyncUrlIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
        try {
            const parsed = new URL(trimmed);
            if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
                issues.push({ line: i + 1, value: trimmed, error: "must be http(s)" });
            }
        } catch {
            issues.push({ line: i + 1, value: trimmed, error: "invalid URL" });
        }
    }
    return issues;
}

function isRefreshValid(raw: string): boolean {
    const trimmed = raw.trim();
    if (trimmed === "") return true; // blank → server default (720)
    const n = Number(trimmed);
    return Number.isInteger(n) && n >= 15 && n <= 10080;
}

function syncHostLabel(url: string): string {
    try { return new URL(url).host; } catch { return url; }
}

function syncRelativeTime(unixSeconds: number): string {
    const diff = Math.floor(Date.now() / 1000) - unixSeconds;
    if (diff < 60) return "just now";
    if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
    if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
    return `${Math.floor(diff / 86400)}d ago`;
}

function parseConfig(raw: string): IndexerConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return {
            ProxyUrl: parsed.ProxyUrl ?? "",
            TimeoutSeconds: typeof parsed.TimeoutSeconds === "number" ? parsed.TimeoutSeconds : undefined,
            SearchResultLimit: typeof parsed.SearchResultLimit === "number" ? parsed.SearchResultLimit : undefined,
            Indexers: parsed.Indexers ?? [],
        };
    } catch {
        return { ProxyUrl: "", Indexers: [] };
    }
}

function serializeConfig(c: IndexerConfig): string {
    const out: IndexerConfig = { Indexers: c.Indexers };
    if (c.ProxyUrl && c.ProxyUrl.trim()) out.ProxyUrl = c.ProxyUrl.trim();
    if (typeof c.TimeoutSeconds === "number" && c.TimeoutSeconds > 0) out.TimeoutSeconds = c.TimeoutSeconds;
    if (typeof c.SearchResultLimit === "number" && c.SearchResultLimit > 0) out.SearchResultLimit = c.SearchResultLimit;
    return JSON.stringify(out);
}

// Positive integer (or empty string = "use fallback"). Rejects decimals and negatives.
function isTimeoutValid(raw: string): boolean {
    if (!raw.trim()) return true;
    const n = Number(raw);
    return Number.isInteger(n) && n > 0 && raw.trim() === n.toString();
}

function isCategoryListValid(raw: string): boolean {
    if (!raw.trim()) return true;
    const parts = raw.split(",").map(p => p.trim()).filter(p => p.length > 0);
    if (parts.length === 0) return true;
    return parts.every(p => /^\d+$/.test(p));
}

// http://host:port, https://..., optionally with user:pass@. Empty string = no proxy.
function isProxyUrlValid(raw: string): boolean {
    if (!raw.trim()) return true;
    try {
        const u = new URL(raw);
        return (u.protocol === "http:" || u.protocol === "https:") && u.host !== "";
    } catch {
        return false;
    }
}

export function IndexersSettings({ config, setNewConfig, savedConfig }: IndexersSettingsProps) {
    const indexerConfig = useMemo(() => parseConfig(config["indexers.instances"]), [config]);
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);

    const handleAdd = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEdit = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDelete = useCallback((index: number) => {
        const next: IndexerConfig = {
            ...indexerConfig,
            Indexers: indexerConfig.Indexers.filter((_, i) => i !== index),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleToggle = useCallback((index: number) => {
        const next: IndexerConfig = {
            ...indexerConfig,
            Indexers: indexerConfig.Indexers.map((x, i) =>
                i === index ? { ...x, Enabled: !x.Enabled } : x
            ),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSave = useCallback((indexer: ConnectionDetails) => {
        const next: IndexerConfig = { ...indexerConfig, Indexers: [...indexerConfig.Indexers] };
        if (editingIndex !== null) {
            next.Indexers[editingIndex] = indexer;
        } else {
            next.Indexers.push(indexer);
        }
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
        handleCloseModal();
    }, [config, indexerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleProxyChange = useCallback((value: string) => {
        const next: IndexerConfig = { ...indexerConfig, ProxyUrl: value };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleTimeoutChange = useCallback((value: string) => {
        const trimmed = value.replace(/[^0-9]/g, "");
        const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
        const next: IndexerConfig = { ...indexerConfig, TimeoutSeconds: n && n > 0 ? n : undefined };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleSearchLimitChange = useCallback((value: string) => {
        const trimmed = value.replace(/[^0-9]/g, "");
        const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
        const next: IndexerConfig = { ...indexerConfig, SearchResultLimit: n && n > 0 ? n : undefined };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const excludePatterns = config["search.exclude-patterns"] ?? "";
    const patternIssues = useMemo(() => validateExcludePatterns(excludePatterns), [excludePatterns]);
    const handleExcludePatternsChange = useCallback((value: string) => {
        setNewConfig({ ...config, "search.exclude-patterns": value });
    }, [config, setNewConfig]);

    const excludeSyncUrls = config["search.exclude-sync-urls"] ?? "";
    const excludeSyncRefresh = config["search.exclude-sync-refresh-minutes"] ?? "";
    const syncUrlIssues = useMemo(() => validateSyncUrls(excludeSyncUrls), [excludeSyncUrls]);
    const handleSyncUrlsChange = useCallback((value: string) => {
        setNewConfig({ ...config, "search.exclude-sync-urls": value });
    }, [config, setNewConfig]);
    const handleSyncRefreshChange = useCallback((value: string) => {
        const cleaned = value.replace(/[^0-9]/g, "");
        setNewConfig({ ...config, "search.exclude-sync-refresh-minutes": cleaned });
    }, [config, setNewConfig]);

    const [syncStatus, setSyncStatus] = useState<ExcludeSyncUrlStatus[]>([]);
    const [isSyncing, setIsSyncing] = useState(false);
    const loadSyncStatus = useCallback(async () => {
        try {
            const res = await fetch("/settings/exclude-sync");
            if (res.ok) setSyncStatus((await res.json()).urls ?? []);
        } catch {
            // status is best-effort; ignore transient failures
        }
    }, []);
    // Load on mount, and re-pull after a save changes the synced URLs. The backend
    // refetches on config change, so poll once immediately and once after it settles.
    const savedSyncUrls = savedConfig?.["search.exclude-sync-urls"] ?? "";
    useEffect(() => {
        void loadSyncStatus();
        const timer = setTimeout(() => { void loadSyncStatus(); }, 2000);
        return () => clearTimeout(timer);
    }, [savedSyncUrls, loadSyncStatus]);
    const handleSyncNow = useCallback(async () => {
        setIsSyncing(true);
        try {
            const res = await fetch("/settings/exclude-sync", { method: "POST" });
            if (res.ok) setSyncStatus((await res.json()).urls ?? []);
        } catch {
            // ignore; the row shows the backend-reported error on the next status load
        } finally {
            setIsSyncing(false);
        }
    }, []);

    const defaultSearchUserAgent = config["api.search-user-agent"] ?? "";
    const handleSearchUserAgentChange = useCallback((value: string) => {
        setNewConfig({ ...config, "api.search-user-agent": value });
    }, [config, setNewConfig]);

    const defaultRetrieveUserAgent = config["api.user-agent"] ?? "";
    const handleRetrieveUserAgentChange = useCallback((value: string) => {
        setNewConfig({ ...config, "api.user-agent": value });
    }, [config, setNewConfig]);

    const proxyUrl = indexerConfig.ProxyUrl ?? "";
    const proxyValid = isProxyUrlValid(proxyUrl);
    const globalTimeoutRaw = typeof indexerConfig.TimeoutSeconds === "number" && indexerConfig.TimeoutSeconds > 0
        ? indexerConfig.TimeoutSeconds.toString()
        : "";
    const globalSearchLimitRaw = typeof indexerConfig.SearchResultLimit === "number" && indexerConfig.SearchResultLimit > 0
        ? indexerConfig.SearchResultLimit.toString()
        : "";

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>
                        <div>Defaults</div>
                        <div className={styles["section-description"]}>
                            Global settings used by indexers when no per-indexer override is set.
                        </div>
                    </div>
                </div>
                <div className={styles["form-grid"]}>
                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-default-proxy" className={styles["form-label"]}>HTTP(S) Proxy URL</label>
                        <input
                            type="text"
                            id="indexers-default-proxy"
                            className={`${styles["form-input"]} ${!proxyValid ? styles.error : ""}`}
                            placeholder="http://proxy:8888"
                            value={proxyUrl}
                            onChange={e => handleProxyChange(e.target.value)}
                        />
                    </div>
                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-default-search-user-agent" className={styles["form-label"]}>
                            Default Search User-Agent <span className={styles["label-hint"]}>(sent when searching indexers; per-indexer override below)</span>
                        </label>
                        <input
                            type="text"
                            id="indexers-default-search-user-agent"
                            className={styles["form-input"]}
                            placeholder="nzbdav/<version>"
                            value={defaultSearchUserAgent}
                            onChange={e => handleSearchUserAgentChange(e.target.value)}
                        />
                        <div className={styles["section-description"]}>
                            Sent on indexer search and caps queries. Leave blank to use the default.
                        </div>
                    </div>
                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-default-retrieve-user-agent" className={styles["form-label"]}>
                            Default Retrieve User-Agent <span className={styles["label-hint"]}>(sent when retrieving the .nzb; per-indexer override below)</span>
                        </label>
                        <input
                            type="text"
                            id="indexers-default-retrieve-user-agent"
                            className={styles["form-input"]}
                            placeholder="nzbdav/<version>"
                            value={defaultRetrieveUserAgent}
                            onChange={e => handleRetrieveUserAgentChange(e.target.value)}
                        />
                        <div className={styles["section-description"]}>
                            Sent when retrieving the .nzb file. Leave blank to use the default.
                        </div>
                    </div>
                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-default-timeout" className={styles["form-label"]}>
                            Request timeout (seconds) <span className={styles["label-hint"]}>(leave blank for {DEFAULT_TIMEOUT_SECONDS}s default)</span>
                        </label>
                        <input
                            type="text"
                            id="indexers-default-timeout"
                            className={`${styles["form-input"]} ${!isTimeoutValid(globalTimeoutRaw) ? styles.error : ""}`}
                            placeholder={DEFAULT_TIMEOUT_SECONDS.toString()}
                            value={globalTimeoutRaw}
                            onChange={e => handleTimeoutChange(e.target.value)}
                        />
                    </div>
                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-default-search-limit" className={styles["form-label"]}>
                            Search results per indexer <span className={styles["label-hint"]}>(blank = {DEFAULT_SEARCH_RESULT_LIMIT}; higher pages the indexer for more results, using more API calls)</span>
                        </label>
                        <input
                            type="text"
                            id="indexers-default-search-limit"
                            className={`${styles["form-input"]} ${!isTimeoutValid(globalSearchLimitRaw) ? styles.error : ""}`}
                            placeholder={DEFAULT_SEARCH_RESULT_LIMIT.toString()}
                            value={globalSearchLimitRaw}
                            onChange={e => handleSearchLimitChange(e.target.value)}
                        />
                    </div>

                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-exclude-patterns" className={styles["form-label"]}>
                            Exclude result patterns <span className={styles["label-hint"]}>(applies to every indexer)</span>
                        </label>
                        <Textarea
                            id="indexers-exclude-patterns"
                            rows={6}
                            spellCheck={false}
                            className={`${styles["form-input"]} ${styles["pattern-input"]} ${patternIssues.length > 0 ? styles.error : ""}`}
                            placeholder={"# one regex per line\n# lines starting with # are comments"}
                            value={excludePatterns}
                            onChange={e => handleExcludePatternsChange(e.target.value)} />
                        {patternIssues.length > 0 && (
                            <div className={styles["pattern-errors"]}>
                                {patternIssues.map((iss, i) => (
                                    <div key={i} className={styles["pattern-error"]}>
                                        <span className={styles["pattern-error-line"]}>Line {iss.line}</span>
                                        <code className={styles["pattern-error-pattern"]}>{iss.pattern}</code>
                                        <span className={styles["pattern-error-message"]}>— {iss.error}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                        <p className={styles["pattern-hint"]}>
                            One JavaScript-style regex per line. Search results whose title matches any pattern
                            are dropped before being returned. Case-insensitive by default — use <code>(?-i:Foo)</code> for
                            case-sensitive. Lines starting with <code>#</code> are comments. Use this to skip
                            releases your setup can't handle, whatever the reason.
                        </p>
                    </div>

                    <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                        <label htmlFor="indexers-exclude-sync-urls" className={styles["form-label"]}>
                            Synced exclude URLs <span className={styles["label-hint"]}>(auto-updating; one URL per line)</span>
                        </label>
                        <Textarea
                            id="indexers-exclude-sync-urls"
                            rows={3}
                            spellCheck={false}
                            className={`${styles["form-input"]} ${styles["pattern-input"]} ${syncUrlIssues.length > 0 ? styles.error : ""}`}
                            placeholder={"# one URL per line\nhttps://raw.githubusercontent.com/.../excluded-regex.json"}
                            value={excludeSyncUrls}
                            onChange={e => handleSyncUrlsChange(e.target.value)} />
                        {syncUrlIssues.length > 0 && (
                            <div className={styles["pattern-errors"]}>
                                {syncUrlIssues.map((iss, i) => (
                                    <div key={i} className={styles["pattern-error"]}>
                                        <span className={styles["pattern-error-line"]}>Line {iss.line}</span>
                                        <code className={styles["pattern-error-pattern"]}>{iss.value}</code>
                                        <span className={styles["pattern-error-message"]}>— {iss.error}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                        <div className={styles["sync-controls"]}>
                            <label htmlFor="indexers-exclude-sync-refresh" className={styles["sync-refresh-label"]}>
                                Refresh every
                            </label>
                            <input
                                type="text"
                                inputMode="numeric"
                                id="indexers-exclude-sync-refresh"
                                className={`${styles["form-input"]} ${styles["sync-refresh-input"]} ${!isRefreshValid(excludeSyncRefresh) ? styles.error : ""}`}
                                placeholder="720"
                                value={excludeSyncRefresh}
                                onChange={e => handleSyncRefreshChange(e.target.value)} />
                            <span className={styles["label-hint"]}>minutes</span>
                            <Button variant="secondary" size="small" onClick={handleSyncNow} disabled={isSyncing}>
                                {isSyncing ? "Syncing…" : "Sync now"}
                            </Button>
                        </div>
                        {syncStatus.length > 0 && (
                            <div className={styles["sync-status"]}>
                                {syncStatus.map((s, i) => (
                                    <div key={i} className={styles["sync-status-row"]}>
                                        {s.error
                                            ? <span className={styles["sync-status-bad"]}>✗ {syncHostLabel(s.url)} — {s.error}</span>
                                            : <span className={styles["sync-status-ok"]}>✓ {syncHostLabel(s.url)} — {s.count} pattern{s.count === 1 ? "" : "s"}{s.lastChecked ? ` · synced ${syncRelativeTime(s.lastChecked)}` : ""}</span>}
                                    </div>
                                ))}
                            </div>
                        )}
                        <p className={styles["pattern-hint"]}>
                            Point at one or more JSON lists of regex patterns (e.g. TRaSH-derived exclude URLs).
                            Accepts <code>{`{ "values": ["…"] }`}</code> or <code>{`[{ "pattern": "…" }]`}</code>.
                            Synced patterns are fetched on the interval above and take precedence; your manual
                            patterns above are merged in after, with exact duplicates removed. If a URL can't be
                            reached, the last good copy keeps working. Save your changes first, then use <strong>Sync now</strong>.
                        </p>
                    </div>
                </div>
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Indexers</div>
                    <Button size="xsmall" onClick={handleAdd}>Add</Button>
                </div>

                {indexerConfig.Indexers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No indexers configured. Add a Newznab-compatible indexer (or aggregator) to enable search.
                    </p>
                ) : (
                    <div className={styles["indexers-grid"]}>
                        {indexerConfig.Indexers.map((indexer, index) => (
                            <IndexerCard
                                key={index}
                                indexer={indexer}
                                onEdit={() => handleEdit(index)}
                                onToggle={() => handleToggle(index)}
                                onDelete={() => handleDelete(index)}
                            />
                        ))}
                    </div>
                )}
            </div>

            <IndexerModal
                show={showModal}
                indexer={editingIndex !== null ? indexerConfig.Indexers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSave}
            />
        </div>
    );
}

type IndexerCardProps = {
    indexer: ConnectionDetails;
    onEdit: () => void;
    onToggle: () => void;
    onDelete: () => void;
};

function IndexerCard({ indexer, onEdit, onToggle, onDelete }: IndexerCardProps) {
    const isDisabled = !indexer.Enabled;
    const host = (() => {
        try { return new URL(indexer.Url).host; }
        catch { return indexer.Url || "—"; }
    })();
    const rateLimit = indexer.MaxRequestsPerMinute && indexer.MaxRequestsPerMinute > 0
        ? `${indexer.MaxRequestsPerMinute} / min`
        : "Unlimited";
    const searchUserAgent = indexer.SearchUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
    const retrieveUserAgent = indexer.RetrieveUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
    const proxy = indexer.ProxyUrl?.trim() ? indexer.ProxyUrl : "Default";
    const timeout = indexer.TimeoutSeconds && indexer.TimeoutSeconds > 0
        ? `${indexer.TimeoutSeconds}s`
        : "Default";
    const resultLimit = indexer.SearchResultLimit && indexer.SearchResultLimit > 0
        ? indexer.SearchResultLimit.toString()
        : "Default";
    const formatLimit = (n: number | undefined, perDay: boolean) => {
        if (!n || n <= 0) return "Unlimited";
        return perDay ? `${n} / day` : `${n} / 24h`;
    };
    const hasResetHour = typeof indexer.HitLimitResetTime === "number"
        && indexer.HitLimitResetTime >= 0
        && indexer.HitLimitResetTime <= 23;
    const apiLimit = formatLimit(indexer.HitLimit, hasResetHour);
    const downloadLimit = formatLimit(indexer.DownloadLimit, hasResetHour);
    const categoriesSummary = (() => {
        if (indexer.IgnoreCategoryFilter) return "All (no filter)";
        const m = indexer.ExtraMovieCategories?.trim();
        const t = indexer.ExtraTvCategories?.trim();
        if (!m && !t) return "Default";
        const parts: string[] = [];
        if (m) parts.push(`+M ${m}`);
        if (t) parts.push(`+T ${t}`);
        return parts.join(" · ");
    })();

    return (
        <div className={`${styles["indexer-card"]} ${isDisabled ? styles["indexer-card-disabled"] : ""}`}>
            <div className={styles["indexer-card-inner"]}>
                <div className={styles["indexer-header"]}>
                    <div className={styles["indexer-header-content"]}>
                        <div className={styles["indexer-name"]}>
                            {indexer.Name || "(unnamed)"}
                            {isDisabled && <span className={styles["indexer-disabled-badge"]}>Disabled</span>}
                        </div>
                        <div className={styles["indexer-host"]}>{host}</div>
                    </div>
                    <div className={styles["indexer-header-actions"]}>
                        <button
                            className={`${styles["header-action-button"]} ${styles["toggle"]} ${isDisabled ? styles["toggle-off"] : styles["toggle-on"]}`}
                            onClick={onToggle}
                            title={isDisabled ? "Enable Indexer" : "Disable Indexer"}
                            aria-pressed={!isDisabled}
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                                <line x1="12" y1="2" x2="12" y2="12" />
                            </svg>
                        </button>
                        <button
                            className={styles["header-action-button"]}
                            onClick={onEdit}
                            title="Edit Indexer"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                            </svg>
                        </button>
                        <button
                            className={`${styles["header-action-button"]} ${styles["delete"]}`}
                            onClick={onDelete}
                            title="Delete Indexer"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <polyline points="3 6 5 6 21 6" />
                                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                            </svg>
                        </button>
                    </div>
                </div>

                <div className={styles["indexer-details"]}>
                    <div className={styles["indexer-detail-row"]}>
                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <line x1="2" y1="12" x2="22" y2="12" />
                                    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Host</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.Url}>{host}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <polyline points="12 6 12 12 16 14" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Rate limit</span>
                                <span className={styles["indexer-detail-value"]}>{rateLimit}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M9 12l2 2 4-4" />
                                    <path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9 9 4.03 9 9z" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Strict matching</span>
                                <span className={styles["indexer-detail-value"]}>
                                    {indexer.EnableStrictMatching ? "Enabled" : "Disabled"}
                                </span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="11" cy="11" r="8" />
                                    <line x1="21" y1="21" x2="16.65" y2="16.65" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Search UA</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.SearchUserAgent ?? indexer.UserAgent ?? ""}>{searchUserAgent}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                                    <polyline points="7 10 12 15 17 10" />
                                    <line x1="12" y1="15" x2="12" y2="3" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Retrieve UA</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.RetrieveUserAgent ?? indexer.UserAgent ?? ""}>{retrieveUserAgent}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Result filtering</span>
                                <span className={styles["indexer-detail-value"]}>
                                    {indexer.Filter?.Enabled ? "Enabled" : "Disabled"}
                                </span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <rect x="2" y="6" width="20" height="12" rx="2" />
                                    <path d="M6 12h.01M10 12h.01M14 12h.01M18 12h.01" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Proxy</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.ProxyUrl ?? ""}>{proxy}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <polyline points="12 6 12 12 16 14" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Timeout</span>
                                <span className={styles["indexer-detail-value"]}>{timeout}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <line x1="8" y1="6" x2="21" y2="6" />
                                    <line x1="8" y1="12" x2="21" y2="12" />
                                    <line x1="8" y1="18" x2="21" y2="18" />
                                    <line x1="3" y1="6" x2="3.01" y2="6" />
                                    <line x1="3" y1="12" x2="3.01" y2="12" />
                                    <line x1="3" y1="18" x2="3.01" y2="18" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Result limit</span>
                                <span className={styles["indexer-detail-value"]}>{resultLimit}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M3 12h4l3-9 4 18 3-9h4" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>API limit</span>
                                <span className={styles["indexer-detail-value"]}>{apiLimit}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                                    <polyline points="7 10 12 15 17 10" />
                                    <line x1="12" y1="15" x2="12" y2="3" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Download limit</span>
                                <span className={styles["indexer-detail-value"]}>{downloadLimit}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M7 7h.01M7 3h5a2 2 0 0 1 1.41.59l7 7a2 2 0 0 1 0 2.82l-7 7a2 2 0 0 1-2.82 0l-7-7A2 2 0 0 1 3 12V7a4 4 0 0 1 4-4z" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Categories</span>
                                <span className={styles["indexer-detail-value"]} title={categoriesSummary}>{categoriesSummary}</span>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}

type IndexerModalProps = {
    show: boolean;
    indexer: ConnectionDetails | null;
    onClose: () => void;
    onSave: (indexer: ConnectionDetails) => void;
};

function IndexerModal({ show, indexer, onClose, onSave }: IndexerModalProps) {
    const [name, setName] = useState("");
    const [url, setUrl] = useState("");
    const [apiKey, setApiKey] = useState("");
    const [searchUserAgent, setSearchUserAgent] = useState("");
    const [retrieveUserAgent, setRetrieveUserAgent] = useState("");
    const [proxyUrl, setProxyUrl] = useState("");
    const [timeoutSeconds, setTimeoutSeconds] = useState("");
    const [searchResultLimit, setSearchResultLimit] = useState("");
    const [maxRpm, setMaxRpm] = useState("0");
    const [hitLimit, setHitLimit] = useState("");
    const [downloadLimit, setDownloadLimit] = useState("");
    const [hitResetTime, setHitResetTime] = useState("");
    const [enabled, setEnabled] = useState(true);
    const [strict, setStrict] = useState(false);
    const [extraMovieCategories, setExtraMovieCategories] = useState("");
    const [extraTvCategories, setExtraTvCategories] = useState("");
    const [ignoreCategoryFilter, setIgnoreCategoryFilter] = useState(false);

    const [filterEnabled, setFilterEnabled] = useState(false);
    const [filterAdvancedOpen, setFilterAdvancedOpen] = useState(false);
    const [filterSkipPassworded, setFilterSkipPassworded] = useState(OPTIMISED_DEFAULTS.SkipPassworded);
    const [filterMinGrabs, setFilterMinGrabs] = useState(OPTIMISED_DEFAULTS.MinGrabs.toString());
    const [filterGrabsGraceHours, setFilterGrabsGraceHours] = useState(OPTIMISED_DEFAULTS.GrabsGraceHours.toString());
    const [filterMaxAgeDaysWithoutGrabs, setFilterMaxAgeDaysWithoutGrabs] = useState(OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString());
    const [filterPreferDownloaded, setFilterPreferDownloaded] = useState(OPTIMISED_DEFAULTS.PreferDownloaded);

    const resetFilterToDefaults = useCallback(() => {
        setFilterSkipPassworded(OPTIMISED_DEFAULTS.SkipPassworded);
        setFilterMinGrabs(OPTIMISED_DEFAULTS.MinGrabs.toString());
        setFilterGrabsGraceHours(OPTIMISED_DEFAULTS.GrabsGraceHours.toString());
        setFilterMaxAgeDaysWithoutGrabs(OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString());
        setFilterPreferDownloaded(OPTIMISED_DEFAULTS.PreferDownloaded);
    }, []);

    const [testState, setTestState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const apiKeyIsMasked = isMaskedSecret(apiKey);

    useEffect(() => {
        if (show) {
            setName(indexer?.Name || "");
            setUrl(indexer?.Url || "");
            setApiKey(indexer?.ApiKey || "");
            setSearchUserAgent(indexer?.SearchUserAgent || indexer?.UserAgent || "");
            setRetrieveUserAgent(indexer?.RetrieveUserAgent || indexer?.UserAgent || "");
            setProxyUrl(indexer?.ProxyUrl || "");
            setTimeoutSeconds(
                indexer?.TimeoutSeconds && indexer.TimeoutSeconds > 0
                    ? indexer.TimeoutSeconds.toString()
                    : ""
            );
            setSearchResultLimit(indexer?.SearchResultLimit && indexer.SearchResultLimit > 0 ? indexer.SearchResultLimit.toString() : "");
            setMaxRpm((indexer?.MaxRequestsPerMinute ?? 0).toString());
            setHitLimit(indexer?.HitLimit && indexer.HitLimit > 0 ? indexer.HitLimit.toString() : "");
            setDownloadLimit(indexer?.DownloadLimit && indexer.DownloadLimit > 0 ? indexer.DownloadLimit.toString() : "");
            setHitResetTime(
                typeof indexer?.HitLimitResetTime === "number" && indexer.HitLimitResetTime >= 0 && indexer.HitLimitResetTime <= 23
                    ? indexer.HitLimitResetTime.toString()
                    : ""
            );
            setEnabled(indexer?.Enabled ?? true);
            setStrict(indexer?.EnableStrictMatching ?? false);
            setExtraMovieCategories(indexer?.ExtraMovieCategories ?? "");
            setExtraTvCategories(indexer?.ExtraTvCategories ?? "");
            setIgnoreCategoryFilter(indexer?.IgnoreCategoryFilter ?? false);
            const f = indexer?.Filter ?? OPTIMISED_DEFAULTS;
            setFilterEnabled(f.Enabled);
            setFilterSkipPassworded(f.SkipPassworded);
            setFilterMinGrabs((f.MinGrabs ?? OPTIMISED_DEFAULTS.MinGrabs).toString());
            setFilterGrabsGraceHours((f.GrabsGraceHours ?? OPTIMISED_DEFAULTS.GrabsGraceHours).toString());
            setFilterMaxAgeDaysWithoutGrabs((f.MaxAgeDaysWithoutGrabs ?? OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs).toString());
            setFilterPreferDownloaded(f.PreferDownloaded);
            setFilterAdvancedOpen(false);
            setTestState('idle');
        }
    }, [show, indexer]);

    useEffect(() => { setTestState('idle'); }, [url, apiKey, searchUserAgent, proxyUrl, timeoutSeconds]);

    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) onClose();
        };
        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTest = useCallback(async () => {
        if (!url.trim() || !apiKey.trim() || apiKeyIsMasked) return;
        setTestState('testing');
        try {
            const fd = new FormData();
            fd.append('url', url);
            fd.append('apiKey', apiKey);
            if (searchUserAgent.trim()) fd.append('userAgent', searchUserAgent);
            if (proxyUrl.trim()) fd.append('proxyUrl', proxyUrl);
            if (timeoutSeconds.trim()) fd.append('timeoutSeconds', timeoutSeconds);
            const r = await fetch('/api/test-indexer-connection', { method: 'POST', body: fd });
            const data = await r.json();
            setTestState(data.status && data.connected ? 'success' : 'error');
        } catch {
            setTestState('error');
        }
    }, [url, apiKey, apiKeyIsMasked, searchUserAgent, proxyUrl, timeoutSeconds]);

    const handleSave = useCallback(() => {
        const rpm = parseInt(maxRpm || "0", 10);
        const timeout = parseInt(timeoutSeconds || "0", 10);
        const srl = parseInt(searchResultLimit || "0", 10);
        const hl = parseInt(hitLimit || "0", 10);
        const dl = parseInt(downloadLimit || "0", 10);
        const hr = hitResetTime.trim() === "" ? NaN : parseInt(hitResetTime, 10);
        const clampNonNegInt = (raw: string, fallback: number) => {
            const n = parseInt(raw || "0", 10);
            return Number.isFinite(n) && n >= 0 ? n : fallback;
        };
        const filterIsClean = !filterEnabled
            && filterSkipPassworded === OPTIMISED_DEFAULTS.SkipPassworded
            && clampNonNegInt(filterMinGrabs, OPTIMISED_DEFAULTS.MinGrabs) === OPTIMISED_DEFAULTS.MinGrabs
            && clampNonNegInt(filterGrabsGraceHours, OPTIMISED_DEFAULTS.GrabsGraceHours) === OPTIMISED_DEFAULTS.GrabsGraceHours
            && clampNonNegInt(filterMaxAgeDaysWithoutGrabs, OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs) === OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs
            && filterPreferDownloaded === OPTIMISED_DEFAULTS.PreferDownloaded;
        const normaliseCategoryList = (raw: string) => {
            const parts = raw.split(",").map(p => p.trim()).filter(p => p.length > 0);
            return parts.length === 0 ? undefined : parts.join(",");
        };
        onSave({
            Name: name.trim(),
            Url: url.trim(),
            ApiKey: apiKey.trim(),
            Enabled: enabled,
            UserAgent: undefined,
            SearchUserAgent: searchUserAgent.trim() || undefined,
            RetrieveUserAgent: retrieveUserAgent.trim() || undefined,
            ProxyUrl: proxyUrl.trim() || undefined,
            TimeoutSeconds: Number.isFinite(timeout) && timeout > 0 ? timeout : undefined,
            SearchResultLimit: Number.isFinite(srl) && srl > 0 ? srl : undefined,
            MaxRequestsPerMinute: Number.isFinite(rpm) && rpm > 0 ? rpm : 0,
            HitLimit: Number.isFinite(hl) && hl > 0 ? hl : undefined,
            DownloadLimit: Number.isFinite(dl) && dl > 0 ? dl : undefined,
            HitLimitResetTime: Number.isFinite(hr) && hr >= 0 && hr <= 23 ? hr : undefined,
            EnableStrictMatching: strict,
            ExtraMovieCategories: normaliseCategoryList(extraMovieCategories),
            ExtraTvCategories: normaliseCategoryList(extraTvCategories),
            IgnoreCategoryFilter: ignoreCategoryFilter || undefined,
            Filter: filterIsClean ? undefined : {
                Enabled: filterEnabled,
                SkipPassworded: filterSkipPassworded,
                MinGrabs: clampNonNegInt(filterMinGrabs, 0),
                GrabsGraceHours: clampNonNegInt(filterGrabsGraceHours, 6),
                MaxAgeDaysWithoutGrabs: clampNonNegInt(filterMaxAgeDaysWithoutGrabs, 0),
                PreferDownloaded: filterPreferDownloaded,
            },
        });
    }, [name, url, apiKey, searchUserAgent, retrieveUserAgent, proxyUrl, timeoutSeconds, searchResultLimit, maxRpm, hitLimit, downloadLimit, hitResetTime, enabled, strict,
        extraMovieCategories, extraTvCategories, ignoreCategoryFilter,
        filterEnabled, filterSkipPassworded, filterMinGrabs, filterGrabsGraceHours,
        filterMaxAgeDaysWithoutGrabs, filterPreferDownloaded, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) onClose();
    }, [onClose]);

    const isUrlValid = (() => {
        if (!url.trim()) return false;
        try { new URL(url); return true; } catch { return false; }
    })();
    const isRpmValid = (() => {
        const n = Number(maxRpm);
        return Number.isInteger(n) && n >= 0 && maxRpm.trim() === n.toString();
    })();
    const isProxyValid = isProxyUrlValid(proxyUrl);
    const isTimeoutFieldValid = isTimeoutValid(timeoutSeconds);
    const isNonNegIntOrBlank = (raw: string) => {
        if (!raw.trim()) return true;
        const n = Number(raw);
        return Number.isInteger(n) && n >= 0 && raw.trim() === n.toString();
    };
    const isHitLimitValid = isNonNegIntOrBlank(hitLimit);
    const isSearchResultLimitValid = isNonNegIntOrBlank(searchResultLimit);
    const isDownloadLimitValid = isNonNegIntOrBlank(downloadLimit);
    const isHitResetValid = (() => {
        if (!hitResetTime.trim()) return true;
        const n = Number(hitResetTime);
        return Number.isInteger(n) && n >= 0 && n <= 23 && hitResetTime.trim() === n.toString();
    })();
    const isExtraMovieCategoriesValid = isCategoryListValid(extraMovieCategories);
    const isExtraTvCategoriesValid = isCategoryListValid(extraTvCategories);
    const isFormValid = name.trim() !== "" && isUrlValid && apiKey.trim() !== ""
        && isRpmValid && isProxyValid && isTimeoutFieldValid
        && isHitLimitValid && isSearchResultLimitValid && isDownloadLimitValid && isHitResetValid
        && isExtraMovieCategoriesValid && isExtraTvCategoriesValid;

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <section
                className={styles["modal-container"]}
                role="dialog"
                aria-modal="true"
                aria-labelledby="indexer-modal-title"
            >
                <div className={styles["modal-header"]}>
                    <h2 id="indexer-modal-title" className={styles["modal-title"]}>
                        {indexer ? "Edit Indexer" : "Add Indexer"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">×</button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-name" className={styles["form-label"]}>Name</label>
                            <input
                                type="text"
                                id="indexer-name"
                                className={styles["form-input"]}
                                placeholder="e.g. My Indexer"
                                value={name}
                                onChange={e => setName(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-url" className={styles["form-label"]}>URL</label>
                            <input
                                type="text"
                                id="indexer-url"
                                className={`${styles["form-input"]} ${!isUrlValid && url !== "" ? styles.error : ""}`}
                                placeholder="https://api.example.com"
                                value={url}
                                onChange={e => setUrl(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-apikey" className={styles["form-label"]}>API Key</label>
                            <input
                                type="password"
                                id="indexer-apikey"
                                className={styles["form-input"]}
                                value={apiKey}
                                onChange={e => setApiKey(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-search-ua" className={styles["form-label"]}>
                                Search User-Agent <span className={styles["label-hint"]}>(optional; overrides the global Search default)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-search-ua"
                                className={styles["form-input"]}
                                placeholder="Leave blank to use global default"
                                value={searchUserAgent}
                                onChange={e => setSearchUserAgent(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-retrieve-ua" className={styles["form-label"]}>
                                Retrieve User-Agent <span className={styles["label-hint"]}>(optional; overrides the global Retrieve default)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-retrieve-ua"
                                className={styles["form-input"]}
                                placeholder="Leave blank to use global default"
                                value={retrieveUserAgent}
                                onChange={e => setRetrieveUserAgent(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-proxy" className={styles["form-label"]}>
                                HTTP(S) Proxy URL <span className={styles["label-hint"]}>(optional; overrides the global default)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-proxy"
                                className={`${styles["form-input"]} ${!isProxyValid && proxyUrl !== "" ? styles.error : ""}`}
                                placeholder="Leave blank to use global default"
                                value={proxyUrl}
                                onChange={e => setProxyUrl(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-rpm" className={styles["form-label"]}>
                                Max requests / minute <span className={styles["label-hint"]}>(0 = unlimited)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-rpm"
                                className={`${styles["form-input"]} ${!isRpmValid && maxRpm !== "" ? styles.error : ""}`}
                                placeholder="0"
                                value={maxRpm}
                                onChange={e => setMaxRpm(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-timeout" className={styles["form-label"]}>
                                Request timeout (seconds) <span className={styles["label-hint"]}>(blank = use global default)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-timeout"
                                className={`${styles["form-input"]} ${!isTimeoutFieldValid && timeoutSeconds !== "" ? styles.error : ""}`}
                                placeholder="Use global default"
                                value={timeoutSeconds}
                                onChange={e => setTimeoutSeconds(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-search-limit" className={styles["form-label"]}>
                                Search result limit <span className={styles["label-hint"]}>(blank = use global default)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-search-limit"
                                className={`${styles["form-input"]} ${!isSearchResultLimitValid ? styles.error : ""}`}
                                placeholder="Use global default"
                                value={searchResultLimit}
                                onChange={e => setSearchResultLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-hit-limit" className={styles["form-label"]}>
                                API hit limit <span className={styles["label-hint"]}>(blank or 0 = unlimited)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-hit-limit"
                                className={`${styles["form-input"]} ${!isHitLimitValid ? styles.error : ""}`}
                                placeholder="Unlimited"
                                value={hitLimit}
                                onChange={e => setHitLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-download-limit" className={styles["form-label"]}>
                                Download limit <span className={styles["label-hint"]}>(blank or 0 = unlimited)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-download-limit"
                                className={`${styles["form-input"]} ${!isDownloadLimitValid ? styles.error : ""}`}
                                placeholder="Unlimited"
                                value={downloadLimit}
                                onChange={e => setDownloadLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-hit-reset-time" className={styles["form-label"]}>
                                Hit reset time <span className={styles["label-hint"]}>(UTC hour 0-23; blank = rolling 24h window)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-hit-reset-time"
                                className={`${styles["form-input"]} ${!isHitResetValid ? styles.error : ""}`}
                                placeholder="Rolling 24h"
                                value={hitResetTime}
                                onChange={e => setHitResetTime(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-enabled"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={enabled}
                                    onChange={e => setEnabled(e.target.checked)}
                                />
                                <label htmlFor="indexer-enabled" className={styles["form-checkbox-label"]}>
                                    Enabled
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-strict"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={strict}
                                    onChange={e => setStrict(e.target.checked)}
                                />
                                <label htmlFor="indexer-strict" className={styles["form-checkbox-label"]}>
                                    Strict matching <span className={styles["label-hint"]}>(drop results whose title doesn't match the request)</span>
                                </label>
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-extra-movie-cats" className={styles["form-label"]}>
                                Extra movie categories <span className={styles["label-hint"]}>(comma-separated; appended to the default 2000/2070)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-extra-movie-cats"
                                className={`${styles["form-input"]} ${!isExtraMovieCategoriesValid ? styles.error : ""}`}
                                placeholder="e.g. 2100,2200"
                                value={extraMovieCategories}
                                onChange={e => setExtraMovieCategories(e.target.value)}
                                disabled={ignoreCategoryFilter}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-extra-tv-cats" className={styles["form-label"]}>
                                Extra TV categories <span className={styles["label-hint"]}>(comma-separated; appended to the default 5000/5070)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-extra-tv-cats"
                                className={`${styles["form-input"]} ${!isExtraTvCategoriesValid ? styles.error : ""}`}
                                placeholder="e.g. 5100,5200"
                                value={extraTvCategories}
                                onChange={e => setExtraTvCategories(e.target.value)}
                                disabled={ignoreCategoryFilter}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-ignore-category-filter"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={ignoreCategoryFilter}
                                    onChange={e => setIgnoreCategoryFilter(e.target.checked)}
                                />
                                <label htmlFor="indexer-ignore-category-filter" className={styles["form-checkbox-label"]}>
                                    Ignore category filter <span className={styles["label-hint"]}>(send no <code>cat=</code> param at all — escape hatch for indexers with fully custom category schemas)</span>
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-filter-enabled"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={filterEnabled}
                                    onChange={e => setFilterEnabled(e.target.checked)}
                                />
                                <label htmlFor="indexer-filter-enabled" className={styles["form-checkbox-label"]}>
                                    Result filtering <span className={styles["label-hint"]}>(uses indexer-supplied metadata to filter and rank this indexer's results; recommended defaults applied when enabled)</span>
                                </label>
                            </div>
                        </div>

                        {filterEnabled && (
                            <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                <button
                                    type="button"
                                    onClick={() => setFilterAdvancedOpen(o => !o)}
                                    style={{
                                        background: "none",
                                        border: "none",
                                        padding: 0,
                                        color: "inherit",
                                        cursor: "pointer",
                                        textDecoration: "underline",
                                        opacity: 0.85,
                                        fontSize: "0.9em",
                                    }}
                                >
                                    {filterAdvancedOpen ? "Hide advanced" : "Show advanced"}
                                </button>
                            </div>
                        )}

                        {filterEnabled && filterAdvancedOpen && (
                            <>
                                <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                    <div className={styles["form-checkbox-wrapper"]}>
                                        <input
                                            type="checkbox"
                                            id="indexer-filter-pw"
                                            className={`${styles["form-checkbox"]} toggle-switch`}
                                            checked={filterSkipPassworded}
                                            onChange={e => setFilterSkipPassworded(e.target.checked)}
                                        />
                                        <label htmlFor="indexer-filter-pw" className={styles["form-checkbox-label"]}>
                                            Skip password-protected releases <span className={styles["label-hint"]}>(items the indexer flags as containing a passworded archive)</span>
                                        </label>
                                    </div>
                                </div>

                                <div className={styles["form-group"]}>
                                    <label htmlFor="indexer-filter-mingrabs" className={styles["form-label"]}>
                                        Minimum download count <span className={styles["label-hint"]}>(0 = no minimum)</span>
                                    </label>
                                    <input
                                        type="text"
                                        id="indexer-filter-mingrabs"
                                        className={styles["form-input"]}
                                        placeholder={OPTIMISED_DEFAULTS.MinGrabs.toString()}
                                        value={filterMinGrabs}
                                        onChange={e => setFilterMinGrabs(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className={styles["form-group"]}>
                                    <label htmlFor="indexer-filter-grace" className={styles["form-label"]}>
                                        Grace period for new releases <span className={styles["label-hint"]}>(hours; 0 = no grace)</span>
                                    </label>
                                    <input
                                        type="text"
                                        id="indexer-filter-grace"
                                        className={styles["form-input"]}
                                        placeholder={OPTIMISED_DEFAULTS.GrabsGraceHours.toString()}
                                        value={filterGrabsGraceHours}
                                        onChange={e => setFilterGrabsGraceHours(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                    <label htmlFor="indexer-filter-maxage" className={styles["form-label"]}>
                                        Drop releases older than this many days with zero downloads <span className={styles["label-hint"]}>(0 = disabled)</span>
                                    </label>
                                    <input
                                        type="text"
                                        id="indexer-filter-maxage"
                                        className={styles["form-input"]}
                                        placeholder={OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString()}
                                        value={filterMaxAgeDaysWithoutGrabs}
                                        onChange={e => setFilterMaxAgeDaysWithoutGrabs(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                    <div className={styles["form-checkbox-wrapper"]}>
                                        <input
                                            type="checkbox"
                                            id="indexer-filter-prefer"
                                            className={`${styles["form-checkbox"]} toggle-switch`}
                                            checked={filterPreferDownloaded}
                                            onChange={e => setFilterPreferDownloaded(e.target.checked)}
                                        />
                                        <label htmlFor="indexer-filter-prefer" className={styles["form-checkbox-label"]}>
                                            Rank by download count <span className={styles["label-hint"]}>(sort results by number of downloads, descending; items without a download count sort below those with one)</span>
                                        </label>
                                    </div>
                                </div>

                                <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                    <button
                                        type="button"
                                        onClick={resetFilterToDefaults}
                                        style={{
                                            background: "none",
                                            border: "none",
                                            padding: 0,
                                            color: "inherit",
                                            cursor: "pointer",
                                            textDecoration: "underline",
                                            opacity: 0.85,
                                            fontSize: "0.9em",
                                        }}
                                    >
                                        Reset to recommended defaults
                                    </button>
                                </div>
                            </>
                        )}
                    </div>

                    {testState === 'error' && (
                        <div className={`${styles.alert} ${styles.alertDanger} mt-4`} role="alert">
                            Connection test failed
                        </div>
                    )}

                    {testState === 'success' && (
                        <div className={`${styles.alert} ${styles.alertSuccess} mt-4`} role="status">
                            Connection test successful!
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}>
                        <Button
                            variant={testState === 'success' ? 'success' : testState === 'error' ? 'danger' : 'secondary'}
                            onClick={handleTest}
                            disabled={!isUrlValid || !apiKey.trim() || apiKeyIsMasked || testState === 'testing'}
                            title={apiKeyIsMasked ? "Enter a new API key to test this connection" : undefined}
                        >
                            {testState === 'testing'
                                ? <Spinner size="sm" />
                                : testState === 'success'
                                    ? '✓ Tested'
                                    : testState === 'error'
                                        ? '✗ Failed'
                                        : 'Test Connection'}
                        </Button>
                    </div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>Cancel</Button>
                        <Button onClick={handleSave} disabled={!isFormValid}>
                            {indexer ? "Save Indexer" : "Add Indexer"}
                        </Button>
                    </div>
                </div>
            </section>
        </div>
    );
}

export function isIndexersSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["indexers.instances"] !== newConfig["indexers.instances"]
        || (config["api.user-agent"] ?? "") !== (newConfig["api.user-agent"] ?? "")
        || (config["api.search-user-agent"] ?? "") !== (newConfig["api.search-user-agent"] ?? "")
        || (config["search.exclude-patterns"] ?? "") !== (newConfig["search.exclude-patterns"] ?? "")
        || (config["search.exclude-sync-urls"] ?? "") !== (newConfig["search.exclude-sync-urls"] ?? "")
        || (config["search.exclude-sync-refresh-minutes"] ?? "") !== (newConfig["search.exclude-sync-refresh-minutes"] ?? "");
}

export function isIndexersSettingsValid(newConfig: Record<string, string>) {
    try {
        const c = parseConfig(newConfig["indexers.instances"]);
        if (!isProxyUrlValid(c.ProxyUrl ?? "")) return false;
        if (c.TimeoutSeconds !== undefined && (!Number.isInteger(c.TimeoutSeconds) || c.TimeoutSeconds <= 0)) return false;
        if (c.SearchResultLimit !== undefined && (!Number.isInteger(c.SearchResultLimit) || c.SearchResultLimit <= 0)) return false;
        for (const i of c.Indexers) {
            if (!i.Name.trim()) return false;
            if (!i.ApiKey.trim()) return false;
            try { new URL(i.Url); } catch { return false; }
            if (!isProxyUrlValid(i.ProxyUrl ?? "")) return false;
            if (i.TimeoutSeconds !== undefined && (!Number.isInteger(i.TimeoutSeconds) || i.TimeoutSeconds <= 0)) return false;
            if (i.SearchResultLimit !== undefined && (!Number.isInteger(i.SearchResultLimit) || i.SearchResultLimit <= 0)) return false;
            if (i.HitLimit !== undefined && (!Number.isInteger(i.HitLimit) || i.HitLimit < 0)) return false;
            if (i.DownloadLimit !== undefined && (!Number.isInteger(i.DownloadLimit) || i.DownloadLimit < 0)) return false;
            if (i.HitLimitResetTime !== undefined
                && (!Number.isInteger(i.HitLimitResetTime) || i.HitLimitResetTime < 0 || i.HitLimitResetTime > 23)) return false;
            if (i.ExtraMovieCategories !== undefined && !isCategoryListValid(i.ExtraMovieCategories)) return false;
            if (i.ExtraTvCategories !== undefined && !isCategoryListValid(i.ExtraTvCategories)) return false;
        }
        if (validateExcludePatterns(newConfig["search.exclude-patterns"] ?? "").length > 0) return false;
        if (validateSyncUrls(newConfig["search.exclude-sync-urls"] ?? "").length > 0) return false;
        const syncRefresh = newConfig["search.exclude-sync-refresh-minutes"] ?? "";
        if (syncRefresh.trim() !== "" && !isRefreshValid(syncRefresh)) return false;
        return true;
    } catch {
        return false;
    }
}
