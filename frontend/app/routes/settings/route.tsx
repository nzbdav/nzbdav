import { Button } from "~/components/ui/button";
import { TabPanel, Tabs, type TabOption } from "~/components/ui/tabs";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { isUsenetSettingsUpdated, UsenetSettings } from "./usenet/usenet";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsUpdated, isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsUpdated, isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { isIndexersSettingsUpdated, isIndexersSettingsValid, IndexersSettings } from "./indexers/indexers";
import { isProfilesSettingsUpdated, isProfilesSettingsValid, ProfilesSettings } from "./profiles/profiles";
import { isMaintenanceSettingsUpdated, Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsUpdated, RepairsSettings } from "./repairs/repairs";
import { isWatchdogSettingsUpdated, WatchdogSettings } from "./watchdog/watchdog";
import { isPreflightSettingsUpdated, PreflightSettings } from "./preflight/preflight";
import { isWatchtowerSettingsUpdated, WatchtowerSettings } from "./watchtower/watchtower";
import { isWardenSettingsUpdated, WardenSettings } from "./warden/warden";
import { isRcloneSettingsUpdated, RcloneSettings } from "./rclone/rclone";
import { useCallback, useState } from "react";
import { useBlocker } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { getAppVersion } from "~/utils/version.server";

const defaultConfig = {
    "general.base-url": "",
    "api.key": "",
    "api.categories": "",
    "api.manual-category": "uncategorized",
    "api.ensure-importable-video": "true",
    "api.ensure-article-existence-categories": "",
    "api.ignore-history-limit": "true",
    "api.download-file-blocklist": "*.nfo, *.par2, *.sfv, *sample.mkv",
    "api.duplicate-nzb-behavior": "increment",
    "api.import-strategy": "symlinks",
    "api.completed-downloads-dir": "",
    "api.user-agent": "",
    "api.search-user-agent": "",
    "usenet.providers": "",
    "usenet.max-download-connections": "0",
    "usenet.max-download-connections-per-stream": "false",
    "usenet.max-download-connections-per-stream-preset": "high",
    "usenet.max-queue-connections": "",
    "usenet.streaming-priority": "80",
    "usenet.article-buffer-size": "40",
    "usenet.pipelined-body-requests": "true",
    "usenet.segment-cache.enabled": "false",
    "usenet.segment-cache.path": "/config/segment-cache",
    "usenet.segment-cache.max-gb": "10",
    "usenet.pipelining.enabled": "false",
    "usenet.pipelining.depth": "8",
    "usenet.cascade.enabled": "false",
    "webdav.user": "admin",
    "webdav.pass": "",
    "webdav.show-hidden-files": "false",
    "webdav.enforce-readonly": "true",
    "webdav.preview-par2-files": "false",
    "rclone.rc-enabled": "false",
    "rclone.host": "",
    "rclone.user": "",
    "rclone.pass": "",
    "rclone.mount-dir": "",
    "media.library-dir": "",
    "arr.instances": "{\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}",
    "indexers.instances": "{\"Indexers\":[]}",
    "profiles.instances": "{\"Profiles\":[]}",
    "play.watchdog-enabled": "true",
    "play.total-budget-seconds": "30",
    "play.hedge-delay-seconds": "3",
    "play.max-candidates": "3",
    "play.max-attempts": "10",
    "play.verify-mode": "none",
    "play.candidate-negative-cache-minutes": "5",
    "grab.stall-failover-enabled": "true",
    "grab.stall-failover-window-seconds": "2",
    "grab.stall-failover-ceiling-seconds": "5",
    "search.exclude-patterns": "",
    "variants.mode": "off",
    "variants.tolerance-pct": "25",
    "variants.max-per-group": "3",
    "variants.replay-strategy": "closest-to-click",
    "variants.fallback-on-failure": "true",
    "variants.eviction-strategy": "lru",
    "variants.eviction-active-grace-seconds": "60",
    "preflight.mode": "off",
    "preflight.max-attempts": "20",
    "preflight.ttl-seconds": "120",
    "preflight.indexer-max-wait-seconds": "5",
    "repair.enable": "false",
    "repair.healthcheck-concurrency": "50",
    "db.is-startup-vacuum-enabled": "false",
    "database.history-retention-days": "90",
    "database.healthcheck-retention-days": "30",
    "maintenance.remove-orphaned-schedule-enabled": "false",
    "maintenance.remove-orphaned-schedule-time": "0",
    "api.nzb-backup-enabled": "false",
    "api.nzb-backup-location": "",
    "watchtower.enabled": "false",
    "watchtower.profile-token": "",
    "watchtower.ranking": "watchdog",
    "watchtower.size-floor-bytes": "524288000",
    "watchtower.size-ceiling-bytes": "0",
    "watchtower.shortlist-depth": "2",
    "watchtower.grab-cap-per-resolve": "3",
    "watchtower.active-set-cap": "100",
    "watchtower.daily-resolve-budget": "60",
    "watchtower.auto-throughput": "false",
    "watchtower.sync-interval-seconds": "3600",
    "watchtower.series-scope": "latest-season",
    "watchtower.season-bundles": "true",
    "watchtower.series-max-episodes": "50",
    "watchtower.series-cap-keep": "newest",
    "watchtower.series-recent-count": "3",
    "watchtower.season-bundle-fallback": "false",
    "watchtower.season-bundle-fallback-scope": "latest-season",
    "watchtower.season-bundle-fallback-recent-count": "2",
    "watchtower.season-bundle-fallback-max-episodes": "50",
    "watchtower.min-grabs": "0",
    "watchtower.verify-sample-count": "3",
    "watchtower.verify-timeout-seconds": "10",
    "watchtower.keepfresh-base-seconds": "21600",
    "watchtower.keepfresh-max-seconds": "604800",
    "watchtower.unavailable-retry-seconds": "21600",
    "watchtower.verbose-logging": "false",
    "warden.hide-dead": "true",
    "warden.quorum": "2",
    "warden.backbone-scope": "true",
}

