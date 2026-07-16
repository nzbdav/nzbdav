import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import {
    decideMigrationStatusPoll,
    MigrationProgressView,
    MigrationShell,
    type MigrationStatus,
} from "~/components/migration-progress";
import { Button } from "~/components/ui/button";
import { Alert, Badge, Spinner } from "~/components/ui/feedback";
import { Checkbox, Input, Select } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { SettingsPage } from "~/components/ui";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type BackupSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type BackupFileEntry = { name: string; bytes: number };

type BackupManifest = {
    id: string;
    createdAt: string;
    kind: string;
    notes: string;
    preserved: boolean;
    appVersion?: string | null;
    lastMainMigration?: string | null;
    files: BackupFileEntry[];
};

type LastRestoreReport = {
    backupId: string;
    restoredAt: string;
    missingBlobRefs: number;
    checkedRefs: number;
};

type BackupListResponse = {
    status?: boolean;
    backups?: BackupManifest[];
    taskRunning?: boolean;
    pendingRestore?: boolean;
    lastRestoreReport?: LastRestoreReport | null;
    error?: string;
};

export function BackupSettings({ config, setNewConfig }: BackupSettingsProps) {
    const [backups, setBackups] = useState<BackupManifest[]>([]);
    const [taskRunning, setTaskRunning] = useState(false);
    const [lastRestoreReport, setLastRestoreReport] = useState<LastRestoreReport | null>(null);
    const [reportDismissed, setReportDismissed] = useState(false);
    const [listError, setListError] = useState<string | null>(null);
    const [busy, setBusy] = useState<string | null>(null);
    const [message, setMessage] = useState<{ text: string; variant: "success" | "danger" | "warning" } | null>(null);
    const [notes, setNotes] = useState("");
    const [backupProgress, setBackupProgress] = useState<string | null>(null);
    const [restoreProgress, setRestoreProgress] = useState<string | null>(null);
    const [wsConnected, setWsConnected] = useState(false);
    const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
    const [confirmRestoreId, setConfirmRestoreId] = useState<string | null>(null);
    const [restoring, setRestoring] = useState(false);
    const [migrationStatus, setMigrationStatus] = useState<MigrationStatus | null>(null);
    const [restorePhase, setRestorePhase] = useState<"idle" | "staging" | "restarting" | "migrating">("idle");
    const fileInputRef = useRef<HTMLInputElement>(null);
    const reloadScheduled = useRef(false);

    const refreshList = useCallback(async () => {
        try {
            const res = await fetch("/api/db-backup-list", { cache: "no-store" });
            const data = (await res.json().catch(() => null)) as BackupListResponse | null;
            if (!res.ok) {
                setListError(data?.error || `Failed to load backups (${res.status})`);
                return;
            }
            setBackups(data?.backups ?? []);
            setTaskRunning(!!data?.taskRunning);
            setLastRestoreReport(data?.lastRestoreReport ?? null);
            setListError(null);
        } catch {
            setListError("Failed to load backups.");
        }
    }, []);

    useEffect(() => {
        void refreshList();
    }, [refreshList]);

    useWebsocketTopic("dbbk", "state", setBackupProgress, {
        onOpen: () => setWsConnected(true),
        onClose: () => setWsConnected(false),
    });

    useWebsocketTopic("dbrs", "state", setRestoreProgress, {
        onOpen: () => setWsConnected(true),
    });

    useEffect(() => {
        if (!message || message.variant !== "success") return;
        const timer = window.setTimeout(() => setMessage(null), 4000);
        return () => window.clearTimeout(timer);
    }, [message]);

    useEffect(() => {
        if (!backupProgress) return;
        if (backupProgress.startsWith("Completed") || backupProgress.startsWith("Failed")) {
            void refreshList();
            setBusy(null);
        }
    }, [backupProgress, refreshList]);

    // Poll migration status while a restore restart is in flight.
    useEffect(() => {
        if (restorePhase !== "restarting" && restorePhase !== "migrating") return;

        let cancelled = false;
        const poll = async () => {
            try {
                const res = await fetch("/api/migration-status", {
                    headers: { accept: "application/json" },
                    cache: "no-store",
                });
                if (cancelled) return;
                const body = res.ok ? await res.json().catch(() => null) : null;
                const decision = decideMigrationStatusPoll(res.status, body);
                if (decision.action === "migrating") {
                    setMigrationStatus(decision.status);
                    setRestorePhase("migrating");
                    if (decision.reloadMs !== undefined && !reloadScheduled.current) {
                        reloadScheduled.current = true;
                        window.setTimeout(() => window.location.reload(), decision.reloadMs);
                    }
                    return;
                }
                if (decision.action === "connecting") {
                    setRestorePhase("restarting");
                }
            } catch {
                if (!cancelled) setRestorePhase("restarting");
            }
        };

        void poll();
        const interval = window.setInterval(poll, 2000);
        return () => {
            cancelled = true;
            window.clearInterval(interval);
        };
    }, [restorePhase]);

    const createBackup = useCallback(async () => {
        setBusy("create");
        setMessage(null);
        setBackupProgress(null);
        try {
            const form = new FormData();
            if (notes.trim()) form.append("notes", notes.trim());
            const res = await fetch("/api/db-backup-create", { method: "POST", body: form });
            if (res.status === 409) {
                setMessage({ text: "A backup or restore task is already running.", variant: "warning" });
                return;
            }
            if (!res.ok) {
                const data = await res.json().catch(() => null);
                setMessage({ text: data?.error || `Backup failed (${res.status})`, variant: "danger" });
                return;
            }
            setNotes("");
            setMessage({ text: "Backup started.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Backup request failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, [notes, refreshList]);

    const updateBackup = useCallback(async (id: string, patch: { preserved?: boolean; notes?: string }) => {
        const form = new FormData();
        form.append("id", id);
        if (patch.preserved !== undefined) form.append("preserved", String(patch.preserved));
        if (patch.notes !== undefined) form.append("notes", patch.notes);
        const res = await fetch("/api/db-backup-update", { method: "POST", body: form });
        if (!res.ok) {
            const data = await res.json().catch(() => null);
            setMessage({ text: data?.error || `Update failed (${res.status})`, variant: "danger" });
            return;
        }
        await refreshList();
    }, [refreshList]);

    const deleteBackup = useCallback(async (id: string) => {
        setConfirmDeleteId(null);
        setBusy(`delete-${id}`);
        try {
            const form = new FormData();
            form.append("id", id);
            const res = await fetch("/api/db-backup-delete", { method: "POST", body: form });
            if (!res.ok) {
                const data = await res.json().catch(() => null);
                setMessage({ text: data?.error || `Delete failed (${res.status})`, variant: "danger" });
                return;
            }
            setMessage({ text: "Backup deleted.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Delete failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, [refreshList]);

    const downloadBackup = useCallback(async (id: string) => {
        setBusy(`download-${id}`);
        setMessage(null);
        try {
            const res = await fetch(`/api/db-backup-download?id=${encodeURIComponent(id)}`);
            if (!res.ok) {
                const data = await res.json().catch(() => null);
                setMessage({ text: data?.error || `Download failed (${res.status})`, variant: "danger" });
                return;
            }
            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = `nzbdav-backup-${id}.zip`;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
        } catch {
            setMessage({ text: "Download failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, []);

    const uploadBackup = useCallback(async (file: File) => {
        setBusy("upload");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", file);
            const res = await fetch("/api/db-backup-upload", { method: "POST", body: form });
            const data = await res.json().catch(() => null);
            if (!res.ok) {
                setMessage({ text: data?.error || `Upload failed (${res.status})`, variant: "danger" });
                return;
            }
            setMessage({ text: "Backup uploaded.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Upload failed.", variant: "danger" });
        } finally {
            setBusy(null);
            if (fileInputRef.current) fileInputRef.current.value = "";
        }
    }, [refreshList]);

    const startRestore = useCallback(async (id: string, acknowledged?: boolean) => {
        if (!acknowledged) {
            setMessage({ text: "You must acknowledge the restore warning.", variant: "warning" });
            return;
        }
        setConfirmRestoreId(null);
        setBusy(`restore-${id}`);
        setRestoreProgress(null);
        setRestoring(true);
        setRestorePhase("staging");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", id);
            const res = await fetch("/api/db-restore", { method: "POST", body: form });
            const data = await res.json().catch(() => null);
            if (res.status === 409) {
                setMessage({ text: "A backup or restore task is already running.", variant: "warning" });
                setRestoring(false);
                setRestorePhase("idle");
                return;
            }
            if (!res.ok) {
                setMessage({ text: data?.error || `Restore failed (${res.status})`, variant: "danger" });
                setRestoring(false);
                setRestorePhase("idle");
                return;
            }
            setRestorePhase("restarting");
        } catch {
            // Expected once the backend exits for restart.
            setRestorePhase("restarting");
        } finally {
            setBusy(null);
        }
    }, []);

    if (restoring && (restorePhase === "restarting" || restorePhase === "migrating")) {
        if (restorePhase === "migrating" && migrationStatus) {
            return (
                <div className="fixed inset-0 z-50 overflow-auto bg-base-300">
                    <MigrationProgressView status={migrationStatus} />
                </div>
            );
        }
        return (
            <div className="fixed inset-0 z-50 overflow-auto bg-base-300">
                <MigrationShell
                    title="Applying database restore"
                    subtitle="The backend is restarting into maintenance mode to swap databases. This page will update automatically."
                >
                    <div className="flex items-center gap-3 text-sm text-base-content/70">
                        <span className="loading loading-spinner loading-sm text-primary" />
                        <span>{restoreProgress || "Waiting for maintenance status…"}</span>
                    </div>
                </MigrationShell>
            </div>
        );
    }

    const scheduleEnabled = config["backup.schedule-enabled"] === "true";
    const scheduleTime = getScheduledTime(config);

    return (
        <SettingsPage>
            {!reportDismissed && lastRestoreReport && lastRestoreReport.missingBlobRefs > 0 && (
                <Alert variant="warning" className="mb-4 text-sm">
                    <div className="flex items-start justify-between gap-3">
                        <div>
                            Last restore of <span className="font-mono">{lastRestoreReport.backupId}</span> found{" "}
                            <strong>{lastRestoreReport.missingBlobRefs}</strong> of{" "}
                            {lastRestoreReport.checkedRefs} blob references pointing to missing files under{" "}
                            <code className="font-mono">blobs/</code>. Affected items may fail to stream until
                            re-downloaded.
                        </div>
                        <Button variant="ghost" size="small" onClick={() => setReportDismissed(true)}>
                            Dismiss
                        </Button>
                    </div>
                </Alert>
            )}

            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-base-content/80">
                    <Checkbox
                        id="backup-schedule-enabled"
                        checked={scheduleEnabled}
                        onChange={(e) =>
                            setNewConfig({
                                ...config,
                                "backup.schedule-enabled": "" + e.target.checked,
                            })
                        }
                    />
                    <span>Schedule database backup daily</span>
                </label>
                <div className="mt-4 flex w-full gap-2">
                    <Select
                        disabled={!scheduleEnabled}
                        value={scheduleTime.hour}
                        onChange={(e) =>
                            setNewConfig({
                                ...config,
                                "backup.schedule-time": buildScheduledTime(
                                    parseInt(e.target.value),
                                    scheduleTime.minute,
                                    scheduleTime.period,
                                ),
                            })
                        }
                    >
                        {Array.from({ length: 12 }, (_, i) => i + 1).map((h) => (
                            <option key={h} value={h}>
                                {h}
                            </option>
                        ))}
                    </Select>
                    <Select
                        disabled={!scheduleEnabled}
                        value={scheduleTime.minute}
                        onChange={(e) =>
                            setNewConfig({
                                ...config,
                                "backup.schedule-time": buildScheduledTime(
                                    scheduleTime.hour,
                                    parseInt(e.target.value),
                                    scheduleTime.period,
                                ),
                            })
                        }
                    >
                        <option value={0}>00</option>
                        <option value={15}>15</option>
                        <option value={30}>30</option>
                        <option value={45}>45</option>
                    </Select>
                    <Select
                        disabled={!scheduleEnabled}
                        value={scheduleTime.period}
                        onChange={(e) =>
                            setNewConfig({
                                ...config,
                                "backup.schedule-time": buildScheduledTime(
                                    scheduleTime.hour,
                                    scheduleTime.minute,
                                    e.target.value as "am" | "pm",
                                ),
                            })
                        }
                    >
                        <option value="am">am</option>
                        <option value="pm">pm</option>
                    </Select>
                </div>
                <p className="text-[11px] leading-relaxed text-base-content/45">
                    Creates a logical <code className="font-mono">.sql</code> dump of all databases under the config
                    volume. Set the <code className="font-mono">TZ</code> env variable for the correct timezone.
                </p>
            </div>

            <hr />

            <div className="space-y-2">
                <label className="block text-sm text-base-content/80" htmlFor="backup-retention-count">
                    Retention count (non-preserved backups)
                </label>
                <Input
                    id="backup-retention-count"
                    type="number"
                    min={0}
                    value={config["backup.retention-count"] ?? "5"}
                    onChange={(e) =>
                        setNewConfig({
                            ...config,
                            "backup.retention-count": e.target.value,
                        })
                    }
                    className="max-w-xs"
                />
                <p className="text-[11px] leading-relaxed text-base-content/45">
                    Keep the newest N backups that are not preserved. Set to 0 to disable pruning. Preserved backups
                    are never deleted by retention.
                </p>
            </div>

            <hr />

            <div className="space-y-3">
                <h2 className="text-sm font-semibold text-base-content">Create backup now</h2>
                <Input
                    placeholder="Optional notes"
                    value={notes}
                    onChange={(e) => setNotes(e.target.value)}
                />
                <div className="flex flex-wrap items-center gap-2">
                    <Button
                        variant="success"
                        disabled={busy === "create" || taskRunning}
                        onClick={() => void createBackup()}
                    >
                        {busy === "create" ? <Spinner className="h-4 w-4" /> : <Icon name="backup" className="!text-[18px]" />}
                        Create Backup
                    </Button>
                    <Button
                        variant="outline"
                        disabled={busy === "upload"}
                        onClick={() => fileInputRef.current?.click()}
                    >
                        {busy === "upload" ? <Spinner className="h-4 w-4" /> : <Icon name="upload" className="!text-[18px]" />}
                        Upload Backup
                    </Button>
                    <input
                        ref={fileInputRef}
                        type="file"
                        accept=".zip,.sql,application/zip,text/plain"
                        className="hidden"
                        onChange={(e) => {
                            const file = e.target.files?.[0];
                            if (file) void uploadBackup(file);
                        }}
                    />
                    {!wsConnected && (
                        <span className="text-[11px] text-base-content/45">Connecting for live progress…</span>
                    )}
                </div>
                {backupProgress && (
                    <p className="font-mono text-xs text-base-content/70">{backupProgress}</p>
                )}
                {restoreProgress && restorePhase === "staging" && (
                    <p className="font-mono text-xs text-base-content/70">{restoreProgress}</p>
                )}
            </div>

            {message && (
                <Alert variant={message.variant === "danger" ? "danger" : message.variant === "warning" ? "warning" : "success"} className="text-sm">
                    {message.text}
                </Alert>
            )}
            {listError && (
                <Alert variant="danger" className="text-sm">
                    {listError}
                </Alert>
            )}

            <hr />

            <div className="space-y-3">
                <div className="flex items-center justify-between gap-2">
                    <h2 className="text-sm font-semibold text-base-content">Backups</h2>
                    <Button variant="ghost" size="small" onClick={() => void refreshList()}>
                        <Icon name="refresh" className="!text-[16px]" />
                        Refresh
                    </Button>
                </div>

                {backups.length === 0 ? (
                    <p className="text-sm text-base-content/50">No backups yet.</p>
                ) : (
                    <ul className="space-y-3">
                        {backups.map((backup) => {
                            const totalBytes = backup.files.reduce((sum, f) => sum + (f.bytes || 0), 0);
                            return (
                                <li
                                    key={backup.id}
                                    className="rounded border border-base-content/10 bg-base-200/40 p-3 space-y-3"
                                >
                                    <div className="flex flex-wrap items-start justify-between gap-2">
                                        <div className="space-y-1">
                                            <div className="flex flex-wrap items-center gap-2">
                                                <span className="font-mono text-sm text-base-content">{backup.id}</span>
                                                <Badge className="badge-sm">{backup.kind}</Badge>
                                                {backup.preserved && <Badge className="badge-sm badge-warning">preserved</Badge>}
                                            </div>
                                            <p className="text-[11px] text-base-content/50">
                                                {formatDate(backup.createdAt)} · {formatBytes(totalBytes)}
                                                {backup.appVersion ? ` · v${backup.appVersion}` : ""}
                                            </p>
                                        </div>
                                        <label className="flex items-center gap-2 text-xs text-base-content/80">
                                            <Checkbox
                                                checked={backup.preserved}
                                                onChange={(e) =>
                                                    void updateBackup(backup.id, { preserved: e.target.checked })
                                                }
                                            />
                                            Preserve
                                        </label>
                                    </div>
                                    <Input
                                        defaultValue={backup.notes ?? ""}
                                        placeholder="Notes"
                                        onBlur={(e) => {
                                            if (e.target.value !== (backup.notes ?? "")) {
                                                void updateBackup(backup.id, { notes: e.target.value });
                                            }
                                        }}
                                    />
                                    <div className="flex flex-wrap gap-2">
                                        <Button
                                            variant="outline"
                                            size="small"
                                            disabled={busy === `download-${backup.id}`}
                                            onClick={() => void downloadBackup(backup.id)}
                                        >
                                            {busy === `download-${backup.id}`
                                                ? <Spinner className="h-4 w-4" />
                                                : <Icon name="download" className="!text-[16px]" />}
                                            Download
                                        </Button>
                                        <Button
                                            variant="warning"
                                            size="small"
                                            disabled={!!busy || taskRunning}
                                            onClick={() => setConfirmRestoreId(backup.id)}
                                        >
                                            <Icon name="restore" className="!text-[16px]" />
                                            Restore
                                        </Button>
                                        <Button
                                            variant="danger"
                                            size="small"
                                            disabled={busy === `delete-${backup.id}`}
                                            onClick={() => setConfirmDeleteId(backup.id)}
                                        >
                                            <Icon name="delete" className="!text-[16px]" />
                                            Delete
                                        </Button>
                                    </div>
                                </li>
                            );
                        })}
                    </ul>
                )}
            </div>

            <p className="text-[11px] leading-relaxed text-base-content/45">
                Backups include <code className="font-mono">db.sqlite</code>,{" "}
                <code className="font-mono">metrics.sqlite</code>, and <code className="font-mono">warden.db</code> as
                logical SQL dumps. The <code className="font-mono">blobs/</code> folder is not included — restoring an
                older dump may leave some items with missing blob files.
            </p>

            <ConfirmModal
                show={confirmDeleteId !== null}
                title="Delete backup"
                message={<>Delete backup <span className="font-mono">{confirmDeleteId}</span>? This cannot be undone.</>}
                cancelText="Cancel"
                confirmText="Delete"
                onCancel={() => setConfirmDeleteId(null)}
                onConfirm={() => {
                    if (confirmDeleteId) void deleteBackup(confirmDeleteId);
                }}
            />

            <ConfirmModal
                show={confirmRestoreId !== null}
                title="Restore database backup"
                message={
                    <>
                        Restoring <span className="font-mono">{confirmRestoreId}</span> replaces all settings, queue,
                        history, and the WebDAV file tree. A pre-restore safety backup is created automatically. The
                        server will restart into maintenance mode to apply the swap.
                    </>
                }
                checkboxMessage="I understand this will replace the current databases"
                cancelText="Cancel"
                confirmText="Restore"
                onCancel={() => setConfirmRestoreId(null)}
                onConfirm={(checked) => {
                    if (confirmRestoreId) void startRestore(confirmRestoreId, checked);
                }}
            />
        </SettingsPage>
    );
}

export function isBackupSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return (
        config["backup.schedule-enabled"] !== newConfig["backup.schedule-enabled"] ||
        config["backup.schedule-time"] !== newConfig["backup.schedule-time"] ||
        config["backup.retention-count"] !== newConfig["backup.retention-count"]
    );
}

function getScheduledTime(config: Record<string, string>): { hour: number; minute: number; period: "am" | "pm" } {
    const totalMinutes = parseInt(config["backup.schedule-time"] || "0");
    const hour24 = Math.floor(totalMinutes / 60);
    return {
        hour: hour24 % 12 || 12,
        minute: totalMinutes % 60,
        period: Math.floor(totalMinutes / 60) >= 12 ? "pm" : "am",
    };
}

function buildScheduledTime(hour: number, minute: number, period: "am" | "pm"): string {
    const hour24 = (hour % 12) + (period === "pm" ? 12 : 0);
    return "" + (hour24 * 60 + minute);
}

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function formatDate(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    return date.toLocaleString();
}
