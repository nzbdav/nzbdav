import { useCallback, useState } from "react";
import { Alert, Button, Icon, SettingsCard, SettingsIntro, SettingsPage, Spinner } from "~/components/ui";

type Message = { text: string; variant: "success" | "danger" } | null;

function downloadName(response: Response): string {
    const header = response.headers.get("content-disposition");
    const match = header?.match(/filename="?([^";]+)"?/i);
    return match?.[1] ?? "nzbdav-support-pack.zip";
}

export function SupportSettings() {
    const [busy, setBusy] = useState(false);
    const [message, setMessage] = useState<Message>(null);

    const download = useCallback(async () => {
        setBusy(true);
        setMessage(null);
        try {
            const response = await fetch("/api/download-support-pack", { cache: "no-store" });
            if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.error || `Support pack failed (${response.status})`);
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.download = downloadName(response);
            document.body.append(anchor);
            anchor.click();
            anchor.remove();
            URL.revokeObjectURL(url);
            setMessage({ text: "Support pack downloaded. Share it only with trusted NzbDAV support.", variant: "success" });
        } catch (error) {
            setMessage({
                text: error instanceof Error ? error.message : "Could not generate the support pack.",
                variant: "danger",
            });
        } finally {
            setBusy(false);
        }
    }, []);

    return (
        <SettingsPage>
            <SettingsIntro>
                Generate a technical support pack to help diagnose an NzbDAV problem.
                It is generated in memory and is not saved on the server.
            </SettingsIntro>

            <Alert variant="warning" className="items-start text-sm">
                <Icon name="privacy_tip" className="mt-0.5 !text-[20px]" />
                <span>
                    Passwords, API keys, tokens, URL credentials, sensitive URL parameters, and IP addresses
                    are automatically redacted. File names, paths, account usernames, DNS names, and non-secret
                    URL paths can remain. Review the archive before sharing it.
                </span>
            </Alert>

            <SettingsCard
                icon="support_agent"
                title="Technical support pack"
                description="A ZIP with recent backend diagnostics for troubleshooting.">
                <ul className="list-inside list-disc space-y-1 text-sm text-base-content/70">
                    <li>Current backend logs from the in-memory buffer</li>
                    <li>Redacted active settings and runtime information</li>
                    <li>Recent provider outage, failover, and consumption metrics</li>
                </ul>
                <p className="text-xs text-base-content/50">
                    It excludes frontend and container logs, databases, backups, NZBs, blobs, environment files,
                    crash dumps, stream traces, and segment-cache data.
                </p>
                <div className="flex flex-wrap items-center gap-3 pt-1">
                    <Button variant="primary" disabled={busy} onClick={() => void download()}>
                        {busy ? <Spinner size="sm" /> : <Icon name="download" className="!text-[18px]" />}
                        {busy ? "Generating…" : "Generate & download"}
                    </Button>
                    <span className="text-xs text-base-content/50" aria-live="polite">
                        {busy ? "Collecting and redacting diagnostics…" : ""}
                    </span>
                </div>
                {message && <Alert variant={message.variant}>{message.text}</Alert>}
            </SettingsCard>
        </SettingsPage>
    );
}
