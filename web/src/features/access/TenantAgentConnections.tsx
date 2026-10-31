import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Drawer, Empty, Flex, Form, Input, Pagination, Select, Tag, Typography } from 'antd';
import type { CredentialBinding } from '../../services/adminApi';
import { IdentityError, identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { credentialApi, credentialPurpose, purposeLabel, type CredentialAccess, type TenantCredential } from '../../services/tenantCredentials';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { CredentialConnectionTag } from '../admin/CredentialConnectionTag';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantCredentialPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { TenantBrowserProfilePanel } from './TenantBrowserProfilePanel';

export type TenantBindingPage = Page<CredentialBinding> & { allRead: boolean; instanceRevision: number; securityRevision: number; agentName: string; lifecycle: string; canUpdate: boolean; removableBindingIds: string[] };
export const tenantBindingApi = (tenant: string, id: string) => tenantApi(tenant, `/agents/${encodeURIComponent(id)}/credential-bindings`);
export function TenantAgentConnections({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const command = useTenantCommand();
  const [page, setPage] = useState(0); const [editing, setEditing] = useState(false); const opener = useRef<HTMLElement | null>(null);
  const state = useIdentityQuery<TenantBindingPage>(tenantBindingApi(tenant, id) + `?page=${page}&pageSize=10`); const view = state.data;
  const reload = () => { command.clearError(); state.reload(); };
  const close = () => { setEditing(false); requestAnimationFrame(() => opener.current?.isConnected ? opener.current.focus({ preventScroll: true }) : document.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus()); };
  const busy = command.busy || command.requiresReload;
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Button onClick={reload} disabled={command.busy || editing}>Reload connections</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : view && <>
      <Flex wrap align="center" gap="var(--ac-space-compact)" className="admin-draft-section-heading"><Typography.Title level={4}>{view.agentName}</Typography.Title><Tag>{view.lifecycle}</Tag></Flex>
      <Typography.Paragraph type="secondary">Bindings give this agent a stable reference to a credential. Its own permissions, capabilities and approval policy still control use. Binding does not connect an account or reveal protected values.</Typography.Paragraph>
      {view.lifecycle !== 'Active' ? <Alert type="info" showIcon title="Archived instance" description="Saved bindings remain readable. Restore this instance before changing them." /> : !view.canUpdate && <Alert type="info" showIcon title="Read-only connections" description="Changing bindings requires update access to this agent and bind access to the exact credential." />}
      {!view.allRead && <Typography.Text type="secondary">Only bindings to credentials you can read are shown.</Typography.Text>}
      <section aria-label="Saved credential bindings"><Flex vertical className="admin-draft-form-section"><Flex wrap justify="space-between" align="center" gap="var(--ac-space-compact)" className="admin-draft-section-heading"><Typography.Title level={4}>Credential bindings</Typography.Title>{view.canUpdate && view.lifecycle === 'Active' && <Button type="primary" disabled={busy} onClick={event => { opener.current = event.currentTarget; setEditing(true); }}>Add binding</Button>}</Flex>
        {!view.items.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={page ? 'No visible bindings on this page. Choose an earlier page.' : 'No credential bindings you can read.'} />}
        {view.items.map(row => <Flex key={row.bindingId} wrap align="start" justify="space-between" gap="var(--ac-space-compact)"><Flex vertical align="start" gap="var(--ac-space-tight)" className="access-row-summary"><Typography.Text strong>{row.reference}</Typography.Text><Typography.Link href={tenantCredentialPath(tenant, row.credentialId)} onClick={followAppLink}>{row.credential.displayName}</Typography.Link><Typography.Text type="secondary">{purposeLabel(credentialPurpose(row.credential))}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><Tag>{row.credential.status}</Tag>{(row.credential.emailAccount || row.credential.driveAccount) && <CredentialConnectionTag account={row.credential} showCredentialStatus={false} />}</Flex></Flex>
          {view.removableBindingIds.includes(row.bindingId) && <Button danger disabled={busy} aria-label={`Remove binding ${row.reference}`} onClick={() => confirmAction(modal, { title: 'Remove credential binding?', okText: 'Remove binding', danger: true,
            content: <Flex vertical gap="var(--ac-space-compact)" className="access-row-summary"><Typography.Text>Agent: {view.agentName}</Typography.Text><Typography.Text>Alias: {row.reference}</Typography.Text><Typography.Text>Credential: {row.credential.displayName}</Typography.Text><Typography.Text>Organization: {context.tenant.name}</Typography.Text><Typography.Text>This removes the association. The shared credential and its independent permissions are preserved.</Typography.Text></Flex>,
            onOk: () => command.run(signal => identityRequest(tenantBindingApi(tenant, id) + `/${row.bindingId}?expectedRevision=${view.securityRevision}&expectedInstanceRevision=${view.instanceRevision}&expectedBindingRevision=${row.revision}`, { method: 'DELETE', signal }), () => { if (page && view.items.length === 1) setPage(page - 1); else state.reload(); message.success('Credential binding removed.'); }) })}>Remove</Button>}
        </Flex>)}
        {view.total > 10 && <Pagination size="small" current={page + 1} total={view.total} pageSize={10} showSizeChanger={false} onChange={value => setPage(value - 1)} />}
      </Flex></section>
    </>}
    <TenantBrowserProfilePanel key={`${tenant}:${id}`} tenant={tenant} id={id} organization={context.tenant.name} onReset={state.reload} />
    {editing && <BindingEditor tenant={tenant} organization={context.tenant.name} id={id} onClose={close} onSaved={() => { close(); state.reload(); }} />}
  </Flex>;
}

function BindingEditor({ tenant, organization, id, onClose, onSaved }: { tenant: string; organization: string; id: string; onClose: () => void; onSaved: () => void }) {
  const { message } = App.useApp(); const command = useTenantCommand(); const [form] = Form.useForm<{ credentialId: string; reference: string }>(); const [dirty, setDirty] = useState(false); const [page, setPage] = useState(0);
  const parent = useIdentityQuery<TenantBindingPage>(tenantBindingApi(tenant, id) + '?page=0&pageSize=1');
  const candidates = useIdentityQuery<Page<TenantCredential>>(credentialApi(tenant) + `?page=${page}&pageSize=10`);
  const selectedId = Form.useWatch('credentialId', form);
  const selected = useIdentityQuery<TenantCredential>(selectedId ? credentialApi(tenant, selectedId) : null);
  const access = useIdentityQuery<CredentialAccess>(selectedId ? credentialApi(tenant, selectedId, '/access') : null);
  const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const reload = () => { parent.reload(); candidates.reload(); selected.reload(); access.reload(); command.clearError(); };
  useEffect(() => { if (command.error instanceof IdentityError && command.error.status === 403) { parent.reload(); candidates.reload(); selected.reload(); access.reload(); } }, [command.error]);
  const item = selected.data?.credential; const current = parent.data;
  const canSave = !!current?.canUpdate && current.lifecycle === 'Active' && !!item && item.status === 'Active' && !item.containerRegistry && item.purpose !== 'ModelInference' && access.data?.permissions.includes('credentials.bind') && !selected.error && !access.error;
  const choices = candidates.data?.items.map(value => value.credential).filter(value => !value.containerRegistry && value.purpose !== 'ModelInference') ?? [];
  if (item && !item.containerRegistry && item.purpose !== 'ModelInference' && !choices.some(value => value.credentialId === item.credentialId)) choices.unshift(item);
  const disabled = command.busy || command.requiresReload;
  return <>{guard.holder}<Drawer open title="Add credential binding" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" aria-label="Save binding" form="tenant-agent-binding" htmlType="submit" loading={command.busy} disabled={disabled || !canSave}>Save binding</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
      {parent.loading ? <AccessLoading /> : parent.error ? <AccessError error={parent.error} retry={reload} /> : current && <>
        <Typography.Paragraph>Agent: <Typography.Text strong>{current.agentName}</Typography.Text><br />Organization: {organization}</Typography.Paragraph>
        {!current.canUpdate || current.lifecycle !== 'Active' ? <Alert type="warning" showIcon title="Bindings cannot be changed" description="Current agent update access and an active instance are required. Your draft is retained; reload after access or status changes." /> : null}
        <Typography.Paragraph type="secondary">Each credential and alias can be bound only once per agent. Binding does not connect the account or grant execution permissions.</Typography.Paragraph>
        <Flex><Button disabled={command.busy} onClick={reload}>Reload latest</Button></Flex>
        <Form form={form} id="tenant-agent-binding" layout="vertical" disabled={disabled} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={values => {
          if (!current || !item || !canSave) return;
          void command.run(signal => identityRequest(tenantBindingApi(tenant, id), { method: 'POST', signal, body: { credentialId: item.credentialId, reference: values.reference.trim().toLowerCase(), expectedRevision: current.securityRevision, expectedInstanceRevision: current.instanceRevision, expectedCredentialRevision: item.revision } }), () => { guard.release(); setDirty(false); message.success('Credential binding saved. Execution permissions are unchanged.'); onSaved(); });
        }}>
          {candidates.error ? <AccessError error={candidates.error} retry={candidates.reload} /> : <>
            <Form.Item name="credentialId" label="Credential" extra="Search is limited to this page of readable credentials. Registry and model-inference credentials cannot be bound." rules={[{ required: true, message: 'Choose a credential.' }]}><Select autoFocus showSearch optionFilterProp="label" loading={candidates.loading} placeholder="Choose a saved credential" options={choices.map(value => ({ value: value.credentialId, label: value.credentialId === selectedId && selected.error ? 'Selected credential unavailable' : `${value.displayName} · ${purposeLabel(credentialPurpose(value))}`, disabled: value.status !== 'Active' }))} notFoundContent="No matching credentials on this page." /></Form.Item>
            {candidates.data && candidates.data.total > 10 && <Pagination size="small" aria-label="Credential pages" current={page + 1} total={candidates.data.total} pageSize={10} showSizeChanger={false} disabled={disabled} onChange={value => setPage(value - 1)} />}
          </>}
          {selectedId && (selected.error || access.error ? <AccessError error={selected.error ?? access.error} retry={reload} /> : selected.loading || access.loading ? <Typography.Text role="status" type="secondary">Checking selected credential…</Typography.Text> : item && <Flex vertical align="start" gap="var(--ac-space-compact)" className="admin-draft-form-section"><Typography.Text strong>{item.displayName}</Typography.Text><Tag>{item.status}</Tag>{(item.emailAccount || item.driveAccount) && <CredentialConnectionTag account={item} showCredentialStatus={false} />}{!access.data?.permissions.includes('credentials.bind') && <Alert type="warning" showIcon title="Bind access is required" description="You can read this credential, but cannot bind it. Choose another credential or ask an access administrator." />}<Typography.Link href={tenantCredentialPath(tenant, item.credentialId)} onClick={followAppLink}>Open credential details</Typography.Link></Flex>)}
          <Form.Item name="reference" label="Alias" extra="1–64 letters, digits or hyphens. Starts with a letter or digit; saved lowercase." rules={[{ required: true, message: 'Enter a stable alias.' }, { validator: (_, value?: string) => !value || /^[a-z0-9][a-z0-9-]{0,63}$/i.test(value.trim()) ? Promise.resolve() : Promise.reject(new Error('Use 1–64 letters, digits or hyphens, starting with a letter or digit.')) }]}><Input maxLength={66} placeholder="primary-account" autoComplete="off" /></Form.Item>
        </Form>
      </>}
    </Flex>
  </Drawer></>;
}
