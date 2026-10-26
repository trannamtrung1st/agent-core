import { preferenceFields, preferenceFromFields, type ModelPreference } from '../../services/modelPreference';
import { ExecutionModelFields } from './ExecutionModelFields';
import { InfoCircleOutlined,LockOutlined,UndoOutlined } from '@ant-design/icons';
import { Alert,App,Button,Collapse,Flex,Form,Input,InputNumber,Select,Spin,Switch,Tooltip,Typography,theme } from 'antd';
import { useEffect,useId,useState,type ReactNode } from 'react';
import { confirmAction } from '../../app/confirmAction';
import { sandboxEligible,sandboxStatusLabel,type SandboxProfile } from '../../services/sandboxes';
import { useAdminDraftStatus } from './AdminDraftStatus';
import { instanceSettingGroups,type InstancePage } from './adminNavigation';
import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { emptyDraft,useInstanceSettingsController,type InstanceSettingsTransport } from './useInstanceSettingsController';

const titles: Record<string, string> = { instructions: 'Agent instructions', conversationPolicy: 'Conversation', behaviorPolicy: 'Behavior', initiativePolicy: 'Initiative', voice: 'Voice', modelDefaults: 'Model defaults', memoryPolicy: 'Memory policy', capabilities: 'Capabilities', triggerPolicy: 'Trigger restrictions', providerPreferences: 'Provider preferences', sandboxPolicy: 'Sandbox environment', stateIsolationPolicy: 'State & Isolation' };
const labels: Record<string, string> = { agentInstructions: 'Agent instructions', responseLength: 'Response length', askOneQuestionAtATime: 'Ask one question at a time', maxOutputTokens: 'Maximum output tokens', interruptionStyle: 'Interruption style', acknowledgeInterruption: 'Acknowledge interruption', avoidUnsupportedClaims: 'Avoid unsupported claims', silenceThresholdMs: 'Silence threshold (ms)', cooldownMs: 'Cooldown (ms)', maxPerSilencePeriod: 'Maximum speaks per silence period', maxConsecutiveProactiveTurns: 'Maximum consecutive proactive turns', maxSilentEvaluations: 'Maximum silent evaluations', maxInactivityMs: 'Maximum inactivity (ms)', voiceId: 'Voice ID', speakingRate: 'Speaking rate', preference: 'Model preference', sessionMemory: 'Session memory', identityUserPromotion: 'Instance memory promotion', identityUserRetrieval: 'Instance memory retrieval', userPromotion: 'User memory promotion', userRetrieval: 'User memory retrieval', selected: 'Selected capabilities', always: 'Always projected capabilities', allowUnreadUnsupportedTypes: 'Allow unread unsupported attachments', language: 'Language', enabled: 'Enabled', triggers: 'Initiative triggers', allowUserScheduling: 'Allow user scheduling', allowOneShot: 'Allow one-shot schedules', allowDaily: 'Allow daily schedules', allowWeekly: 'Allow weekly schedules', allowIndefiniteRecurrence: 'Allow indefinite recurrence', maxActiveRegistrations: 'Maximum active schedules', oneShotHorizonDays: 'One-shot horizon (days)', minRecurrenceDays: 'Minimum recurrence (days)', allowedSourceKinds: 'Allowed sources', allowFixedInterval: 'Allow fixed intervals', minFixedIntervalSeconds: 'Minimum fixed interval (seconds)', speechRecognizer: 'Speech recognizer', speechSynthesizer: 'Speech synthesizer', interruptionClassifier: 'Interruption classifier', allowedProfileIds: 'Allowed Sandbox profiles', profileId: 'Default Sandbox profile', initializationScript: 'Instance initialization script', cpu:'CPU cores', memoryMiB:'Memory (MiB)', maxCommandSeconds:'Maximum command (seconds)', idleTtlSeconds:'Idle expiry (seconds)', maxLifetimeSeconds:'Maximum lifetime (seconds)', storageBytes:'Canonical /working storage (MiB)' };
const isolationLabels: Record<string, string> = { conversationContextScope: 'Conversation context', homeAccess: 'Home access', workingCleanup: 'Working folder cleanup', browserIsolation: 'Browser isolation', agentStateVisibility: 'Agent state visibility', experienceRecord: 'Experience recording', experienceRecall: 'Experience recall', profileEvidence: 'Profile evidence', crossSessionContinuity: 'Cross-conversation continuity' };
const isolationChoices: Record<string, string> = { Session: 'Conversation', CurrentRequest: 'Current request', ReadWrite: 'Read and write', ReadOnly: 'Read only', Disabled: 'Disabled', OnSessionDeletion: 'On conversation deletion', AfterTerminal: 'After terminal work', HostDefault: 'Host default', PersistentAgent: 'Persistent per agent', EphemeralSession: 'Ephemeral per conversation', Off: 'Off', CurrentSession: 'Current conversation', InstanceSummary: 'Instance summary' };
const fieldOrder: Record<string, string[]> = {
  conversationPolicy: ['language', 'responseLength', 'maxOutputTokens', 'askOneQuestionAtATime'],
  sandboxPolicy: ['allowedProfileIds', 'profileId', 'cpu', 'memoryMiB', 'maxCommandSeconds', 'idleTtlSeconds', 'maxLifetimeSeconds', 'storageBytes', 'initializationScript'],
};
const choiceLabels: Record<string, string> = { concise: 'Concise', balanced: 'Balanced', acknowledgeThenContinue: 'Acknowledge, then continue', answerNewTurn: 'Answer new turn' };
const nullable = new Set(['initializationScript','maxConsecutiveProactiveTurns', 'maxSilentEvaluations', 'maxInactivityMs', 'preference', 'speechRecognizer', 'speechSynthesizer']);
const numerics = new Set(['cpu','memoryMiB','maxCommandSeconds','idleTtlSeconds','maxLifetimeSeconds','storageBytes','maxOutputTokens', 'silenceThresholdMs', 'cooldownMs', 'maxPerSilencePeriod', 'maxConsecutiveProactiveTurns', 'maxSilentEvaluations', 'maxInactivityMs', 'speakingRate', 'maxActiveRegistrations', 'oneShotHorizonDays', 'minRecurrenceDays', 'minFixedIntervalSeconds']);