export async function loader({ request }: Route.LoaderArgs) {
    // fetch the config items
    const configItems = await backendClient.getConfig(Object.keys(defaultConfig));

    // transform to a map
    const config: Record<string, string> = defaultConfig;
    for (const item of configItems) {
        config[item.configName] = item.configValue;
    }

    return {
        config: config,
        appVersion: (await getAppVersion()) ?? "unknown",
    }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body {...props.loaderData} />
    );
}

type BodyProps = {
    config: Record<string, string>,
    appVersion: string,
};

function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = useState(props.config);
    const [newConfig, setNewConfig] = useState(config);
    const [isSaving, setIsSaving] = useState(false);
    const [isSaved, setIsSaved] = useState(false);
    const [saveError, setSaveError] = useState<string | null>(null);
    type SettingsTab =
        | "usenet"
        | "indexers"
        | "profiles"
        | "watchdog"
        | "preflight"
        | "watchtower"
        | "warden"
        | "sabnzbd"
        | "webdav"
        | "arrs"
        | "repairs"
        | "rclone"
        | "maintenance";
    const [activeTab, setActiveTab] = useState<SettingsTab>("usenet");

    // derived variables
    const iseUsenetUpdated = isUsenetSettingsUpdated(config, newConfig);
    const isSabnzbdUpdated = isSabnzbdSettingsUpdated(config, newConfig);
    const isWebdavUpdated = isWebdavSettingsUpdated(config, newConfig);
    const isArrsUpdated = isArrsSettingsUpdated(config, newConfig);
    const isIndexersUpdated = isIndexersSettingsUpdated(config, newConfig);
    const isProfilesUpdated = isProfilesSettingsUpdated(config, newConfig);
    const isRepairsUpdated = isRepairsSettingsUpdated(config, newConfig);
    const isWatchdogUpdated = isWatchdogSettingsUpdated(config, newConfig);
    const isPreflightUpdated = isPreflightSettingsUpdated(config, newConfig);
    const isRcloneUpdated = isRcloneSettingsUpdated(config, newConfig);
    const isMaintenanceUpdated = isMaintenanceSettingsUpdated(config, newConfig);
    const isWatchtowerUpdated = isWatchtowerSettingsUpdated(config, newConfig);
    const isWardenUpdated = isWardenSettingsUpdated(config, newConfig);
    const isUpdated = iseUsenetUpdated || isSabnzbdUpdated || isWebdavUpdated || isArrsUpdated || isIndexersUpdated || isProfilesUpdated || isRepairsUpdated || isWatchdogUpdated || isPreflightUpdated || isRcloneUpdated || isMaintenanceUpdated || isWatchtowerUpdated || isWardenUpdated;
    const navigationBlocker = useNavigationBlocker(isUpdated);

    const usenetTitle = iseUsenetUpdated ? "Usenet •" : "Usenet";
    const indexersTitle = isIndexersUpdated ? "Indexers •" : "Indexers";
    const profilesTitle = isProfilesUpdated ? "Search Profiles •" : "Search Profiles";
    const watchdogTitle = isWatchdogUpdated ? "Watchdog •" : "Watchdog";
    const preflightTitle = isPreflightUpdated ? "Preflight •" : "Preflight";
    const watchtowerTitle = isWatchtowerUpdated ? "Watchtower •" : "Watchtower";
    const wardenTitle = isWardenUpdated ? "Warden •" : "Warden";
    const sabnzbdTitle = isSabnzbdUpdated ? "SABnzbd •" : "SABnzbd";
    const webdavTitle = isWebdavUpdated ? "WebDAV •" : "WebDAV";
    const arrsTitle = isArrsUpdated ? "Radarr/Sonarr •" : "Radarr/Sonarr";
    const repairsTitle = isRepairsUpdated ? "Repairs •" : "Repairs";
    const rcloneTitle = isRcloneUpdated ? "Rclone Server •" : "Rclone Server";
    const maintenanceTitle = isMaintenanceUpdated ? "Maintenance •" : "Maintenance";
    const tabOptions: TabOption<SettingsTab>[] = [
        { id: "usenet", label: usenetTitle, icon: "cloud" },
        { id: "indexers", label: indexersTitle, icon: "travel_explore" },
        { id: "profiles", label: profilesTitle, icon: "tune" },
        { id: "watchdog", label: watchdogTitle, icon: "monitor_heart" },
        { id: "preflight", label: preflightTitle, icon: "fact_check" },
        { id: "watchtower", label: watchtowerTitle, icon: "cell_tower" },
        { id: "warden", label: wardenTitle, icon: "shield" },
        { id: "sabnzbd", label: sabnzbdTitle, icon: "download" },
        { id: "webdav", label: webdavTitle, icon: "folder_shared" },
        { id: "arrs", label: arrsTitle, icon: "sync_alt" },
        { id: "repairs", label: repairsTitle, icon: "build" },
        { id: "rclone", label: rcloneTitle, icon: "dns" },
        { id: "maintenance", label: maintenanceTitle, icon: "settings_suggest" },
    ];

    const saveButtonLabel = isSaving ? "Saving..."
        : !isUpdated && isSaved ? "Saved"
        : !isUpdated && !isSaved ? "There are no changes to save"
        : isSabnzbdUpdated && !isSabnzbdSettingsValid(newConfig) ? "Invalid SABnzbd settings"
        : isWebdavUpdated && !isWebdavSettingsValid(newConfig) ? "Invalid WebDAV settings"
        : isArrsUpdated && !isArrsSettingsValid(newConfig) ? "Invalid Arrs settings"
        : isIndexersUpdated && !isIndexersSettingsValid(newConfig) ? "Invalid Indexers settings"
        : isProfilesUpdated && !isProfilesSettingsValid(newConfig) ? "Invalid Search Profiles settings"
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = useCallback(() => {
        setNewConfig(config);
        setIsSaved(false);
        setSaveError(null);
    }, [config, setNewConfig]);

    const onSave = useCallback(async () => {
        setIsSaving(true);
        setIsSaved(false);
        setSaveError(null);
        try {
            const response = await fetch("/settings/update", {
                method: "POST",
                body: (() => {
                    const form = new FormData();
                    const changedConfig = getChangedConfig(config, newConfig);
                    form.append("config", JSON.stringify(changedConfig));
                    return form;
                })()
            });
            if (!response.ok) {
                throw new Error(`Settings update failed with status ${response.status}`);
            }
            setConfig(newConfig);
            setIsSaved(true);
        } catch {
            setSaveError("Could not save settings. Check the server logs and try again.");
        } finally {
            setIsSaving(false);
        }
    }, [config, newConfig, setIsSaving, setIsSaved, setConfig]);

    return (
        <div className="flex flex-col gap-6 px-4 py-4 md:px-8">
            <Tabs options={tabOptions} value={activeTab} onChange={setActiveTab} />
            <TabPanel>
                {activeTab === "usenet" && <UsenetSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "indexers" && <IndexersSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "profiles" && <ProfilesSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "watchdog" && <WatchdogSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "preflight" && <PreflightSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "watchtower" && <WatchtowerSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "warden" && <WardenSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "sabnzbd" && <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} appVersion={props.appVersion} />}
                {activeTab === "webdav" && <WebdavSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "arrs" && <ArrsSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "repairs" && <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "rclone" && <RcloneSettings config={newConfig} setNewConfig={setNewConfig} />}
                {activeTab === "maintenance" && <Maintenance savedConfig={config} config={newConfig} setNewConfig={setNewConfig} />}
            </TabPanel>
            {saveError && (
                <div role="alert" className="rounded border border-red-500/50 bg-red-500/10 px-3 py-2 text-sm text-red-200">
                    {saveError}
                </div>
            )}
            <div className="flex flex-wrap justify-end gap-2 border-t border-slate-700/70 pt-4">
                {isUpdated && <Button
                    className="min-w-28"
                    variant="secondary"
                    disabled={!isUpdated}
                    onClick={onClear}>
                    Clear
                </Button>}
                <Button
                    className="min-w-28"
                    variant={saveButtonVariant}
                    disabled={isSaveButtonDisabled}
                    onClick={onSave}>
                    {saveButtonLabel}
                </Button>
            </div>
            <ConfirmModal
                show={navigationBlocker.showConfirmation}
                title="Unsaved Changes"
                message={<>You have unsaved changes.<br/>Are you sure you want to leave this page?</>}
                cancelText="Stay"
                confirmText="Leave"
                onCancel={navigationBlocker.onCancelNavigation}
                onConfirm={navigationBlocker.onConfirmNavigation}
            />
        </div>
    );
}

function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>
): Record<string, string> {
    let changedConfig: Record<string, string> = {};
    let configKeys = Object.keys(defaultConfig);
    for (const configKey of configKeys) {
        if (config[configKey] !== newConfig[configKey]) {
            changedConfig[configKey] = newConfig[configKey];
        }
    }
    return changedConfig;
}

function useNavigationBlocker(isConfigUpdated: boolean) {
    const blocker = useBlocker(isConfigUpdated);

    const onConfirmNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.proceed();
        }
    }, [blocker]);

    const onCancelNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.reset();
        }
    }, [blocker]);

    return {
        showConfirmation: blocker.state === "blocked",
        onConfirmNavigation,
        onCancelNavigation
    }
}