import { useEffect, useState } from "react";
import styles from "./live-reads.module.css";
import { receiveMessage } from "~/utils/websocket-util";

const activeReadsTopic = { ar: 'state' };

type ProviderUsage = { host: string; nickname?: string | null; segments: number };
type Read = {
    id: string;
    fileName: string;
    path: string;
    startedAt: number;
    lastActivityAt: number;
    bytesRead: number;
    fileSize: number | null;
    providers: ProviderUsage[];
};
type Snapshot = { reads: Read[] };

const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "—";
    const labels = host.split(".").filter(Boolean);
    if (labels.length === 0) return host;
    if (labels.length === 1) return labels[0];
    if (labels.length === 2) return labels[0];
    if (GENERIC_HOST_PREFIXES.has(labels[0].toLowerCase())) return labels[1];
    return labels[0].length >= labels[1].length ? labels[0] : labels[1];
}

function shortName(name: string): string {
    if (!name) return "—";
    const max = 28;
    return name.length <= max ? name : name.slice(0, max - 1) + "…";
}

export function LiveReads() {
    const [snapshot, setSnapshot] = useState<Snapshot | null>(null);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => {
                try { setSnapshot(JSON.parse(message)); }
                catch { /* ignore malformed frames */ }
            });
            ws.onopen = () => ws.send(JSON.stringify(activeReadsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code === 1008) {
                globalThis.location.assign("/login");
                return;
            }
            !disposed && setTimeout(() => connect(), 1000);
            setSnapshot(null);
        }
        return connect();
    }, []);

    const reads = snapshot?.reads ?? [];
    if (reads.length === 0) return null;

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Active Reads
            </div>
            <div className={styles.list}>
                {reads.map(r => <ReadRow key={r.id} item={r} />)}
            </div>
        </div>
    );
}

function ReadRow({ item }: { item: Read }) {
    const totalSegments = item.providers.reduce((acc, p) => acc + p.segments, 0);
    return (
        <div className={styles.row} title={item.fileName}>
            <div className={styles.fileName}>{shortName(item.fileName)}</div>
            <div className={styles.providers}>
                {item.providers.length === 0
                    ? <span className={styles.providersIdle}>buffering…</span>
                    : item.providers.map((p, i) => (
                        <span key={p.host} className={styles.providersEntry}>
                            {i > 0 && <span className={styles.providersSep}>·</span>}
                            <span className={styles.providersHost} title={p.host}>{p.nickname?.trim() || stripHost(p.host)}</span>
                            {totalSegments > 0 && (
                                <span className={styles.providersPct}>
                                    {Math.round((p.segments / totalSegments) * 100)}%
                                </span>
                            )}
                        </span>
                    ))}
            </div>
        </div>
    );
}
