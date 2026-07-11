import { Checkbox, Input } from "~/components/ui/form";
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    return (
        <div className={'space-y-6'}>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="webdav-user-input">WebDAV User</label>
                <Input
                    {...className(['w-full', !isValidUser(config["webdav.user"]) && 'border-red-500 focus:border-red-500'])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="webdav-user-help">
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="max-queue-connections-input">Queue Download Connections</label>
                <Input
                    {...className(['w-full', !isValidMaxQueueConnections(config["usenet.max-queue-connections"]) && 'border-red-500 focus:border-red-500'])}
                    type="text"
                    id="max-queue-connections-input"
                    aria-describedby="max-queue-connections-help"
                    placeholder="Auto (all connections)"
                    value={config["usenet.max-queue-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-queue-connections": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="max-queue-connections-help">
                    Connections available to queue imports. Leave blank to use all provider connections.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="webdav-pass-input">WebDAV Password</label>
                <Input
                    className={'w-full'}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="webdav-pass-help">
                    Use this password to connect to the webdav.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                        id="segment-cache-enabled-checkbox"
                        aria-describedby="segment-cache-enabled-help"
                        checked={config["usenet.segment-cache.enabled"] === "true"}
                        onChange={e => setNewConfig({ ...config, "usenet.segment-cache.enabled": String(e.target.checked) })} />
                    <span>Enable Segment Cache</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="segment-cache-enabled-help">
                    Cache decoded segments on disk so repeat reads and seeks avoid provider traffic. Takes effect after restart.
                </p>
                {config["usenet.segment-cache.enabled"] === "true" && (
                    <div className="grid gap-4 border-l border-slate-700 pl-4 sm:grid-cols-2">
                        <label className="space-y-2 text-sm text-slate-300">
                            <span>Cache path</span>
                            <Input
                                className={`w-full ${!isValidSegmentCachePath(config["usenet.segment-cache.path"]) ? "border-red-500" : ""}`}
                                value={config["usenet.segment-cache.path"]}
                                placeholder="/config/segment-cache"
                                onChange={e => setNewConfig({ ...config, "usenet.segment-cache.path": e.target.value })} />
                        </label>
                        <label className="space-y-2 text-sm text-slate-300">
                            <span>Maximum size (GB)</span>
                            <Input
                                className={`w-full ${!isPositiveInteger(config["usenet.segment-cache.max-gb"]) ? "border-red-500" : ""}`}
                                inputMode="numeric"
                                value={config["usenet.segment-cache.max-gb"]}
                                onChange={e => setNewConfig({ ...config, "usenet.segment-cache.max-gb": e.target.value })} />
                        </label>
                    </div>
                )}
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="max-download-connections-input">Max Download Connections</label>
                <Input
                    {...className(['w-full', !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && 'border-red-500 focus:border-red-500'])}
                    type="text"
                    id="max-download-connections-input"
                    aria-describedby="max-download-connections-help"
                    placeholder="15"
                    value={config["usenet.max-download-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="max-download-connections-help">
                    The maximum number of connections that will be used for downloading articles from your usenet provider(s).
                    Configure this to the minimum number of connections that will fully saturate your server's bandwidth.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</label>
                <div className="flex w-full">
                    <Input
                        className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? 'border-red-500 focus:border-red-500' : undefined}
                        type="text"
                        id="streaming-priority-input"
                        aria-describedby="streaming-priority-help"
                        placeholder="80"
                        value={config["usenet.streaming-priority"]}
                        onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                    <span className="flex items-center rounded-r border border-l-0 border-slate-600 bg-slate-800 px-2 text-sm text-slate-300">%</span>
                </div>
                <p className="text-xs leading-relaxed text-slate-400" id="streaming-priority-help">
                    When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="article-buffer-size-input">Article Buffer Size</label>
                <Input
                    {...className(['w-full', !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && 'border-red-500 focus:border-red-500'])}
                    type="text"
                    id="article-buffer-size-input"
                    aria-describedby="article-buffer-size-help"
                    placeholder="40"
                    value={config["usenet.article-buffer-size"]}
                    onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="article-buffer-size-help">
                    The number of articles to buffer ahead, per stream, when reading from the webdav.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                    id="pipelined-body-requests-checkbox"
                    aria-describedby="pipelined-body-requests-help"
                    checked={config["usenet.pipelined-body-requests"] === "true"}
                    onChange={e => setNewConfig({ ...config, "usenet.pipelined-body-requests": "" + e.target.checked })} />
                    <span>Pipelined article downloads</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="pipelined-body-requests-help">
                    Fetch articles in small NNTP batches for smoother WebDAV streaming. Queue imports use the
                    separate <strong>Enable NNTP pipelining</strong> toggle under Usenet settings. Disable this
                    to use the legacy one-at-a-time API while retaining the configured article buffer.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })}  />
                    <span>{`Enforce Read-Only`}</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="readonly-help">
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })}  />
                    <span>{`Show hidden files on Dav Explorer`}</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="show-hidden-files-help">
                    Hidden files or directories are those whose names are prefixed by a period.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })}  />
                    <span>{`Preview par2 files on Dav Explorer`}</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="preview-par2-files-help">
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </p>
            </div>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.max-queue-connections"] !== newConfig["usenet.max-queue-connections"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["usenet.pipelined-body-requests"] !== newConfig["usenet.pipelined-body-requests"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
        || config["usenet.segment-cache.enabled"] !== newConfig["usenet.segment-cache.enabled"]
        || config["usenet.segment-cache.path"] !== newConfig["usenet.segment-cache.path"]
        || config["usenet.segment-cache.max-gb"] !== newConfig["usenet.segment-cache.max-gb"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    const segmentCacheValid = newConfig["usenet.segment-cache.enabled"] !== "true"
        || (isValidSegmentCachePath(newConfig["usenet.segment-cache.path"])
            && isPositiveInteger(newConfig["usenet.segment-cache.max-gb"]));
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidMaxQueueConnections(newConfig["usenet.max-queue-connections"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"])
        && segmentCacheValid;
}

function isValidSegmentCachePath(value: string): boolean {
    return value.trim().length > 0;
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidMaxDownloadConnections(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidMaxQueueConnections(value: string): boolean {
    return value.trim() === "" || isPositiveInteger(value);
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}