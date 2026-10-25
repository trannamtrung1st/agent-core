import { useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Form, Input, Select, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { secretApi, type TenantSecret } from '../../services/tenantSpaces';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { TenantResourceAccess } from './TenantResourceAccess';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantEditorCommand } from './useTenantEditorCommand';
import { useTenantCommand } from './useTenantCommand';

type SecretAccess = { permissions: string[]; securityRevision: number };
export function TenantSecrets({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [selected, setSelected] = useState<string>(); const [creating, setCreating] = useState(false); const opener = useRef<HTMLElement | null>(null);
  const state = useIdentityQuery<Page<TenantSecret>>(secretApi(tenant) + `?page=${page}&pageSize=20`);
  const close = () => { setSelected(undefined); setCreating(false); requestAnimationFrame(() => opener.current?.isConnected ? opener.current.focus() : document.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus()); };
  return <Flex vertical gap="var(--ac-space-section)"><AdminCollectionToolbar label="loaded Secrets" value={search} onChange={setSearch} showScrollHint={false} filters={<><Button onClick={state.reload}>Reload</Button>{context.permissions.includes('secrets.manage') && <Button type="primary" onClick={event => { opener.current = event.currentTarget; setCreating(true); }}>Create Secret</Button>}</>} />
    <Typography.Paragraph type="secondary">Secrets are protected organization resources. Values can be created or replaced and cannot be read back. Bindings and permissions are managed separately.</Typography.Paragraph>
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : !state.data ? <AccessLoading /> : <Table<TenantSecret> className="admin-collection-table" rowKey="secretId" dataSource={state.data.items.filter(s => `${s.name} ${s.description}`.toLowerCase().includes(search.toLowerCase()))} pagination={{ current: page + 1, pageSize: 20, total: state.data.total, responsive: true, showSizeChanger: false, onChange: n => setPage(n - 1) }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching Secrets on this page.' : 'No Secrets you can read.'} /> }} columns={[
      { title: 'Secret', render: (_, s) => <Flex vertical><Button type="link" style={{ alignSelf: 'flex-start' }} onClick={event => { opener.current = event.currentTarget; setSelected(s.secretId); }}>{s.name}</Button><Typography.Text type="secondary">{s.description}</Typography.Text></Flex> },
      { title: 'Status', width: 100, render: (_, s) => <Tag>{s.status}</Tag> }
    ]} />}
    {creating && <SecretEditor context={context} mode="create" onClose={close} onSaved={() => { close(); state.reload(); }} />}
    {selected && <SecretInspector key={`${tenant}:${selected}`} context={context} id={selected} onClose={close} onChanged={state.reload} />}
  </Flex>;
}
function SecretInspector({ context, id, onClose, onChanged }: { context: TenantContext; id: string; onClose: () => void; onChanged: () => void }) {
  const tenant = context.tenant.tenantId; const state = useIdentityQuery<TenantSecret>(secretApi(tenant, id)); const access = useIdentityQuery<SecretAccess>(secretApi(tenant, id, '/access')); const [editing, setEditing] = useState<'edit' | 'replace'>(); const command = useTenantCommand(); const { modal } = App.useApp();
  const reload = () => { state.reload(); access.reload(); onChanged(); }; const secret = state.data; const manage = access.data?.permissions.includes('secrets.manage');
  return <Drawer open title="Secret details" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={onClose} keyboard={!command.busy && !editing} maskClosable={!command.busy && !editing} closable={!command.busy && !editing}>
    {state.error || access.error ? <AccessError error={state.error ?? access.error} retry={reload} /> : !secret || !access.data ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)">
      <Typography.Title level={4}>{secret.name}</Typography.Title>{command.error !== undefined && <AccessError error={command.error} />}<Descriptions column={1} size="small" items={[{ label: 'Organization', children: context.tenant.name }, { label: 'Description', children: secret.description || 'No description' }, { label: 'Status', children: <Tag>{secret.status}</Tag> }, { label: 'Value', children: 'Protected · cannot be read back' }]} />
      {!manage && <Alert type="info" showIcon title="Read-only Secret" />}
      <Flex wrap gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={reload}>Reload details</Button>{manage && <><Button disabled={command.busy} onClick={() => setEditing('edit')}>Edit Secret</Button><Button disabled={command.busy} onClick={() => setEditing('replace')}>Replace protected value</Button><Button danger disabled={command.busy || command.requiresReload} onClick={() => confirmAction(modal, { title: `Delete ${secret.name}?`, content: 'Its protected value will be removed. References and saved Runs will no longer authorize use. This cannot be undone.', okText: 'Delete Secret', danger: true, onOk: () => command.run(async signal => { const current = await identityRequest<SecretAccess>(secretApi(tenant, id, '/access'), { signal }); return identityRequest(secretApi(tenant, id) + `?expectedRevision=${current.securityRevision}&expectedSecretRevision=${secret.revision}`, { method: 'DELETE', signal }); }, () => { onChanged(); onClose(); }) })}>Delete Secret</Button></>}</Flex>
      {access.data.permissions.includes('iam.roles.read') && <Collapse items={[{ key: 'access', label: 'Secret permissions', children: <TenantResourceAccess tenant={tenant} resourceType="secret" resourceId={id} /> }]} />}
      {editing && <SecretEditor context={context} secret={secret} mode={editing} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); reload(); }} />}
    </Flex>}
  </Drawer>;
}
function SecretEditor({ context, secret, mode, onClose, onSaved }: { context: TenantContext; secret?: TenantSecret; mode: 'create' | 'edit' | 'replace'; onClose: () => void; onSaved: () => void }) {
  const [id] = useState(() => secret?.secretId ?? crypto.randomUUID());
  const [dirty, setDirty] = useState(false); const [revision, setRevision] = useState(secret?.revision ?? 0); const command = useTenantEditorCommand(); const [form] = Form.useForm(); const guard = useAccessDraftGuard(dirty, command.busy, onClose); const tenant = context.tenant.tenantId;
  return <>{guard.holder}<Drawer open title={mode === 'create' ? 'Create Secret' : mode === 'edit' ? 'Edit Secret' : 'Replace protected value'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy} destroyOnHidden
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" form="secret-editor" htmlType="submit" loading={command.busy} disabled={command.blocked}>Save Secret</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">{command.error !== undefined && <AccessError error={command.error} />}{command.unknown && <Alert type="warning" showIcon title="Save outcome is unconfirmed" description="Check current Secrets before starting another deliberate change. No save is repeated." />}{command.blocked && <Button disabled={command.busy} onClick={() => void command.refresh(signal => secret ? identityRequest<TenantSecret>(secretApi(tenant, secret.secretId), { signal }) : identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal }).then(() => undefined), value => { if (value) setRevision(value.revision); })}>Reload current Secret · keep draft</Button>}<Typography.Paragraph type="secondary">{context.tenant.name} · Protected values cannot be read back. A saved Secret does not grant an agent access.</Typography.Paragraph>
      <Form id="secret-editor" form={form} layout="vertical" preserve={false} disabled={command.busy} initialValues={secret ?? { name: '', description: '', status: 'Active' }} onValuesChange={() => setDirty(true)} onFinish={values => void command.save(async signal => {
        const current = await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal });
        const saved = await identityRequest<TenantSecret>(secretApi(tenant, secret?.secretId, mode === 'replace' ? '/value' : ''), { method: mode === 'create' ? 'POST' : mode === 'replace' ? 'PUT' : 'PATCH', signal, body: { ...values, ...(mode === 'create' ? { secretId: id } : {}), expectedRevision: current.revision, ...(secret ? { expectedSecretRevision: revision } : {}) } });
        if (saved.secretId !== id || saved.revision !== (mode === 'create' ? 1 : revision + 1)) throw new IdentityCompletionError(false); return saved;
      }, () => { form.resetFields(); guard.release(); onSaved(); })}>
        {mode !== 'replace' && <><Form.Item name="name" label="Name" rules={[{ required: true, whitespace: true }]}><Input autoFocus maxLength={160} /></Form.Item><Form.Item name="description" label="Description"><Input.TextArea maxLength={2048} autoSize={{ minRows: 2, maxRows: 5 }} /></Form.Item>{mode === 'edit' && <Form.Item name="status" label="Status"><Select options={[{ value: 'Active', label: 'Active' }, { value: 'Disabled', label: 'Disabled' }]} /></Form.Item>}</>}
        {mode !== 'edit' && <Form.Item name="protectedValue" label={mode === 'replace' ? 'New protected value' : 'Protected value'} rules={[{ required: true }]}><Input.Password autoFocus={mode === 'replace'} autoComplete="new-password" maxLength={65536} /></Form.Item>}
      </Form>
    </Flex>
  </Drawer></>;
}
