import { AdminRequestError } from '../../services/adminApi';
import { App } from 'antd';
import { showAdminFailure } from './adminFailure';
import { useCallback, useEffect, useRef, useState } from 'react';
import { listInstanceSettings, patchInstanceSettings, type SettingValue, type SettingsSection } from '../../services/instanceConfiguration';
import { listModels } from '../../services/api';
import { describeAdminError } from './adminErrors';
export type SettingsDraft = { set: Record<string, SettingValue>; clear: string[] };
export const emptyDraft = (): SettingsDraft => ({ set: {}, clear: [] });
/** Exact-owner reads and independent sparse section drafts; pages only select presentation. */
export function useInstanceSettingsController(instanceId: string, archived: boolean, active: boolean, onUpdated: () => void) {
  const { message } = App.useApp();
  const [sections, setSections] = useState<SettingsSection[] | null>(null);
  const [drafts, setDrafts] = useState<Record<string, SettingsDraft>>({});
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [models, setModels] = useState<{ key: string; displayName: string; supportedReasoningEfforts: string[] }[]>([]);
  const [defaultModelKey, setDefaultModelKey] = useState<string | null>(null);
  const epoch = useRef(0); const saving = useRef(false); const owner = useRef(instanceId); owner.current = instanceId;
  useEffect(() => { epoch.current++; saving.current = false; setSections(null); setDrafts({}); setError(null); setBusy(false); return () => { epoch.current++; }; }, [instanceId]);
  const reload = useCallback(async () => {
    const request = ++epoch.current; setLoading(true); setError(null); setSections(null);
    try { const result = await listInstanceSettings(instanceId); if (owner.current === instanceId && request === epoch.current) setSections(result); }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to read settings. Retry before saving; drafts are retained.').message); }
    finally { if (request === epoch.current) setLoading(false); }
  }, [instanceId]);
  // A pending save owns its refresh; re-entering the tab must not supersede that read.
  useEffect(() => { if (active && !saving.current) void reload(); }, [reload, active]);
  useEffect(() => { let current = true; listModels().then(c => { if (current) { setModels(c.models); setDefaultModelKey(c.defaultKey); } }).catch(() => {}); return () => { current = false; }; }, []);
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
      await patchInstanceSettings(instanceId, section, draft.set, draft.clear);
      if (request !== epoch.current || owner.current !== instanceId) return;
      committed = true;
      setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
      onUpdated();
      // Resolving one section can also change another (for example Voice disables speech aliases).
      setSections(null); setLoading(true);
      const refreshed = await listInstanceSettings(instanceId);
      if (request === epoch.current && owner.current === instanceId) setSections(refreshed);
    } catch (e) {
      if (request === epoch.current) {
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
    const request = epoch.current; let nextRevision = expectedRevision; let committed = 0;
    saving.current = true; setBusy(true); setError(null);
    try {
      for (const section of pending) {
        const draft = drafts[section.section];
        const updated = await patchInstanceSettings(instanceId, { ...section, instanceRevision: nextRevision }, draft.set, draft.clear);
        if (request !== epoch.current || owner.current !== instanceId) throw new Error('The Instance owner changed.');
        nextRevision = updated.instanceRevision; committed++;
        setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
      }
      setSections(null); setLoading(true);
      const refreshed = await listInstanceSettings(instanceId);
      if (request === epoch.current && owner.current === instanceId) setSections(refreshed);
      return nextRevision;
    } catch (reason) {
      const failure = describeAdminError(reason, 'Settings could not be saved.');
      const message = `${committed ? `${committed} settings section(s) were saved. ` : ''}${failure.message} Refresh settings; uncommitted edits are retained.`;
      if (request === epoch.current) setError(message);
      throw new AdminRequestError(message, failure.diagnosticId);
    } finally { if (request === epoch.current) { saving.current = false; setBusy(false); setLoading(false); } }
  }
  return { sections, drafts, setDrafts, loading, busy, error, models, defaultModelKey, reload, change, inherit, save, saveAll };
}
