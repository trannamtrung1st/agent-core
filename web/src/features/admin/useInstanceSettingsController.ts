import { AdminRequestError } from '../../services/adminApi';
import { App } from 'antd';
import { showAdminFailure } from './adminFailure';
import { useCallback, useEffect, useRef, useState } from 'react';
import { listInstanceSettings, patchInstanceSettings, type SettingValue, type SettingsSection } from '../../services/instanceConfiguration';
import { listModels } from '../../services/api';
import { describeAdminError } from './adminErrors';
import { IdentityCompletionError } from '../../services/identity';
export type SettingsDraft = { set: Record<string, SettingValue>; clear: string[] };
export const emptyDraft = (): SettingsDraft => ({ set: {}, clear: [] });
export type InstanceSettingsTransport = {
  list: (id: string, signal?: AbortSignal) => Promise<SettingsSection[]>;
  patch: (id: string, section: SettingsSection, set: Record<string, SettingValue>, clear: string[], signal?: AbortSignal) => Promise<SettingsSection>;
  models: (signal?: AbortSignal) => ReturnType<typeof listModels>;
};
const ownerSettingsTransport: InstanceSettingsTransport = { list: id => listInstanceSettings(id), patch: (id, section, set, clear) => patchInstanceSettings(id, section, set, clear), models: () => listModels() };
/** Exact-owner reads and independent sparse section drafts; pages only select presentation. */
export function useInstanceSettingsController(instanceId: string, archived: boolean, active: boolean, onUpdated: () => void, externalRevision?: number, deferRefresh = false, transport = ownerSettingsTransport) {
  const { message } = App.useApp();
  const [sections, setSections] = useState<SettingsSection[] | null>(null);
  const [drafts, setDrafts] = useState<Record<string, SettingsDraft>>({});
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [models, setModels] = useState<{ key: string; displayName: string; supportedReasoningEfforts: string[] }[]>([]);
  const [defaultModelKey, setDefaultModelKey] = useState<string | null>(null);
  const [modelError, setModelError] = useState(false); const [modelAttempt, setModelAttempt] = useState(0);
  const epoch = useRef(0); const saving = useRef(false); const owner = useRef(instanceId); owner.current = instanceId;
  const scope = useRef(new AbortController()); const read = useRef<AbortController | null>(null);
  useEffect(() => { const controller = new AbortController(); scope.current = controller; epoch.current++; saving.current = false; setSections(null); setDrafts({}); setError(null); setBusy(false); return () => { epoch.current++; controller.abort(); read.current?.abort(); }; }, [instanceId, transport]);
  const reload = useCallback(async () => {
    read.current?.abort(); const controller = new AbortController(); read.current = controller;
    const request = ++epoch.current; setLoading(true); setError(null); setSections(null);
    try { const result = await transport.list(instanceId, controller.signal); if (owner.current === instanceId && request === epoch.current) setSections(result); }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to read settings. Retry before saving; drafts are retained.').message); }
    finally { if (request === epoch.current) setLoading(false); }
  }, [instanceId, transport]);
  // A pending save owns its refresh; re-entering the tab must not supersede that read.
  useEffect(() => { if (active && !saving.current && !deferRefresh) void reload(); }, [reload, active, externalRevision, deferRefresh]);
  useEffect(() => { const controller = new AbortController(); setModels([]); setDefaultModelKey(null); setModelError(false); transport.models(controller.signal).then(c => { if (!controller.signal.aborted) { setModels(c.models); setDefaultModelKey(c.defaultKey); } }).catch(() => { if (!controller.signal.aborted) setModelError(true); }); return () => controller.abort(); }, [transport, modelAttempt]);
  function change(section: SettingsSection, field: string, value: SettingValue) {
    setDrafts(previous => {
      const draft = previous[section.section] ?? emptyDraft(); const set = { ...draft.set };
      if (JSON.stringify(value) === JSON.stringify(section.effective[field])) delete set[field]; else set[field] = value;
      return { ...previous, [section.section]: { set, clear: draft.clear.filter(k => k !== field) } };
    });
  }
  function inherit(section: SettingsSection, field: string) {
    setDrafts(previous => { const draft = previous[section.section] ?? emptyDraft(); const set = { ...draft.set }; delete set[field];
      return { ...previous, [section.section]: { set, clear: [...new Set([...draft.clear, field])] } }; });
  }
  async function save(section: SettingsSection, reset = false) {
    const draft = reset ? { set: {}, clear: Object.keys(section.overrides) } : drafts[section.section] ?? emptyDraft();
    if (archived || busy || !sections || !Object.keys(draft.set).length && !draft.clear.length) return;
    const request = epoch.current; let committed = false; saving.current = true; setBusy(true); setError(null);
    try {
      await transport.patch(instanceId, section, draft.set, draft.clear, scope.current.signal);
      if (request !== epoch.current || owner.current !== instanceId) return;
      committed = true;
      setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
      onUpdated();
      // Resolving one section can also change another (for example Voice disables speech aliases).
      setSections(null); setLoading(true);
      const refreshed = await transport.list(instanceId, scope.current.signal);
      if (request === epoch.current && owner.current === instanceId) setSections(refreshed);
    } catch (e) {
      if (request === epoch.current) {
        if (e instanceof IdentityCompletionError) { committed = true; setDrafts(current => ({ ...current, [section.section]: emptyDraft() })); setSections(null); }
        const detail = describeAdminError(e, committed ? 'Settings could not be refreshed.' : 'Settings could not be saved. Reload while retaining your draft.');
        const text = committed ? `Settings were saved. ${detail.message} Retry settings; other drafts are retained.` : detail.message;
        setError(text);
        showAdminFailure(message, new AdminRequestError(text, detail.diagnosticId), 'Settings could not be saved.', committed ? 'Refresh failed' : 'Save failed');
      }
    }
    finally { if (request === epoch.current) { saving.current = false; setBusy(false); setLoading(false); } }
  }
  async function saveAll(expectedRevision: number): Promise<number> {
    if (archived || saving.current || !sections) throw new Error('Read current settings before saving.');
    const pending = sections.filter(section => { const draft = drafts[section.section]; return draft && (Object.keys(draft.set).length || draft.clear.length); });
    const request = epoch.current; let nextRevision = expectedRevision; let nextSecurityRevision = sections[0]?.securityRevision; let committed = 0;
    saving.current = true; setBusy(true); setError(null);
    try {
      for (const section of pending) {
        const draft = drafts[section.section];
        let updated: SettingsSection;
        try { updated = await transport.patch(instanceId, { ...section, instanceRevision: nextRevision, ...(nextSecurityRevision === undefined ? {} : { securityRevision: nextSecurityRevision }) }, draft.set, draft.clear, scope.current.signal); }
        catch (error) { if (error instanceof IdentityCompletionError && request === epoch.current) { committed++; setDrafts(current => ({ ...current, [section.section]: emptyDraft() })); setSections(null); } throw error; }
        if (request !== epoch.current || owner.current !== instanceId) throw new Error('The Instance owner changed.');
        nextRevision = updated.instanceRevision; nextSecurityRevision = updated.securityRevision; committed++;
        setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
      }
      setSections(null); setLoading(true);
      const refreshed = await transport.list(instanceId, scope.current.signal);
      if (request === epoch.current && owner.current === instanceId) setSections(refreshed);
      return nextRevision;
    } catch (reason) {
      const failure = describeAdminError(reason, 'Settings could not be saved.');
      const message = `${committed ? `${committed} settings section(s) were saved. ` : ''}${failure.message} Refresh settings; uncommitted edits are retained.`;
      if (request === epoch.current) setError(message);
      throw new AdminRequestError(message, failure.diagnosticId);
    } finally { if (request === epoch.current) { saving.current = false; setBusy(false); setLoading(false); } }
  }
  return { sections, drafts, setDrafts, loading, busy, error, models, defaultModelKey, modelError, reloadModels: () => setModelAttempt(value => value + 1), reload, change, inherit, save, saveAll };
}
