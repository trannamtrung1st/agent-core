import { useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Form, Input, Radio, Switch, Table, Tabs, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, identityRequest, type Page, type TenantContext, tenantApi } from '../../services/identity';
import { currentSpaceAccess, isPrivateSpace, spaceApi, type SpaceAccess, type TenantSpace } from '../../services/tenantSpaces';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { TenantWorkspaceFiles } from '../files/TenantWorkspaceFiles';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantSpacePath, type TenantSpacePage } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantEditorCommand } from './useTenantEditorCommand';
import { useTenantCommand } from './useTenantCommand';
import { TenantResourceAccess } from './TenantResourceAccess';
import { TenantSpaceResources } from './TenantSpaceResources';
import { TenantSpaceMembers } from './TenantSpaceMembers';
import { TenantSpaceEnvironments } from './TenantSpaceEnvironments';

export function TenantSpaces({ context, id, page = 'overview' }: { context: TenantContext; id?: string; page?: TenantSpacePage }) {
  const tenant = context.tenant.tenantId; const [index, setIndex] = useState(0); const [search, setSearch] = useState(''); const [creating, setCreating] = useState(false);
  const opener = useRef<HTMLElement | null>(null);
  const state = useIdentityQuery<Page<TenantSpace>>(!id ? spaceApi(tenant) + `?page=${index}&pageSize=20` : null);
  if (id) return <SpaceDetail key={`${tenant}:${id}`} context={context} id={id} page={page} />;
  const close = () => { setCreating(false); requestAnimationFrame(() => opener.current?.focus()); };
  return <Flex vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar value={search} onChange={setSearch} label="loaded Spaces" showScrollHint={false} filters={<><Button onClick={state.reload}>Reload</Button>{context.permissions.includes('spaces.create') && <Button type="primary" onClick={event => { opener.current = event.currentTarget; setCreating(true); }}>Create Shared Space</Button>}</>} />
    <Typography.Paragraph type="secondary">Spaces own shared context and live files. Every agent also has one Private Space. Membership supplies context; permissions separately control access and actions.</Typography.Paragraph>
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : state.loading ? <AccessLoading /> : <Table<TenantSpace> className="admin-collection-table" rowKey="spaceId" dataSource={state.data?.items.filter(s => `${s.name} ${s.description}`.toLowerCase().includes(search.toLowerCase()))}
      pagination={{ current: index + 1, pageSize: 20, total: state.data?.total, showSizeChanger: false, responsive: true, onChange: value => setIndex(value - 1) }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching Spaces on this page.' : 'No Spaces you can read.'} /> }} columns={[
        { title: 'Space', render: (_, s) => <Flex vertical><Typography.Link href={tenantSpacePath(tenant, s.spaceId)} onClick={followAppLink}>{s.name}</Typography.Link><Typography.Text type="secondary">{s.description}</Typography.Text></Flex> },
        { title: 'Kind', width: 96, render: (_, s) => <Tag>{isPrivateSpace(s) ? 'Private' : 'Shared'}</Tag> }
      ]} />}
    {creating && <SpaceEditor context={context} onClose={close} onSaved={s => { close(); navigateToAppPath(tenantSpacePath(tenant, s.spaceId)); }} />}
  </Flex>;
}
function SpaceDetail({ context, id, page }: { context: TenantContext; id: string; page: TenantSpacePage }) {
  const tenant = context.tenant.tenantId; const state = useIdentityQuery<TenantSpace>(spaceApi(tenant, id)); const access = useIdentityQuery<SpaceAccess>(spaceApi(tenant, id, '/access'));
  const retained = useRef<{ space: TenantSpace; access: SpaceAccess } | undefined>(undefined);
  if (state.data && access.data) retained.current = { space: state.data, access: access.data };
  const [accessScope, setAccessScope] = useState<'space' | 'workspace'>('space');
  const [editing, setEditing] = useState(false); const command = useTenantCommand(); const { modal, message } = App.useApp();
  const reload = () => { state.reload(); access.reload(); };
  if (state.error || access.error) return <AccessError error={state.error ?? access.error} retry={reload} />;
  if (!retained.current) return <AccessLoading />;
  const space = retained.current.space; const spaceAccess = retained.current.access; const permissions = spaceAccess.permissions;
  return <Flex vertical gap="var(--ac-space-section)">
    <Flex wrap align="center" justify="space-between" gap="var(--ac-space-compact)"><Flex vertical><Typography.Title level={3} style={{ margin: 0 }}>{space.name}</Typography.Title><Typography.Text type="secondary">{isPrivateSpace(space) ? 'Private Space · owned by one agent' : 'Shared Space'} · {context.tenant.name}</Typography.Text></Flex><Button href={tenantSpacePath(tenant)} onClick={followAppLink}>All Spaces</Button></Flex>
    <Tabs activeKey={page} onChange={value => navigateToAppPath(tenantSpacePath(tenant, id, value as TenantSpacePage))} items={(['overview', 'members', 'workspace', 'environments', 'access'] as const).map(key => ({ key, label: key === 'members' ? 'Members' : key[0].toUpperCase() + key.slice(1) }))} />
    {page === 'access' ? <Flex vertical gap="var(--ac-space-section)"><Radio.Group aria-label="Space access scope" value={accessScope} onChange={event => setAccessScope(event.target.value)} options={[{ value: 'space', label: 'Space context' }, { value: 'workspace', label: 'Home files' }]} /><TenantResourceAccess key={`${id}:${accessScope}`} tenant={tenant} resourceType={accessScope} resourceId={accessScope === 'space' ? id : `home:${id}`} /></Flex> : page === 'workspace' ? <Flex vertical gap="var(--ac-space-section)"><TenantWorkspaceFiles key={`${tenant}:${id}`} owner={{ kind: 'home', tenantId: tenant, spaceId: id }} organization={context.tenant.name} /><TenantSpaceResources context={context} space={space} access={spaceAccess} onChanged={reload} /></Flex>
      : page === 'members' ? <TenantSpaceMembers context={context} space={space} access={spaceAccess} onChanged={reload} />
      : page === 'environments' ? <TenantSpaceEnvironments context={context} space={space} access={spaceAccess} onChanged={reload} /> : <>
        {command.error !== undefined && <AccessError error={command.error} />}
        <Descriptions column={1} size="small" items={[
          { label: 'Description', children: space.description || 'No description' }, { label: 'Instructions', children: <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{space.instructions || 'No Space instructions'}</Typography.Paragraph> },
          { label: 'Environments', children: space.environmentsEnabled ? 'Enabled · activation is optional for each Session' : 'Disabled · profiles and variables stay saved' },
          ...(space.ownerInstanceId ? [{ label: 'Owner agent', children: <Typography.Link href={`/t/${tenant}/admin/agents/${space.ownerInstanceId}/profile`} onClick={followAppLink}>Open owner agent</Typography.Link> }] : [])
        ]} />
        {!permissions.includes('spaces.manage') && <Alert type="info" showIcon title="Read-only Space" description="Ask an organization access administrator for permission to change this Space." />}
        <Flex wrap gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={reload}>Reload details</Button>{permissions.includes('spaces.manage') && <Button disabled={command.busy} onClick={() => setEditing(true)}>Edit Space</Button>}{!isPrivateSpace(space) && permissions.includes('spaces.delete') && <Button danger disabled={command.busy || command.requiresReload} onClick={() => confirmAction(modal, { title: `Delete ${space.name}?`, content: 'Its memberships, Environments and variables will become unavailable. Retained Runs keep their original context. This cannot be undone.', danger: true, okText: 'Delete Space', onOk: () => command.run(async signal => { const current = await currentSpaceAccess(tenant, id, signal); return identityRequest(spaceApi(tenant, id) + `?expectedRevision=${current.securityRevision}&expectedSpaceRevision=${space.revision}`, { method: 'DELETE', signal }); }, () => { message.success('Space deleted.'); navigateToAppPath(tenantSpacePath(tenant)); }) })}>Delete Space</Button>}</Flex>
      </>}
    {editing && <SpaceEditor context={context} space={space} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); reload(); }} />}
  </Flex>;
}
function SpaceEditor({ context, space, onClose, onSaved }: { context: TenantContext; space?: TenantSpace; onClose: () => void; onSaved: (s: TenantSpace) => void }) {
  const tenant = context.tenant.tenantId; const [id] = useState(() => space?.spaceId ?? crypto.randomUUID()); const [dirty, setDirty] = useState(false); const [baseline, setBaseline] = useState(space); const command = useTenantEditorCommand();
  const [form] = Form.useForm(); const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  return <>{guard.holder}<Drawer open title={space ? 'Edit Space' : 'Create Shared Space'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" form="space-editor" htmlType="submit" loading={command.busy} disabled={command.blocked}>Save Space</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">{command.error !== undefined && <AccessError error={command.error} />}{command.unknown && <Alert type="warning" showIcon title="Save outcome is unconfirmed" description="Check current Spaces before starting another deliberate change. No save is repeated." />}{command.blocked && <Button disabled={command.busy} onClick={() => void command.refresh(signal => space ? identityRequest<TenantSpace>(spaceApi(tenant, space.spaceId), { signal }) : identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal }).then(() => undefined), value => { if (value) setBaseline(value); })}>Reload current Space · keep draft</Button>}<Typography.Paragraph type="secondary">Space instructions apply to future Runs. Current Runs retain their captured context.</Typography.Paragraph>
      <Form id="space-editor" form={form} layout="vertical" disabled={command.busy} initialValues={space ?? { name: '', description: '', instructions: '', environmentsEnabled: false }} onValuesChange={() => setDirty(true)} onFinish={values => void command.save(async signal => {
        const current = await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal });
        const saved = await identityRequest<TenantSpace>(spaceApi(tenant, space?.spaceId), { method: space ? 'PATCH' : 'POST', signal, body: { ...values, ...(space ? { expectedSpaceRevision: baseline!.revision } : { spaceId: id }), expectedRevision: current.revision } });
        if (saved.spaceId !== id || saved.revision !== (baseline?.revision ?? 0) + 1) throw new IdentityCompletionError(false); return saved;
      }, saved => { guard.release(); onSaved(saved); })}>
        <Form.Item name="name" label="Name" rules={[{ required: true, whitespace: true }]}><Input autoFocus maxLength={160} /></Form.Item>
        <Form.Item name="description" label="Description"><Input.TextArea maxLength={2048} autoSize={{ minRows: 2, maxRows: 5 }} /></Form.Item>
        <Form.Item name="instructions" label="Instructions"><Input.TextArea maxLength={16384} autoSize={{ minRows: 5, maxRows: 12 }} /></Form.Item>
        {space && <Form.Item name="environmentsEnabled" label="Enable Environments" valuePropName="checked" help="Turning this off invalidates saved Environment selections. Profiles and variables remain available to manage."><Switch /></Form.Item>}
      </Form>
    </Flex>
  </Drawer></>;
}
