import { Button } from "~/components/ui/button";
import { Spinner } from "~/components/ui/feedback";
import { Input, Select } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";
import { isMaskedSecret } from "~/utils/config-mask";

type ArrsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

interface ConnectionDetails {
    Host: string;
    ApiKey: string;
}

interface QueueRule {
    Message: string;
    Action: number;
}

interface ArrConfig {
    RadarrInstances: ConnectionDetails[];
    SonarrInstances: ConnectionDetails[];
    QueueRules: QueueRule[];
}

function parseArrConfig(value: string): ArrConfig {
    try {
        const parsed = JSON.parse(value);
        if (parsed &&
            Array.isArray(parsed.RadarrInstances) &&
            Array.isArray(parsed.SonarrInstances) &&
            Array.isArray(parsed.QueueRules)) {
            return parsed;
        }
    } catch {
    }

    return {
        RadarrInstances: [],
        SonarrInstances: [],
        QueueRules: [],
    };
}

const queueStatusMessages = [
    {
        display: "Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible.",
        searchTerm: "Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible."
    },
    {
        display: "Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.",
        searchTerm: "Found matching movie via grab history, but release was matched to movie by ID. Manual Import required."
    },
    {
        display: "Episode was not found in the grabbed release",
        searchTerm: "was not found in the grabbed release"
    },
    {
        display: "Episode(s) was/were unexpected considering the folder name",
        searchTerm: "unexpected considering the"
    },
    {
        display: "Not an upgrade for existing episode file(s)",
        searchTerm: "Not an upgrade for existing episode file(s)"
    },
    {
        display: "Not an upgrade for existing movie file",
        searchTerm: "Not an upgrade for existing movie file"
    },
    {
        display: "Not a Custom Format upgrade",
        searchTerm: "Not a Custom Format upgrade"
    },
    {
        display: "No files found are eligible for import",
        searchTerm: "No files found are eligible for import"
    },
    {
        display: "Episode file already imported",
        searchTerm: "Episode file already imported"
    },
    {
        display: "No audio tracks detected",
        searchTerm: "No audio tracks detected"
    },
    {
        display: "Invalid season or episode",
        searchTerm: "Invalid season or episode"
    },
    {
        display: "Single episode file contains all episodes in seasons",
        searchTerm: "Single episode file contains all episodes in seasons"
    },
    {
        display: "Unable to determine if file is a sample",
        searchTerm: "Unable to determine if file is a sample"
    },
    {
        display: "Sample",
        searchTerm: "Sample"
    },
    {
        display: "Found archive file, might need to be extracted",
        searchTerm: "Found archive file, might need to be extracted"
    },
];

