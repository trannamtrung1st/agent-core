import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Flex, Form, Input, InputNumber, Select, Spin, Switch, Tooltip, Typography, theme } from 'antd';
import { InfoCircleOutlined, LockOutlined, UndoOutlined } from '@ant-design/icons';
import { listInstanceSettings, patchInstanceSettings, type SettingValue, type SettingsSection } from '../../services/instanceConfiguration';
import { confirmAction } from '../../app/confirmAction';
import { describeAdminError } from './adminErrors';
import { listModels } from '../../services/api';
import { InstanceExecutionBudgets } from './ExecutionBudgetsSection';
import { orderedReasoningEfforts } from '../models/reasoningEfforts';

const titles: Record<string, string> = { instructions: 'Operating instructions', conversationPolicy: 'Conversation', behaviorPolicy: 'Behavior', initiativePolicy: 'Initiative', voice: 'Voice', modelDefaults: 'Model defaults', memoryPolicy: 'Memory policy', capabilities: 'Capabilities', triggerPolicy: 'Trigger restrictions', providerPreferences: 'Provider preferences' };
const labels: Record<string, string> = { systemInstructions: 'Operating instructions', responseLength: 'Response length', askOneQuestionAtATime: 'Ask one question at a time', maxOutputTokens: 'Maximum output tokens', interruptionStyle: 'Interruption style', acknowledgeInterruption: 'Acknowledge interruption', avoidUnsupportedClaims: 'Avoid unsupported claims', silenceThresholdMs: 'Silence threshold (ms)', cooldownMs: 'Cooldown (ms)', maxPerSilencePeriod: 'Maximum speaks per silence period', maxConsecutiveProactiveTurns: 'Maximum consecutive proactive turns', maxSilentEvaluations: 'Maximum silent evaluations', maxInactivityMs: 'Maximum inactivity (ms)', voiceId: 'Voice ID', speakingRate: 'Speaking rate', catalogKey: 'Default model', reasoningEffort: 'Reasoning effort', sessionMemory: 'Session memory', identityUserPromotion: 'Instance memory promotion', identityUserRetrieval: 'Instance memory retrieval', userPromotion: 'User memory promotion', userRetrieval: 'User memory retrieval', selected: 'Selected capabilities', always: 'Always projected capabilities', allowUnreadUnsupportedTypes: 'Allow unread unsupported attachments', language: 'Language', enabled: 'Enabled', triggers: 'Initiative triggers', allowUserScheduling: 'Allow user scheduling', allowOneShot: 'Allow one-shot schedules', allowDaily: 'Allow daily schedules', allowWeekly: 'Allow weekly schedules', allowIndefiniteRecurrence: 'Allow indefinite recurrence', maxActiveRegistrations: 'Maximum active schedules', oneShotHorizonDays: 'One-shot horizon (days)', minRecurrenceDays: 'Minimum recurrence (days)', allowedSourceKinds: 'Allowed sources', allowFixedInterval: 'Allow fixed intervals', minFixedIntervalSeconds: 'Minimum fixed interval (seconds)', languageModel: 'Language model', speechRecognizer: 'Speech recognizer', speechSynthesizer: 'Speech synthesizer', interruptionClassifier: 'Interruption classifier' };
const fieldOrder: Record<string, string[]> = {
  conversationPolicy: ['language', 'responseLength', 'maxOutputTokens', 'askOneQuestionAtATime'],
};
const choiceLabels: Record<string, string> = { concise: 'Concise', balanced: 'Balanced', acknowledgeThenContinue: 'Acknowledge, then continue', answerNewTurn: 'Answer new turn' };
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
  const formId = useId();
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
    {sections && <Collapse styles={{ header: { alignItems: 'center' } }} activeKey={expanded} onChange={keys => setExpanded(Array.isArray(keys) ? keys : [keys])} items={sections.map(section => {
      const draft = drafts[section.section] ?? emptyDraft(); const own = customized[section.section] ?? Object.keys(section.overrides).length > 0;
      const changed = Object.keys(draft.set).length + draft.clear.length > 0;
      const title = titles[section.section];
      const hasOverrides = Object.keys(draft.set).length > 0 || Object.keys(section.overrides).some(field => !draft.clear.includes(field));
      return { key: section.section, label: title, extra: <Flex align="center" gap={token.paddingXS}>
        {changed && <Typography.Text type="secondary">Unsaved changes</Typography.Text>}
        {hasOverrides && <Tooltip trigger={['hover', 'focus', 'click']} title={changed ? 'Unsaved Instance overrides' : 'This section has Instance overrides.'}><InfoCircleOutlined tabIndex={0} aria-label={`${title}: Instance override`} /></Tooltip>}
      </Flex>, children: <Flex vertical gap={token.padding} role="group" aria-label={`${title} settings`}>
        {!own && <Button style={{ alignSelf: 'flex-start' }} aria-label={`Customize ${title}`} disabled={archived || busy} onClick={() => setCustomized(current => ({ ...current, [section.section]: true }))}>Customize</Button>}
        {section.section === 'instructions' && own && <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Saving replaces the complete operating instructions for this Instance. Review the inherited instructions before editing.</Typography.Paragraph>}
        <Form layout="vertical" className="admin-config-form admin-settings-form admin-settings-field-grid instance-settings-form">
          {(fieldOrder[section.section] ?? Object.keys(section.effective)).filter(field => field in section.effective).map(field => {
            const inherited = section.effective[field];
            const value = draft.clear.includes(field) ? section.definitionDefaults?.[field] ?? null : field in draft.set ? draft.set[field] : inherited;
            const constraint = section.constraints?.[field];
            const locked = typeof constraint?.requiredBoolean === 'boolean';
            const disabled = archived || busy || !own || draft.clear.includes(field) || locked;
            const label = labels[field] ?? field[0].toUpperCase() + field.slice(1);
            const id = `${formId}-${section.section}-${field}`;
            const boolean = typeof inherited === 'boolean';
            const wide = Array.isArray(inherited) || field === 'systemInstructions' || field === 'enabled';
            const status = draft.clear.includes(field) ? 'Will inherit on save' : field in draft.set ? 'Unsaved Instance override' : section.sources[field] === 'instance' ? 'Instance override' : null;
            const defaultValue = section.definitionDefaults?.[field];
            const defaultText = Array.isArray(defaultValue) ? defaultValue.join(', ') || 'None' : defaultValue === null ? 'None' : String(defaultValue);
            const fieldLabel = <Flex align="center" wrap gap={token.paddingXS}>
              {boolean ? <label htmlFor={id}>{label}</label> : <span>{label}</span>}
              {status && <Tooltip trigger={['hover', 'focus', 'click']} title={`${status}. Definition default: ${defaultText}.`}><Button type="text" size="small" icon={<InfoCircleOutlined />} aria-label={`${label}: ${status}`} /></Tooltip>}
              {constraint && <Tooltip trigger={['hover', 'focus', 'click']} title={constraint.reason}><Button type="text" size="small" icon={<LockOutlined />} aria-label={`${label}: Definition restriction`} /></Tooltip>}
              {own && field in section.overrides && <Tooltip trigger={['hover', 'focus']} title="Reset to Definition default"><Button type="text" size="small" icon={<UndoOutlined />} aria-label={`Reset ${label}`} disabled={archived || busy || draft.clear.includes(field)} onClick={() => inherit(section, field)} /></Tooltip>}
            </Flex>;
            let control;
            if (boolean) control = <Switch id={id} aria-label={label} checked={value === true} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (numerics.has(field)) control = <InputNumber id={id} aria-label={label} value={typeof value === 'number' ? value : null} min={constraint?.minimum ?? undefined} max={constraint?.maximum ?? undefined} disabled={disabled} className="instance-settings-control" onChange={v => { if (v !== null || nullable.has(field)) change(section, field, v); }} />;
            else if (field === 'catalogKey') control = <Select id={id} className="instance-settings-control" aria-label={label} value={value as string | null} disabled={disabled || !models.length} allowClear options={models.map(m => ({ label: m.displayName, value: m.key }))} onChange={v => change(section, field, v ?? null)} />;
            else if (field === 'reasoningEffort') {
              const selected = (draft.clear.includes('catalogKey') ? section.definitionDefaults.catalogKey
                : 'catalogKey' in draft.set ? draft.set.catalogKey : section.effective.catalogKey) as string | null;
              const model = models.find(m => m.key === (selected ?? defaultModelKey));
              control = <Select id={id} className="instance-settings-control instance-settings-short-control" aria-label={label} value={value as string | null} disabled={disabled || !model} allowClear options={orderedReasoningEfforts(model?.supportedReasoningEfforts ?? []).map(e => ({ label: e, value: e }))} onChange={v => change(section, field, v ?? null)} />;
            } else if (field === 'responseLength' || field === 'interruptionStyle') control = <Select id={id} className={`instance-settings-control${field === 'responseLength' ? ' instance-settings-short-control' : ''}`} aria-label={label} value={value as string} disabled={disabled} options={(field === 'responseLength' ? ['concise', 'balanced'] : ['acknowledgeThenContinue', 'answerNewTurn']).map(v => ({ label: choiceLabels[v], value: v }))} onChange={v => change(section, field, v)} />;
            else if (Array.isArray(inherited)) control = <Select id={id} className="instance-settings-control" aria-label={label} mode="multiple" options={(section.definitionDefaults?.[field] as string[] ?? []).map(v => ({ label: v, value: v }))} value={Array.isArray(value) ? value : []} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (field === 'systemInstructions') control = <Input.TextArea id={id} aria-label={label} autoSize={{ minRows: 6, maxRows: 18 }} maxLength={8000} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            else control = <Input id={id} className="instance-settings-control" aria-label={label} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            return <Form.Item key={field} className={[wide && "instance-settings-field-wide", boolean && "instance-settings-switch-field"].filter(Boolean).join(" ")} htmlFor={id} label={boolean ? undefined : fieldLabel}>{boolean ? <Flex align="center" gap={token.paddingSM}>{fieldLabel}{control}</Flex> : control}</Form.Item>;
          })}
        </Form>
        {own && <Flex wrap gap={token.paddingXS}>
          <Button type="primary" aria-label={`Save ${title}`} disabled={archived || busy || !changed} loading={busy} onClick={() => void save(section)}>Save</Button>
          <Button aria-label={`Discard ${title} changes`} disabled={busy || archived || !changed && Object.keys(section.overrides).length > 0} onClick={() => {
            setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
            setCustomized(current => ({ ...current, [section.section]: Object.keys(section.overrides).length > 0 }));
          }}>Discard</Button>
          <Button aria-label={`Reset ${title} to Definition`} disabled={archived || busy || !Object.keys(section.overrides).length} onClick={() => confirmAction(modal, { title: `Reset ${title}?`, content: "Clears only this section’s overrides and discards its unsaved edits. Other settings and budget profiles are preserved.", okText: "Reset section", onOk: () => save(section, true) })}>Reset</Button>
        </Flex>}
        <Typography.Text type="secondary">Instance adopted v{section.definitionVersion} · revision {section.instanceRevision}</Typography.Text>
      </Flex> };
    })} />}
    <InstanceExecutionBudgets instanceId={instanceId} archived={archived} onUpdated={onUpdated} />
  </Flex>;
}
