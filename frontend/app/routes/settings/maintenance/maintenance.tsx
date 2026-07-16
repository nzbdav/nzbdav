import { SettingsPage } from "~/components/ui";
import { Checkbox, Input, Select } from "~/components/ui/form";
import { RemoveUnlinkedFiles } from "./remove-unlinked-files/remove-unlinked-files";
import { RenameWindowsInvalidDavPaths } from "./rename-windows-invalid-dav-paths/rename-windows-invalid-dav-paths";
import { ConvertStrmToSymlinks } from "./strm-to-symlinks/strm-to-symlinks";
import { RecreateStrmFiles } from "./recreate-strm-files/recreate-strm-files";
import { MigrateDatabaseFilesToBlobstore } from "./migrate-database-files-to-blobstore/migrate-database-files-to-blobstore";
import { ResetHealthCheckStats } from "./reset-health-check-stats/reset-health-check-stats";
import type { Dispatch, SetStateAction } from "react";

type MaintenanceProps = {
    savedConfig: Record<string, string>,
    config: Record<string, string>,
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>,
};

export function Maintenance({ savedConfig, config, setNewConfig }: MaintenanceProps) {
    return (
        <SettingsPage>
                <div className="space-y-2">
                    <label className="flex items-center gap-2 text-sm text-base-content/80">
                    <Checkbox
                        id="db-startup-vacuum-enabled-checkbox"
                        aria-describedby="db-startup-vacuum-enabled-help"
                        checked={config["db.is-startup-vacuum-enabled"] === "true"}
                        onChange={e => setNewConfig({ ...config, "db.is-startup-vacuum-enabled": "" + e.target.checked })}  />
                    <span>Perform Database Vacuum on Start</span>
                </label>
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="db-startup-vacuum-enabled-help">
                        When enabled, NzbDAV will run a SQLite VACUUM on the database at every startup. This reclaims unused disk space and can improve query performance over time, but may increase startup time for large databases.
                    </p>
                </div>
                <hr />
                <div className="space-y-2">
                    <label className="block text-sm text-base-content/80" htmlFor="history-retention-days">
                        SAB History Retention (days)
                    </label>
                    <Input
                        id="history-retention-days"
                        type="number"
                        min={0}
                        aria-describedby="history-retention-days-help"
                        value={config["database.history-retention-days"] ?? "90"}
                        onChange={e => setNewConfig({
                            ...config,
                            "database.history-retention-days": e.target.value,
                        })}
                        className="max-w-xs"
                    />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="history-retention-days-help">
                        Automatically prune SAB history rows older than this many days.
                        Pruning unlinks mounts from SAB history but does not delete WebDAV files —
                        they remain under /content until you delete them (or Remove Orphaned Files
                        removes items with no library symlink/STRM). History pruning alone does not
                        make items eligible for orphan removal. Set to 0 to keep everything.
                        Can also be set with DATABASE_HISTORY_RETENTION_DAYS.
                    </p>
                </div>
                <hr />
                <div className="space-y-2">
                    <label className="block text-sm text-base-content/80" htmlFor="healthcheck-retention-days">
                        Health-Check History Retention (days)
                    </label>
                    <Input
                        id="healthcheck-retention-days"
                        type="number"
                        min={0}
                        aria-describedby="healthcheck-retention-days-help"
                        value={config["database.healthcheck-retention-days"] ?? "30"}
                        onChange={e => setNewConfig({
                            ...config,
                            "database.healthcheck-retention-days": e.target.value,
                        })}
                        className="max-w-xs"
                    />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="healthcheck-retention-days-help">
                        Automatically prune health-check result rows older than this many days. Set to 0 to keep everything.
                        Can also be set with the DATABASE_HEALTHCHECK_RETENTION_DAYS environment variable.
                    </p>
                </div>
                <hr />
                <div className="space-y-2">
                    <label className="flex items-center gap-2 text-sm text-base-content/80">
                    <Checkbox
                        id="remove-orphaned-schedule-enabled-checkbox"
                        aria-describedby="remove-orphaned-schedule-help"
                        checked={isScheduledOrphanTaskEnabled(config)}
                        onChange={e => setNewConfig({ ...config, "maintenance.remove-orphaned-schedule-enabled": "" + e.target.checked })}  />
                    <span>{'Schedule "Remove Orphaned Files" Task Daily'}</span>
                </label>
                    <div className="mt-4 flex w-full gap-2">
                        <Select
                            disabled={!isScheduledOrphanTaskEnabled(config)}
                            value={getScheduledTime(config).hour}
                            onChange={e => setNewConfig({
                                ...config,
                                "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                                    parseInt(e.target.value),
                                    getScheduledTime(config).minute,
                                    getScheduledTime(config).period
                                )
                            })}>
                            {Array.from({ length: 12 }, (_, i) => i + 1).map(h => (
                                <option key={h} value={h}>{h}</option>
                            ))}
                        </Select>
                        <Select
                            disabled={!isScheduledOrphanTaskEnabled(config)}
                            value={getScheduledTime(config).minute}
                            onChange={e => setNewConfig({
                                ...config,
                                "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                                    getScheduledTime(config).hour,
                                    parseInt(e.target.value),
                                    getScheduledTime(config).period
                                )
                            })}>
                            <option value={0}>00</option>
                            <option value={15}>15</option>
                            <option value={30}>30</option>
                            <option value={45}>45</option>
                        </Select>
                        <Select
                            disabled={!isScheduledOrphanTaskEnabled(config)}
                            value={getScheduledTime(config).period}
                            onChange={e => setNewConfig({
                                ...config,
                                "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                                    getScheduledTime(config).hour,
                                    getScheduledTime(config).minute,
                                    e.target.value as "am" | "pm"
                                )
                            })}>
                            <option value="am">am</option>
                            <option value="pm">pm</option>
                        </Select>
                    </div>
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="remove-orphaned-schedule-help">
                        When enabled, the "Remove Orphaned Files" task will run every day at the specified time.
                        You may need to set the TZ env variable to ensure the correct timezone.
                    </p>
                </div>
            <div className={'mt-6 space-y-3'}>
                <hr />
                <div className="space-y-3">
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Remove Orphaned Files
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <RemoveUnlinkedFiles savedConfig={savedConfig} />
                        </div>
                    </details>
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Rename Windows-Invalid Paths
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <RenameWindowsInvalidDavPaths savedConfig={savedConfig} />
                        </div>
                    </details>
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Convert Strm Files to Symlnks
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <ConvertStrmToSymlinks savedConfig={savedConfig} />
                        </div>
                    </details>
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Recreate STRM Files
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <RecreateStrmFiles savedConfig={savedConfig} />
                        </div>
                    </details>
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Migrate Large Database Blobs to Blobstore
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <MigrateDatabaseFilesToBlobstore savedConfig={savedConfig} />
                        </div>
                    </details>
                    <details className={'overflow-hidden rounded border border-base-content/10'}>
                        <summary className="flex cursor-pointer items-center justify-between px-4 py-3 text-sm font-semibold text-base-content hover:bg-base-content/5">
                            Reset Health-Check Statistics
                        </summary>
                        <div className={'border-t border-base-content/10 p-4'}>
                            <ResetHealthCheckStats />
                        </div>
                    </details>
                </div>
            </div>
        </SettingsPage>
    );
}

