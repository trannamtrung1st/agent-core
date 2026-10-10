import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Flex, Form, Input, InputNumber, Segmented, Select, Spin, Switch, Typography, theme } from 'antd';
import { listInstanceSettings, patchInstanceSettings, type SettingValue, type SettingsSection } from '../../services/instanceConfiguration';
import { confirmAction } from '../../app/confirmAction';
import { describeAdminError } from './adminErrors';
import { listModels } from '../../services/api';
import { InstanceExecutionBudgets } from './ExecutionBudgetsSection';
import { orderedReasoningEfforts } from '../models/reasoningEfforts';

const titles: Record<string, string> = { instructions: 'Operating instructions', conversationPolicy: 'Conversation', behaviorPolicy: 'Behavior', initiativePolicy: 'Initiative', voice: 'Voice', modelDefaults: 'Model defaults', memoryPolicy: 'Memory policy', capabilities: 'Capabilities', triggerPolicy: 'Trigger restrictions', providerPreferences: 'Provider preferences' };
const labels: Record<string, string> = { systemInstructions: 'Operating instructions', responseLength: 'Response length', askOneQuestionAtATime: 'Ask one question at a time', maxOutputTokens: 'Maximum output tokens', interruptionStyle: 'Interruption style', acknowledgeInterruption: 'Acknowledge interruption', avoidUnsupportedClaims: 'Avoid unsupported claims', silenceThresholdMs: 'Silence threshold (ms)', cooldownMs: 'Cooldown (ms)', maxPerSilencePeriod: 'Maximum speaks per silence period', maxConsecutiveProactiveTurns: 'Maximum consecutive proactive turns', maxSilentEvaluations: 'Maximum silent evaluations', maxInactivityMs: 'Maximum inactivity (ms)', voiceId: 'Voice ID', speakingRate: 'Speaking rate', catalogKey: 'Default model', reasoningEffort: 'Reasoning effort', sessionMemory: 'Session memory', identityUserPromotion: 'Instance memory promotion', identityUserRetrieval: 'Instance memory retrieval', userPromotion: 'User memory promotion', userRetrieval: 'User memory retrieval', selected: 'Selected capabilities', always: 'Always projected capabilities', allowUnreadUnsupportedTypes: 'Allow unread unsupported attachments' };
const nullable = new Set(['maxConsecutiveProactiveTurns', 'maxSilentEvaluations', 'maxInactivityMs', 'catalogKey', 'reasoningEffort', 'speechRecognizer', 'speechSynthesizer']);
const numerics = new Set(['maxOutputTokens', 'silenceThresholdMs', 'cooldownMs', 'maxPerSilencePeriod', 'maxConsecutiveProactiveTurns', 'maxSilentEvaluations', 'maxInactivityMs', 'speakingRate', 'maxActiveRegistrations', 'oneShotHorizonDays', 'minRecurrenceDays', 'minFixedIntervalSeconds']);
type Draft = { set: Record<string, SettingValue>; clear: string[] };
const emptyDraft = (): Draft => ({ set: {}, clear: [] });

