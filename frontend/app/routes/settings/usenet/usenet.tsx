import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, type ReactNode, type CSSProperties, useState, useCallback, useEffect, useMemo, useRef } from "react";
import { Button } from "~/components/ui/button";
import { Icon } from "~/components/ui/icon";
import { receiveMessage } from "~/utils/websocket-util";
import { isMaskedSecret } from "~/utils/config-mask";
import {
    DndContext,
    type DragEndEvent,
    closestCenter,
    KeyboardSensor,
    PointerSensor,
    useSensor,
    useSensors,
} from "@dnd-kit/core";
import {
    SortableContext,
    arrayMove,
    rectSortingStrategy,
    sortableKeyboardCoordinates,
    useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";

const usenetConnectionsTopic = {'cxs': 'state'};
const benchmarkTopic = {'bench': 'state'};
const USAGE_POLL_INTERVAL_MS = 10_000;

// Mirrors the camelCase JSON the backend benchmark endpoint + websocket emit.
type BenchmarkLatency = { minMs: number; avgMs: number; samples: number };
type BenchmarkSweepPoint = { connections: number; mbPerSec: number };
type BenchmarkPipeliningPoint = { depth: number; mbPerSec: number };
type BenchmarkPipelining = {
    testedAtConnections: number;
    baselineMbPerSec: number;
    tested: BenchmarkPipeliningPoint[];
    recommendEnabled: boolean;
    recommendedDepth: number;
};
type BenchmarkResult = {
    latency?: BenchmarkLatency | null;
    throughputTested: boolean;
    pipeliningOnly: boolean;
    sweep: BenchmarkSweepPoint[];
    recommendedConnections?: number | null;
    providerConnectionCap?: number | null;
    pipelining?: BenchmarkPipelining | null;
    dataUsedBytes: number;
    warnings: string[];
};
type BenchmarkProgress = {
    phase: string;
    status: string;
    percent: number;
    currentConnections?: number | null;
    dataUsedBytes: number;
    sweep: BenchmarkSweepPoint[];
};
type BenchmarkIntensity = "quick" | "thorough";

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
}

type ConnectionDetails = {
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
    Priority?: number;
    PipeliningDepth?: number | null;
    // Optional user-set label. Shown in the UI in place of Host when present;
    // Host stays the real NNTP target.
    Nickname?: string;
    PreviousType?: ProviderType;
    // null/0 = uncapped. Stored as bytes; the modal lets the user type a
    // friendlier MB/GB/TB value that gets converted on save.
    ByteLimit?: number | null;
    // Counter adjustment, used for "initial used" on a freshly added block
    // and zeroed on reset. Bytes.
    BytesUsedOffset?: number;
    // unix-ms cutoff. Hourly rows older than this are excluded from the live
    // usage gauge. A reset bumps this to Date.now().
    BytesUsedResetAt?: number;
};

// camelCase matches the JSON wire format — ASP.NET Core MVC defaults to
// camelCase serialization, so we mirror that here instead of fighting it.
type ProviderUsage = {
    index: number;
    host: string;
    nickname?: string | null;
    bytesUsed: number;
    byteLimit: number | null;
    overLimit: boolean;
    bytesPerDay: number;
    daysRemaining: number | null;
};

function formatDaysRemaining(days: number): string {
    // Friendlier than "0.3 days" or "847 days" — round to the unit that's
    // actually useful at this horizon.
    if (days < 1) {
        const hours = Math.max(1, Math.round(days * 24));
        return `~${hours}h left at this pace`;
    }
    if (days < 60) return `~${Math.round(days)} days left at this pace`;
    const months = days / 30;
    if (months < 24) return `~${Math.round(months)} months left at this pace`;
    return `~${Math.round(months / 12)} years left at this pace`;
}

const BYTE_UNITS = [
    { label: "MB", multiplier: 1_000_000 },
    { label: "GB", multiplier: 1_000_000_000 },
    { label: "TB", multiplier: 1_000_000_000_000 },
] as const;
type ByteUnitLabel = typeof BYTE_UNITS[number]["label"];

function bytesToValueAndUnit(bytes: number | null | undefined): { value: string; unit: ByteUnitLabel } {
    if (!bytes || bytes <= 0) return { value: "", unit: "GB" };
    // Pick the largest unit that keeps the number readable (>= 1).
    const choice = [...BYTE_UNITS].reverse().find(u => bytes >= u.multiplier) ?? BYTE_UNITS[1];
    const v = bytes / choice.multiplier;
    // Trim trailing zeros so "500" doesn't display as "500.000".
    return { value: Number(v.toFixed(3)).toString(), unit: choice.label };
}

function valueAndUnitToBytes(value: string, unit: ByteUnitLabel): number | null {
    const trimmed = value.trim();
    if (trimmed === "") return null;
    const n = Number(trimmed);
    if (!isFinite(n) || n <= 0) return null;
    const u = BYTE_UNITS.find(x => x.label === unit) ?? BYTE_UNITS[1];
    return Math.round(n * u.multiplier);
}

