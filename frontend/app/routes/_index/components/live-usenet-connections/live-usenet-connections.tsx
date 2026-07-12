import { useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Icon } from "~/components/ui";

const usenetConnectionsTopic = {'cxs': 'state'};

type LiveUsenetConnectionsProps = {
    hasUsenetProviders: boolean,
};

export function LiveUsenetConnections({ hasUsenetProviders }: LiveUsenetConnectionsProps) {
    const [connections, setConnections] = useState<string | null>(null);
    const parts = (connections || "0|0|0|0|1|0").split("|");
    const [_0, _1, _2, live, max, idle] = parts.map(x => Number(x));
    const active = live - idle;
    const activePercent = 100 * (active / max);
    const livePercent = 100 * (live / max);

    useEffect(() => {
        if (!hasUsenetProviders) {
            setConnections(null);
            return;
        }

        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => setConnections(message));
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) {
                globalThis.location.assign("/login");
                return;
            }
            !disposed && setTimeout(() => connect(), 1000);
            setConnections(null);
        }
        return connect();
    }, [hasUsenetProviders]);

    return (
        <section className="mt-6 rounded border border-slate-700/70 bg-slate-800/50 p-3">
            <div className="mb-2 flex items-center gap-2 text-[11px] uppercase tracking-wide text-slate-500">
                <Icon name="cloud" className="!text-[16px]" />
                Usenet Connections
            </div>
            {hasUsenetProviders && (
                <div className="relative mb-2 h-1 overflow-hidden rounded-full bg-slate-700">
                    <div
                        className="absolute inset-y-0 left-0 bg-emerald-500 transition-all duration-300"
                        style={{ width: `${livePercent}%` }}
                    />
                    <div
                        className="absolute inset-y-0 left-0 bg-blue-500 transition-all duration-300"
                        style={{ width: `${activePercent}%` }}
                    />
                </div>
            )}
            <div className="text-xs text-slate-400">
                {!hasUsenetProviders && "No providers configured"}
                {hasUsenetProviders && connections && `${live} connected / ${max} max`}
                {hasUsenetProviders && !connections && (
                    <span className="flex items-center gap-1.5">
                        <Icon name="progress_activity" className="animate-spin !text-[14px]" />
                        Connecting
                    </span>
                )}
            </div>
            {hasUsenetProviders && connections && (
                <div className="mt-1 font-mono text-[11px] text-slate-500">{active} active</div>
            )}
        </section>
    );
}