export type InstanceSandboxContext = { profiles: readonly Pick<SandboxProfile, 'id' | 'name' | 'availability'>[] | null; summary: (state: { dirty: boolean; busy: boolean; revision?: number }) => ReactNode };
export function InstanceSettingsSection({ instanceId, archived, active = true, page, onDirtyChange, onBusyChange, onUpdated, transport, confirmationContext, sandboxContext, saveBlocked = false, externalBusy = false, refreshKey }: { instanceId: string; archived: boolean; active?: boolean; page?: InstancePage; onDirtyChange?: (dirty: boolean) => void; onBusyChange?: (busy: boolean) => void; onUpdated: () => void; transport: InstanceSettingsTransport; confirmationContext?: string; sandboxContext?: InstanceSandboxContext; saveBlocked?: boolean; externalBusy?: boolean; refreshKey?: number }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [expanded, setExpanded] = useState<string[]>([]);
  const formId = useId();
  const settings = useInstanceSettingsController(instanceId, archived, active, onUpdated, refreshKey, false, transport);
  const sections = settings.sections;
  const drafts = settings.drafts;
  const { setDrafts, error, models, defaultOfferingId, save, saveAll, change, inherit } = settings;
  const busy = settings.busy || externalBusy;
  const loading = settings.loading;
  useEffect(() => { onBusyChange?.(busy || loading); }, [busy, loading, onBusyChange]);
  const reload = settings.reload;
  const resetSection = (section: NonNullable<typeof sections>[number]) => save(section, true);
  useEffect(() => { setExpanded([]); }, [instanceId]);
  const profileChoices = sandboxContext?.profiles ?? null;
  const requested = page ? instanceSettingGroups[page] ?? [] : undefined;
  const dirtySections = Object.entries(drafts).filter(([, draft]) => Object.keys(draft.set).length + draft.clear.length > 0).map(([key]) => key);
  useAdminDraftStatus(Object.entries(instanceSettingGroups).filter(([, sections]) => sections?.some(section => dirtySections.includes(section))).map(([key]) => key));
  const independentDirty = Object.values(settings.drafts).some(draft => Object.keys(draft.set).length + draft.clear.length > 0);
  useEffect(() => { onDirtyChange?.(independentDirty); }, [independentDirty, onDirtyChange]);
  useEffect(() => { if (requested) setExpanded(current => [...new Set([...current, ...requested])]); }, [page]);
  const configuration = useInstanceConfigurationEditor('Settings', { dirty: independentDirty, busy: busy || loading, revision: sections?.[0]?.instanceRevision, blocked: !sections || saveBlocked ? 'Read current settings before saving.' : undefined, save: saveAll, discard: () => setDrafts({}), refresh: settings.reload });
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" className="admin-settings-intro" style={{ margin: 0 }}>{page==='sandbox'?'Unchanged values inherit the Definition policy and chosen Profile. Only edited fields become Instance overrides. The next Run adopts saved changes; a changed environment is replaced only after safe termination.':'Only fields you change become Instance overrides. Unchanged fields inherit the selected Definition. Changes apply to the next new Run in any existing conversation. Current Runs are unchanged.'}</Typography.Paragraph>
    <Button style={{ alignSelf: "flex-start" }} disabled={busy || loading} onClick={() => void reload()}>Reload settings</Button>
    {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Retry settings</Button>} />}
    {settings.modelError && <Alert type="warning" showIcon title="Model catalog unavailable" description="Saved model choices are preserved. Reload models before changing the default model." action={<Button disabled={busy} onClick={settings.reloadModels}>Reload models</Button>} />}
    {loading && <Spin aria-label="Loading Instance settings" />}
    {sections && <Collapse styles={{ header: { alignItems: 'center' } }} activeKey={expanded} onChange={keys => setExpanded(Array.isArray(keys) ? keys : [keys])} items={sections.filter(section => !requested || requested.includes(section.section)).map(section => {
      const draft = drafts[section.section] ?? emptyDraft();
      const changed = Object.keys(draft.set).length + draft.clear.length > 0;
      const title = titles[section.section];
      const hasOverrides = Object.keys(draft.set).some(field => field !== 'allowUnreadUnsupportedTypes') || Object.keys(section.overrides).some(field => field !== 'allowUnreadUnsupportedTypes' && !draft.clear.includes(field));
      return { key: section.section, label: <Flex wrap align="center" gap={token.paddingXS}>
        <span id={section.section === "triggerPolicy" ? "restrictions" : section.section} tabIndex={-1} className="admin-settings-anchor">{title}</span>
        {changed && <Typography.Text type="secondary">Unsaved changes</Typography.Text>}
        {hasOverrides && <Tooltip trigger={['hover', 'focus', 'click']} title={changed ? 'Unsaved Instance overrides' : 'This section has Instance overrides.'}><InfoCircleOutlined tabIndex={0} aria-label={`${title}: Instance override`} /></Tooltip>}
      </Flex>, children: <Flex vertical gap={token.padding} role="group" aria-label={`${title} settings`}>
        {section.section === 'instructions' && <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Saving replaces the complete agent instructions for this Instance. Review the inherited instructions before editing. Configures agent behavior at Runtime. Agent Core rules, permissions and approvals cannot be overridden.</Typography.Paragraph>}
        {section.section === 'sandboxPolicy' && <Typography.Paragraph type="secondary" style={{margin:0}}>Allowed profiles inherit the Definition list unless narrowed here. Each Session chooses independently from that set; the default is its initial choice. The Profile script runs first, then the optional Instance script for every chosen profile. Limits are capped by each Profile. Setup scripts run once per generation and never become agent instructions.</Typography.Paragraph>}
        <Form layout="vertical" className={`admin-config-form admin-settings-form admin-settings-field-grid instance-settings-form${section.section === 'instructions' ? ' admin-settings-instructions' : section.section === 'sandboxPolicy' ? ' sandbox-settings-fields' : ''}`}>
          {(fieldOrder[section.section] ?? Object.keys(section.effective)).filter(field => field in section.effective && field !== 'allowUnreadUnsupportedTypes').map(field => {
            const inherited = section.effective[field];
            const value = draft.clear.includes(field) ? section.definitionDefaults?.[field] ?? null : field in draft.set ? draft.set[field] : inherited;
            const constraint = section.constraints?.[field];
            const locked = typeof constraint?.requiredBoolean === 'boolean';
            const disabled = archived || busy || draft.clear.includes(field) || locked;
            const label = (section.section === 'stateIsolationPolicy' ? isolationLabels[field] : labels[field]) ?? field[0].toUpperCase() + field.slice(1);
            const id = `${formId}-${section.section}-${field}`;
            const boolean = typeof inherited === 'boolean';
            const wide = field === 'profileId' || Array.isArray(inherited) || field === 'agentInstructions' || field === 'initializationScript' || field === 'enabled';
            const status = draft.clear.includes(field) ? 'Will inherit on save' : field in draft.set ? 'Unsaved Instance override' : section.sources[field] === 'instance' ? 'Instance override' : section.section === 'sandboxPolicy' ? (section.sources[field] === 'profile' ? 'Inherited Profile' : 'Inherited Definition') : null;
            const defaultValue = section.definitionDefaults?.[field];
            const defaultText = section.section === 'sandboxPolicy' && field === 'storageBytes' && typeof defaultValue === 'number' ? `${defaultValue / 1048576} MiB` : Array.isArray(defaultValue) ? defaultValue.join(', ') || 'None' : defaultValue === null ? 'None' : String(defaultValue);
            const fieldLabel = <Flex className="instance-settings-field-label" align="center" gap={token.paddingXS}>
              {boolean ? <label htmlFor={id}>{label}</label> : <span>{label}</span>}
              {status && <Tooltip trigger={['hover', 'focus', 'click']} title={`${status}. ${section.section === "sandboxPolicy" ? "Inherited value" : "Definition default"}: ${defaultText}.`}><Button type="text" size="small" icon={<InfoCircleOutlined />} aria-label={`${label}: ${status}`} /></Tooltip>}
              {constraint && <Tooltip trigger={['hover', 'focus', 'click']} title={constraint.reason}><Button type="text" size="small" icon={<LockOutlined />} aria-label={`${label}: ${section.section === "sandboxPolicy" && field !== "profileId" && field !== "allowedProfileIds" ? "Profile ceiling" : "Definition restriction"}`} /></Tooltip>}
              {field in section.overrides && <Tooltip trigger={['hover', 'focus']} title={section.section === "sandboxPolicy" ? "Reset to inherited value" : "Reset to Definition default"}><Button type="text" size="small" icon={<UndoOutlined />} aria-label={`Reset ${label}`} disabled={archived || busy || draft.clear.includes(field)} onClick={() => inherit(section, field)} /></Tooltip>}
            </Flex>;
            let control;
            if (boolean) control = <Switch id={id} aria-label={label} checked={value === true} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (section.section === 'sandboxPolicy' && field === 'storageBytes') control = <InputNumber id={id} aria-label={label}
              value={typeof value === 'number' ? value / 1048576 : null}
              min={constraint?.minimum == null ? undefined : constraint.minimum / 1048576}
              max={constraint?.maximum == null ? undefined : constraint.maximum / 1048576}
              step={1} disabled={disabled} className="instance-settings-control"
              onChange={mib=>{if(mib!==null)change(section,field,Math.round(mib*1048576));}} />;
            else if (numerics.has(field)) control = <InputNumber id={id} aria-label={label} value={typeof value === 'number' ? value : null} min={constraint?.minimum ?? undefined} max={constraint?.maximum ?? undefined} disabled={disabled} className="instance-settings-control" onChange={v => { if (v !== null || nullable.has(field)) change(section, field, v); }} />;
            else if (field === 'allowedProfileIds') control = <Select id={id} className="instance-settings-control" aria-label={label} mode="multiple" maxCount={16}
              value={Array.isArray(value) ? value : []} disabled={disabled || !constraint?.choices?.length}
              options={(constraint?.choices ?? []).map(p => { const profile=profileChoices?.find(v=>v.id===p); return {label:profile?`${profile.name} · ${sandboxStatusLabel(profile)}`:p,value:p}; })}
              onChange={v=>change(section,field,v)} />;
            else if (field === 'profileId') control = <Select id={id} className="instance-settings-control" aria-label="Sandbox profile" value={value as string | null} disabled={disabled || !constraint?.choices?.length || page==='sandbox'&&!profileChoices} options={(constraint?.choices ?? []).filter(p => { const allowed=draft.clear.includes('allowedProfileIds')?section.definitionDefaults.allowedProfileIds:draft.set.allowedProfileIds??section.effective.allowedProfileIds; return !Array.isArray(allowed)||allowed.includes(p); }).map(p => {const profile=profileChoices?.find(v=>v.id===p);return {label:profile?`${profile.name} · ${sandboxStatusLabel(profile)}`:`${p} · Unavailable`,value:p,disabled:!profile||!sandboxEligible(profile)};})} onChange={v => change(section, field, v)} />;
            else if (field === 'preference') {
              const model = preferenceFields(value as ModelPreference | null);
              control = <ExecutionModelFields models={models} offeringId={model.key} reasoningEffort={model.effort} defaultOfferingId={defaultOfferingId ?? undefined}
                disabled={disabled} modelLabel="Instance model" effortLabel="Instance reasoning" defaultLabel="Use Definition default" showLabels
                onChange={(key, effort) => change(section, field, preferenceFromFields(key, effort))} />;
            } else if (field === 'responseLength' || field === 'interruptionStyle') control = <Select id={id} className={`instance-settings-control${field === 'responseLength' ? ' instance-settings-short-control' : ''}`} aria-label={label} value={value as string} disabled={disabled} options={(field === 'responseLength' ? ['concise', 'balanced'] : ['acknowledgeThenContinue', 'answerNewTurn']).map(v => ({ label: choiceLabels[v], value: v }))} onChange={v => change(section, field, v)} />;
            else if (section.section === 'stateIsolationPolicy' && constraint?.choices) control = <Select id={id} className="instance-settings-control" aria-label={label} value={value as string} disabled={disabled} options={constraint.choices.map(choice => ({ value: choice, label: isolationChoices[choice] ?? choice }))} onChange={next => change(section, field, next)} />;
            else if (Array.isArray(inherited)) control = <Select id={id} className="instance-settings-control" aria-label={label} mode="multiple" maxCount={16} options={(section.definitionDefaults?.[field] as string[] ?? []).map(v => ({ label: v, value: v }))} value={Array.isArray(value) ? value : []} disabled={disabled} onChange={v => change(section, field, v)} />;
            else if (field === 'agentInstructions' || field === 'initializationScript') control = <Input.TextArea id={id} aria-label={label} autoSize={field === 'initializationScript' ? { minRows: 4, maxRows: 14 } : { minRows: 6, maxRows: 18 }} maxLength={field === "initializationScript" ? 16384 : 8000} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            else control = <Input id={id} className="instance-settings-control" aria-label={label} value={value as string ?? ''} disabled={disabled} onChange={e => change(section, field, nullable.has(field) && e.target.value === "" ? null : e.target.value)} />;
            return <Form.Item key={field} className={[wide && "instance-settings-field-wide", boolean && "instance-settings-switch-field"].filter(Boolean).join(" ")} htmlFor={id} label={boolean ? undefined : fieldLabel} extra={section.section === 'sandboxPolicy' && field === 'storageBytes' ? 'Sampled Session /working budget. Excess or unknown usage stops execution; written files remain. Home and container-only storage have separate limits.' : field === 'allowedProfileIds' ? 'Clear this override with Reset to inherit the Definition list. The default must belong to the saved allowed set.' : field === 'profileId' ? 'Initial choice for new Sessions. A Session retains its own selection until explicitly reset.' : undefined}>{boolean ? <Flex align="center" gap={token.paddingSM}>{fieldLabel}{control}</Flex> : control}</Form.Item>;
          })}
        </Form>
        <Flex wrap gap={token.paddingXS}>
          {!configuration.managed && <Button type="primary" aria-label={`Save ${title}`} disabled={archived || busy || saveBlocked || !changed} loading={busy} onClick={() => void save(section)}>Save</Button>}
          {!configuration.managed && <Button aria-label={`Discard ${title} changes`} disabled={busy || archived || !changed && Object.keys(section.overrides).length > 0} onClick={() => {
            setDrafts(current => ({ ...current, [section.section]: emptyDraft() }));
          }}>Discard</Button>}
          <Button aria-label={`Reset ${title} to Definition`} disabled={archived || busy || saveBlocked || !Object.keys(section.overrides).length} onClick={() => confirmAction(modal, { title: `Reset ${title}?`, content: `Clears only this section’s overrides and discards its unsaved edits. Other settings and budget profiles are preserved.${confirmationContext ? ` ${confirmationContext}` : ''}`, okText: "Reset section", onOk: () => resetSection(section) })}>{section.section === "sandboxPolicy" ? "Reset to inherited" : "Reset"}</Button>
        </Flex>
        <Typography.Text type="secondary">Instance adopted v{section.definitionVersion} · revision {section.instanceRevision}</Typography.Text>
      </Flex> };
    })} />}
    {page === 'sandbox' && sandboxContext?.summary({ dirty: dirtySections.length > 0, busy: busy || loading, revision: sections?.[0]?.instanceRevision })}

  </Flex>;
}
