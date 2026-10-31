import { useEffect, useRef, useState, type ComponentRef } from 'react';
import { Alert, App, Button, Empty, Flex, Input, Pagination, Switch, Table, Tag, Typography } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantSkillDetail, type TenantSkillEditor, type TenantSkillItem } from '../../services/tenantAgentConfiguration';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { SkillDrawer, type SkillDrawerValue } from '../admin/SkillDrawer';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const skillFeedbackKey = 'tenant-skill-change';
const lostRead = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
const stale = (error: unknown) => error instanceof IdentityError && [401, 403, 404, 409].includes(error.status);
const valueOf = (item?: TenantSkillItem): SkillDrawerValue => item ? { ...item.skill, id: item.skill.key.slice(item.skill.key.indexOf(':') + 1), capabilities: item.skill.requiredCapabilities.join(', ') } : { name: '', description: '', procedure: '', projection: 'OnDemand', enabled: true, capabilities: '' };
type Selection = { key?: string; inspect: boolean; opener: HTMLElement };

export function TenantAgentSkills({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const command = useTenantCommand();
  const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [query, setQuery] = useState(''); const [refresh, setRefresh] = useState(0);
  const [selection, setSelection] = useState<Selection>(); const [confirming, setConfirming] = useState(false); const [committed, setCommitted] = useState(false);
  const state = useIdentityQuery<TenantSkillEditor>(tenantAgentApi(tenant, id, `/skills/editor?page=${page}&pageSize=10&search=${encodeURIComponent(query)}`), String(refresh));
  const view = state.data; const busy = command.busy || confirming; const reloadButton = useRef<ComponentRef<typeof Button>>(null);
  const alive = useRef(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; confirmation.current?.destroy(); }; }, []);
  useEffect(() => { if (view) setCommitted(false); }, [view]);
  useEffect(() => { if (lostRead(state.error)) { setSelection(undefined); confirmation.current?.destroy(); } }, [state.error]);
  const guard = useAccessDraftGuard(false, busy, () => {});
  const blocked = busy || committed || stale(command.error) || state.loading;
  function reload() { command.clearError(); setRefresh(value => value + 1); }
  function close() { const opener = selection?.opener; setSelection(undefined); requestAnimationFrame(() => { if (opener?.isConnected) opener.focus(); else reloadButton.current?.focus(); }); }
  async function mutate(operation: string, item: TenantSkillItem, enabled?: boolean) {
    if (!view || blocked || selection) return;
    await command.run(async signal => {
      try { await identityRequest(tenantAgentApi(tenant, id, '/skills/commands'), { method: 'POST', signal, body: { operation, key: item.skill.key, enabled, expectedSkillRevision: item.skill.revision, expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision } }); }
      catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
    }, () => { setCommitted(true); message.success({ key: skillFeedbackKey, content: 'Skill change saved. Reading current values.' }); setRefresh(value => value + 1); });
  }
  function confirm(operation: string, item: TenantSkillItem) {
    if (!view || blocked || selection) return;
    message.destroy(skillFeedbackKey); let submitted = false; setConfirming(true);
    const title = operation === 'customize' ? 'Customize Definition Skill?' : operation === 'delete' ? 'Delete Instance Skill?' : 'Reset Skill choice?';
    confirmation.current = confirmAction(modal, { title, danger: operation === 'delete', okText: operation === 'customize' ? 'Customize Skill' : operation === 'delete' ? 'Delete Skill' : 'Reset choice',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>Skill: {item.skill.name}</Typography.Text><Typography.Text>Agent: {view.agent.instance.persona.name}. Organization: {context.tenant.name}.</Typography.Text><Typography.Text>{operation === 'customize' ? 'Create an independent local procedure and disable this inherited source atomically. Future Definition changes will not update the copy.' : operation === 'delete' ? 'Remove this local procedure for future Runs.' : 'Remove the stored enabled choice and inherit the source default for future Runs.'}</Typography.Text></Flex>,
      onOk: async () => { if (!alive.current) return; submitted = true; setConfirming(false); await mutate(operation, item); },
      afterClose: () => { confirmation.current = null; if (alive.current) { setConfirming(false); if (submitted) requestAnimationFrame(() => reloadButton.current?.focus()); } }
    });
  }
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to Agents</Typography.Link><Button ref={reloadButton} disabled={busy || !!selection} onClick={reload}>Reload Skills</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {committed && <Alert type="info" showIcon title="Skill change was saved" description="Reload reads current facts and never replays a command." />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && <>
      <TenantAgentHeading view={{ agent: view.agent, canUpdate: view.canCreate || view.items.some(item => item.canManage) }} readOnlyDescription="Skill changes require typed manage access on the item. Creating a local Skill requires organization-wide Skill read and manage access." />
      <AgentConfigurationPanel title="Skills" label="Instance Skills" description="Procedures supply know-how, never capabilities or execution authority. Changes apply to the next new Run." bodyGap="section">
        {!view.allReadable && <Alert type="info" showIcon title="Only readable Skills are shown" description="Search and totals include only the Skills you can read." />}
        <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" disabled={blocked || !!selection || !view.canCreate} onClick={event => setSelection({ inspect: false, opener: event.currentTarget })}>New Instance Skill</Button><Input.Search enterButton="Search" aria-label="Search readable Skills" placeholder="Search readable Skills" maxLength={128} value={search} disabled={busy || !!selection} onChange={event => setSearch(event.target.value)} onSearch={value => { setPage(0); setQuery(value.trim()); }} /></Flex>
        <Table<TenantSkillItem> size="small" className="admin-collection-table" tableLayout="fixed" rowKey={item => item.skill.key} pagination={false} dataSource={view.items} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={query ? 'No matching readable Skills.' : 'No readable Skills on this page.'} /> }} columns={[{ title: 'Skill and configuration', render: (_, item) => {
          const s = item.skill; return <Flex vertical gap="var(--ac-space-compact)">
            <Typography.Text strong className="admin-instance-id">{s.name}</Typography.Text><Typography.Text className="admin-instance-id">{s.description}</Typography.Text>
            <Flex wrap gap="var(--ac-space-compact)"><Tag>{s.origin === 'BuiltIn' ? 'Built-in' : s.origin}</Tag><Tag>{s.projection === 'Always' ? 'Always' : 'On demand'}</Tag><Typography.Text type="secondary">{s.origin === 'BuiltIn' ? 'Core procedure · read-only' : s.definitionVersion ? `Definition v${s.definitionVersion} · read-only content` : 'Local procedure'}</Typography.Text></Flex>
            {s.sourceDefinitionId && <Typography.Text type="secondary" className="admin-instance-id">Copied from Definition v{s.sourceDefinitionVersion} · Skill {s.sourceDefinitionSkillId}</Typography.Text>}
            <Typography.Text type="secondary" className="admin-instance-id">Requires: {s.requiredCapabilities.join(', ') || 'No capabilities'}</Typography.Text>
            {!!s.missingCapabilities.length && <Typography.Text type="warning" className="admin-instance-id">Missing capabilities: {s.missingCapabilities.join(', ')}</Typography.Text>}
            <Flex data-feedback-actions wrap align="center" gap="var(--ac-space-compact)"><Switch aria-label={`Enable Skill ${s.name}`} checked={s.origin === 'BuiltIn' ? s.enabledChoice ?? s.enabled : s.enabled} disabled={blocked || !!selection || !item.canManage} onChange={enabled => void mutate('set_enabled', item, enabled)} /><Typography.Text>{s.enabled ? 'Effective: enabled' : 'Effective: disabled'}</Typography.Text>{s.origin !== 'Instance' && <Typography.Text type="secondary">{s.enabledOverride == null ? 'Inherited choice' : 'Instance choice'}</Typography.Text>}</Flex>
            {s.origin === 'BuiltIn' && !s.enabled && s.enabledChoice && <Typography.Text type="secondary">{s.missingCapabilities.length ? 'Missing required capabilities' : 'Suppressed by management policy'}</Typography.Text>}
            <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button disabled={command.busy || !!selection} onClick={event => setSelection({ key: s.key, inspect: s.origin !== 'Instance' || !item.canManage, opener: event.currentTarget })}>{s.origin === 'Instance' && item.canManage ? 'Edit Skill' : 'Inspect Skill'}</Button>{item.canCustomize && <Button disabled={command.busy || committed || stale(command.error)} onClick={() => confirm('customize', item)}>Customize</Button>}{s.origin === 'Instance' && item.canManage && <Button danger disabled={command.busy || committed || stale(command.error)} onClick={() => confirm('delete', item)}>Delete</Button>}{s.origin !== 'Instance' && s.enabledOverride != null && item.canManage && <Button disabled={command.busy || committed || stale(command.error)} onClick={() => confirm('reset_enabled', item)}>Reset choice</Button>}</Flex>
          </Flex>;
        } }]} />
        {view.total > 10 && <Pagination size="small" aria-label="Skill pages" current={page + 1} total={view.total} pageSize={10} showSizeChanger={false} disabled={busy || !!selection} onChange={value => setPage(value - 1)} />}
      </AgentConfigurationPanel>
    </>}
    {selection && <TenantSkillDrawer key={selection.key ?? 'new'} tenant={tenant} organization={context.tenant.name} id={id} selection={selection} onClose={close} onReadLost={() => { close(); setRefresh(value => value + 1); }} onSaved={() => { close(); setCommitted(true); setRefresh(value => value + 1); }} />}
  </Flex>;
}

