import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Descriptions, Flex, Form, Modal, Select, Spin, Switch, Typography, theme } from 'antd';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { applyIsolation, deriveIsolationPreset, isolationValue, presetIsolation, previewIsolation, readIsolation,
  type IsolationDraft, type IsolationPlan, type IsolationPreset, type IsolationView } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';
import { followAppLink } from '../../app/appLink';
import { instancePath } from './adminNavigation';

import { isolationLabels as labels, isolationChoiceLabels as choiceLabels, isolationOwnerLabels } from './stateIsolationPresentation';

const emptyDraft = (view: IsolationView): IsolationDraft => ({ settings: [], experienceEnabled: view.experienceEnabled, allowMaintenance: view.allowMaintenance });
const display = (value: SettingValue) => typeof value === 'boolean' ? value ? 'On' : 'Off' : value == null ? 'None' : choiceLabels[String(value)] ?? String(value);

export function StateIsolationConfigurationSection({ instanceId, active, archived, onUpdated, onDirtyChange }: {
  instanceId: string; active: boolean; archived: boolean; onUpdated: () => void; onDirtyChange: (dirty: boolean) => void;
}) {
  const { token } = theme.useToken();
  const [view, setView] = useState<IsolationView>(); const [draft, setDraft] = useState<IsolationDraft>();
  const [plan, setPlan] = useState<IsolationPlan>(); const [error, setError] = useState<string>(); const [busy, setBusy] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [pendingPreset, setPendingPreset] = useState<IsolationPreset>();
  const presetRequest = useRef<AbortController | null>(null);
  const readRequest = useRef<AbortController | null>(null);
  const generation = useRef(0);
  useEffect(() => {
    generation.current++; setView(undefined); setDraft(undefined); setPlan(undefined); setError(undefined); setPendingPreset(undefined);
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
      setView(next); setDraft(current => current ?? emptyDraft(next)); setError(undefined);
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
      if (request === generation.current) { setView(saved); setDraft(emptyDraft(saved)); setError(undefined); onUpdated(); }
      return saved.revisions.instance;
    } catch (reason) { if (request === generation.current) setError(reason instanceof Error ? reason.message : 'Apply failed. Your draft is retained.'); throw reason; }
    finally { if (request === generation.current) setBusy(false); }
  }
  const editor = useInstanceConfigurationEditor('State & Isolation', { dirty: changed, busy: busy || previewing, revision: view?.revisions.instance,
    blocked: !plan ? 'Read the State & Isolation preview before saving.' : plan.blockingReasons[0], save,
    discard: () => { if (view) setDraft(emptyDraft(view)); }, refresh });
  function edit(field: string, value: SettingValue | undefined) {
    setDraft(current => {
      if (!current) return current;
      const patch = current.settings.find(p => p.section === 'stateIsolationPolicy') ?? { section: 'stateIsolationPolicy', set: {}, clear: [] };
      const next = { ...patch, set: { ...patch.set }, clear: patch.clear.filter(f => f !== field) };
      if (value === undefined) { delete next.set[field]; next.clear.push(field); } else next.set[field] = value;
      return { ...current, settings: [...current.settings.filter(p => p.section !== patch.section), next] };
    });
  }
  async function choose(preset: IsolationPreset) {
    presetRequest.current?.abort(); const controller = new AbortController(); presetRequest.current = controller; setBusy(true);
    try { const next = await presetIsolation(instanceId, preset, controller.signal); if (!controller.signal.aborted) { setDraft(next.draft); setPlan(next); setError(undefined); } }
    catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Preset preview unavailable. Retry.'); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  }
  const state = view?.settings.find(s => s.section === 'stateIsolationPolicy');
  return <Flex vertical gap={token.padding}>
    <Modal className="isolation-preset-dialog" title="Replace unsaved isolation changes?" open={active && !!pendingPreset} centered
      okText="Replace draft" cancelText="Keep editing" onCancel={() => setPendingPreset(undefined)}
      onOk={() => { if (pendingPreset) { void choose(pendingPreset); setPendingPreset(undefined); } }}>
      <Typography.Paragraph>The selected preset will replace your unsaved State & Isolation draft, including Memory, Experience and maintenance changes. Review its preview before saving.</Typography.Paragraph>
    </Modal>
    {error && <Alert type="error" showIcon title={error} action={<Button onClick={() => void refresh().catch(() => {})}>Retry isolation</Button>} />}
    {!view && busy && <Spin aria-label="Loading State & Isolation" />}
    {view && draft && state && <>
      <AgentConfigurationPanel title="Continuity and resource policy" label="State & Isolation configuration" bodyGap="section"
        description="Presets prepare editable drafts. Existing history, home files and browser profile bytes are retained.">
        <Form layout="vertical" className="admin-config-form admin-settings-field-grid">
          <Form.Item label="Preset" className="instance-settings-field-wide" extra="Session-isolated keeps multi-turn conversation within one Session. Stateless requires New chat for each request and supports text input.">
            <Select aria-label="Isolation preset" value={deriveIsolationPreset(view, draft)} disabled={archived || busy}
              options={[{ value: 'Persistent', label: 'Persistent assistant' }, { value: 'SessionIsolated', label: 'Session-isolated assistant' }, { value: 'Stateless', label: 'Stateless · new Session for each request' }, { value: 'Custom', label: 'Custom', disabled: true }]}
              onChange={value => { if (value !== 'Custom') { if (changed) setPendingPreset(value as IsolationPreset); else void choose(value as IsolationPreset); } }} />
          </Form.Item>
          {Object.keys(state.effective).map(field => {
            const value = isolationValue(view, draft, state.section, field);
            const patch = draft.settings.find(p => p.section === state.section);
            const source = patch?.clear.includes(field) ? 'Will inherit' : patch && field in patch.set ? 'Draft override' : state.sources[field] === 'instance' ? 'Instance override' : 'Inherited Definition';
            return <Form.Item key={field} label={labels[field] ?? field} extra={<Flex wrap align="center" gap={token.paddingXS}><Typography.Text type="secondary">{source}</Typography.Text>
              {(field in state.overrides || patch && field in patch.set) && <Button size="small" type="link" disabled={archived || busy} aria-label={`Inherit ${labels[field] ?? field}`} onClick={() => edit(field, undefined)}>Inherit</Button>}</Flex>}>
              {typeof value === 'boolean' ? <Switch aria-label={labels[field] ?? field} checked={value} disabled={archived || busy || state.constraints?.[field]?.requiredBoolean === false} onChange={v => edit(field, v)} /> :
                <Select aria-label={labels[field] ?? field} value={value as string} disabled={archived || busy}
                  options={(state.constraints?.[field]?.choices ?? []).map(v => ({ value: v, label: choiceLabels[v] ?? v }))} onChange={v => edit(field, v)} />}
            </Form.Item>;
          })}
          <Form.Item label="Experience enablement" extra="The existing Experience owner is updated in the same transaction."><Switch aria-label="Experience enablement" checked={draft.experienceEnabled} disabled={archived || busy} onChange={v => setDraft({ ...draft, experienceEnabled: v })} /></Form.Item>
          <Form.Item label="Autonomous identity maintenance" extra="The existing maintenance owner is updated in the same transaction."><Switch aria-label="Autonomous identity maintenance" checked={draft.allowMaintenance} disabled={archived || busy} onChange={v => setDraft({ ...draft, allowMaintenance: v })} /></Form.Item>
        </Form>
        <Flex wrap gap={token.paddingSM}>{(['memory-policy', 'experience', 'memory', 'credentials'] as const).map(page => <a key={page} href={instancePath(instanceId, page)} onClick={followAppLink}>{page === 'memory-policy' ? 'Edit detailed Memory policy' : page === 'credentials' ? 'Inspect shared credentials' : page === 'memory' ? 'Inspect learned Memory' : 'Inspect Experience'}</a>)}</Flex>
        <Typography.Text type="secondary">Shared email, Drive and other bound accounts retain their own authority and external effects. Conversation isolation does not isolate those accounts.</Typography.Text>
      </AgentConfigurationPanel>
      <AgentConfigurationPanel title="Change preview" label="Isolation change preview" bodyGap="default">
        {previewing && <Spin aria-label="Preparing isolation preview" />}
        {plan?.blockingReasons.map(reason => <Alert key={reason} type="warning" showIcon title={reason} />)}
        {plan && <><Typography.Paragraph type="secondary" style={{ margin: 0 }}>{plan.notice}</Typography.Paragraph>
          {plan.changes.length ? <Descriptions column={1} size="small" items={plan.changes.map(change => ({ key: `${change.owner}:${change.field}`, label: `${isolationOwnerLabels[change.owner] ?? 'Configuration'} · ${labels[change.field] ?? change.field}`,
            children: `${display(change.before)} → ${display(change.after)} · ${change.source === 'instance' ? 'Instance override' : 'Inherited Definition'}` }))} /> : <Typography.Text>No configuration changes.</Typography.Text>}</>}
        {!editor.managed && <Button type="primary" disabled={archived || busy || previewing || !changed || !plan || !!plan.blockingReasons.length} onClick={() => { if (view) void save(view.revisions.instance).catch(() => {}); }}>Apply changes</Button>}
      </AgentConfigurationPanel>
    </>}
  </Flex>;
}
