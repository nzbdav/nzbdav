export class WebdavDirectoryNotFoundError extends Error {
    public constructor(public readonly directory: string) {
        super("The WebDAV directory does not exist.");
        this.name = "WebdavDirectoryNotFoundError";
    }
}

class BackendClient {
    public async isOnboarding(): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/is-onboarding";

        const response = await fetch(url, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            }
        });

        if (!response.ok) {
            throw new Error(`Failed to fetch onboarding status: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.isOnboarding;
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/create-account";

        const response = await fetch(url, {
            method: "POST",
            headers: {
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to create account: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.status;
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/authenticate";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to authenticate: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.authenticated;
    }

    public async getQueue(limit: number, start: number = 0): Promise<QueueResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=queue&start=${start}&limit=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get queue: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.queue;
    }

    public async getHistory(limit: number, start: number = 0): Promise<HistoryResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=history&start=${start}&pageSize=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get history: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.history;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        const config = await this.getConfig(["api.manual-category"]);
        const category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const url = process.env.BACKEND_URL + `/api?mode=addfile&cat=${category}&priority=0&pp=0`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("nzbFile", nzbFile, nzbFile.name);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to add nzb file: ${(await response.json()).error}`);
        }
        const data = await response.json();
        if (!data.nzo_ids || data.nzo_ids.length != 1) {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return data.nzo_ids[0];
    }

    public async searchIndexers(q: string, limit: number = 100): Promise<SearchIndexersResponse> {
        const url = process.env.BACKEND_URL + "/api/search-indexers";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("q", q);
                form.append("limit", String(limit));
                return form;
            })()
        });
        if (!response.ok) {
            throw new Error(`Failed to search indexers: ${(await response.json()).error}`);
        }
        return await response.json();
    }

    public async addNzbFromUrl(nzbUrl: string, nzbName: string): Promise<string> {
        const config = await this.getConfig(["api.manual-category"]);
        const category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const params = new URLSearchParams({
            mode: "addurl",
            cat: category,
            priority: "0",
            pp: "0",
            name: nzbUrl,
            nzbname: nzbName,
        });
        const url = process.env.BACKEND_URL + `/api?${params.toString()}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to add nzb url: ${(await response.json()).error}`);
        }
        const data = await response.json();
        if (!data.nzo_ids || data.nzo_ids.length !== 1) {
            throw new Error("Failed to add nzb url: unexpected response format");
        }
        return data.nzo_ids[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const url = process.env.BACKEND_URL + "/api/list-webdav-directory";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("directory", directory);
                return form;
            })()
        });

        if (!response.ok) {
            const error = (await response.json()).error;
            if (response.status === 400 && error === "The directory does not exist.") {
                throw new WebdavDirectoryNotFoundError(directory);
            }
            throw new Error(`Failed to list webdav directory: ${error}`);
        }
        const data = await response.json();
        return data.items;
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const url = process.env.BACKEND_URL + "/api/get-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const key of keys) {
                    form.append("config-keys", key);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to get config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.configItems || [];
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/update-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const item of configItems) {
                    form.append(item.configName, item.configValue);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to update config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.status;
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-queue";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check queue: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getWatchdogEntries(limit: number = 200): Promise<WatchdogEntry[]> {
        const url = process.env.BACKEND_URL + `/api/get-watchdog-entries?limit=${limit}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "GET", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get watchdog entries: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.entries ?? [];
    }

    public async getExcludeSyncStatus(): Promise<ExcludeSyncUrlStatus[]> {
        const url = process.env.BACKEND_URL + "/api/exclude-sync";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "GET", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get exclude-sync status: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.urls || [];
    }

    public async refreshExcludeSync(): Promise<ExcludeSyncUrlStatus[]> {
        const url = process.env.BACKEND_URL + "/api/exclude-sync";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to refresh exclude-sync: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.urls || [];
    }

    public async clearWatchdogEntries(): Promise<number> {
        const url = process.env.BACKEND_URL + `/api/clear-watchdog-entries`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to clear watchdog entries: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.deleted ?? 0;
    }

    public async clearHealthCheckHistory(): Promise<{ deletedResults: number; deletedStats: number }> {
        const url = process.env.BACKEND_URL + `/api/clear-health-check-history`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to clear health-check history: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return {
            deletedResults: data.deletedResults ?? 0,
            deletedStats: data.deletedStats ?? 0,
        };
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-history";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check history: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getOverviewStats(
        window: OverviewWindow = "24h",
        sections: OverviewSections = "all",
    ): Promise<OverviewStatsResponse> {
        const url = `${process.env.BACKEND_URL}/api/get-overview-stats?window=${window}&sections=${sections}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get overview stats: ${(await response.json()).error}`);
        }
        return await response.json();
    }

    public async getLogs(params: GetLogsParams = {}): Promise<GetLogsResponse> {
        const qs = new URLSearchParams();
        if (params.limit !== undefined) qs.set("limit", String(params.limit));
        if (params.levels && params.levels.length > 0) qs.set("levels", params.levels.join(","));
        if (params.source) qs.set("source", params.source);
        if (params.search) qs.set("search", params.search);
        if (params.beforeSequence !== undefined) qs.set("beforeSequence", String(params.beforeSequence));
        const url = `${process.env.BACKEND_URL}/api/get-logs${qs.toString() ? `?${qs.toString()}` : ""}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "GET", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get logs: ${(await response.json()).error}`);
        }
        return await response.json();
    }

    public async getWatchtower(params: WatchtowerQuery = {}): Promise<WatchtowerData> {
        const qs = new URLSearchParams();
        if (params.state) qs.set("state", params.state);
        if (params.q) qs.set("q", params.q);
        if (params.sort) qs.set("sort", params.sort);
        if (params.offset) qs.set("offset", String(params.offset));
        if (params.limit) qs.set("limit", String(params.limit));
        if (params.expander) qs.set("expander", params.expander);
        if (params.statsOnly) qs.set("statsOnly", "1");
        const query = qs.toString();
        const url = process.env.BACKEND_URL + "/api/get-watchtower" + (query ? `?${query}` : "");
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { method: "GET", headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get watchtower: ${(await response.json()).error}`);
        }
        return await response.json();
    }

    public async watchtowerMutate(fields: Record<string, string>): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/watchtower-mutate";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const form = new FormData();
        for (const [k, v] of Object.entries(fields)) form.append(k, v);
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey }, body: form });
        if (!response.ok) {
            throw new Error(`Watchtower action failed: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.status;
    }

    public async discoverStremioCatalogs(manifestUrl: string): Promise<DiscoverCatalogsResponse> {
        const url = process.env.BACKEND_URL + "/api/watchtower-discover-catalogs";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const form = new FormData();
        form.append("url", manifestUrl);
        const response = await fetch(url, { method: "POST", headers: { "x-api-key": apiKey }, body: form });
        if (!response.ok) {
            throw new Error(`${(await response.json()).error}`);
        }
        return await response.json();
    }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
    slots: QueueSlot[],
    noofslots: number,
}

export type QueueSlot = {
    nzo_id: string,
    priority: string,
    filename: string,
    cat: string,
    percentage: string,
    true_percentage: string,
    status: string,
    mb: string,
    mbleft: string,
    indexer?: string | null,
    providers?: ProviderUsage[] | null,
}

export type ProviderUsage = {
    host: string,
    nickname?: string | null,
    segments: number,
}

export type HistoryResponse = {
    slots: HistorySlot[],
    noofslots: number,
}

export type HistorySlot = {
    nzo_id: string,
    nzb_name: string,
    name: string,
    category: string,
    status: string,
    bytes: number,
    storage: string,
    download_time: number,
    completed: number,
    fail_message: string,
    nzb_blob_id?: string,
    indexer?: string | null,
    providers?: ProviderUsage[] | null,
}

export type WatchdogOutcome =
    | "PreVerifyAvailable"
    | "PreVerifyDead"
    | "PreVerifyTimeout"
    | "Cancelled"
    | "EnqueueFailed"
    | "QueueFailed"
    | "QueueCompleted"
    | "BudgetTimeout"
    | "ExcludedByPattern";

export type WatchdogEntry = {
    clickId: string,
    attemptedAtUnix: number,
    contentType: string,
    requestedTitle: string,
    candidateTitle: string,
    indexerName: string,
    size: number,
    rankIndex: number,
    outcome: WatchdogOutcome,
    failReason: string | null,
    durationMs: number,
    isWinner: boolean,
    providerHost?: string | null,
    providerNickname?: string | null,
}

export type DirectoryItem = {
    name: string,
    isDirectory: boolean,
    size: number | null | undefined,
    nzbBlobId?: string,
}

export type ConfigItem = {
    configName: string,
    configValue: string,
}

export type ExcludeSyncUrlStatus = {
    url: string,
    count: number,
    fetchedAt: number | null,
    lastChecked: number | null,
    error: string | null,
}

export type WatchtowerQuery = {
    state?: string,
    q?: string,
    sort?: string,
    offset?: number,
    limit?: number,
    expander?: string,
    statsOnly?: boolean,
}

export type WatchtowerData = {
    status: boolean,
    enabled: boolean,
    sources: WatchtowerSource[],
    items: WatchtowerItem[],
    shows: WatchtowerItem[],
    total: number,
    hasMore: boolean,
    stats: WatchtowerStats,
}

export type WatchtowerSource = {
    id: string,
    kind: string,
    name: string,
    url?: string | null,
    enabled: boolean,
    cap: number,
    seriesScope?: string | null,
    lastSyncedAtUnix?: number | null,
    lastSyncError?: string | null,
}

export type WatchtowerItem = {
    key: string,
    type: string,
    contentId: string,
    title: string,
    state: string,
    provenanceCount: number,
    expanderKey?: string | null,
    childTotal?: number | null,
    childReady?: number | null,
    childUnavailable?: number | null,
    shortlistCount: number,
    winnerTitle?: string | null,
    winnerSize: number,
    lastVerifiedAtUnix?: number | null,
    nextCheckAtUnix?: number | null,
    failReason?: string | null,
}

export type WatchtowerStats = {
    total: number,
    ready: number,
    scouting: number,
    unavailable: number,
    parked: number,
    expanders: number,
}

export type DiscoveredCatalog = {
    type: string,
    id: string,
    name: string,
    url: string,
    extraRequired?: string | null,
}

export type DiscoverCatalogsResponse = {
    status: boolean,
    error?: string,
    addonName?: string | null,
    catalogs: DiscoveredCatalog[],
}

export type SearchIndexersResponse = {
    status: boolean,
    error?: string,
    results: SearchIndexerResult[],
    indexers: IndexerStatus[],
}

export type SearchIndexerResult = {
    indexer: string,
    title: string,
    nzbUrl: string,
    size: number,
    posted: string | null,
}

export type IndexerStatus = {
    name: string,
    ok: boolean,
    resultCount: number,
    error: string | null,
    elapsedMs: number,
}

export type TestUsenetConnectionRequest = {
    host: string,
    port: string,
    useSsl: string,
    user: string,
    pass: string
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number,
    items: HealthCheckQueueItem[]
}

export type HealthCheckQueueItem = {
    id: string,
    name: string,
    path: string,
    releaseDate: string | null,
    lastHealthCheck: string | null,
    nextHealthCheck: string | null,
    progress: number,
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[],
    items: HealthCheckResult[]
}

export type HealthCheckStats = {
    result: HealthResult,
    repairStatus: RepairAction,
    count: number
}

export type HealthCheckResult = {
    id: string,
    createdAt: string,
    davItemId: string,
    path: string,
    result: HealthResult,
    repairStatus: RepairAction,
    message: string | null
}

export enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

export enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}

export type OverviewWindow = "24h" | "7d" | "30d" | "all";
export type OverviewSections = "all" | "window" | "detail" | "static" | string;

export type OverviewStatsResponse = {
    window: OverviewWindow,
    includedSections?: string[],
    tiles: {
        activeReads: number,
        articlesPerMinute: number,
        errorsPerMinute: number,
        bytesServedPerMinute: number,
    },
    throughput: ThroughputPoint[],
    totalArticles: number,
    totalErrors: number,
    totalBytesFetched: number,
    providers: ProviderRow[],
    catalogue: {
        fileCount: number,
        totalBytes: number,
        largestFileBytes: number,
        addedLast7Days: number,
    },
    sessions: {
        count: number,
        totalBytesServed: number,
        avgDurationMs: number,
        longestDurationMs: number,
        biggestReadBytes: number,
    },
    heatmap: {
        maxCell: number,
        mode: HeatmapMode,
        windowStartMs: number,
        windowEndMs: number,
        bucketSizeMs: number,
        cells: HeatmapCell[],
    },
    latency: {
        p50Ms: number,
        p95Ms: number,
        p99Ms: number,
        samples: number,
        buckets: LatencyBucket[],
    },
    errors: ErrorSlice[],
    indexers: IndexerRow[],
    indexerApiUsage: IndexerApiUsageRow[],
    lifetime: {
        bytesFetched: number,
        bytesRead: number,
        articles: number,
        readSessions: number,
        readSeconds: number,
        firstSeenAt: number | null,
    },
    records: {
        bestDayBytes: number,
        bestDayAt: number | null,
        bestHourBytes: number,
        bestHourAt: number | null,
    },
    failover: FailoverBlock,
    metricsHealth: {
        queued: number,
        dropped: number,
        lastSuccessfulFlushAtMs: number,
        lastFlushError: string | null,
    },
}

export type FailoverBlock = {
    articlesRecovered: number,
    previousArticlesRecovered: number | null,
    segmentsCovered: number,
    readsSaved: number,
    readSessions: number,
    totalArticles: number,
    bucketSizeMs: number,
    rescuedBy: FailoverProvider[],
    rescuedFrom: FailoverFrom[],
    reasons: FailoverReason[],
    buckets: FailoverBucket[],
}

export type FailoverProvider = {
    provider: string,
    nickname?: string | null,
    saves: number,
}

export type FailoverFrom = {
    provider: string,
    nickname?: string | null,
    misses: number,
}

export type FailoverReason = {
    status: string,
    count: number,
}

export type FailoverBucket = {
    bucket: number,
    counts: number[],
}

export type ThroughputPoint = {
    bucket: number,
    articles: number,
    errors: number,
    bytesServed: number,
}

export type ProviderRow = {
    provider: string,
    nickname?: string | null,
    articles: number,
    bytesFetched: number,
    errors: number,
    retries: number,
    avgDurationMs: number,
    errorRate: number,
    spark: number[],
}

export type HeatmapMode = "day" | "week" | "month" | "year";

export type HeatmapCell = {
    bucket: number,
    count: number,
}

export type LatencyBucket = {
    loMs: number,
    hiMs: number,
    count: number,
}

export type ErrorSlice = {
    status: string,
    count: number,
}

export type IndexerRow = {
    name: string,
    completed: number,
    failed: number,
    bytesCompleted: number,
    avgSeconds: number,
    successRate: number,
}

export type IndexerApiUsageRow = {
    name: string,
    apiHits: number,
    apiHitLimit: number | null,
    downloadHits: number,
    downloadHitLimit: number | null,
    resetAtMs: number,
    resetHourUtc: number | null,
}

export type ActiveReadsMessage = {
    reads: ActiveRead[],
}

export type ActiveRead = {
    id: string,
    fileName: string,
    path: string,
    startedAt: number,
    lastActivityAt: number,
    bytesRead: number,
    currentOffset: number,
    fileSize: number | null,
    providers: { host: string, nickname?: string | null, segments: number }[],
}

export type LiveStatsMessage = {
    activeReads: number,
    articlesPerMinute: number,
    errorsPerMinute: number,
    bytesServedPerMinute: number,
    ts: number,
}

export type LogLevel = "Verbose" | "Debug" | "Information" | "Warning" | "Error" | "Fatal";

export type LogEntry = {
    seq: number,
    ts: number,
    level: LogLevel,
    msg: string,
    source: string | null,
    exception: string | null,
}

export type GetLogsParams = {
    limit?: number,
    levels?: LogLevel[],
    source?: string,
    search?: string,
    beforeSequence?: number,
}

export type GetLogsResponse = {
    status: boolean,
    error?: string,
    entries: LogEntry[],
    countsByLevel: Record<string, number>,
    oldestSequence: number,
    newestSequence: number,
    capacity: number,
}

export type LogBroadcastMessage = {
    entries: LogEntry[],
}