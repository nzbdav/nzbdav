import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { DndContext, PointerSensor, KeyboardSensor, useSensor, useSensors, closestCenter, type DragEndEvent } from "@dnd-kit/core";
import { SortableContext, arrayMove, verticalListSortingStrategy, sortableKeyboardCoordinates } from "@dnd-kit/sortable";
import { LiveTiles } from "./components/live-tiles/live-tiles";
import { LiveReadsPanel } from "./components/live-reads-panel/live-reads-panel";
import { ActivityHeatmap } from "./components/activity-heatmap/activity-heatmap";
import { ThroughputChart } from "./components/throughput-chart/throughput-chart";
import { LatencyHistogram } from "./components/latency-histogram/latency-histogram";
import { ErrorDonut } from "./components/error-donut/error-donut";
import { ProviderScoreboard } from "./components/provider-scoreboard/provider-scoreboard";
import { IndexerScoreboard } from "./components/indexer-scoreboard/indexer-scoreboard";
import { IndexerApiUsage } from "./components/indexer-api-usage/indexer-api-usage";
import { SessionsBlock } from "./components/sessions-block/sessions-block";
import { CatalogueBlock } from "./components/catalogue-block/catalogue-block";
import { LifetimeBlock } from "./components/lifetime-block/lifetime-block";
import { RecordsBlock } from "./components/records-block/records-block";
import { FailoverSaves } from "./components/failover-saves/failover-saves";
import { SortableRow } from "./components/sortable-row/sortable-row";
import { useRowOrder } from "./utils/use-row-order";
import {
    EMPTY_OVERVIEW_STATS,
    mergeOverviewStats,
    type LiveStatsMessage,
    type OverviewStatsResponse,
    type OverviewWindow,
} from "./utils/merge-overview-stats";

const topicNames = {
    liveStats: 'ls',
};
const topicSubscriptions = {
    [topicNames.liveStats]: 'state',
};

const WINDOWS: { value: OverviewWindow, label: string }[] = [
    { value: "24h", label: "24h" },
    { value: "7d", label: "7d" },
    { value: "30d", label: "30d" },
    { value: "all", label: "All" },
];

const DEFAULT_ROW_ORDER = [
    "liveTiles",
    "liveReads",
    "throughput",
    "activity",
    "latency",
    "errorsSessions",
    "providers",
    "failover",
    "indexers",
    "indexerApiUsage",
    "recordsCatalogue",
    "lifetime",
] as const;

/** Shell-only loader — stats load client-side in sections so first paint is instant. */
export async function loader() {
    return { stats: null as OverviewStatsResponse | null };
}

export function shouldRevalidate() {
    return false;
}

function Skeleton({ height = 120 }: { height?: number }) {
    return (
        <div
            className={styles.skeleton}
            style={{ minHeight: height }}
            aria-hidden="true"
        />
    );
}

