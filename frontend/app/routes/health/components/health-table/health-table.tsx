import type { HealthCheckQueueItem } from "~/clients/backend-client.server";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { Badge, Icon } from "~/components/ui";

export type HealthTableProps = {
    isEnabled: boolean,
    healthCheckItems: HealthCheckQueueItem[],
}

export function HealthTable({ isEnabled, healthCheckItems }: HealthTableProps) {

    return (
        <section className="w-full overflow-hidden rounded-lg border border-base-content/10 bg-base-100 shadow-md">
            <div className="flex flex-wrap items-center justify-between gap-4 p-4 md:p-6">
                <h2 className="text-xl font-semibold text-base-content">Schedule</h2>
                <Badge className="px-3 py-1 text-xs text-base-content/60">
                    Only {healthCheckItems.length} shown
                </Badge>
            </div>

            {!isEnabled ? (
                <div className="px-5 py-14 text-center text-base-content/60">
                    <Icon name="health_and_safety" className="mb-4 !text-[48px] text-primary/70" />
                    <div className="mb-2 text-base font-semibold text-base-content">Enable Repairs In Settings</div>
                    <div className="mx-auto max-w-md text-sm leading-relaxed">
                        Once you enable repairs, all mounted usenet files will be queued for continuous health monitoring
                    </div>
                </div>
            ) : healthCheckItems.length === 0 ? (
                <div className="px-5 py-14 text-center text-base-content/60">
                    <Icon name="health_and_safety" className="mb-4 !text-[48px] text-primary/70" />
                    <div className="mb-2 text-base font-semibold text-base-content">No Items To Health-Check</div>
                    <div className="mx-auto max-w-md text-sm leading-relaxed">
                        Once you begin processing nzbs, the mounted usenet files will be queued for continuous health monitoring
                    </div>
                </div>
            ) : (
                <div className="min-h-[200px] overflow-x-auto">
                    <table className="m-0 w-full border-collapse text-base-content/80">
                        <thead className="max-[899px]:hidden">
                            <tr>
                                <th className="w-1/2 bg-base-300/70 px-6 py-4 text-left text-xs font-semibold uppercase tracking-wide text-base-content/80">Name</th>
                                <th className="w-[100px] whitespace-nowrap bg-base-300/70 px-3 py-4 text-center text-xs font-semibold uppercase tracking-wide text-base-content/80">Created</th>
                                <th className="w-[100px] whitespace-nowrap bg-base-300/70 px-3 py-4 text-center text-xs font-semibold uppercase tracking-wide text-base-content/80">Last Check</th>
                                <th className="w-[100px] whitespace-nowrap bg-base-300/70 px-3 py-4 text-center text-xs font-semibold uppercase tracking-wide text-base-content/80">Next Check</th>
                            </tr>
                        </thead>
                        <tbody>
                            {healthCheckItems.map(item => (
                                <tr key={item.id} className="border-b border-base-content/10 last:border-b-0">
                                    <td className="max-w-[200px] px-6 py-4 align-middle max-[899px]:max-w-none max-[899px]:p-6">
                                        <div className="flex min-w-0 flex-col gap-1">
                                            <div className="break-all text-sm font-medium leading-snug text-base-content"><Truncate>{item.name}</Truncate></div>
                                            <div className="break-all text-xs italic leading-snug text-base-content/50"><Truncate>{item.path}</Truncate></div>
                                            <div className="hidden max-[899px]:block">
                                                <DateDetailsTable item={item} />
                                            </div>
                                        </div>
                                    </td>
                                    <td className="max-w-[200px] px-3 py-4 text-center text-xs tabular-nums text-base-content/60 max-[899px]:hidden">
                                        {formatAge(item.releaseDate, 'Unknown')}
                                    </td>
                                    <td className="max-w-[200px] px-3 py-4 text-center text-xs tabular-nums text-base-content/60 max-[899px]:hidden">
                                        {formatAge(item.lastHealthCheck, 'Never')}
                                    </td>
                                    <td className="max-w-[200px] px-3 py-4 text-center text-xs tabular-nums text-base-content/60 max-[899px]:hidden">
                                        {item.progress > 0
                                            ? <HealthProgressBadge percentage={item.progress} />
                                            : formatWhen(item.nextHealthCheck, 'ASAP')
                                        }
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </section>
    );
}

function DateDetailsTable({ item }: { item: HealthCheckQueueItem }) {
    return (
        <div className="mt-3 rounded-md border border-base-content/10 bg-base-300/50 p-3">
            <div className="flex items-center justify-between border-b border-base-content/10 py-2 first:pt-0">
                <div className="min-w-20 text-[11px] font-medium uppercase tracking-wide text-base-content/60">Created</div>
                <div className="ml-3 flex-1 text-right text-xs tabular-nums text-base-content">
                    {formatAge(item.releaseDate, 'Unknown')}
                </div>
            </div>
            <div className="flex items-center justify-between border-b border-base-content/10 py-2">
                <div className="min-w-20 text-[11px] font-medium uppercase tracking-wide text-base-content/60">Last Health Check</div>
                <div className="ml-3 flex-1 text-right text-xs tabular-nums text-base-content">
                    {formatAge(item.lastHealthCheck, 'Never')}
                </div>
            </div>
            <div className="flex items-center justify-between pt-2">
                <div className="min-w-20 text-[11px] font-medium uppercase tracking-wide text-base-content/60">Next Health Check</div>
                <div className="ml-3 flex-1 text-right text-xs tabular-nums text-base-content">
                    {item.progress > 0
                        ? <HealthProgressBadge percentage={item.progress} />
                        : formatWhen(item.nextHealthCheck, 'ASAP')
                    }
                </div>
            </div>
        </div>
    );
}

function formatAge(dateString: string | null, fallback: string) {
    if (!dateString) return fallback;
    const age = Math.max(0, Math.floor((Date.now() - new Date(dateString).getTime()) / 1000));
    if (Number.isNaN(age)) return fallback;
    if (age < 5) return 'just now';
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

function formatWhen(dateString: string | null, fallback: string) {
    if (!dateString) return fallback;
    const delta = Math.floor((new Date(dateString).getTime() - Date.now()) / 1000);
    if (Number.isNaN(delta)) return fallback;
    if (delta <= 0) return 'soon';
    if (delta < 60) return `in ${delta}s`;
    if (delta < 3600) return `in ${Math.floor(delta / 60)}m`;
    if (delta < 86400) return `in ${Math.floor(delta / 3600)}h`;
    return `in ${Math.floor(delta / 86400)}d`;
}

function HealthProgressBadge({ percentage }: { percentage: number }) {
    const progress = Math.max(0, Math.min(percentage, 100));
    return (
        <span className="relative inline-block w-[85px] overflow-hidden rounded-full border border-primary/40 bg-base-300/70 px-1.5 py-0.5 font-mono text-xs text-base-content">
            <span className="absolute inset-y-0 left-0 bg-primary/40" style={{ width: `${progress}%` }} />
            <span className="relative">{percentage}%</span>
        </span>
    );
}
