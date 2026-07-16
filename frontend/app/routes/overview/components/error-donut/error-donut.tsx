import { useMemo, useState } from "react";
import styles from "./error-donut.module.css";
import type { ErrorSlice } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type ErrorDonutProps = {
    errors: ErrorSlice[],
}

// Severity-aware muted palette. The two grays are for non-critical signals
// (Missing is the most common and just means "article wasn't on the first
// provider"); the rest escalate toward real problems.
const COLORS: Record<string, string> = {
    Missing: "#71717a",  // zinc-500 — info / common
    Other:   "#52525b",  // zinc-600 — unknown
    Timeout: "#ca8a04",  // amber-600 — warning
    Corrupt: "#9333ea",  // purple-600 — distinct
    Network: "#dc2626",  // red-600 — real network issue
    Auth:    "#0284c7",  // sky-600 — blocking / config
};
const DEFAULT_COLOR = "#525252";

const HARD_FAILURE_STATUSES = new Set(["Timeout", "Corrupt", "Auth", "Network", "Other"]);

export function ErrorBreakdown({ errors }: ErrorDonutProps) {
    const [hover, setHover] = useState<string | null>(null);

    const { hardFailures, missTotal, hardTotal } = useMemo(() => {
        const hardFailures = errors
            .filter(e => e.status !== "Missing")
            .map(e => ({
                ...e,
                color: COLORS[e.status] ?? DEFAULT_COLOR,
            }));
        const missTotal = errors
            .filter(e => e.status === "Missing")
            .reduce((s, e) => s + e.count, 0);
        const hardTotal = hardFailures.reduce((s, e) => s + e.count, 0);
        return { hardFailures, missTotal, hardTotal };
    }, [errors]);

    const hardSegments = useMemo(() => {
        return hardFailures.map(e => ({
            ...e,
            fraction: hardTotal > 0 ? e.count / hardTotal : 0,
        }));
    }, [hardFailures, hardTotal]);

    const allClear = missTotal === 0 && hardTotal === 0;

    return (
        <section className="card w-full min-w-0 overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-3 p-4">
            <div>
                <h3 className="card-title text-base">Error breakdown</h3>
                <p className="text-xs text-base-content/50">Hard failures vs expected provider misses</p>
            </div>

            {allClear ? (
                <div className={styles.allClear}>
                    <div className={styles.allClearDot} />
                    <div>
                        <div className={styles.allClearTitle}>All clear</div>
                        <div className={styles.allClearSub}>No fetch errors in this window.</div>
                    </div>
                </div>
            ) : (
                <>
                    <div className={styles.headlineRow}>
                        <div className={styles.headline}>
                            <span className={styles.headlineCount}>{formatNumber(hardTotal)}</span>
                            <span className={styles.headlineLabel}>{hardTotal === 1 ? "error" : "errors"}</span>
                        </div>
                        {missTotal > 0 && (
                            <div className={styles.headlineMuted}>
                                <span className={styles.headlineCountMuted}>{formatNumber(missTotal)}</span>
                                <span className={styles.headlineLabel}>provider misses</span>
                            </div>
                        )}
                    </div>

                    {hardTotal > 0 ? (
                        <>
                            <div
                                className={styles.stack}
                                onMouseLeave={() => setHover(null)}
                                role="img"
                                aria-label={`${hardTotal} hard fetch errors broken down by type`}
                            >
                                {hardSegments.map(s => (
                                    <div
                                        key={s.status}
                                        className={`${styles.stackSeg} ${hover && hover !== s.status ? styles.stackSegDim : ""}`}
                                        style={{
                                            flex: s.count,
                                            background: s.color,
                                        }}
                                        onMouseEnter={() => setHover(s.status)}
                                        title={`${s.status}: ${formatNumber(s.count)} (${formatPercent(s.fraction * 100, 1)})`}
                                    />
                                ))}
                            </div>

                            <ul className={styles.legend}>
                                {hardSegments.map(s => (
                                    <li
                                        key={s.status}
                                        className={`${styles.legendItem} ${hover === s.status ? styles.legendActive : ""}`}
                                        onMouseEnter={() => setHover(s.status)}
                                        onMouseLeave={() => setHover(null)}
                                    >
                                        <span className={styles.swatch} style={{ background: s.color }} />
                                        <span className={styles.legendLabel}>{s.status}</span>
                                        <span className={styles.legendCount}>{formatNumber(s.count)}</span>
                                        <span className={styles.legendPct}>{formatPercent(s.fraction * 100, 0)}</span>
                                    </li>
                                ))}
                            </ul>
                        </>
                    ) : (
                        <div className={styles.noHardErrors}>
                            No hard fetch failures — misses above are expected failover.
                        </div>
                    )}

                    {missTotal > 0 && (
                        <div className={styles.missNote}>
                            <span className={styles.swatch} style={{ background: COLORS.Missing }} />
                            <span>
                                Provider misses are articles not found on the first provider tried;
                                failover usually recovers them.
                            </span>
                        </div>
                    )}
                </>
            )}
            </div>
        </section>
    );
}

// Backwards-compatible export so the existing import name keeps working.
export { ErrorBreakdown as ErrorDonut };

// Exported for tests — hard failures exclude Missing.
export function isHardFailureStatus(status: string): boolean {
    return HARD_FAILURE_STATUSES.has(status);
}