function formatBytes(bytes: number): string {
    if (!isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pool Connections",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        const parsed = JSON.parse(jsonString);
        return parsed && Array.isArray(parsed.Providers)
            ? parsed
            : { Providers: [] };
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

function providerKey(p: ConnectionDetails): string {
    return `${p.Host}::${p.Port}::${p.User}`;
}

type DragBits = {
    setNodeRef: (node: HTMLElement | null) => void;
    setActivatorNodeRef: (node: HTMLElement | null) => void;
    attributes: any;
    listeners: any;
    style: CSSProperties;
    isDragging: boolean;
};

function SortableItem({ id, disabled, children }: { id: string; disabled: boolean; children: (drag: DragBits) => ReactNode }) {
    const { setNodeRef, setActivatorNodeRef, attributes, listeners, transform, transition, isDragging } = useSortable({ id, disabled });
    const style: CSSProperties = {
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.6 : 1,
        zIndex: isDragging ? 2 : undefined,
    };
    return <>{children({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging })}</>;
}

export function UsenetSettings({ config, setNewConfig }: UsenetSettingsProps) {
    // state
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const [usage, setUsage] = useState<{[index: number]: ProviderUsage}>({});
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);
    const cascadeEnabled = config["usenet.cascade.enabled"] === "true";

    // handlers
    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleToggleProvider = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const isDisabled = current.Type === ProviderType.Disabled;
        const updated: ConnectionDetails = isDisabled
            ? { ...current, Type: current.PreviousType ?? ProviderType.Pooled, PreviousType: undefined }
            : { ...current, Type: ProviderType.Disabled, PreviousType: current.Type };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleResetUsage = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const label = current.Nickname?.trim() || current.Host;
        if (!confirm(`Reset bytes-used counter for "${label}" to zero?\n\nThis only rewinds the gauge for this provider's data cap. Historical metrics and graphs are untouched. Takes effect after you save settings.`)) return;
        const updated: ConnectionDetails = {
            ...current,
            BytesUsedOffset: 0,
            BytesUsedResetAt: Date.now(),
        };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const providers = [...providerConfig.Providers];
        if (editingIndex !== null) {
            providers[editingIndex] = provider;
        } else {
            providers.push({ ...provider, Priority: providers.length });
        }
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }) });
        handleCloseModal();
    }, [config, providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleApplyPipelining = useCallback((enabled: boolean) => {
        setNewConfig(prev => ({
            ...prev,
            "usenet.pipelining.enabled": enabled ? "true" : "false",
        }));
    }, [setNewConfig]);

    const handleReorder = useCallback((from: number, to: number) => {
        if (from === to) return;
        const providers = arrayMove(providerConfig.Providers, from, to)
            .map((p, i) => ({ ...p, Priority: i }));
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }) });
    }, [config, providerConfig, setNewConfig]);

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const handleDragEnd = useCallback((event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id) return;
        const ids = providerConfig.Providers.map(providerKey);
        const from = ids.indexOf(String(active.id));
        const to = ids.indexOf(String(over.id));
        if (from !== -1 && to !== -1) handleReorder(from, to);
    }, [providerConfig, handleReorder]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => handleConnectionsMessage(message));
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, handleConnectionsMessage]);

    // Poll provider usage. Backend computes "bytes since reset + offset" from
    // the persisted hourly rollup plus the in-memory tracker; cheap enough to
    // hit on a 10s tick. We skip while the edit modal is open since the user
    // may be mid-edit and we don't want the card behind the modal flickering.
    useEffect(() => {
        let disposed = false;
        async function fetchUsage() {
            try {
                const response = await fetch('/api/get-provider-usage');
                if (!response.ok || disposed) return;
                const data: { providers?: ProviderUsage[] } = await response.json();
                if (disposed || !data.providers) return;
                const next: {[index: number]: ProviderUsage} = {};
                for (const p of data.providers) next[p.index] = p;
                setUsage(next);
            } catch {
                // network blips are fine — next tick retries.
            }
        }
        fetchUsage();
        if (showModal) return () => { disposed = true; };
        const id = setInterval(fetchUsage, USAGE_POLL_INTERVAL_MS);
        return () => { disposed = true; clearInterval(id); };
    }, [showModal, providerConfig.Providers.length]);

    // view
    return (
        <div className={'space-y-6'}>
            <div className={'space-y-4'}>
                <div className={'flex items-center justify-between text-lg font-semibold text-white'}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="small" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={'rounded border border-slate-700/70 bg-slate-800/40 px-3 py-2 text-sm text-slate-400'}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
                    <SortableContext items={providerConfig.Providers.map(providerKey)} strategy={rectSortingStrategy}>
                    <div className={styles["providers-grid"]}>
                        {providerConfig.Providers.map((provider, index) => {
                            const isDisabled = provider.Type === ProviderType.Disabled;
                            return (
                            <SortableItem key={providerKey(provider)} id={providerKey(provider)} disabled={!cascadeEnabled}>
                            {({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging }) => (
                            <div ref={setNodeRef} style={style} className={`${styles["provider-card"]} ${isDisabled ? styles["provider-card-disabled"] : ""}`}>
                                <div className={styles["provider-card-inner"]}>
                                    <div className={styles["provider-header"]}>
                                        <div className={styles["provider-header-content"]}>
                                            <div className={styles["provider-host"]}>
                                                {cascadeEnabled && !isDisabled && (
                                                    <span style={{ display: "inline-block", marginRight: 8, padding: "2px 8px", fontSize: 9, fontWeight: 600, letterSpacing: "0.08em", color: "var(--text-muted)", background: "var(--bg-surface-2)", border: "1px solid var(--border-subtle)", borderRadius: 6, verticalAlign: "middle" }}>
                                                        #{index + 1}
                                                    </span>
                                                )}
                                                {provider.Nickname?.trim() || provider.Host}
                                                {isDisabled && <span className={styles["provider-disabled-badge"]}>Disabled</span>}
                                            </div>
                                            {provider.Nickname?.trim() && (
                                                <div className={styles["provider-host-secondary"]}>
                                                    {provider.Host}
                                                </div>
                                            )}
                                            <div className={styles["provider-port"]}>
                                                Port {provider.Port}
                                            </div>
                                        </div>
                                        <div className={styles["provider-header-actions"]}>
                                            {cascadeEnabled && (
                                                <button
                                                    type="button"
                                                    ref={setActivatorNodeRef}
                                                    className={styles["header-action-button"]}
                                                    style={{ cursor: isDragging ? "grabbing" : "grab", touchAction: "none" }}
                                                    title="Drag to reorder"
                                                    aria-label="Drag to reorder"
                                                    {...attributes}
                                                    {...listeners}
                                                >
                                                    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                                                        <circle cx="9" cy="5" r="1.6" /><circle cx="15" cy="5" r="1.6" />
                                                        <circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" />
                                                        <circle cx="9" cy="19" r="1.6" /><circle cx="15" cy="19" r="1.6" />
                                                    </svg>
                                                </button>
                                            )}
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["toggle"]} ${isDisabled ? styles["toggle-off"] : styles["toggle-on"]}`}
                                                onClick={() => handleToggleProvider(index)}
                                                title={isDisabled ? "Enable Provider" : "Disable Provider"}
                                                aria-pressed={!isDisabled}
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                                    <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                                                    <line x1="12" y1="2" x2="12" y2="12" />
                                                </svg>
                                            </button>
                                            <button
                                                className={'rounded bg-white/10 p-1.5 text-slate-300 hover:bg-white/20'}
                                                onClick={() => handleEditProvider(index)}
                                                title="Edit Provider"
                                            >
                                                <Icon name="edit" className="!text-[18px]" />
                                            </button>
                                            <button
                                                className={`${'rounded bg-white/10 p-1.5 text-slate-300 hover:bg-white/20'} ${'hover:text-red-400'}`}
                                                onClick={() => handleDeleteProvider(index)}
                                                title="Delete Provider"
                                            >
                                                <Icon name="delete" className="!text-[18px]" />
                                            </button>
                                        </div>
                                    </div>

                                    <div className={'mt-4 border-t border-slate-700/70 pt-3'}>
                                        <div className={'grid grid-cols-1 gap-3 sm:grid-cols-2'}>

                                            <div className={'relative flex min-w-0 items-center gap-2'}>
                                                <div className={'text-blue-400'}>
                                                    <Icon name="person" className="!text-[18px]" />
                                                </div>
                                                <div className={'flex min-w-0 flex-col'}>
                                                    <span className={'text-[11px] uppercase tracking-wide text-slate-500'}>Username</span>
                                                    <span className={'truncate text-sm text-slate-200'}>{provider.User}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Connections</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {connections[index]
                                                            ? `${connections[index].live} / ${provider.MaxConnections} max`
                                                            : `${provider.MaxConnections} max`}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={'relative flex min-w-0 items-center gap-2'}>
                                                <div className={'text-blue-400'}>
                                                    <Icon name={provider.UseSsl ? "lock" : "lock_open"} className="!text-[18px]" />
                                                </div>
                                                <div className={'flex min-w-0 flex-col'}>
                                                    <span className={'text-[11px] uppercase tracking-wide text-slate-500'}>Security</span>
                                                    <span className={'truncate text-sm text-slate-200'}>
                                                        {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={'relative flex min-w-0 items-center gap-2'}>
                                                <div className={'text-blue-400'}>
                                                    <Icon name="account_tree" className="!text-[18px]" />
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Behavior</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {PROVIDER_TYPE_LABELS[provider.Type]}
                                                    </span>
                                                </div>
                                            </div>

                                        </div>

                                        <UsageRow
                                            provider={provider}
                                            usage={usage[index]}
                                            onReset={() => handleResetUsage(index)}
                                        />
                                    </div>
                                </div>
                            </div>
                            )}
                            </SortableItem>
                            );
                        })}
                    </div>
                    </SortableContext>
                    </DndContext>
                )}
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Cascade (Optional)</div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="cascade-enabled"
                            className={styles["form-checkbox"]}
                            checked={cascadeEnabled}
                            onChange={(e) => {
                                const enabling = e.target.checked;
                                const needsSeed = enabling && providerConfig.Providers.every(p => !p.Priority);
                                const providers = needsSeed
                                    ? providerConfig.Providers.map((p, i) => ({ ...p, Priority: i }))
                                    : providerConfig.Providers;
                                setNewConfig({
                                    ...config,
                                    "usenet.cascade.enabled": enabling ? "true" : "false",
                                    "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }),
                                });
                            }}
                        />
                        <label htmlFor="cascade-enabled" className={styles["form-checkbox-label"]}>
                            Enable cascade routing
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Sets the order your providers are used. Drag the cards to arrange them. While this is off, all
                        providers are used together.
                    </div>
                </div>
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>NNTP Pipelining</div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="pipelining-enabled"
                            className={styles["form-checkbox"]}
                            checked={config["usenet.pipelining.enabled"] === "true"}
                            onChange={(e) => setNewConfig({
                                ...config,
                                "usenet.pipelining.enabled": e.target.checked ? "true" : "false",
                            })}
                        />
                        <label htmlFor="pipelining-enabled" className={styles["form-checkbox-label"]}>
                            Enable NNTP pipelining
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Batch BODY requests on a single connection during queue imports and benchmarks.
                        WebDAV streaming uses the separate <strong>Pipelined article downloads</strong> toggle
                        under WebDAV settings.
                    </div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <label htmlFor="pipelining-depth" className={styles["form-label"]}>
                        Default pipeline depth
                    </label>
                    <input
                        type="text"
                        id="pipelining-depth"
                        className={`${styles["form-input"]} ${config["usenet.pipelining.depth"] !== undefined && config["usenet.pipelining.depth"] !== "" && !isPositiveInteger(config["usenet.pipelining.depth"]) ? styles.error : ""}`}
                        placeholder="8"
                        value={config["usenet.pipelining.depth"] ?? ""}
                        onChange={(e) => setNewConfig({ ...config, "usenet.pipelining.depth": e.target.value })}
                    />
                    <div className={styles["form-hint"]}>
                        Requests kept in flight per connection (1–64). 8 is a good default. Each
                        provider can override this in its own settings.
                    </div>
                </div>
            </div>

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
                onApplyPipelining={handleApplyPipelining}
                defaultPipeliningDepth={config["usenet.pipelining.depth"] || "8"}
            />
        </div>
    );
}

type UsageRowProps = {
    provider: ConnectionDetails;
    usage: ProviderUsage | undefined;
    onReset: () => void;
};

function UsageRow({ provider, usage, onReset }: UsageRowProps) {
    const limit = provider.ByteLimit ?? null;
    const used = usage?.bytesUsed ?? 0;
    const hasLimit = limit !== null && limit > 0;
    const pct = hasLimit ? Math.min(100, (used / (limit as number)) * 100) : 0;
    // Thresholds match the soft-warning levels the backend would alert on if
    // we wired notifications. Keeping the same numbers here means the colors
    // tell the same story as any future alert email or webhook.
    const tone = hasLimit
        ? (pct >= 100 ? "danger" : pct >= 95 ? "danger" : pct >= 80 ? "warn" : "ok")
        : "neutral";

    const showAnything = hasLimit || used > 0 || usage !== undefined;
    if (!showAnything) return null;

    return (
        <div className={styles["usage-row"]}>
            <div className={styles["usage-header"]}>
                <span className={styles["usage-label"]}>
                    {hasLimit ? "Data Cap" : "Data Used"}
                </span>
                <span className={styles[`usage-value-${tone}`]}>
                    {hasLimit
                        ? `${formatBytes(used)} / ${formatBytes(limit as number)}  ·  ${pct.toFixed(1)}%`
                        : formatBytes(used)}
                </span>
                <button
                    type="button"
                    className={styles["usage-reset"]}
                    onClick={onReset}
                    title="Reset the counter to zero (e.g. after buying a new block)"
                >
                    Reset
                </button>
            </div>
            {hasLimit && (
                <div className={styles["usage-bar-track"]}>
                    <div
                        className={`${styles["usage-bar-fill"]} ${styles[`usage-bar-${tone}`]}`}
                        style={{ width: `${pct}%` }}
                    />
                </div>
            )}
            {usage && usage.daysRemaining !== null && usage.daysRemaining !== undefined && !usage.overLimit && (
                <div className={styles["usage-hint"]}>
                    {formatDaysRemaining(usage.daysRemaining)}
                </div>
            )}
            {usage?.overLimit && (
                <div className={styles["usage-warning"]}>
                    Data cap reached. This provider is paused to keep in-flight fetches from overshooting. Reset the counter or raise the cap to resume.
                </div>
            )}
        </div>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
    onApplyPipelining: (enabled: boolean) => void;
    defaultPipeliningDepth: string;
};

function ProviderModal({ show, provider, onClose, onSave, onApplyPipelining, defaultPipeliningDepth }: ProviderModalProps) {
    const isEditing = provider !== null;
    const initialLimit = bytesToValueAndUnit(provider?.ByteLimit);
    const initialUsed = bytesToValueAndUnit(provider?.BytesUsedOffset);

    const [nickname, setNickname] = useState(provider?.Nickname || "");
    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [pipeliningDepth, setPipeliningDepth] = useState(provider?.PipeliningDepth?.toString() || "");
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    const [limitValue, setLimitValue] = useState(initialLimit.value);
    const [limitUnit, setLimitUnit] = useState<ByteUnitLabel>(initialLimit.unit);
    const [initialUsedValue, setInitialUsedValue] = useState(initialUsed.value);
    const [initialUsedUnit, setInitialUsedUnit] = useState<ByteUnitLabel>(initialUsed.unit);
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);
    const [intensity, setIntensity] = useState<BenchmarkIntensity>("quick");
    const [isBenchmarking, setIsBenchmarking] = useState(false);
    const [benchmarkProgress, setBenchmarkProgress] = useState<BenchmarkProgress | null>(null);
    const [benchmarkResult, setBenchmarkResult] = useState<BenchmarkResult | null>(null);
    const [benchmarkError, setBenchmarkError] = useState<string | null>(null);
    const [pipeliningOnly, setPipeliningOnly] = useState(false);
    const benchmarkAbortRef = useRef<AbortController | null>(null);
    const passIsMasked = isMaskedSecret(pass);

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            const lim = bytesToValueAndUnit(provider?.ByteLimit);
            const used = bytesToValueAndUnit(provider?.BytesUsedOffset);
            setNickname(provider?.Nickname || "");
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setPipeliningDepth(provider?.PipeliningDepth?.toString() || "");
            setType(provider?.Type ?? ProviderType.Pooled);
            setLimitValue(lim.value);
            setLimitUnit(lim.unit);
            setInitialUsedValue(used.value);
            setInitialUsedUnit(used.unit);
            setConnectionTested(false);
            setTestError(null);
            setIntensity("quick");
            setIsBenchmarking(false);
            setBenchmarkProgress(null);
            setBenchmarkResult(null);
            setBenchmarkError(null);
            setPipeliningOnly(false);
        }
    }, [show, provider]);

    // Stop any in-flight speed test when the modal closes or unmounts so it
    // aborts on the backend and frees its connections immediately.
    useEffect(() => {
        if (!show) benchmarkAbortRef.current?.abort();
    }, [show]);
    useEffect(() => () => benchmarkAbortRef.current?.abort(), []);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        if (passIsMasked) return;

        setIsTestingConnection(true);
        setTestError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass, passIsMasked]);

    const handleAutoTune = useCallback(async () => {
        // Abort any previous run still in flight before starting a new one.
        benchmarkAbortRef.current?.abort();
        const controller = new AbortController();
        benchmarkAbortRef.current = controller;

        setIsBenchmarking(true);
        setBenchmarkError(null);
        setBenchmarkResult(null);
        setBenchmarkProgress({ phase: "latency", status: "Starting speed test…", percent: 0, dataUsedBytes: 0, sweep: [] });

        // Live progress over the websocket — best-effort eye-candy; the POST
        // below returns the authoritative result regardless.
        let ws: WebSocket | null = null;
        try {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onopen = () => ws?.send(JSON.stringify(benchmarkTopic));
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== 'bench') return;
                try {
                    const update = JSON.parse(message) as BenchmarkProgress;
                    // Ignore the terminal "done" frame (incl. any replayed from a
                    // previous run) so the bar doesn't flash to 100% then restart.
                    if (update.phase !== 'done') setBenchmarkProgress(update);
                } catch { /* ignore malformed progress */ }
            });
            ws.onerror = () => ws?.close();
        } catch { /* progress is optional */ }

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);
            formData.append('max-connections', maxConnections || "10");
            formData.append('intensity', intensity);
            formData.append('pipelining-only', pipeliningOnly ? 'true' : 'false');

            const response = await fetch('/api/benchmark-usenet-connection', {
                method: 'POST', body: formData, signal: controller.signal,
            });
            if (!response.ok) {
                setBenchmarkError("The speed test couldn't run. Please try again.");
                return;
            }
            const data = await response.json();
            if (!data.status || !data.result) {
                setBenchmarkError(data.error || "The speed test couldn't run.");
                return;
            }
            setBenchmarkResult(data.result as BenchmarkResult);
            setConnectionTested(true); // a successful benchmark also proves the connection
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                // Cancelled by the user (Cancel button or closing the modal) — not an error.
                setBenchmarkProgress(null);
            } else {
                setBenchmarkError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
            }
        } finally {
            setIsBenchmarking(false);
            if (benchmarkAbortRef.current === controller) benchmarkAbortRef.current = null;
            ws?.close();
        }
    }, [host, port, useSsl, user, pass, maxConnections, intensity, pipeliningOnly]);

    const handleApplyRecommendation = useCallback(() => {
        if (!benchmarkResult) return;
        // In pipelining-only mode there's no connection recommendation, so this
        // leaves Max Connections untouched and only applies pipelining.
        if (benchmarkResult.recommendedConnections && benchmarkResult.recommendedConnections > 0) {
            setMaxConnections(String(benchmarkResult.recommendedConnections));
        }
        if (benchmarkResult.pipelining) {
            setPipeliningDepth(String(benchmarkResult.pipelining.recommendedDepth));
            onApplyPipelining(benchmarkResult.pipelining.recommendEnabled);
        }
    }, [benchmarkResult, onApplyPipelining]);

    const handleCancelBenchmark = useCallback(() => {
        benchmarkAbortRef.current?.abort();
    }, []);

    const handleSave = useCallback(() => {
        const byteLimit = valueAndUnitToBytes(limitValue, limitUnit);
        const initialUsedBytes = valueAndUnitToBytes(initialUsedValue, initialUsedUnit);

        // On a brand-new provider, an initial-used value also sets ResetAt to
        // now — otherwise the metrics rollup would count any pre-existing
        // history for the same hostname twice. On edit, leave ResetAt alone
        // (the dedicated Reset button is the right surface for that).
        const isNew = !isEditing;
        const offsetToPersist = isNew
            ? (initialUsedBytes ?? 0)
            : (provider?.BytesUsedOffset ?? 0);
        const resetAtToPersist = isNew && initialUsedBytes !== null
            ? Date.now()
            : (provider?.BytesUsedResetAt ?? 0);

        const trimmedNickname = nickname.trim();
        onSave({
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
            PipeliningDepth: pipeliningDepth.trim() === "" ? null : parseInt(pipeliningDepth, 10),
            Priority: provider?.Priority ?? 0,
            Nickname: trimmedNickname === "" ? undefined : trimmedNickname,
            PreviousType: type === ProviderType.Disabled ? provider?.PreviousType : undefined,
            ByteLimit: byteLimit,
            BytesUsedOffset: offsetToPersist,
            BytesUsedResetAt: resetAtToPersist,
        });
    }, [type, host, port, useSsl, user, pass, maxConnections, pipeliningDepth, nickname, provider, isEditing, limitValue, limitUnit, initialUsedValue, initialUsedUnit, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isPipeliningDepthValid = pipeliningDepth.trim() === ""
        || (isPositiveInteger(pipeliningDepth) && Number(pipeliningDepth) <= 64);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections)
        && isPipeliningDepthValid;

    // The speed test doesn't need Max Connections (it can recommend one), just
    // a reachable provider.
    const canBenchmark = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== "";

    const canSave = isFormValid && (connectionTested || passIsMasked || type == ProviderType.Disabled);

    if (!show) return null;

    return (
        <div className={'fixed inset-0 z-50 flex items-center justify-center bg-slate-900/80 p-4'} onClick={handleOverlayClick}>
            <div className={'max-h-[90dvh] w-full max-w-xl overflow-y-auto rounded border border-slate-700 bg-slate-900 shadow-xl'}>
                <div className={'flex items-center justify-between border-b border-slate-700 px-4 py-3'}>
                    <h2 className={'text-lg font-semibold text-white'}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={'rounded p-1 text-slate-300 hover:bg-white/10 hover:text-white'} onClick={onClose} aria-label="Close">
                        <Icon name="close" className="!text-[20px]" />
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="provider-nickname" className={styles["form-label"]}>
                                Nickname (optional)
                            </label>
                            <input
                                type="text"
                                id="provider-nickname"
                                className={styles["form-input"]}
                                placeholder="e.g. Main provider"
                                value={nickname}
                                onChange={(e) => setNickname(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                Friendly label shown in the UI in place of the hostname.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={'form-input w-full'}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={'space-y-2'}>
                            <label htmlFor="provider-port" className={'block text-sm font-medium text-slate-200'}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${'form-input w-full'} ${!isPositiveInteger(port) && port !== "" ? 'border-red-500 focus:border-red-500' : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={'space-y-2'}>
                            <label htmlFor="provider-user" className={'block text-sm font-medium text-slate-200'}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={'form-input w-full'}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={'space-y-2'}>
                            <label htmlFor="provider-pass" className={'block text-sm font-medium text-slate-200'}>
                                Password
                            </label>
                            <input
                                type="password"
                                id="provider-pass"
                                className={'form-input w-full'}
                                placeholder="password"
                                value={pass}
                                onChange={(e) => {
                                    setPass(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={'space-y-2'}>
                            <label htmlFor="provider-max-connections" className={'block text-sm font-medium text-slate-200'}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${'form-input w-full'} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? 'border-red-500 focus:border-red-500' : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pipelining-depth" className={styles["form-label"]}>
                                Pipeline depth
                            </label>
                            <input
                                type="text"
                                id="provider-pipelining-depth"
                                className={`${styles["form-input"]} ${!isPipeliningDepthValid ? styles.error : ""}`}
                                placeholder={defaultPipeliningDepth || "8"}
                                value={pipeliningDepth}
                                onChange={(e) => setPipeliningDepth(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                Requests kept in flight per connection (1–64) when NNTP pipelining is
                                enabled. Leave blank to use the global default.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={'form-select w-full'}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pool Connections</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                            </select>
                        </div>


                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={styles["form-checkbox"]}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        setConnectionTested(false);
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={'text-sm text-slate-300'}>
                                    Use SSL
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label className={styles["form-label"]}>
                                Data Cap (optional)
                            </label>
                            <div className={styles["form-paired-input"]}>
                                <input
                                    type="text"
                                    inputMode="decimal"
                                    className={styles["form-input"]}
                                    placeholder="Leave blank for no cap"
                                    value={limitValue}
                                    onChange={(e) => setLimitValue(e.target.value)}
                                />
                                <select
                                    className={styles["form-select"]}
                                    value={limitUnit}
                                    onChange={(e) => setLimitUnit(e.target.value as ByteUnitLabel)}
                                >
                                    {BYTE_UNITS.map(u => (
                                        <option key={u.label} value={u.label}>{u.label}</option>
                                    ))}
                                </select>
                            </div>
                            <div className={styles["form-hint"]}>
                                For block accounts: total bytes you've purchased. The provider auto-pauses at ~95% of this value to absorb in-flight requests, so set the cap to your full block size. The 5% headroom keeps you from overshooting.
                            </div>
                        </div>

                        {!isEditing && (
                            <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                <label className={styles["form-label"]}>
                                    Already Used (optional)
                                </label>
                                <div className={styles["form-paired-input"]}>
                                    <input
                                        type="text"
                                        inputMode="decimal"
                                        className={styles["form-input"]}
                                        placeholder="0"
                                        value={initialUsedValue}
                                        onChange={(e) => setInitialUsedValue(e.target.value)}
                                    />
                                    <select
                                        className={styles["form-select"]}
                                        value={initialUsedUnit}
                                        onChange={(e) => setInitialUsedUnit(e.target.value as ByteUnitLabel)}
                                    >
                                        {BYTE_UNITS.map(u => (
                                            <option key={u.label} value={u.label}>{u.label}</option>
                                        ))}
                                    </select>
                                </div>
                                <div className={styles["form-hint"]}>
                                    Seed the counter when migrating a partially-used block from another client. Leave empty for a fresh block.
                                </div>
                            </div>
                        )}
                    </div>

                    {testError && (
                        <div role="alert" className="mt-4 rounded border border-red-600/50 bg-red-500/10 px-3 py-2 text-xs text-red-200">
                            {testError}
                        </div>
                    )}

                    {connectionTested && (
                        <div role="status" className="mt-4 rounded border border-emerald-600/50 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-200">
                            Connection test successful!
                        </div>
                    )}

                    <BenchmarkPanel
                        canBenchmark={canBenchmark}
                        isBenchmarking={isBenchmarking}
                        intensity={intensity}
                        setIntensity={setIntensity}
                        pipeliningOnly={pipeliningOnly}
                        setPipeliningOnly={setPipeliningOnly}
                        progress={benchmarkProgress}
                        result={benchmarkResult}
                        error={benchmarkError}
                        onRun={handleAutoTune}
                        onCancel={handleCancelBenchmark}
                        onApply={handleApplyRecommendation}
                    />
                </div>

                <div className={'flex justify-end border-t border-slate-700 px-4 py-3'}>
                    <div className={'hidden'}></div>
                    <div className={'flex gap-2'}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!canSave ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

type BenchmarkPanelProps = {
    canBenchmark: boolean;
    isBenchmarking: boolean;
    intensity: BenchmarkIntensity;
    setIntensity: (value: BenchmarkIntensity) => void;
    pipeliningOnly: boolean;
    setPipeliningOnly: (value: boolean) => void;
    progress: BenchmarkProgress | null;
    result: BenchmarkResult | null;
    error: string | null;
    onRun: () => void;
    onCancel: () => void;
    onApply: () => void;
};

function BenchmarkPanel(props: BenchmarkPanelProps) {
    const {
        canBenchmark, isBenchmarking, intensity, setIntensity,
        pipeliningOnly, setPipeliningOnly, progress, result, error, onRun, onCancel, onApply,
    } = props;
    const [applied, setApplied] = useState(false);
    // A fresh result means the previous "Applied" state no longer holds.
    useEffect(() => { setApplied(false); }, [result]);

    const recommended = result?.recommendedConnections ?? null;
    const livePoints = isBenchmarking ? (progress?.sweep ?? []) : (result?.sweep ?? []);
    const bestSpeed = result?.throughputTested && result.sweep.length > 0
        ? Math.max(...result.sweep.map(p => p.mbPerSec))
        : null;
    const pipe = result?.pipelining ?? null;
    const pipeBest = pipe && pipe.tested.length > 0 ? Math.max(...pipe.tested.map(t => t.mbPerSec)) : (pipe?.baselineMbPerSec ?? 0);
    const pipeGainPct = pipe && pipe.baselineMbPerSec > 0 ? Math.round((pipeBest / pipe.baselineMbPerSec - 1) * 100) : 0;
    const canApply = !!result && result.throughputTested && (recommended != null || (result.pipeliningOnly && !!pipe));

    return (
        <div className={styles["bench-panel"]}>
            <div className={styles["bench-head"]}>
                <div className={styles["bench-heading"]}>
                    <div className={styles["bench-title"]}>Auto-tune connections</div>
                    <div className={styles["form-hint"]} style={{ marginTop: 0 }}>
                        {pipeliningOnly
                            ? "Keeps your Max Connections and just measures the best NNTP pipelining depth at that count."
                            : "Runs a real speed & latency test, then recommends the best connection count and pipelining settings."}
                    </div>
                </div>
                <div className={styles["bench-controls"]}>
                    <div className={styles["bench-intensity"]} role="group" aria-label="Test intensity">
                        <Button
                            variant={intensity === "quick" ? "primary" : "secondary"}
                            onClick={() => setIntensity("quick")}
                            disabled={isBenchmarking}
                            aria-pressed={intensity === "quick"}
                        >
                            Quick
                        </Button>
                        <Button
                            variant={intensity === "thorough" ? "primary" : "secondary"}
                            onClick={() => setIntensity("thorough")}
                            disabled={isBenchmarking}
                            aria-pressed={intensity === "thorough"}
                        >
                            Thorough
                        </Button>
                    </div>
                    <Button variant="primary" onClick={onRun} disabled={!canBenchmark || isBenchmarking}>
                        {isBenchmarking ? "Testing…" : (pipeliningOnly ? "Test pipelining" : "Run speed test")}
                    </Button>
                    {isBenchmarking && (
                        <Button variant="secondary" onClick={onCancel}>Cancel</Button>
                    )}
                </div>
            </div>

            <div className={styles["form-checkbox-wrapper"]} style={{ marginTop: 12 }}>
                <input
                    type="checkbox"
                    id="bench-pipe-only"
                            className={styles["form-checkbox"]}
                    checked={pipeliningOnly}
                    disabled={isBenchmarking}
                    onChange={(e) => setPipeliningOnly(e.target.checked)}
                />
                <label htmlFor="bench-pipe-only" className={styles["form-checkbox-label"]}>
                    Only tune pipelining (keep my Max Connections)
                </label>
            </div>

            <div className={styles["form-hint"]}>
                {pipeliningOnly
                    ? "Won't change your connection count — it tests pipelining depth at the Max Connections you've set. Run it idle for the cleanest read."
                    : (intensity === "quick"
                        ? "Quick downloads roughly 100 MB of real data — light on metered / block accounts."
                        : "Thorough downloads roughly 400 MB for steadier numbers on fast connections.")}
            </div>

            {error && (
                <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: 12 }}>{error}</div>
            )}

            {isBenchmarking && progress && (
                <div className={styles["bench-progress"]}>
                    <div className={styles["bench-progress-head"]}>
                        <span>{progress.status}</span>
                        <span>{formatBytes(progress.dataUsedBytes)} used</span>
                    </div>
                    <div className={styles["usage-bar-track"]}>
                        <div
                            className={`${styles["usage-bar-fill"]} ${styles["bench-progress-fill"]}`}
                            style={{ width: `${Math.max(2, Math.min(100, progress.percent))}%` }}
                        />
                    </div>
                </div>
            )}

            {livePoints.length > 0 && !(isBenchmarking ? pipeliningOnly : result?.pipeliningOnly) && (
                <SweepChart points={livePoints} recommended={recommended} />
            )}

            {result && !isBenchmarking && (
                <>
                    {result.pipeliningOnly ? (
                        pipe ? (
                            <>
                                <DepthChart pipe={pipe} />
                                <div className={styles["bench-stats"]}>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Pipelining</span>
                                        <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>
                                            {pipe.recommendEnabled ? `Depth ${pipe.recommendedDepth}` : "Off"}
                                        </span>
                                        <span className={styles["bench-stat-sub"]}>
                                            {pipe.recommendEnabled ? `≈ +${pipeGainPct}% vs off` : "no real gain"}
                                        </span>
                                    </div>
                                    {result.latency && (
                                        <div className={styles["bench-stat"]}>
                                            <span className={styles["bench-stat-label"]}>Latency</span>
                                            <span className={styles["bench-stat-value"]}>{result.latency.avgMs} ms</span>
                                            <span className={styles["bench-stat-sub"]}>{result.latency.minMs} ms min</span>
                                        </div>
                                    )}
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Tested at</span>
                                        <span className={styles["bench-stat-value"]}>{pipe.testedAtConnections}</span>
                                        <span className={styles["bench-stat-sub"]}>connections</span>
                                    </div>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Data used</span>
                                        <span className={styles["bench-stat-value"]}>{formatBytes(result.dataUsedBytes)}</span>
                                    </div>
                                </div>
                                <div className={styles["bench-pipe"]}>
                                    {pipe.recommendEnabled
                                        ? <>Turn on <strong>NNTP pipelining</strong> at depth <strong>{pipe.recommendedDepth}</strong> — measurably faster at your {pipe.testedAtConnections} connections.</>
                                        : <>NNTP pipelining didn’t help at your {pipe.testedAtConnections} connections — leave it off.</>}
                                </div>
                            </>
                        ) : (
                            <div className={styles["bench-note"]}>
                                Couldn’t measure pipelining{result.latency ? ` (latency ${result.latency.avgMs} ms)` : ""}. Try again when idle.
                            </div>
                        )
                    ) : result.throughputTested && recommended ? (
                        <div className={styles["bench-stats"]}>
                            <div className={styles["bench-stat"]}>
                                <span className={styles["bench-stat-label"]}>Recommended</span>
                                <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>{recommended}</span>
                                <span className={styles["bench-stat-sub"]}>
                                    connection{recommended === 1 ? "" : "s"}{bestSpeed != null ? ` · ≈ ${bestSpeed.toFixed(1)} MB/s` : ""}
                                </span>
                            </div>
                            {result.latency && (
                                <div className={styles["bench-stat"]}>
                                    <span className={styles["bench-stat-label"]}>Latency</span>
                                    <span className={styles["bench-stat-value"]}>{result.latency.avgMs} ms</span>
                                    <span className={styles["bench-stat-sub"]}>{result.latency.minMs} ms min</span>
                                </div>
                            )}
                            {result.providerConnectionCap != null && (
                                <div className={styles["bench-stat"]}>
                                    <span className={styles["bench-stat-label"]}>Provider cap</span>
                                    <span className={styles["bench-stat-value"]}>{result.providerConnectionCap}</span>
                                    <span className={styles["bench-stat-sub"]}>max at once</span>
                                </div>
                            )}
                            <div className={styles["bench-stat"]}>
                                <span className={styles["bench-stat-label"]}>Data used</span>
                                <span className={styles["bench-stat-value"]}>{formatBytes(result.dataUsedBytes)}</span>
                            </div>
                        </div>
                    ) : (
                        <div className={styles["bench-note"]}>
                            Latency measured{result.latency ? ` — ${result.latency.avgMs} ms avg` : ""}. Download something first to get a connection recommendation.
                        </div>
                    )}

                    {!result.pipeliningOnly && pipe && (
                        <div className={styles["bench-pipe"]}>
                            {pipe.recommendEnabled
                                ? <>Turn on <strong>NNTP pipelining</strong> at depth <strong>{pipe.recommendedDepth}</strong> — measurably faster on this connection.</>
                                : <>NNTP pipelining didn’t help here — leave it off.</>}
                        </div>
                    )}

                    {result.warnings.length > 0 && (
                        <ul className={styles["bench-warnings"]}>
                            {result.warnings.map((w, i) => <li key={i}>{w}</li>)}
                        </ul>
                    )}

                    {canApply && (
                        <div className={styles["bench-actions"]}>
                            <Button variant={applied ? "secondary" : "primary"} onClick={() => { onApply(); setApplied(true); }}>
                                {applied ? "Applied ✓ — review & save" : (result.pipeliningOnly ? "Apply pipelining" : "Apply recommendation")}
                            </Button>
                        </div>
                    )}
                </>
            )}
        </div>
    );
}

function SweepChart({ points, recommended }: { points: BenchmarkSweepPoint[]; recommended: number | null }) {
    const max = Math.max(...points.map(p => p.mbPerSec), 0.0001);
    return (
        <div className={styles["bench-chart"]}>
            <div className={styles["bench-chart-bars"]}>
                {points.map((p, i) => {
                    const isRec = recommended != null && p.connections === recommended;
                    const height = Math.max(4, Math.round((p.mbPerSec / max) * 104));
                    return (
                        <div key={i} className={`${styles["bench-chart-col"]} ${isRec ? styles["bench-chart-col-rec"] : ""}`}>
                            <span className={styles["bench-chart-val"]}>
                                {p.mbPerSec >= 10 ? p.mbPerSec.toFixed(0) : p.mbPerSec.toFixed(1)}
                            </span>
                            <div
                                className={styles["bench-chart-bar"]}
                                style={{ height: `${height}px` }}
                                title={`${p.connections} connections → ${p.mbPerSec.toFixed(1)} MB/s`}
                            />
                            <span className={styles["bench-chart-label"]}>{p.connections}</span>
                        </div>
                    );
                })}
            </div>
            <div className={styles["bench-chart-foot"]}>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>MB/s by connection count</span>
                {recommended != null && <span className={styles["form-hint"]} style={{ margin: 0 }}>recommended: {recommended}</span>}
            </div>
        </div>
    );
}

function DepthChart({ pipe }: { pipe: BenchmarkPipelining }) {
    const points = [
        { label: "Off", mbPerSec: pipe.baselineMbPerSec, rec: !pipe.recommendEnabled },
        ...pipe.tested.map(t => ({
            label: String(t.depth),
            mbPerSec: t.mbPerSec,
            rec: pipe.recommendEnabled && t.depth === pipe.recommendedDepth,
        })),
    ];
    const max = Math.max(...points.map(p => p.mbPerSec), 0.0001);
    return (
        <div className={styles["bench-chart"]}>
            <div className={styles["bench-chart-bars"]}>
                {points.map((p, i) => {
                    const height = Math.max(4, Math.round((p.mbPerSec / max) * 104));
                    return (
                        <div key={i} className={`${styles["bench-chart-col"]} ${p.rec ? styles["bench-chart-col-rec"] : ""}`}>
                            <span className={styles["bench-chart-val"]}>
                                {p.mbPerSec >= 10 ? p.mbPerSec.toFixed(0) : p.mbPerSec.toFixed(1)}
                            </span>
                            <div
                                className={styles["bench-chart-bar"]}
                                style={{ height: `${height}px` }}
                                title={`${p.label} → ${p.mbPerSec.toFixed(1)} MB/s`}
                            />
                            <span className={styles["bench-chart-label"]}>{p.label}</span>
                        </div>
                    );
                })}
            </div>
            <div className={styles["bench-chart-foot"]}>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>MB/s by pipeline depth</span>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>
                    {pipe.recommendEnabled ? `best: depth ${pipe.recommendedDepth}` : "best: off"}
                </span>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
        || config["usenet.pipelining.enabled"] !== newConfig["usenet.pipelining.enabled"]
        || config["usenet.pipelining.depth"] !== newConfig["usenet.pipelining.depth"]
        || config["usenet.cascade.enabled"] !== newConfig["usenet.cascade.enabled"]
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}