export function InstanceSettingsSection({ instanceId, archived, active = true, onUpdated }: { instanceId: string; archived: boolean; active?: boolean; onUpdated: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [expanded, setExpanded] = useState<string[]>([]);
  const [sections, setSections] = useState<SettingsSection[] | null>(null);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({}); const [customized, setCustomized] = useState<Record<string, boolean>>({});
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [models, setModels] = useState<{ key: string; displayName: string; supportedReasoningEfforts: string[] }[]>([]);
  const [defaultModelKey, setDefaultModelKey] = useState<string | null>(null);
  const epoch = useRef(0); const saving = useRef(false); const owner = useRef(instanceId); owner.current = instanceId;
  useEffect(() => { epoch.current++; saving.current = false; setSections(null); setExpanded([]); setDrafts({}); setCustomized({}); setError(null); setBusy(false); }, [instanceId]);
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
      if (reset) setCustomized(current => ({ ...current, [section.section]: false }));
      onUpdated();
      // Resolving one section can also change another (for example Voice disables speech aliases).
      setSections(null); setLoading(true);
      const refreshed = await listInstanceSettings(instanceId);
      if (request === epoch.current && owner.current === instanceId) setSections(refreshed);
    } catch (e) { if (request === epoch.current) setError(committed
      ? `Settings were saved. ${describeAdminError(e, 'Settings could not be refreshed.').message} Retry settings; other drafts are retained.`
      : describeAdminError(e, 'Settings could not be saved. Reload while retaining your draft.').message); }
    finally { if (request === epoch.current) { saving.current = false; setBusy(false); setLoading(false); } }
  }
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Only fields you change become Instance overrides. Unchanged fields inherit the selected Definition. Changes apply to the next new Run in any existing conversation. Current Runs are unchanged.</Typography.Paragraph>
    <Button style={{ alignSelf: "flex-start" }} disabled={busy || loading} onClick={() => void reload()}>Reload settings</Button>
    {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Retry settings</Button>} />}
    {loading && <Spin aria-label="Loading Instance settings" />}
    {sections && <Collapse activeKey={expanded} onChange={keys => setExpanded(Array.isArray(keys) ? keys : [keys])} items={sections.map(section => {
      const draft = drafts[section.section] ?? emptyDraft(); const own = customized[section.section] ?? Object.keys(section.overrides).length > 0;
      const changed = Object.keys(draft.set).length + draft.clear.length > 0;
      return { key: section.section, label: titles[section.section], extra: <Typography.Text type="secondary">{Object.keys(section.overrides).length ? 'Instance override' : 'Definition default'}</Typography.Text>, children: <Flex vertical gap={token.paddingSM}>
        <Segmented aria-label={`${titles[section.section]} source`} value={own ? 'customize' : 'inherit'} disabled={archived || busy} onChange={event => {
          if (event === 'customize') setCustomized(current => ({ ...current, [section.section]: true }));
          else if (Object.keys(section.overrides).length) confirmAction(modal, { title: `Reset ${titles[section.section]}?`, content: 'Clears only this section’s overrides. Other settings and budget profiles are preserved.', okText: 'Reset section', onOk: () => save(section, true) });
          else setCustomized(current => ({ ...current, [section.section]: false }));
        }} options={[{ label: 'Inherit Definition', value: 'inherit' }, { label: 'Customize', value: 'customize' }]} />
        {section.section === 'instructions' && own && <Alert type="info" showIcon title="Full replacement" description="This replaces the complete operating instructions for this Instance. Review the inherited instructions before saving." />}
        <Form layout="vertical" className="admin-config-form">
          {Object.entries(section.effective).map(([field, inherited]) => {
            const value = draft.clear.includes(field) ? section.definitionDefaults?.[field] ?? null : field in draft.set ? draft.set[field] : inherited;
            const disabled = archived || busy || !own || draft.clear.includes(field);
            const label = labels[field] ?? field[0].toUpperCase() + field.slice(1);
            let control;
            if (typeof inherited === 'boolean') control = <Switch aria-label={label} checked={value === true} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (numerics.has(field)) control = <InputNumber aria-label={label} value={typeof value === 'number' ? value : null} disabled={disabled} style={{ width: '100%' }} onChange={v => { if (v !== null || nullable.has(field)) change(section, field, v); }} />;
            else if (field === 'catalogKey') control = <Select aria-label={label} value={value as string | null} disabled={disabled || !models.length} allowClear options={models.map(m => ({ label: m.displayName, value: m.key }))} onChange={v => change(section, field, v ?? null)} />;
            else if (field === 'reasoningEffort') {
              const selected = (draft.clear.includes('catalogKey') ? section.definitionDefaults.catalogKey
                : 'catalogKey' in draft.set ? draft.set.catalogKey : section.effective.catalogKey) as string | null;
              const model = models.find(m => m.key === (selected ?? defaultModelKey));
              control = <Select aria-label={label} value={value as string | null} disabled={disabled || !model} allowClear options={orderedReasoningEfforts(model?.supportedReasoningEfforts ?? []).map(e => ({ label: e, value: e }))} onChange={v => change(section, field, v ?? null)} />;
            } else if (field === 'responseLength' || field === 'interruptionStyle') control = <Select aria-label={label} value={value as string} disabled={disabled} options={(field === 'responseLength' ? ['concise', 'balanced'] : ['acknowledgeThenContinue', 'answerNewTurn']).map(v => ({ label: v, value: v }))} onChange={v => change(section, field, v)} />;
            else if (Array.isArray(inherited)) control = <Select aria-label={label} mode="multiple" options={(section.definitionDefaults?.[field] as string[] ?? []).map(v => ({ label: v, value: v }))} value={Array.isArray(value) ? value : []} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (field === 'systemInstructions') control = <Input.TextArea aria-label={label} autoSize={{ minRows: 6, maxRows: 18 }} maxLength={8000} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            else control = <Input aria-label={label} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            return <Form.Item key={field} label={label} extra={<Flex wrap gap={token.paddingXS}><Typography.Text type="secondary">{draft.clear.includes(field) ? 'Will inherit on save' : field in draft.set ? 'Unsaved Instance override' : section.sources[field] === 'instance' ? 'Instance override' : 'Definition default'}</Typography.Text>{own && field in section.overrides && <Button size="small" disabled={archived || busy} onClick={() => inherit(section, field)}>Reset {label}</Button>}</Flex>}>{control}</Form.Item>;
          })}
        </Form>
        {own && <Flex wrap gap={token.paddingXS}><Button disabled={archived || busy || !Object.keys(section.overrides).length} onClick={() => confirmAction(modal, { title: `Reset ${titles[section.section]}?`, content: "Clears only this section’s overrides. Other settings and budget profiles are preserved.", okText: "Reset section", onOk: () => save(section, true) })}>Reset {titles[section.section]} to Definition</Button><Button type="primary" aria-label={`Save ${titles[section.section]}`} disabled={archived || busy || !changed} loading={busy} onClick={() => void save(section)}>Save {titles[section.section]}</Button><Button disabled={busy || !changed} onClick={() => setDrafts(current => ({ ...current, [section.section]: emptyDraft() }))}>Discard section edits</Button></Flex>}
        <Typography.Text type="secondary">Instance adopted v{section.definitionVersion} · revision {section.instanceRevision}</Typography.Text>
      </Flex> };
    })} />}
    <InstanceExecutionBudgets instanceId={instanceId} archived={archived} onUpdated={onUpdated} />
  </Flex>;
}
