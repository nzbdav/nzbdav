import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type RenameWindowsInvalidDavPathsProps = {
    savedConfig: Record<string, string>
};

export function RenameWindowsInvalidDavPaths({ savedConfig }: RenameWindowsInvalidDavPathsProps) {
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [runStarted, setRunStarted] = useState<boolean>(false);
    const [statusError, setStatusError] = useState<string | null>(null);
    const progressMessage = progress?.replace("Dry Run - ", "");

    const windowsSafeEnabled = savedConfig["webdav.windows-safe-paths"] !== "false";
    const isDone = progressMessage?.startsWith("Done");
    const isFinished = progressMessage?.startsWith("Done")
        || progressMessage?.startsWith("Failed")
        || progressMessage?.startsWith("Aborted");
    const isRunning = !isFinished && (isFetching || runStarted);
    const isRunButtonEnabled = windowsSafeEnabled && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? "success" : "secondary";
    const runButtonLabel = isRunning ? "Running..." : "Apply Renames";

    useWebsocketTopic("rwip", "state", setProgress, {
        onOpen: () => setConnected(true),
        onClose: () => setProgress(null),
    });

    useEffect(() => {
        if (isFinished)
            setRunStarted(false);
    }, [isFinished]);

    const startTask = useCallback(async (url: string) => {
        setStatusError(null);
        setProgress(null);
        setRunStarted(true);
        setIsFetching(true);
        try {
            const response = await fetch(url);
            if (response.status === 409) {
                setStatusError("Task already running.");
                setRunStarted(false);
                return;
            }
            if (!response.ok) {
                setStatusError(`Request failed (${response.status}).`);
                setRunStarted(false);
            }
        } catch {
            setStatusError("Request failed.");
            setRunStarted(false);
        } finally {
            setIsFetching(false);
        }
    }, []);

    const onApply = useCallback(async () => {
        await startTask("/api/rename-windows-invalid-dav-paths");
    }, [startTask]);

    const onDryRun = useCallback(async () => {
        await startTask("/api/rename-windows-invalid-dav-paths/dry-run");
    }, [startTask]);

    const dryRunButton =
        <Button
            className={"inline-flex"}
            disabled={!isRunButtonEnabled}
            onClick={onDryRun}
            variant="warning"
            size="small"
        >
            <Icon name="science" className="!text-[18px]" />
            perform a dry-run
        </Button>;

    return (
        <>
            {!windowsSafeEnabled &&
                <Alert className={"mb-3"} variant="warning">
                    Warning
                    <ul className={"mt-2 list-disc space-y-1 pl-5"}>
                        <li className={"text-xs"}>
                            Enable &quot;Windows-safe paths&quot; under the WebDAV settings tab before running this task.
                        </li>
                    </ul>
                </Alert>
            }
            {windowsSafeEnabled &&
                <Alert className={"mb-3"} variant="warning">
                    <span className="font-semibold">Note</span>
                    <ul className={"mt-2 list-disc space-y-1 pl-5"}>
                        <li className={"text-xs"}>
                            Make a backup of your NzbDAV database prior to applying renames.
                        </li>
                        <li className={"text-xs"}>
                            Prefer a dry-run first to review the report of paths that would change.
                        </li>
                    </ul>
                </Alert>
            }
            <div className={"space-y-3"}>
                <div className="space-y-2">
                    <div className={"flex flex-col gap-3 sm:flex-row sm:items-center"}>
                        <Button
                            className={"shrink-0"}
                            variant={runButtonVariant}
                            onClick={onApply}
                            disabled={!isRunButtonEnabled}
                        >
                            <Icon name={isRunning ? "progress_activity" : "play_arrow"} className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`} />
                            {runButtonLabel}
                        </Button>
                        <div className={"font-mono text-xs text-base-content/80"}>
                            {statusError ?? progress}
                            {isDone && <>
                                &nbsp;<a href="/api/rename-windows-invalid-dav-paths/audit">Report.</a>
                            </>}
                        </div>
                    </div>
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="rename-windows-invalid-paths-help">
                        <br />
                        This task finds WebDAV items whose names contain characters invalid on Windows
                        (or trailing dots/spaces / reserved device names) and renames those path
                        components to match the current Windows-safe sanitizer. Child paths are updated
                        for the whole subtree. Name collisions get a <code>_2</code> / <code>_3</code> suffix.
                        Start with a {dryRunButton} to generate a report without writing changes.
                        <br />
                        <br />
                        Library STRM files and symlinks target items by DavItem Id under <code>/.ids</code>,
                        so organized libraries keep working after renames. WebDAV browse paths and any
                        on-disk STRM file locations that mirror the old path will change.
                    </p>
                </div>
            </div>
        </>
    );
}
