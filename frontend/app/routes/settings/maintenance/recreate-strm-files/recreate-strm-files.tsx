import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Checkbox } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { useCallback, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type RecreateStrmFilesProps = {
    savedConfig: Record<string, string>
};

export function RecreateStrmFiles({ savedConfig }: RecreateStrmFilesProps) {
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [runStarted, setRunStarted] = useState<boolean>(false);
    const [rewriteAll, setRewriteAll] = useState<boolean>(false);
    const [error, setError] = useState<string | null>(null);

    const completedDir = savedConfig["api.completed-downloads-dir"]?.trim();
    const importStrategy = savedConfig["api.import-strategy"] ?? "symlinks";
    const baseUrl = savedConfig["general.base-url"]?.trim();
    const isStrmStrategy = importStrategy === "strm";
    const canRun = !!completedDir && !!baseUrl && isStrmStrategy;

    const isFinished = !!progress && (
        progress.startsWith("Done") || progress.startsWith("Failed") || progress.startsWith("Aborted")
    );
    const isRunning = runStarted && !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = canRun && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? "success" : "secondary";
    const runButtonLabel = isRunning ? "Running..." : "Run Task";

    useWebsocketTopic("rstm", "state", (msg) => {
        if (runStarted) setProgress(msg);
    }, {
        onOpen: () => setConnected(true),
        onClose: () => {
            setConnected(false);
            if (!runStarted) setProgress(null);
        },
    });

    const onRun = useCallback(async () => {
        setError(null);
        setRunStarted(true);
        setIsFetching(true);
        setProgress(null);
        try {
            const qs = rewriteAll ? "?rewriteAll=true" : "";
            const response = await fetch(`/api/recreate-strm-files${qs}`);
            if (response.status === 409) {
                setError("Another maintenance task is already running.");
                setRunStarted(false);
                return;
            }
            if (!response.ok) {
                setError(`Request failed (${response.status}).`);
                setRunStarted(false);
            }
        } catch (e) {
            setError(e instanceof Error ? e.message : "Request failed.");
            setRunStarted(false);
        } finally {
            setIsFetching(false);
        }
    }, [rewriteAll]);

    return (
        <>
            {!canRun &&
                <Alert className={"mb-3"} variant="warning">
                    Warning
                    <ul className={"mt-2 list-disc space-y-1 pl-5"}>
                        {!isStrmStrategy &&
                            <li className={"text-xs"}>
                                Import strategy must be set to STRM Files under the SABnzbd tab.
                            </li>
                        }
                        {!completedDir &&
                            <li className={"text-xs"}>
                                Configure Completed Downloads Directory under the SABnzbd tab.
                            </li>
                        }
                        {!baseUrl &&
                            <li className={"text-xs"}>
                                Configure Base URL under the SABnzbd tab (used inside STRM URLs).
                            </li>
                        }
                    </ul>
                </Alert>
            }
            {canRun &&
                <Alert className={"mb-3"} variant="warning">
                    <span className="font-semibold">Note</span>
                    <ul className={"mt-2 list-disc space-y-1 pl-5"}>
                        <li className={"text-xs"}>
                            Writes STRM sidecars under `{completedDir}` mirroring `/content`.
                        </li>
                        <li className={"text-xs"}>
                            Default mode only creates missing files or updates ones whose URL content changed
                            (e.g. after a base-url change). Rewrite-all forces every file to be rewritten and
                            will update mtimes, which may trigger a media-server library rescan.
                        </li>
                    </ul>
                </Alert>
            }
            {error &&
                <Alert className={"mb-3"} variant="danger">{error}</Alert>
            }
            <div className={"space-y-3"}>
                <label className="flex items-center gap-2 text-sm text-base-content/80">
                    <Checkbox
                        id="recreate-strm-rewrite-all"
                        checked={rewriteAll}
                        onChange={e => setRewriteAll(e.target.checked)}
                        disabled={isRunning}
                    />
                    <span>Rewrite all STRM files (update mtimes)</span>
                </label>
                <div className={"flex flex-col gap-3 sm:flex-row sm:items-center"}>
                    <Button
                        className={"shrink-0"}
                        variant={runButtonVariant}
                        onClick={onRun}
                        disabled={!isRunButtonEnabled}
                    >
                        <Icon
                            name={isRunning ? "progress_activity" : "play_arrow"}
                            className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
                        />
                        {runButtonLabel}
                    </Button>
                    <div className={"whitespace-pre-wrap font-mono text-xs text-base-content/80"}>
                        {progress}
                    </div>
                </div>
                <p className="text-[11px] leading-relaxed text-base-content/45">
                    Use this to repair libraries that missed STRM creation (e.g. season packs processed
                    before the create-for-all-videos fix), or to refresh URLs after changing Base URL.
                </p>
            </div>
        </>
    );
}