export default function Overview(_props: Route.ComponentProps) {
    const [stats, setStats] = useState<OverviewStatsResponse>(EMPTY_OVERVIEW_STATS);
    const [window, setWindow] = useState<OverviewWindow>("24h");
    const [editMode, setEditMode] = useState(false);
    const [connectedAt, setConnectedAt] = useState<number | null>(null);
    const [lastLiveStatsAt, setLastLiveStatsAt] = useState<number | null>(null);
    const [transportFailed, setTransportFailed] = useState(false);
    const [liveClock, setLiveClock] = useState(() => Date.now());
    const [windowLoaded, setWindowLoaded] = useState(false);
    const [detailLoaded, setDetailLoaded] = useState(false);
    const [staticLoaded, setStaticLoaded] = useState(false);
    const { order, save, reset } = useRowOrder(DEFAULT_ROW_ORDER);

    const liveTiles = stats.tiles;
    const isLongWindow = window === "7d" || window === "30d" || window === "all";

    // Window section: load on mount / window change, poll every 30s while visible.
    useEffect(() => {
        let cancelled = false;
        setWindowLoaded(false);
        if (isLongWindow) setDetailLoaded(true);
        else setDetailLoaded(false);

        const fetchWindow = async () => {
            if (typeof document !== "undefined" && document.hidden) return;
            try {
                const res = await fetch(`/api/get-overview-stats?window=${window}&sections=window`);
                if (!res.ok || cancelled) return;
                const data: OverviewStatsResponse = await res.json();
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setWindowLoaded(true);
            } catch { /* network blip, retry next tick */ }
        };

        fetchWindow();
        const interval = setInterval(fetchWindow, 30_000);
        const onVisible = () => { if (!document.hidden) fetchWindow(); };
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            cancelled = true;
            clearInterval(interval);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [window, isLongWindow]);

    // Detail (latency + errors): once per 24h window selection — not on the 30s poll.
    useEffect(() => {
        if (isLongWindow) return;
        let cancelled = false;
        (async () => {
            try {
                const res = await fetch(`/api/get-overview-stats?window=${window}&sections=detail`);
                if (!res.ok || cancelled) return;
                const data: OverviewStatsResponse = await res.json();
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setDetailLoaded(true);
            } catch { /* ignore */ }
        })();
        return () => { cancelled = true; };
    }, [window, isLongWindow]);

    // Static blocks: once per page visit.
    useEffect(() => {
        let cancelled = false;
        (async () => {
            try {
                const res = await fetch(`/api/get-overview-stats?window=${window}&sections=static`);
                if (!res.ok || cancelled) return;
                const data: OverviewStatsResponse = await res.json();
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setStaticLoaded(true);
            } catch { /* ignore */ }
        })();
        return () => { cancelled = true; };
    }, []); // eslint-disable-line react-hooks/exhaustive-deps -- once per visit

    const onWsMessage = useCallback((topic: string, message: string) => {
        if (topic !== topicNames.liveStats) return;
        try {
            const live: LiveStatsMessage = JSON.parse(message);
            setStats(s => ({
                ...s,
                tiles: {
                    activeReads: live.activeReads,
                    articlesPerMinute: live.articlesPerMinute,
                    errorsPerMinute: live.errorsPerMinute,
                    bytesServedPerMinute: live.bytesServedPerMinute,
                }
            }));
            setLastLiveStatsAt(Date.now());
            setTransportFailed(false);
        } catch { /* ignore */ }
    }, []);

    useEffect(() => {
        const interval = setInterval(() => setLiveClock(Date.now()), 5_000);
        return () => clearInterval(interval);
    }, []);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(globalThis.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWsMessage);
            ws.onopen = () => {
                setConnectedAt(Date.now());
                ws.send(JSON.stringify(topicSubscriptions));
            };
            ws.onclose = (event) => {
                setConnectedAt(null);
                setTransportFailed(true);
                if (event.code === 1008) {
                    globalThis.location.assign("/login");
                    return;
                }
                if (!disposed) setTimeout(connect, 1000);
            };
            ws.onerror = () => { ws.close(); };
        }
        connect();
        return () => { disposed = true; ws?.close(); };
    }, [onWsMessage]);

    const heartbeatAge = liveClock - (lastLiveStatsAt ?? connectedAt ?? liveClock);
    const liveStatsStale = transportFailed || (connectedAt !== null && heartbeatAge > 15_000);
    const metricsError = stats.metricsHealth?.lastFlushError;
    const droppedMetrics = stats.metricsHealth?.dropped ?? 0;

    const rowContent = useMemo<Record<string, ReactNode>>(() => ({
        liveTiles: <LiveTiles tiles={liveTiles} />,
        liveReads: <LiveReadsPanel />,
        throughput: windowLoaded
            ? (
                <ThroughputChart
                    points={stats.throughput}
                    totalArticles={stats.totalArticles}
                    totalErrors={stats.totalErrors}
                    totalBytesServed={stats.sessions.totalBytesServed}
                    window={window}
                />
            )
            : <Skeleton height={180} />,
        activity: windowLoaded
            ? (
                <ActivityHeatmap
                    maxCell={stats.heatmap.maxCell}
                    mode={stats.heatmap.mode}
                    windowStartMs={stats.heatmap.windowStartMs}
                    windowEndMs={stats.heatmap.windowEndMs}
                    bucketSizeMs={stats.heatmap.bucketSizeMs}
                    cells={stats.heatmap.cells}
                />
            )
            : <Skeleton height={140} />,
        latency: !isLongWindow
            ? (detailLoaded
                ? (
                    <LatencyHistogram
                        p50Ms={stats.latency.p50Ms}
                        p95Ms={stats.latency.p95Ms}
                        p99Ms={stats.latency.p99Ms}
                        samples={stats.latency.samples}
                        buckets={stats.latency.buckets}
                    />
                )
                : <Skeleton height={160} />)
            : null,
        errorsSessions: (
            <div className={styles.twoCol}>
                {!isLongWindow && (
                    detailLoaded
                        ? <ErrorDonut errors={stats.errors} />
                        : <Skeleton height={160} />
                )}
                {windowLoaded
                    ? <SessionsBlock sessions={stats.sessions} window={window} />
                    : <Skeleton height={160} />}
            </div>
        ),
        providers: windowLoaded
            ? <ProviderScoreboard providers={stats.providers} window={window} />
            : <Skeleton height={160} />,
        failover: windowLoaded
            ? <FailoverSaves failover={stats.failover} window={window} />
            : <Skeleton height={180} />,
        indexers: staticLoaded
            ? <IndexerScoreboard indexers={stats.indexers} />
            : <Skeleton height={140} />,
        indexerApiUsage: staticLoaded
            ? <IndexerApiUsage rows={stats.indexerApiUsage} />
            : <Skeleton height={120} />,
        recordsCatalogue: (
            <div className={styles.twoCol}>
                {staticLoaded
                    ? <RecordsBlock records={stats.records} />
                    : <Skeleton height={120} />}
                {staticLoaded
                    ? <CatalogueBlock catalogue={stats.catalogue} />
                    : <Skeleton height={120} />}
            </div>
        ),
        lifetime: staticLoaded
            ? <LifetimeBlock lifetime={stats.lifetime} />
            : <Skeleton height={120} />,
    }), [liveTiles, stats, window, isLongWindow, windowLoaded, detailLoaded, staticLoaded]);

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const onDragEnd = (event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id) return;
        const oldIndex = order.indexOf(String(active.id));
        const newIndex = order.indexOf(String(over.id));
        if (oldIndex < 0 || newIndex < 0) return;
        save(arrayMove(order, oldIndex, newIndex));
    };

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h2 className={styles.title}>Overview</h2>
                <div className={styles.headerActions}>
                    {editMode && (
                        <button
                            type="button"
                            className={styles.resetBtn}
                            onClick={reset}
                            title="Restore default order">
                            Reset
                        </button>
                    )}
                    <button
                        type="button"
                        className={editMode ? styles.editBtnActive : styles.editBtn}
                        onClick={() => setEditMode(v => !v)}
                        aria-pressed={editMode}
                        title={editMode ? "Done editing layout" : "Reorder widgets"}>
                        {editMode ? "Done" : "Edit layout"}
                    </button>
                    <div className={styles.windowToggle} role="tablist">
                        {WINDOWS.map(w => (
                            <button
                                key={w.value}
                                role="tab"
                                aria-selected={window === w.value}
                                className={window === w.value ? styles.windowActive : styles.windowOption}
                                onClick={() => setWindow(w.value)}>{w.label}</button>
                        ))}
                    </div>
                </div>
            </div>

            {(liveStatsStale || metricsError || droppedMetrics > 0) && (
                <div
                    role="alert"
                    className={`rounded border px-3 py-2 text-xs ${
                        metricsError
                            ? "border-red-500/50 bg-red-500/10 text-red-200"
                            : "border-amber-600/50 bg-amber-500/10 text-amber-200"
                    }`}>
                    {metricsError
                        ? `Metrics storage is unavailable: ${metricsError}`
                        : droppedMetrics > 0
                            ? `${droppedMetrics.toLocaleString()} metrics were dropped before they could be stored.`
                            : "Live updates are reconnecting. Values below may be stale."}
                </div>
            )}

            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
                <SortableContext items={order} strategy={verticalListSortingStrategy}>
                    {order.map(id => {
                        const content = rowContent[id];
                        if (!content) return null;
                        return (
                            <SortableRow key={id} id={id} editMode={editMode}>
                                {content}
                            </SortableRow>
                        );
                    })}
                </SortableContext>
            </DndContext>
        </div>
    );
}
