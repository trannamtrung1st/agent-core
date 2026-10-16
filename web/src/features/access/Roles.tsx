import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useRef, useState } from 'react';
import { Alert, Button, Checkbox, Collapse, Drawer, Empty, Flex, Form, Input, Table, Tag, Typography, message } from 'antd';
import { EditOutlined, EyeOutlined, PlusOutlined } from '@ant-design/icons';
import { appMessageConfig } from '../../app/appFeedback';
import { AdminCollectionToolbar, useAdminCollectionSearch } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { identityRequest, tenantApi, IdentityError, type Permission, type Role, type TenantContext } from '../../services/identity';
import { AccessError, AccessLoading, accessErrorMessage } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

const families: Record<string, string> = { agents: 'Agents', 'agent-definitions': 'Agent definitions', 'agent-runs': 'Agent runs', 'iam.roles': 'Roles and assignments', 'security.audit': 'Security audit', 'tenant.governance': 'Organization governance', 'core-events': 'Events' };
export function permissionFamily(key: string) { const family = key.slice(0, key.lastIndexOf('.')); return families[family] ?? (family.charAt(0).toUpperCase() + family.slice(1).replaceAll('-', ' ')); }
export function permissionLabel(key: string) { const action = key.slice(key.lastIndexOf('.') + 1).replaceAll('_', ' '); return action === 'grant exception' ? 'Authorize an exact action once' : action.charAt(0).toUpperCase() + action.slice(1); }

