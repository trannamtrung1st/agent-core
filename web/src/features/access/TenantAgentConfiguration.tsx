import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Typography } from 'antd';
import { identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { tenantAgentApi, readTenantAgentEditor, patchTenantAgentSettings, type TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import type { TenantSpace } from '../../services/tenantSpaces';
import type { listModels } from '../../services/api';
import { followAppLink } from '../../app/appLink';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { InstancePersonaFields } from '../admin/InstancePersonaFields';
import { InstanceSettingsSection } from '../admin/InstanceSettingsSection';
import type { InstanceSettingsTransport } from '../admin/useInstanceSettingsController';
import { isPersonaDraftDirty, parsePersonaJson, personaFieldsToJson, syncPersonaOnTabChange, type PersonaFields } from '../admin/instanceManagedControls';
import { TenantAgentHeading } from './TenantAgentHeading';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantSpacePath, tenantAccessPath, tenantAgentPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantAgentConfiguration({ context, id, page }: { context: TenantContext; id: string; page: 'profile' | 'settings' }) {
  return page === 'profile' ? <TenantAgentProfile context={context} id={id} /> : <TenantAgentSettings context={context} id={id} />;
}
function TenantAgentProfile({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { message } = App.useApp(); const command = useTenantCommand();
  const state = useIdentityQuery<TenantAgentEditor>(tenantAgentApi(tenant, id, '/editor'));
  const privateSpace = useIdentityQuery<TenantSpace>(tenantAgentApi(tenant, id, '/private-space'));
  const [saved, setSaved] = useState<PersonaFields>(); const [persona, setPersona] = useState<PersonaFields>(); const [json, setJson] = useState(''); const [tab, setTab] = useState('form'); const [error, setError] = useState<string>(); const initialized = useRef(false);
  useEffect(() => { if (!state.data) return; const value = state.data.agent.instance.persona; setSaved(value); if (!initialized.current) { initialized.current = true; setPersona(value); setJson(personaFieldsToJson(value)); } }, [state.data]);
  const dirty = !!saved && !!persona && isPersonaDraftDirty(saved, tab, persona, json);
  const guard = useAccessDraftGuard(dirty, command.busy, () => {});
  const reload = () => { command.clearError(); state.reload(); };
  const view = state.data; const writable = !!view?.canUpdate && view.agent.instance.lifecycle === 'Active';
  const save = () => {
    if (!view || !persona || !writable) return;
    let value: PersonaFields;
    try { value = tab === 'json' ? parsePersonaJson(json) : persona; for (const key of ['name', 'role', 'description', 'tone'] as const) if (!value[key].trim() || value[key].length > (key === 'description' ? 1024 : 256)) throw new Error('Complete all persona fields. Name, role and tone allow 256 characters; description allows 1,024.'); }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Repair the persona before saving.'); return; }
    setError(undefined);
    void command.run(signal => identityRequest<{ instance: TenantAgentEditor['agent']['instance'] }>(tenantAgentApi(tenant, id, '/persona'), { method: 'PUT', signal, body: { persona: value, expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision, expectedPersonaRevision: view.agent.instance.personaRevision } }), result => { setPersona(result.instance.persona); setSaved(result.instance.persona); setJson(personaFieldsToJson(result.instance.persona)); message.success('Persona saved. New Runs use the updated guidance.'); state.reload(); });
  };
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button disabled={command.busy} onClick={reload}>Reload profile</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && persona && <>
      <TenantAgentHeading view={view} />
      {privateSpace.data && <Typography.Link href={tenantSpacePath(tenant, privateSpace.data.spaceId)} onClick={followAppLink}>Open Private Space</Typography.Link>}
      <AgentConfigurationPanel title="Persona" label="Persona editor" description="Persona guides this agent’s behavior. Core permissions and approvals remain independent." bodyGap="section">
        <InstancePersonaFields persona={persona} onChange={value => { setPersona(value); setError(undefined); }} tab={tab} onTabChange={next => { const result = syncPersonaOnTabChange(tab, next, persona, json); if (!result.ok) { setError(result.error); return; } setPersona(result.persona); setJson(result.personaJsonDraft); setTab(next); setError(undefined); }} json={json} onJsonChange={value => { setJson(value); setError(undefined); }} disabled={!writable || command.busy || command.requiresReload} />
        {error && <Alert type="error" showIcon title={error} />}
        {writable && <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" aria-label="Save persona" loading={command.busy} disabled={!dirty || command.requiresReload} onClick={save}>Save persona</Button><Button disabled={command.busy || !dirty} onClick={() => { setPersona(saved!); setJson(personaFieldsToJson(saved!)); setError(undefined); }}>Discard persona changes</Button></Flex>}
        {dirty && <Typography.Text type="secondary">Unsaved persona changes. Reload keeps your draft; leaving asks before discarding.</Typography.Text>}
      </AgentConfigurationPanel>
    </>}
  </Flex>;
}
function TenantAgentSettings({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const [view, setView] = useState<TenantAgentEditor>(); const [dirty, setDirty] = useState(false); const [busy, setBusy] = useState(false);
  const guard = useAccessDraftGuard(dirty, busy, () => {});
  const transport = useMemo<InstanceSettingsTransport>(() => ({
    list: async (_, signal) => { signal?.throwIfAborted(); setView(undefined); const current = await readTenantAgentEditor(tenant, id, signal); if (!signal?.aborted) setView(current); return current.settings.map(section => ({ ...section, securityRevision: current.securityRevision })); },
    patch: (_, section, set, clear, signal) => patchTenantAgentSettings(tenant, id, section, set, clear, signal),
    models: signal => identityRequest<Awaited<ReturnType<typeof listModels>>>(tenantApi(tenant, '/chat/models'), { signal })
  }), [tenant, id]);
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link>
    {view && <TenantAgentHeading view={view} />}
    <Typography.Paragraph type="secondary">Settings define behavior and capability eligibility. The agent’s own roles and approval policy still control execution.</Typography.Paragraph>
    <Typography.Link href={tenantAgentPath(tenant, id, 'state-isolation')} onClick={followAppLink}>State, Memory and Voice enablement share the coordinated State & Isolation editor</Typography.Link>
    <InstanceSettingsSection confirmationContext={view ? `Agent: ${view.agent.instance.persona.name}. Organization: ${context.tenant.name}.` : undefined} instanceId={id} archived={!view?.canUpdate || view.agent.instance.lifecycle !== 'Active'} transport={transport} onUpdated={() => {}} onDirtyChange={setDirty} onBusyChange={setBusy} />
    {view?.unavailableSettings.includes('sandboxPolicy') && <Alert type="info" showIcon title="Sandbox has a separate settings page" description={<Typography.Link href={tenantAgentPath(tenant, id, 'sandbox')} onClick={followAppLink}>Open Sandbox configuration and saved setup verification. Independent Sandbox and profile access is required.</Typography.Link>} />}
  </Flex>;
}
