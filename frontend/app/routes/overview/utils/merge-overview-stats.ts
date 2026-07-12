import type {
    LiveStatsMessage,
    OverviewStatsResponse,
    OverviewWindow,
} from "~/clients/backend-client.server";

export type { LiveStatsMessage, OverviewStatsResponse, OverviewWindow };

export const EMPTY_OVERVIEW_STATS: OverviewStatsResponse = {
    window: "24h",
    includedSections: [],
    tiles: {
        activeReads: 0,
        articlesPerMinute: 0,
        errorsPerMinute: 0,
        bytesServedPerMinute: 0,
    },
    throughput: [],
    totalArticles: 0,
    totalErrors: 0,
    totalBytesFetched: 0,
    providers: [],
    catalogue: { fileCount: 0, totalBytes: 0, largestFileBytes: 0, addedLast7Days: 0 },
    sessions: {
        count: 0,
        totalBytesServed: 0,
        avgDurationMs: 0,
        longestDurationMs: 0,
        biggestReadBytes: 0,
    },
    heatmap: {
        maxCell: 0,
        mode: "day",
        windowStartMs: 0,
        windowEndMs: 0,
        bucketSizeMs: 3_600_000,
        cells: [],
    },
    latency: { p50Ms: 0, p95Ms: 0, p99Ms: 0, samples: 0, buckets: [] },
    errors: [],
    indexers: [],
    indexerApiUsage: [],
    lifetime: {
        bytesFetched: 0,
        bytesRead: 0,
        articles: 0,
        readSessions: 0,
        readSeconds: 0,
        firstSeenAt: null,
    },
    records: {
        bestDayBytes: 0,
        bestDayAt: null,
        bestHourBytes: 0,
        bestHourAt: null,
    },
    failover: {
        articlesRecovered: 0,
        previousArticlesRecovered: null,
        segmentsCovered: 0,
        readsSaved: 0,
        readSessions: 0,
        totalArticles: 0,
        bucketSizeMs: 3_600_000,
        rescuedBy: [],
        rescuedFrom: [],
        reasons: [],
        buckets: [],
    },
    metricsHealth: {
        queued: 0,
        dropped: 0,
        lastSuccessfulFlushAtMs: 0,
        lastFlushError: null,
    },
};

export function mergeOverviewStats(
    prev: OverviewStatsResponse,
    partial: OverviewStatsResponse,
): OverviewStatsResponse {
    const sections = new Set(partial.includedSections ?? ["all"]);
    const includeAll = sections.has("all");
    const next: OverviewStatsResponse = { ...prev };

    if (includeAll || sections.has("window")) {
        next.window = partial.window;
        next.tiles = partial.tiles;
        next.throughput = partial.throughput;
        next.totalArticles = partial.totalArticles;
        next.totalErrors = partial.totalErrors;
        next.totalBytesFetched = partial.totalBytesFetched;
        next.providers = partial.providers;
        next.sessions = partial.sessions;
        next.heatmap = partial.heatmap;
        next.failover = partial.failover;
        next.metricsHealth = partial.metricsHealth;
    }
    if (includeAll || sections.has("detail")) {
        next.latency = partial.latency;
        next.errors = partial.errors;
    }
    if (includeAll || sections.has("static")) {
        next.catalogue = partial.catalogue;
        next.indexers = partial.indexers;
        next.indexerApiUsage = partial.indexerApiUsage;
        next.lifetime = partial.lifetime;
        next.records = partial.records;
    }

    next.includedSections = [
        ...new Set([...(prev.includedSections ?? []), ...(partial.includedSections ?? [])]),
    ];
    return next;
}
