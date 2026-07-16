import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type RemoveUnlinkedFilesProps = {
    savedConfig: Record<string, string>
};

export function RemoveUnlinkedFiles({ savedConfig }: RemoveUnlinkedFilesProps) {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    // Replayed non-terminal ctp state must not look like an active run until the user clicks.
    const [runStarted, setRunStarted] = useState<boolean>(false);
    const [statusError, setStatusError] = useState<string | null>(null);
    const progressMessage = progress?.replace('Dry Run - ', '');

    // derived variables
    const libraryDir = savedConfig["media.library-dir"];
    const isDone = progressMessage?.startsWith("Done");
    const isFinished = progressMessage?.startsWith("Done") || progressMessage?.startsWith("Failed") || progressMessage?.startsWith("Aborted");
    const isRunning = !isFinished && (isFetching || runStarted);
    const isRunButtonEnabled = !!libraryDir && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "Running..." : "Run Task";

    useWebsocketTopic("ctp", "state", setProgress, {
        onOpen: () => setConnected(true),
        onClose: () => setProgress(null),
    });

    useEffect(() => {
        if (isFinished)
            setRunStarted(false);
    }, [isFinished]);

    const startTask = useCallback(async (url: string) => {
        setStatusError(null);
        // Clear any stale terminal message so isFinished doesn't mask the new run
        // until its first progress message arrives.
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

    // events
    const onRun = useCallback(async () => {
        await startTask("/api/remove-unlinked-files");
    }, [startTask]);

    const onDryRun = useCallback(async () => {
        await startTask("/api/remove-unlinked-files/dry-run");
    }, [startTask]);

    // view
    const dryRunButton =
        <Button
            className={'inline-flex'}
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
            {!libraryDir &&
                <Alert className={'mb-3'} variant="warning">
                    Warning
                    <ul className={'mt-2 list-disc space-y-1 pl-5'}>
                        <li className={'text-xs'}>
                            You must first configure the Library Directory setting before running this task.
                            Head over to the Repairs tab.
                        </li>
                    </ul>
                </Alert>
            }
            {libraryDir &&
                <Alert className={'mb-3'} variant="danger">
                    <span className="font-semibold">Danger</span>
                    <ul className={'mt-2 list-disc space-y-1 pl-5'}>
                        <li className={'text-xs'}>
                            Make a backup of your NzbDAV database prior to running this task
                        </li>
                        <li className={'text-xs'}>
                            Files will be removed from the webdav and will not be recoverable without a backup
                        </li>
                    </ul>
                </Alert>
            }
            <div className={'space-y-3'}>
                <div className="space-y-2">
                    <div className={'flex flex-col gap-3 sm:flex-row sm:items-center'}>
                        <Button
                            className={'shrink-0'}
                            variant={runButtonVariant}
                            onClick={onRun}
                            disabled={!isRunButtonEnabled}
                        >
                            <Icon name={isRunning ? "progress_activity" : "play_arrow"} className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`} />
                            {runButtonLabel}
                        </Button>
                        <div className={'font-mono text-xs text-base-content/80'}>
                            {statusError ?? progress}
                            {isDone && <>
                                &nbsp;<a href="/api/remove-unlinked-files/audit">Audit.</a>
                            </>}
                        </div>
                    </div>
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="cleanup-task-progress-help">
                        <br />
                        This task will scan your organized media library for all symlinked or *.strm linked files.
                        Any file on the webdav that is not pointed to by your library will be deleted.
                        If you would like to see what would be deleted without running the task, you can {dryRunButton}.
                        The dry-run will not delete anything.
                        <br />
                        <br />
                        Note: Files still present in the History table will not be removed when running this task.
                        It is assumed that files still present in the History table have not yet been imported by Arrs
                        and they are expected to not yet have a corresponding symlink/strm in the Library folder.
                        These files will remain intact until Arrs have a chance to process them and remove them from the
                        History table.
                    </p>
                </div>
            </div>
        </>
    );
}