export function Roles({ context }: { context: TenantContext }) {
  const state = useIdentityQuery<{ items: Role[]; revision: number }>(tenantApi(context.tenant.tenantId, '/roles'));
  const catalog = useIdentityQuery<{ items: Permission[] }>(tenantApi(context.tenant.tenantId, '/permissions'));
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [editing, setEditing] = useState<Role | 'new' | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const closeEditor = () => { setEditing(null); requestAnimationFrame(() => opener.current?.isConnected ? opener.current.focus({ preventScroll: true }) : document.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus()); };
  const editable = context.permissions.includes('iam.roles.manage');
  const [feedback, holder] = message.useMessage(appMessageConfig);
  return <Flex vertical gap="var(--ac-space-section)">{holder}
    <AdminCollectionToolbar showScrollHint={false} label="roles" value={search} onChange={setSearch} filters={editable && <Button type="primary" icon={<PlusOutlined aria-hidden />} onClick={event => { opener.current = event.currentTarget; setEditing('new'); }}>Create role</Button>} />
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<Role> className="admin-collection-table" rowKey="roleId" pagination={pagination} dataSource={state.data?.items.filter(role => `${role.name} ${role.permissions.map(permissionFamily).join(' ')}`.toLowerCase().includes(search.toLowerCase()))} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No roles match this search." /> }} columns={[
      { title: 'Role', render: (_, role) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{role.name}</Typography.Text><Flex wrap gap="var(--ac-space-tight)">{role.isBuiltin && <Tag>Built-in</Tag>}{role.humanOnly && <Tag>People only</Tag>}<Typography.Text type="secondary">{role.permissions.length} {role.permissions.length === 1 ? 'permission' : 'permissions'}</Typography.Text></Flex></Flex> },
      { title: 'Actions', width: 104, render: (_, role) => <Button icon={editable && !role.isBuiltin ? <EditOutlined aria-hidden /> : <EyeOutlined aria-hidden />} aria-label={`${editable && !role.isBuiltin ? 'Edit' : 'Inspect'} ${role.name}`} onClick={event => { opener.current = event.currentTarget; setEditing(role); }}>{editable && !role.isBuiltin ? 'Edit' : 'Inspect'}</Button> }
    ]} />}
    {editing && <RoleEditor key={editing === 'new' ? 'new' : editing.roleId} role={editing === 'new' ? null : editing} context={context} permissions={catalog.data?.items ?? []} catalogError={catalog.error} catalogRetry={catalog.reload} reloadState={state.reload} revision={state.data?.revision ?? context.revision} editable={editable && (editing === 'new' || !editing.isBuiltin)} onClose={closeEditor} onSaved={() => { closeEditor(); state.reload(); void feedback.success('Role saved. Access changes apply to the next protected action.'); }} />}
  </Flex>;
}
function RoleEditor({ role, context, permissions, catalogError, catalogRetry, reloadState, revision, editable, onClose, onSaved }: {
  role: Role | null; context: TenantContext; permissions: Permission[]; catalogError?: Error; catalogRetry: () => void; reloadState: () => void; revision: number; editable: boolean; onClose: () => void; onSaved: () => void;
}) {
  const [form] = Form.useForm<{ name: string }>(); const [selected, setSelected] = useState<string[]>(role?.permissions ?? []);
  const [openGroups, setOpenGroups] = useState<string[]>([]);
  const [query, setQuery] = useState(''); const [dirty, setDirty] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>();
  const [feedback, holder] = message.useMessage(appMessageConfig);
  const guard = useAccessDraftGuard(dirty, busy, onClose);
  const save = async ({ name }: { name: string }) => {
    if (!selected.length) { setError(new Error('Select at least one permission.')); return; }
    setBusy(true); setError(undefined);
    try { await identityRequest(tenantApi(context.tenant.tenantId, role ? `/roles/${role.roleId}` : '/roles'), { method: role ? 'PUT' : 'POST', body: { name, permissions: selected, expectedRevision: revision } }); setDirty(false); onSaved(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const groups = [...new Set(permissions.map(p => permissionFamily(p.key)))];
  return <Drawer className="access-editor-drawer" title={editable ? role ? 'Edit role' : 'Create role' : role?.name} open onClose={guard.close} size="large" styles={{ wrapper: { maxWidth: '100vw' } }} footer={<Flex justify="end" gap="var(--ac-space-compact)" wrap><Button disabled={busy} onClick={guard.close}>{editable ? 'Cancel' : 'Close'}</Button>{editable && <Button form="access-role-form" type="primary" htmlType="submit" loading={busy}>Save role</Button>}</Flex>}>
    {holder}{guard.holder}<Flex vertical gap="var(--ac-space-section)">
      <Typography.Paragraph type="secondary">{context.tenant.name} · Roles describe authority. Assigning a role is a separate step.</Typography.Paragraph>
      {!editable && <Alert type="info" showIcon title={role?.isBuiltin ? 'Built-in roles are maintained by the platform. Create a custom role for different permissions.' : 'You can inspect this role. Editing requires permission to manage roles.'} />}
      {error !== undefined && <Alert type="error" showIcon role="alert" title={error instanceof Error && error.message === 'Select at least one permission.' ? error.message : accessErrorMessage(error)} action={error instanceof IdentityError && error.status === 409 ? <Button onClick={() => { reloadState(); catalogRetry(); }}>Reload latest access</Button> : undefined} />}
      <Form id="access-role-form" form={form} layout="vertical" initialValues={{ name: role?.name ?? '' }} onValuesChange={() => setDirty(true)} onFinish={values => void save(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)} disabled={!editable || busy}>
        <Form.Item name="name" label="Role name" rules={[{ required: true, whitespace: true, message: 'Enter a role name.' }, { max: 160 }]}><Input autoFocus={editable} maxLength={160} /></Form.Item>
      </Form>
      <Flex justify="space-between" align="center" gap="var(--ac-space-compact)" wrap><Typography.Text strong>Permissions</Typography.Text><Typography.Text type="secondary" aria-live="polite">{selected.length} selected{selected.some(key => permissions.some(p => p.key === key && p.humanOnly)) ? ' · People only' : ''}</Typography.Text></Flex>
      <Input aria-label="Search permissions" placeholder="Search permissions" value={query} onChange={event => setQuery(event.target.value)} allowClear />
      {catalogError ? <AccessError error={catalogError} retry={catalogRetry} /> : !permissions.length ? <AccessLoading /> : <Collapse items={groups.map(group => ({ key: group, label: group, forceRender: !!query, children: <Flex vertical gap="var(--ac-space-default)">{permissions.filter(p => permissionFamily(p.key) === group && `${group} ${permissionLabel(p.key)} ${p.key}`.toLowerCase().includes(query.toLowerCase())).map(permission => <Checkbox aria-label={`${permissionLabel(permission.key)} ${permissionFamily(permission.key).toLowerCase()}`} key={permission.key} disabled={!editable || busy} checked={selected.includes(permission.key)} onChange={event => { setDirty(true); setSelected(current => event.target.checked ? [...current, permission.key] : current.filter(key => key !== permission.key)); }}>{permissionLabel(permission.key)}{permission.humanOnly && <Typography.Text type="secondary"> · People only</Typography.Text>}</Checkbox>)}</Flex> })).filter(item => !query || permissions.some(p => permissionFamily(p.key) === item.key && `${item.key} ${permissionLabel(p.key)} ${p.key}`.toLowerCase().includes(query.toLowerCase())))} activeKey={query ? groups : openGroups} onChange={keys => setOpenGroups(typeof keys === 'string' ? [keys] : keys)} />}
    </Flex>
  </Drawer>;
}
