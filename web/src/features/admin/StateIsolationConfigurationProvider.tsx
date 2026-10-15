import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { useAdminDraftStatus } from './AdminDraftStatus';
import { applyIsolation, presetIsolation, previewIsolation, readIsolation,
  type IsolationDraft, type IsolationPlan, type IsolationPresetAction, type IsolationView } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';

const emptyDraft = (view: IsolationView): IsolationDraft => ({ settings: [], experienceEnabled: view.experienceEnabled, allowMaintenance: view.allowMaintenance });

function useIsolationController({ instanceId, active, archived, onUpdated, onDirtyChange }: {
  instanceId: string; active: boolean; archived: boolean; onUpdated: () => void; onDirtyChange: (dirty: boolean) => void;
}) {
  const [view, setView] = useState<IsolationView>(); const [draft, setDraft] = useState<IsolationDraft>();
  const [plan, setPlan] = useState<IsolationPlan>(); const [error, setError] = useState<string>(); const [busy, setBusy] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const presetRequest = useRef<AbortController | null>(null);
  const readRequest = useRef<AbortController | null>(null);
  // A preset may deliberately keep a flag false even when its saved baseline is false.
  const editedFlags = useRef(new Map<'experienceEnabled' | 'allowMaintenance', 'manual' | 'preset'>());
  const generation = useRef(0);
  useEffect(() => {
    generation.current++; editedFlags.current.clear(); setView(undefined); setDraft(undefined); setPlan(undefined); setError(undefined); setBusy(false); setPreviewing(false);
    return () => { generation.current++; presetRequest.current?.abort(); readRequest.current?.abort(); };
  }, [instanceId]);
  const changed = !!draft && !!view && (draft.settings.some(p => Object.keys(p.set).length > 0 || p.clear.length > 0)
    || draft.experienceEnabled !== view.experienceEnabled || draft.allowMaintenance !== view.allowMaintenance);
  useEffect(() => { onDirtyChange(changed); }, [changed, onDirtyChange]);
  const refresh = useCallback(async () => {
    readRequest.current?.abort();
    const controller = new AbortController(); readRequest.current = controller; setBusy(true);
    try {
      const next = await readIsolation(instanceId, controller.signal);
      if (controller.signal.aborted) return;
      setView(next);
      setDraft(current => current ? { ...current,
        experienceEnabled: editedFlags.current.has('experienceEnabled') ? current.experienceEnabled : next.experienceEnabled,
        allowMaintenance: editedFlags.current.has('allowMaintenance') ? current.allowMaintenance : next.allowMaintenance
      } : emptyDraft(next));
      setError(undefined);
    } catch (reason) {
      if (!controller.signal.aborted) { setError(reason instanceof Error ? reason.message : 'State & Isolation is unavailable. Retry.'); throw reason; }
    } finally { if (!controller.signal.aborted) setBusy(false); }
  }, [instanceId]);
  useEffect(() => {
    if (!active) { presetRequest.current?.abort(); readRequest.current?.abort(); setBusy(false); return; }
    void refresh().catch(() => {});
    return () => readRequest.current?.abort();
  }, [active, refresh]);
  useEffect(() => {
    if (!view || !draft) return;
    const controller = new AbortController(); setPlan(undefined); setPreviewing(true);
    const timer = setTimeout(() => {
      previewIsolation(instanceId, draft, controller.signal).then(next => { if (!controller.signal.aborted) { setPlan(next); setError(undefined); } })
        .catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Preview unavailable. Retry.'); })
        .finally(() => { if (!controller.signal.aborted) setPreviewing(false); });
    }, 200);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [instanceId, active, draft, view]);
  async function save(revision: number) {
    if (archived || !plan || previewing || plan.blockingReasons.length) throw new Error('Read a valid current preview before saving.');
    const request = generation.current; setBusy(true);
    try {
      let currentPlan = plan;
      // Earlier disjoint groups in this same Save may advance the Instance revision.
      // Keep the exact resulting revision and both independent opt-in fences.
      if (plan.expected.instance !== revision) {
        if (revision < plan.expected.instance) throw new Error('Configuration changed. Refresh the preview while retaining your draft.');
        currentPlan = await previewIsolation(instanceId, plan.draft, new AbortController().signal);
        if (currentPlan.expected.instance !== revision || currentPlan.expected.experience !== plan.expected.experience
          || currentPlan.expected.maintenance !== plan.expected.maintenance || currentPlan.blockingReasons.length)
          throw new Error('Configuration changed. Refresh the preview while retaining your draft.');
      }
      if (request !== generation.current) throw new Error('The Instance owner changed.');
      const saved = await applyIsolation(instanceId, currentPlan);
      if (request === generation.current) { editedFlags.current.clear(); setView(saved); setDraft(emptyDraft(saved)); setError(undefined); onUpdated(); }
      return saved.revisions.instance;
    } catch (reason) { if (request === generation.current) setError(reason instanceof Error ? reason.message : 'Apply failed. Your draft is retained.'); throw reason; }
    finally { if (request === generation.current) setBusy(false); }
  }
  const editor = useInstanceConfigurationEditor('State & Isolation', { dirty: changed, busy: busy || previewing, revision: view?.revisions.instance,
    blocked: !plan ? 'Read the State & Isolation preview before saving.' : plan.blockingReasons[0], save,
    discard, refresh });
  function editFlag(field: 'experienceEnabled' | 'allowMaintenance', value: boolean) {
    if (!view) return;
    if (value === view[field] && editedFlags.current.get(field) !== 'preset') editedFlags.current.delete(field);
    else editedFlags.current.set(field, editedFlags.current.get(field) ?? 'manual');
    setDraft(current => current ? { ...current, [field]: value } : current);
  }
  function discard() { if (view) { editedFlags.current.clear(); setDraft(emptyDraft(view)); } }
  function edit(section: string, field: string, value: SettingValue | undefined) {
    const saved = view?.settings.find(s => s.section === section);
    if (!saved || archived) return;
    setDraft(current => {
      if (!current) return current;
      const patch = current.settings.find(p => p.section === section) ?? { section, set: {}, clear: [] };
      const next = { ...patch, set: { ...patch.set }, clear: patch.clear.filter(f => f !== field) };
      if (value === undefined) { delete next.set[field]; if (field in saved.overrides) next.clear.push(field); }
      else if (value === saved.effective[field]) delete next.set[field];
      else next.set[field] = value;
      return { ...current, settings: [...current.settings.filter(p => p.section !== patch.section), next] };
    });
  }
  async function choose(preset: IsolationPresetAction) {
    presetRequest.current?.abort(); const controller = new AbortController(); presetRequest.current = controller; setBusy(true);
    try { const next = await presetIsolation(instanceId, preset, controller.signal); if (!controller.signal.aborted) {
      editedFlags.current.set('experienceEnabled', 'preset'); editedFlags.current.set('allowMaintenance', 'preset');
      setDraft(next.draft); setPlan(next); setError(undefined);
    } }
    catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Preset preview unavailable. Retry.'); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  }
  function reset(section: string) {
    const saved = view?.settings.find(s => s.section === section);
    if (!saved) return;
    setDraft(current => current ? { ...current, settings: [...current.settings.filter(p => p.section !== section),
      { section, set: {}, clear: Object.keys(saved.overrides).filter(field => section !== 'voice' || field === 'enabled') }] } : current);
  }
  const pages = draft?.settings.filter(p => Object.keys(p.set).length || p.clear.length).map(p => p.section === 'memoryPolicy' ? 'memory-policy' : p.section === 'voice' ? 'voice' : 'state-isolation') ?? [];
  useAdminDraftStatus(changed ? [...new Set(['state-isolation', ...pages])] : []);
  return { view, draft, plan, error, busy, previewing, changed, editor, refresh, save, discard, editFlag, edit, choose, reset };
}

const IsolationConfiguration = createContext<ReturnType<typeof useIsolationController> | null>(null);
export const useStateIsolationConfiguration = () => useContext(IsolationConfiguration);

/** One atomic draft for State policy, Memory and Voice enablement across the Instance workspace. */
export function StateIsolationConfigurationProvider({ children, ...props }: Parameters<typeof useIsolationController>[0] & { children: ReactNode }) {
  const controller = useIsolationController(props);
  return <IsolationConfiguration.Provider value={controller}>{children}</IsolationConfiguration.Provider>;
}
