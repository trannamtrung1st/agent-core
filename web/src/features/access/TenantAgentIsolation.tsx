import { useEffect, useState } from 'react';
import { Alert, Button, Flex, Form, Select, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantIsolationPlan, type TenantIsolationView } from '../../services/tenantAgentConfiguration';
import { deriveIsolationPreset, isolationValue, type IsolationDraft, type IsolationPlan } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';
import { IsolationChangePreview } from '../admin/IsolationChangePreview';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { StateIsolationPolicyFields, StateIsolationToggleField } from '../admin/StateIsolationPolicyFields';
import { TenantAgentHeading } from './TenantAgentHeading';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAccessPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const memoryLabels: Record<string, string> = { sessionMemory: 'Session memory', identityUserPromotion: 'Instance memory promotion', identityUserRetrieval: 'Instance memory retrieval', userPromotion: 'User memory promotion', userRetrieval: 'User memory retrieval' };
const presets = [{ value: 'Persistent', label: 'Persistent' }, { value: 'SessionIsolated', label: 'Session isolated' }, { value: 'Stateless', label: 'Stateless' }, { value: 'DefinitionDefaults', label: 'Definition defaults' }];

export function TenantAgentIsolation({ context, id }: { context: TenantContext; id: string }) {
  return <TenantIsolationEditor key={`${context.tenant.tenantId}:${id}`} context={context} id={id} />;
}
function TenantIsolationEditor({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const root = tenantAgentApi(tenant, id, '/state-isolation');
  const state = useIdentityQuery<TenantIsolationView>(root); const command = useTenantCommand();
  const [draft, setDraft] = useState<IsolationDraft>(); const [plan, setPlan] = useState<IsolationPlan>();
  const [mustReload, setMustReload] = useState(false); const [saved, setSaved] = useState(false);
  const lostAccess = command.error instanceof IdentityError && [401, 403, 404].includes(command.error.status);
  const dirty = !!draft; const guard = useAccessDraftGuard(dirty && !lostAccess, command.busy, () => {});
  useEffect(() => { if (state.data) { setPlan(undefined); setMustReload(false); } }, [state.data]);
  useEffect(() => { if (lostAccess || state.error instanceof IdentityError && [401, 403, 404].includes(state.error.status)) { setDraft(undefined); setPlan(undefined); } }, [lostAccess, state.error]);
  const view = state.data; const configuration = view?.configuration;
  const current = draft ?? { settings: [], experienceEnabled: configuration?.experienceEnabled ?? false, allowMaintenance: configuration?.allowMaintenance ?? false };
  const disabled = !view?.canUpdate || view.agent.instance.lifecycle !== 'Active' || command.busy || mustReload || command.requiresReload;
  const reload = () => { setPlan(undefined); command.clearError(); state.reload(); };
  const changeDraft = (value: IsolationDraft) => { setDraft(value); setPlan(undefined); setSaved(false); };
  const patch = (section: string, field: string, value?: SettingValue) => {
    const old = current.settings.find(item => item.section === section) ?? { section, set: {}, clear: [] };
    const set = { ...old.set }; delete set[field]; if (value !== undefined) set[field] = value;
    const clear = old.clear.filter(item => item !== field); if (value === undefined) clear.push(field);
    changeDraft({ ...current, settings: [...current.settings.filter(item => item.section !== section), { section, set, clear }] });
  };
  const accept = (result: TenantIsolationPlan, adopt: boolean) => {
    if (!view || result.securityRevision !== view.securityRevision || result.plan.expected.instance !== configuration?.revisions.instance
      || result.plan.expected.experience !== configuration.revisions.experience || result.plan.expected.maintenance !== configuration.revisions.maintenance) {
      setMustReload(true); setPlan(undefined); return;
    }
    if (adopt) changeDraft(result.plan.draft); setPlan(result.plan);
  };
  const source = (section: string, field: string) => {
    const savedSection = configuration!.settings.find(item => item.section === section)!;
    const pending = current.settings.find(item => item.section === section);
    const inherited = pending?.clear.includes(field) || !(pending && field in pending.set) && !(field in savedSection.overrides);
    return <Flex wrap gap="var(--ac-space-compact)"><Typography.Text type="secondary">{inherited ? 'Inherited from Definition' : 'Instance override'}</Typography.Text><Button type="link" size="small" disabled={disabled || inherited} onClick={() => patch(section, field)}>Use Definition default</Button></Flex>;
  };
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button disabled={command.busy} onClick={reload}>Reload configuration</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {saved && <Alert type="success" showIcon title="Configuration saved" description="The coordinated metadata change is saved. New Runs use the updated policy." />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : !lostAccess && view && configuration && <>
      <TenantAgentHeading view={view} />
      <AgentConfigurationPanel title="State & Isolation" label="Coordinated State and Isolation" description="Review state, memory and learning eligibility together. Saved opt-ins do not grant execution permissions." bodyGap="section">
        <Form layout="vertical">
          <Form.Item label="Preset draft" extra={`Current recipe: ${deriveIsolationPreset(configuration, current)}. Choosing a preset prepares a draft; Save is a separate action.`}>
            <Select aria-label="Preset draft" placeholder="Choose a preset" disabled={disabled} options={presets} value={undefined} onChange={preset => void command.run(signal => identityRequest<TenantIsolationPlan>(root + `/presets/${preset}`, { method: 'POST', signal }), result => accept(result, true))} />
          </Form.Item>
          <StateIsolationPolicyFields value={field => isolationValue(configuration, current, 'stateIsolationPolicy', field)} choices={field => configuration.settings.find(item => item.section === 'stateIsolationPolicy')!.constraints?.[field]?.choices ?? []}
            disabled={() => disabled} extra={field => source('stateIsolationPolicy', field)} onChange={(field, value) => patch('stateIsolationPolicy', field, value)} />
          <Typography.Title level={5}>Memory and Voice</Typography.Title>
          <div className="admin-settings-field-grid">{Object.entries(memoryLabels).map(([field, label]) => <StateIsolationToggleField key={field} label={label} checked={isolationValue(configuration, current, 'memoryPolicy', field) === true} disabled={disabled} extra={source('memoryPolicy', field)} onChange={value => patch('memoryPolicy', field, value)} />)}
            <StateIsolationToggleField label="Voice enabled" checked={isolationValue(configuration, current, 'voice', 'enabled') === true} disabled={disabled} extra={source('voice', 'enabled')} onChange={value => patch('voice', 'enabled', value)} />
          </div>
          <Typography.Title level={5}>Learning eligibility</Typography.Title>
          <div className="admin-settings-field-grid"><StateIsolationToggleField label="Experience enabled" checked={current.experienceEnabled} disabled={disabled} onChange={value => changeDraft({ ...current, experienceEnabled: value })} />
            <StateIsolationToggleField label="Allow identity maintenance" checked={current.allowMaintenance} disabled={disabled} onChange={value => changeDraft({ ...current, allowMaintenance: value })} /></div>
        </Form>
        <Typography.Paragraph type="secondary">These settings save eligibility metadata. Experience generation and maintenance execution are qualified separately. Existing history, home files and Artifacts remain. Runtime and native enforcement are not observed here.</Typography.Paragraph>
      </AgentConfigurationPanel>
      <div aria-label={plan ? 'Configuration preview' : undefined}>
      <IsolationChangePreview changes={plan?.changes} blockingReasons={plan?.blockingReasons} notice={plan?.notice}>
        {mustReload && <Alert type="warning" showIcon title="Read current configuration before continuing" description="The save outcome or preview is no longer current. Reload keeps your draft and never repeats a save." />}
        {!plan && <Typography.Text type="secondary">Preview your draft before saving. Reload retains your choices and requires a fresh preview.</Typography.Text>}
        <Flex wrap gap="var(--ac-space-compact)">
          <Button disabled={disabled || !draft} loading={command.busy} onClick={() => void command.run(signal => identityRequest<TenantIsolationPlan>(root + '/preview', { method: 'POST', signal, body: current }), result => accept(result, false))}>Preview changes</Button>
          <Button type="primary" disabled={disabled || !plan || !plan.changes.length || !!plan.blockingReasons.length} onClick={() => {
            if (!plan || !view) return; setMustReload(true); setPlan(undefined);
            void command.run(async signal => {
              const result = await identityRequest<{ instance: { instanceId: string; revision: number } }>(root + '/apply', { method: 'POST', signal, body: { expectedRevision: view.securityRevision, expected: plan.expected, draft: plan.draft, fingerprint: plan.fingerprint } });
              if (result?.instance?.instanceId !== id || !Number.isSafeInteger(result.instance.revision) || result.instance.revision < 1) throw new IdentityCompletionError();
              return result;
            }, () => { setDraft(undefined); setSaved(true); state.reload(); });
          }}>Save coordinated configuration</Button>
          <Button disabled={command.busy || !draft} onClick={() => { setDraft(undefined); setPlan(undefined); }}>Discard draft</Button>
        </Flex>
        {dirty && <Typography.Text type="secondary">Unsaved coordinated draft. Reload keeps these choices; leaving asks before discarding.</Typography.Text>}
      </IsolationChangePreview>
      </div>
    </>}
  </Flex>;
}
