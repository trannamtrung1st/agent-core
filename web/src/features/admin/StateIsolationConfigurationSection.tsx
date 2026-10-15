import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Flex, Form, Spin, Typography, theme } from 'antd';
import { StateIsolationPolicyFields, StateIsolationToggleField } from './StateIsolationPolicyFields';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { applyIsolation, deriveIsolationPreset, isolationValue, presetIsolation, previewIsolation, readIsolation,
  type IsolationDraft, type IsolationPlan, type IsolationPresetAction, type IsolationView } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';
import { followAppLink } from '../../app/appLink';
import { instancePath } from './adminNavigation';

import { isolationLabels as labels } from './stateIsolationPresentation';
import { IsolationPresetControl } from './IsolationPresetControl';
import { IsolationChangePreview } from './IsolationChangePreview';

const emptyDraft = (view: IsolationView): IsolationDraft => ({ settings: [], experienceEnabled: view.experienceEnabled, allowMaintenance: view.allowMaintenance });

export function StateIsolationConfigurationSection({ instanceId, active, archived, onUpdated, onDirtyChange }: {
  instanceId: string; active: boolean; archived: boolean; onUpdated: () => void; onDirtyChange: (dirty: boolean) => void;
}) {
  const { token } = theme.useToken();
  const [view, setView] = useState<IsolationView>(); const [draft, setDraft] = useState<IsolationDraft>();
  const [plan, setPlan] = useState<IsolationPlan>(); const [error, setError] = useState<string>(); const [busy, setBusy] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const presetRequest = useRef<AbortController | null>(null);
  const readRequest = useRef<AbortController | null>(null);
  // A preset may deliberately keep a flag false even when its saved baseline is false.
  const editedFlags = useRef(new Map<'experienceEnabled' | 'allowMaintenance', 'manual' | 'preset'>());
  const generation = useRef(0);
  useEffect(() => {
    generation.current++; editedFlags.current.clear(); setView(undefined); setDraft(undefined); setPlan(undefined); setError(undefined);
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
    if (!active) { presetRequest.current?.abort(); readRequest.current?.abort(); return; }
    void refresh().catch(() => {});
    return () => readRequest.current?.abort();
  }, [active, refresh]);
  useEffect(() => {
    if (!active || !view || !draft) return;
    const controller = new AbortController(); setPlan(undefined); setPreviewing(true);
    const timer = setTimeout(() => {
      previewIsolation(instanceId, draft, controller.signal).then(next => { if (!controller.signal.aborted) { setPlan(next); setError(undefined); } })
        .catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Preview unavailable. Retry.'); })
        .finally(() => { if (!controller.signal.aborted) setPreviewing(false); });
    }, 200);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [instanceId, active, draft, view]);
  async function save(revision: number) {
    if (!plan || previewing || plan.blockingReasons.length) throw new Error('Read a valid current preview before saving.');
    if (plan.expected.instance !== revision) throw new Error('Configuration changed. Refresh the preview while retaining your draft.');
    const request = generation.current; setBusy(true);
    try {
      const saved = await applyIsolation(instanceId, plan);
      if (request === generation.current) { editedFlags.current.clear(); setView(saved); setDraft(emptyDraft(saved)); setError(undefined); onUpdated(); }
      return saved.revisions.instance;
    } catch (reason) { if (request === generation.current) setError(reason instanceof Error ? reason.message : 'Apply failed. Your draft is retained.'); throw reason; }
    finally { if (request === generation.current) setBusy(false); }
  }
  const editor = useInstanceConfigurationEditor('State & Isolation', { dirty: changed, busy: busy || previewing, revision: view?.revisions.instance,
    blocked: !plan ? 'Read the State & Isolation preview before saving.' : plan.blockingReasons[0], save,
    discard: () => { if (view) { editedFlags.current.clear(); setDraft(emptyDraft(view)); } }, refresh });
  function editFlag(field: 'experienceEnabled' | 'allowMaintenance', value: boolean) {
    if (!view) return;
    if (value === view[field] && editedFlags.current.get(field) !== 'preset') editedFlags.current.delete(field);
    else editedFlags.current.set(field, editedFlags.current.get(field) ?? 'manual');
    setDraft(current => current ? { ...current, [field]: value } : current);
  }
  function edit(field: string, value: SettingValue | undefined) {
    setDraft(current => {
      if (!current) return current;
      const patch = current.settings.find(p => p.section === 'stateIsolationPolicy') ?? { section: 'stateIsolationPolicy', set: {}, clear: [] };
      const next = { ...patch, set: { ...patch.set }, clear: patch.clear.filter(f => f !== field) };
      if (value === undefined) { delete next.set[field]; next.clear.push(field); } else next.set[field] = value;
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
  const state = view?.settings.find(s => s.section === 'stateIsolationPolicy');
  return <Flex vertical gap={token.padding}>
    {error && <Alert type="error" showIcon title={error} action={<Button onClick={() => void refresh().catch(() => {})}>Retry isolation</Button>} />}
    {!view && busy && <Spin aria-label="Loading State & Isolation" />}
    {view && draft && state && <>
      <AgentConfigurationPanel title="Continuity and resource policy" label="State & Isolation configuration" bodyGap="section"
        description="Presets prepare editable drafts. Existing history, home files and browser profile bytes are retained.">
        <Form layout="vertical" className="admin-config-form instance-settings-form">
          <Flex vertical gap={token.padding}>
            <IsolationPresetControl value={deriveIsolationPreset(view, draft)} disabled={archived || busy} dirty={changed}
              active={active} scope="Instance" useDefinitionDefaults onSelect={preset => void choose(preset)} />
            <StateIsolationPolicyFields value={field => isolationValue(view, draft, state.section, field)}
              choices={field => state.constraints?.[field]?.choices ?? []}
              disabled={field => archived || busy || state.constraints?.[field]?.requiredBoolean === false}
              onChange={edit} extra={field => {
              const patch = draft.settings.find(p => p.section === state.section);
              const source = patch?.clear.includes(field) ? 'Will inherit on save' : patch && field in patch.set ? 'Unsaved Instance override' : state.sources[field] === 'instance' ? 'Instance override' : 'Inherited Definition';
              return <Flex wrap align="center" gap={token.paddingXS}><Typography.Text type="secondary">{source}</Typography.Text>
                {!patch?.clear.includes(field) && (field in state.overrides || patch && field in patch.set) && <Button size="small" type="link" disabled={archived || busy} aria-label={`Inherit ${labels[field] ?? field}`} onClick={() => edit(field, undefined)}>Inherit</Button>}</Flex>;
            }}>
              <StateIsolationToggleField label="Experience enablement" checked={draft.experienceEnabled} disabled={archived || busy}
                extra="The existing Experience owner is updated in the same transaction." onChange={v => editFlag('experienceEnabled', v)} />
              <StateIsolationToggleField label="Autonomous identity maintenance" checked={draft.allowMaintenance} disabled={archived || busy}
                extra="The existing maintenance owner is updated in the same transaction." onChange={v => editFlag('allowMaintenance', v)} />
            </StateIsolationPolicyFields>
          </Flex>
        </Form>
        <Flex wrap gap={token.paddingSM}>{(['memory-policy', 'voice', 'experience', 'memory', 'credentials'] as const).map(page => <a key={page} href={instancePath(instanceId, page)} onClick={followAppLink}>{page === 'memory-policy' ? 'Edit detailed Memory policy' : page === 'voice' ? 'Edit Voice settings' : page === 'credentials' ? 'Inspect shared credentials' : page === 'memory' ? 'Inspect learned Memory' : 'Inspect Experience'}</a>)}</Flex>
        <Typography.Text type="secondary">Shared email, Drive and other bound accounts retain their own authority and external effects. Conversation isolation does not isolate those accounts.</Typography.Text>
      </AgentConfigurationPanel>
      <IsolationChangePreview changes={plan?.changes} notice={plan?.notice} blockingReasons={plan?.blockingReasons} busy={previewing}>
        {!editor.managed && <Button type="primary" disabled={archived || busy || previewing || !changed || !plan || !!plan.blockingReasons.length} onClick={() => { if (view) void save(view.revisions.instance).catch(() => {}); }}>Apply changes</Button>}
      </IsolationChangePreview>
    </>}
  </Flex>;
}