export function isMaintenanceSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["db.is-startup-vacuum-enabled"] !== newConfig["db.is-startup-vacuum-enabled"]
        || config["database.history-retention-days"] !== newConfig["database.history-retention-days"]
        || config["database.healthcheck-retention-days"] !== newConfig["database.healthcheck-retention-days"]
        || config["maintenance.remove-orphaned-schedule-enabled"] !== newConfig["maintenance.remove-orphaned-schedule-enabled"]
        || config["maintenance.remove-orphaned-schedule-time"] !== newConfig["maintenance.remove-orphaned-schedule-time"];
}

function isScheduledOrphanTaskEnabled(config: Record<string, string>) {
    return config["maintenance.remove-orphaned-schedule-enabled"] === "true";
}

function getScheduledTime(config: Record<string, string>): { hour: number, minute: number, period: "am" | "pm" } {
    const totalMinutes = parseInt(config["maintenance.remove-orphaned-schedule-time"] || "0");
    const hour24 = Math.floor(totalMinutes / 60);
    return {
        hour: hour24 % 12 || 12, // 0→12 (midnight), 1→1, ..., 12→12 (noon), 13→1, ...
        minute: totalMinutes % 60,
        period: Math.floor(totalMinutes / 60) >= 12 ? "pm" : "am"
    };
}

function buildScheduledTime(hour: number, minute: number, period: "am" | "pm"): string {
    const hour24 = (hour % 12) + (period === "pm" ? 12 : 0);
    return "" + (hour24 * 60 + minute);
}