export function ArrsSettings({ config, setNewConfig }: ArrsSettingsProps) {
    const arrConfig = parseArrConfig(config["arr.instances"]);

    const updateConfig = useCallback((newArrConfig: ArrConfig) => {
        setNewConfig({ ...config, "arr.instances": JSON.stringify(newArrConfig) });
    }, [config, setNewConfig]);

    const addRadarrInstance = useCallback(() => {
        updateConfig({
            ...arrConfig,
            RadarrInstances: [
                ...arrConfig.RadarrInstances,
                { Host: "", ApiKey: "" }
            ]
        });
    }, [arrConfig, updateConfig]);

    const removeRadarrInstance = useCallback((index: number) => {
        updateConfig({
            ...arrConfig,
            RadarrInstances: arrConfig.RadarrInstances
                .filter((_: any, i: number) => i !== index)
        });
    }, [arrConfig, updateConfig]);

    const updateRadarrInstance = useCallback((index: number, field: keyof ConnectionDetails, value: string) => {
        updateConfig({
            ...arrConfig,
            RadarrInstances: arrConfig.RadarrInstances
                .map((instance: any, i: number) =>
                    i === index ? { ...instance, [field]: value } : instance
                )
        });
    }, [arrConfig, updateConfig]);

    const addSonarrInstance = useCallback(() => {
        updateConfig({
            ...arrConfig,
            SonarrInstances: [
                ...arrConfig.SonarrInstances,
                { Host: "", ApiKey: "" }
            ]
        });
    }, [arrConfig, updateConfig]);

    const removeSonarrInstance = useCallback((index: number) => {
        updateConfig({
            ...arrConfig,
            SonarrInstances: arrConfig.SonarrInstances
                .filter((_: any, i: number) => i !== index)
        });
    }, [arrConfig, updateConfig]);

    const updateSonarrInstance = useCallback((index: number, field: keyof ConnectionDetails, value: string) => {
        updateConfig({
            ...arrConfig,
            SonarrInstances: arrConfig.SonarrInstances
                .map((instance: any, i: number) =>
                    i === index ? { ...instance, [field]: value } : instance
                )
        });
    }, [arrConfig, updateConfig]);

    const updateQueueAction = useCallback((searchTerm: string, action: number) => {
        // update the queue rule if it already exists
        const newQueueRules = (arrConfig.QueueRules || [])
            .filter((queueRule: QueueRule) => queueStatusMessages.map(x => x.searchTerm).includes(queueRule.Message))
            .map((queueRule: QueueRule) => queueRule.Message == searchTerm
                ? { Message: searchTerm, Action: action }
                : queueRule
            );

        // add the new queue rule if it doesn't already exist
        if (!newQueueRules.find((queueRule: QueueRule) => queueRule.Message == searchTerm))
            newQueueRules.push({ Message: searchTerm, Action: action });

        // update the config
        updateConfig({
            ...arrConfig,
            QueueRules: newQueueRules
        })
    }, [arrConfig, updateConfig])


    return (
        <div className={'space-y-6'}>
            <div className={'space-y-4'}>
                <div className={'flex items-center justify-between text-lg font-semibold text-white'}>
                    <div>Radarr Instances</div>
                    <Button variant="primary" size="small" onClick={addRadarrInstance}>
                        Add
                    </Button>
                </div>
                {arrConfig.RadarrInstances.length === 0 ? (
                    <p className={'rounded border border-slate-700/70 bg-slate-800/40 px-3 py-2 text-sm text-slate-400'}>No Radarr instances configured. Click on the "Add" button to get started.</p>
                ) : (
                    arrConfig.RadarrInstances.map((instance: any, index: number) =>
                        <InstanceForm
                            key={index}
                            instance={instance}
                            index={index}
                            type="radarr"
                            onUpdate={updateRadarrInstance}
                            onRemove={removeRadarrInstance}
                        />
                    )
                )}
            </div>
            <hr />
            <div className={'space-y-4'}>
                <div className={'flex items-center justify-between text-lg font-semibold text-white'}>
                    <div>Sonarr Instances</div>
                    <Button variant="primary" size="small" onClick={addSonarrInstance}>
                        Add
                    </Button>
                </div>
                {arrConfig.SonarrInstances.length === 0 ? (
                    <p className={'rounded border border-slate-700/70 bg-slate-800/40 px-3 py-2 text-sm text-slate-400'}>No Sonarr instances configured. Click on the "Add" button to get started.</p>
                ) : (
                    arrConfig.SonarrInstances.map((instance: any, index: number) =>
                        <InstanceForm
                            key={index}
                            instance={instance}
                            index={index}
                            type="sonarr"
                            onUpdate={updateSonarrInstance}
                            onRemove={removeSonarrInstance}
                        />
                    )
                )}
            </div>
            <hr />
            <div className={'space-y-4'}>
                <div className={'flex items-center justify-between text-lg font-semibold text-white'}>
                    <div>Automatic Queue Management</div>
                </div>
                <p className={'rounded border border-slate-700/70 bg-slate-800/40 px-3 py-2 text-sm text-slate-400'}>
                    Configure what to do for items stuck in Radarr / Sonarr queues.
                    Different actions can be configured for different status messages.
                    Only `usenet` queue items will be acted upon.
                </p>
                <ul>
                    {queueStatusMessages.map((queueStatusMessage, index) =>
                        <li key={index} className={'flex flex-col gap-2 border-b border-slate-700/50 py-3 sm:flex-row sm:items-center sm:justify-between'}>
                            <div className={'text-sm text-slate-300 sm:max-w-[70%]'}>{queueStatusMessage.display}</div>
                            <Select
                                className={'w-full'}
                                value={arrConfig.QueueRules.find((x: QueueRule) => x.Message == queueStatusMessage.searchTerm)?.Action ?? "0"}
                                onChange={e => updateQueueAction(queueStatusMessage.searchTerm, Number(e.target.value))}
                            >
                                <option value="0">Do Nothing</option>
                                <option value="1">Remove</option>
                                <option value="2">Remove and Blocklist</option>
                                <option value="3">Remove, Blocklist, and Search</option>
                            </Select>
                        </li>
                    )}
                </ul>
            </div>
        </div>
    );
}