function TenantSkillDrawer({ tenant, organization, id, selection, onClose, onReadLost, onSaved }: { tenant: string; organization: string; id: string; selection: Selection; onClose: () => void; onReadLost: () => void; onSaved: () => void }) {
  const { message } = App.useApp(); const command = useTenantCommand(); const [refresh, setRefresh] = useState(0); const [dirty, setDirty] = useState(false); const [initial, setInitial] = useState<SkillDrawerValue>();
  const state = useIdentityQuery<TenantSkillDetail | TenantSkillEditor>(tenantAgentApi(tenant, id, selection.key ? `/skills/${encodeURIComponent(selection.key)}/editor` : '/skills/editor?pageSize=1'), String(refresh));
  const detail = state.data && 'item' in state.data ? state.data : undefined; const view = state.data;
  useEffect(() => { if (view) setInitial(current => selection.inspect || !current ? valueOf(detail?.item) : current); }, [view, selection.inspect]);
  useEffect(() => { if (lostRead(state.error)) { setInitial(undefined); onReadLost(); } }, [state.error]);
  const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const allowed = !!view && view.agent.instance.lifecycle === 'Active' && (detail ? detail.item.canManage && detail.item.skill.origin === 'Instance' : 'canCreate' in view && view.canCreate);
  const disabled = state.loading || !!state.error || !allowed || stale(command.error);
  const reload = () => { command.clearError(); setRefresh(value => value + 1); };
  const notice = <Flex vertical gap="var(--ac-space-compact)"><Flex wrap gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={reload}>Reload latest Skill</Button>{dirty && view && <Button disabled={command.busy || state.loading} onClick={() => { setInitial(valueOf(detail?.item)); setDirty(false); }}>Discard draft</Button>}</Flex>{state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : !selection.inspect && !allowed && <Alert type="warning" showIcon title="Skill cannot be changed" description="Your draft is retained. Current typed manage access and an Active instance are required." />}{command.error !== undefined && <AccessError error={command.error} retry={reload} />}{dirty && <Typography.Text type="secondary">Unsaved changes. Reload keeps this draft; Cancel asks before discarding.</Typography.Text>}</Flex>;
  if (lostRead(state.error)) return guard.holder;
  return <>{guard.holder}<SkillDrawer open title={selection.inspect ? 'Skill inspection' : selection.key ? 'Edit Instance Skill' : 'New Instance Skill'} value={initial ?? null} readOnly={selection.inspect} idReadOnly={!!selection.key} busy={command.busy} disabled={disabled} saveDisabled={!dirty} notice={notice}
    context={<><Typography.Text>Agent: {view?.agent.instance.persona.name ?? 'Loading…'}. Organization: {organization}.</Typography.Text><br />Procedures apply to future Runs and grant no capabilities or IAM authority. Inherited and Built-in content is read-only.</>} contentLabel={selection.inspect ? 'Skill content' : 'Instance Skill editor'} focusTriggerAfterClose={false} onValuesChange={() => setDirty(true)} onClose={guard.close}
    onSave={async value => {
      if (!view || disabled || !dirty) return;
      await command.run(async signal => {
        try { await identityRequest(tenantAgentApi(tenant, id, '/skills/commands'), { method: 'POST', signal, body: { operation: selection.key ? 'update' : 'create', key: selection.key, expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision, expectedSkillRevision: detail?.item.skill.revision,
          input: { ...(selection.key || !value.id?.trim() ? {} : { id: value.id.trim() }), name: value.name, description: value.description, procedure: value.procedure, projection: value.projection, enabled: value.enabled, requiredCapabilities: value.capabilities.split(',').map(part => part.trim()).filter(Boolean) } } }); }
        catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
      }, () => { guard.release(); setDirty(false); message.success({ key: skillFeedbackKey, content: 'Skill saved. Changes apply to the next new Run.' }); onSaved(); });
    }} /></>;
}
