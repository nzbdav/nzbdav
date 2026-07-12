import { Checkbox, Input } from "~/components/ui/form";
import { type Dispatch, type SetStateAction } from "react";
import { isPositiveInteger } from "../usenet/usenet";

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"];
    const arrConfig = JSON.parse(config["arr.instances"]);
    const areArrInstancesConfigured =
        arrConfig.RadarrInstances.length > 0 ||
        arrConfig.SonarrInstances.length > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    const helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library-Directory and Radarr/Sonarr instances are configured.";

    return (
        <div className={'space-y-6'}>
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-slate-300">
                    <Checkbox
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })}  />
                    <span>{`Enable Background Repairs`}</span>
                </label>
                <p className="text-xs leading-relaxed text-slate-400" id="enable-repairs-help">
                    {helpText}
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="healthcheck-concurrency-input">Health Check Concurrency</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.healthcheck-concurrency"] || "50") ? "border-red-500" : ""}`}
                    type="text"
                    id="healthcheck-concurrency-input"
                    aria-describedby="healthcheck-concurrency-help"
                    placeholder="50"
                    value={config["repair.healthcheck-concurrency"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.healthcheck-concurrency": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="healthcheck-concurrency-help">
                    The maximum number of concurrent NNTP connections used for health check STAT commands.
                    Lower values reduce connection pressure on your usenet providers during health checks.
                    Capped at your total provider pool size.
                </p>
            </div>
            <hr />
            <div className="space-y-2">
                <label className="block text-sm font-medium text-slate-200" htmlFor="library-dir-input">Library Directory</label>
                <Input
                    className={'w-full'}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <p className="text-xs leading-relaxed text-slate-400" id="library-dir-help">
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your NzbDAV container.
                </p>
            </div>
        </div>
    );
}

export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["repair.healthcheck-concurrency"] !== newConfig["repair.healthcheck-concurrency"]
        || config["media.library-dir"] !== newConfig["media.library-dir"];
}

export function isRepairsSettingsValid(newConfig: Record<string, string>) {
    const value = newConfig["repair.healthcheck-concurrency"];
    return value === undefined || value === "" || isPositiveInteger(value);
}