interface InstanceFormProps {
    instance: ConnectionDetails;
    index: number;
    type: 'radarr' | 'sonarr';
    onUpdate: (index: number, field: keyof ConnectionDetails, value: string) => void;
    onRemove: (index: number) => void;
}

function InstanceForm({ instance, index, type, onUpdate, onRemove }: InstanceFormProps) {
    const [connectionState, setConnectionState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');

    useEffect(() => {
        setConnectionState('idle');
    }, [instance.Host, instance.ApiKey]);

    const testConnection = useCallback(async (host: string, apiKey: string) => {
        if (!host.trim() || !apiKey.trim() || isMaskedSecret(apiKey)) {
            return;
        }

        setConnectionState('testing');

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('apiKey', apiKey);

            const response = await fetch('/api/test-arr-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();

            if (result.status && result.connected) {
                setConnectionState('success');
            } else {
                setConnectionState('error');
            }
        } catch (error) {
            setConnectionState('error');
        }
    }, []);

    return (
        <div className={'relative rounded-lg border border-slate-700/70 bg-gray-800 p-4 shadow-md'}>
            <button
                className={'absolute right-2 top-2 rounded p-1 text-slate-400 hover:bg-white/10 hover:text-red-400'}
                onClick={() => onRemove(index)}
                aria-label="Remove instance"
            >
                <Icon name="close" className="!text-[18px]" />
            </button>
            <div className="space-y-4">
                <div className="space-y-2">
                    <label className="block text-sm font-medium text-slate-200">Host</label>
                    <div className="flex w-full">
                        <Input
                            type="text"
                            placeholder={type === "radarr" ? "http://localhost:7878" : "http://localhost:8989"}
                            value={instance.Host}
                            onChange={e => onUpdate(index, 'Host', e.target.value)} />
                        {instance.Host.trim() && instance.ApiKey.trim() && !isMaskedSecret(instance.ApiKey) && (
                            <Button
                                variant={connectionState === 'success' ? 'success' :
                                    connectionState === 'error' ? 'danger' : 'secondary'}
                                onClick={() => testConnection(instance.Host, instance.ApiKey)}
                                disabled={connectionState === 'testing'}
                                className={'shrink-0'}
                            >
                                {
                                    connectionState === 'testing' ? (
                                        <Spinner />
                                    ) : connectionState === 'success' ? (
                                        <Icon name="check" className="!text-[18px]" />
                                    ) : connectionState === 'error' ? (
                                        <Icon name="close" className="!text-[18px]" />
                                    ) : (
                                        'Test Conn'
                                    )
                                }
                            </Button>
                        )}
                    </div>
                </div>
                <div className="space-y-2">
                    <label className="block text-sm font-medium text-slate-200">API Key</label>
                    <Input
                        type="password"
                        className={'w-full'}
                        value={instance.ApiKey}
                        onChange={e => onUpdate(index, 'ApiKey', e.target.value)} />
                </div>
            </div>
        </div>
    );
}

export function isArrsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["arr.instances"] !== newConfig["arr.instances"];
}

export function isArrsSettingsValid(newConfig: Record<string, string>) {
    try {
        const arrConfig: ArrConfig = JSON.parse(newConfig["arr.instances"] || "{}");

        // Validate all Radarr instances
        for (const instance of arrConfig.RadarrInstances || []) {
            if (!isValidHost(instance.Host) || !isValidApiKey(instance.ApiKey)) {
                return false;
            }
        }

        // Validate all Sonarr instances
        for (const instance of arrConfig.SonarrInstances || []) {
            if (!isValidHost(instance.Host) || !isValidApiKey(instance.ApiKey)) {
                return false;
            }
        }

        return true;
    } catch {
        return false;
    }
}

function isValidHost(host: string): boolean {
    if (host.trim().length === 0) return false;
    try {
        new URL(host);
        return true;
    } catch {
        return false;
    }
}

function isValidApiKey(apiKey: string): boolean {
    return apiKey.trim().length > 0;
}