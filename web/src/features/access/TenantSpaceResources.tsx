import { useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Form, Input, Select, Table, Tabs, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { spaceApi, spaceResourceKey, type SpaceAccess, type SpaceFileResource, type SpaceFileResourceAccess, type TenantSpace } from '../../services/tenantSpaces';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantResourceAccess } from './TenantResourceAccess';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantEditorCommand } from './useTenantEditorCommand';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';

const kinds = ['Unsupported', 'Knowledge', 'Reference', 'Template', 'StaticAsset'];
export function TenantSpaceResources({ context, space, access, onChanged }: { context: TenantContext; space: TenantSpace; access: SpaceAccess; onChanged: () => void }) {
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [selected, setSelected] = useState<string>();
  const [creating, setCreating] = useState(false); const state = useIdentityQuery<Page<SpaceFileResource>>(spaceApi(tenant, space.spaceId, `/resources?page=${page}`));
  const changed = () => { state.reload(); onChanged(); setSelected(undefined); setCreating(false); };
  return <Flex vertical gap="var(--ac-space-section)">
    <Flex wrap align="center" justify="space-between" gap="var(--ac-space-compact)"><Typography.Title level={4} style={{ margin: 0 }}>Knowledge and resource snapshots</Typography.Title><Flex wrap gap="var(--ac-space-compact)"><Button onClick={state.reload}>Reload snapshots</Button>{access.permissions.includes('resources.manage') && <Button onClick={() => setCreating(true)}>Register snapshot</Button>}</Flex></Flex>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Register an immutable snapshot of a home file, then add it to a membership. Changes to the live file do not refresh saved content. Resource permissions and review still apply.</Typography.Paragraph>
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : state.loading ? <AccessLoading /> : <Table<SpaceFileResource> className="admin-collection-table" rowKey="resourceId" dataSource={state.data?.items} pagination={{ current: page + 1, pageSize: 50, total: state.data?.total, showSizeChanger: false, responsive: true, onChange: p => setPage(p - 1) }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No snapshots you can read." /> }} columns={[
      { title: 'Snapshot', render: (_, r) => <Flex vertical><Typography.Text style={{ overflowWrap: 'anywhere' }}>{r.logicalPath}</Typography.Text><Typography.Text type="secondary">{kinds[r.kind]} · revision {r.revision} · {r.byteLength} bytes</Typography.Text></Flex> },
      { title: 'Actions', width: 100, render: (_, r) => <Button onClick={() => setSelected(r.resourceId)}>Inspect</Button> }
    ]} />}
    {creating && <SnapshotEditor key="new" context={context} space={space} onClose={() => setCreating(false)} onSaved={changed} />}
    {selected && <SnapshotInspector key={selected} context={context} space={space} id={selected} onClose={() => setSelected(undefined)} onSaved={changed} />}
  </Flex>;
}
function SnapshotInspector({ context, space, id, onClose, onSaved }: { context: TenantContext; space: TenantSpace; id: string; onClose: () => void; onSaved: () => void }) {
  const state = useIdentityQuery<SpaceFileResourceAccess>(spaceApi(context.tenant.tenantId, space.spaceId, `/resources/${id}/access`));
  if (!state.data) return <Drawer open title="Inspect snapshot" onClose={onClose}>{state.error ? <AccessError error={state.error} retry={state.reload} /> : <AccessLoading />}</Drawer>;
  return <SnapshotEditor context={context} space={space} initial={state.data} onClose={onClose} onSaved={onSaved} />;
}
function SnapshotEditor({ context, space, initial, onClose, onSaved }: { context: TenantContext; space: TenantSpace; initial?: SpaceFileResourceAccess; onClose: () => void; onSaved: () => void }) {
  const tenant = context.tenant.tenantId; const [id] = useState(() => initial?.resource.resourceId ?? crypto.randomUUID());
  const [baseline, setBaseline] = useState(initial); const [spaceRevision, setSpaceRevision] = useState(initial?.spaceRevision ?? space.revision);
  const [dirty, setDirty] = useState(false); const [tab, setTab] = useState('snapshot'); const [form] = Form.useForm(); const command = useTenantEditorCommand(); const deletion = useTenantCommand(); const { modal } = App.useApp();
  const editable = !initial || baseline?.permissions.includes('resources.manage'); const busy = command.busy || deletion.busy;
  const guard = useAccessDraftGuard(dirty, busy, onClose); const path = spaceApi(tenant, space.spaceId, `/resources/${id}`);
  return <>{guard.holder}<Drawer open className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} title={initial ? `Snapshot · ${initial.resource.logicalPath}` : 'Register file snapshot'} onClose={guard.close} keyboard={!busy} maskClosable={!busy} closable={!busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>{editable ? 'Cancel' : 'Close'}</Button>{editable && tab === 'snapshot' && <Button type="primary" form="space-resource-editor" htmlType="submit" loading={command.busy} disabled={command.blocked || deletion.requiresReload}>{initial ? 'Refresh snapshot' : 'Register snapshot'}</Button>}</Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      {baseline?.permissions.includes('iam.roles.read') && <Tabs activeKey={tab} onChange={setTab} items={[{ key: 'snapshot', label: 'Snapshot' }, { key: 'access', label: 'Access' }]} />}
      {tab === 'access' && initial ? <TenantResourceAccess tenant={tenant} resourceType="resource" resourceId={spaceResourceKey(initial.resource)} /> : <>
        {command.error !== undefined && <AccessError error={command.error} />}{deletion.error !== undefined && <AccessError error={deletion.error} />}
        {command.unknown && <Alert type="warning" showIcon title="Save outcome is unconfirmed" description="Inspect the current snapshot before starting another change. This save is not repeated." />}
        {command.blocked && <Button disabled={busy} onClick={() => void command.refresh(async signal => {
          if (initial) return identityRequest<SpaceFileResourceAccess>(path + '/access', { signal });
          const value = await identityRequest<TenantSpace>(spaceApi(tenant, space.spaceId), { signal }); setSpaceRevision(value.revision); return undefined;
        }, value => { if (value) { setBaseline(value); setSpaceRevision(value.spaceRevision); } })}>Reload current snapshot · keep draft</Button>}
        <Typography.Paragraph type="secondary" style={{ margin: 0 }}>The snapshot keeps exact bytes from the source file. Refreshing changes the source revision; Runs with an older pin cannot silently read the replacement.</Typography.Paragraph>
        {initial && <Descriptions column={1} size="small" items={[{ label: 'Revision', children: initial.resource.revision }, { label: 'Media type', children: initial.resource.mediaType }, { label: 'Content hash', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{initial.resource.contentSha256}</Typography.Text> }]} />}
        <Form id="space-resource-editor" form={form} layout="vertical" disabled={!editable || busy} initialValues={initial ? { sourcePath: initial.resource.sourcePath, logicalPath: initial.resource.logicalPath, kind: kinds[initial.resource.kind] } : { sourcePath: '/home/', logicalPath: '', kind: 'Knowledge' }} onValuesChange={() => setDirty(true)} onFinish={values => void command.save(async signal => {
          const current = await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal });
          const saved = await identityRequest<SpaceFileResource>(path, { method: 'PUT', signal, body: { ...values, expectedRevision: current.revision, expectedSpaceRevision: spaceRevision, expectedResourceRevision: baseline?.resource.revision ?? 0 } });
          if (saved.resourceId !== id || saved.spaceId !== space.spaceId || saved.revision !== (baseline?.resource.revision ?? 0) + 1) throw new IdentityCompletionError(false); return saved;
        }, () => { guard.release(); onSaved(); })}>
          {initial && !initial.resource.sourcePath ? <Typography.Paragraph type="secondary">The home source path requires current home-file read access.</Typography.Paragraph> : <Form.Item name="sourcePath" label="Home source file" rules={[{ required: true }, { pattern: /^\/home\/.+/, message: 'Choose an exact file below /home/.' }]}><Input autoFocus={editable} maxLength={4095} /></Form.Item>}
          <Form.Item name="logicalPath" label="Snapshot logical path" rules={[{ required: true, whitespace: true }]}><Input maxLength={240} /></Form.Item>
          <Form.Item name="kind" label="Resource kind" rules={[{ required: true }]}><Select options={kinds.slice(1).map(value => ({ value, label: value === 'StaticAsset' ? 'Static asset' : value }))} /></Form.Item>
        </Form>
        {initial && baseline?.permissions.includes('resources.delete') && <Button danger disabled={busy || command.blocked || deletion.requiresReload} style={{ alignSelf: 'flex-start' }} onClick={() => confirmAction(modal, { title: 'Delete saved snapshot?', content: 'Remove its membership bindings first. Retained history keeps the original identity; future reads will be unavailable.', danger: true, okText: 'Delete snapshot', onOk: () => deletion.run(async signal => {
          const current = await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal });
          return identityRequest(path + `?expectedRevision=${current.revision}&expectedSpaceRevision=${spaceRevision}&expectedResourceRevision=${baseline.resource.revision}`, { method: 'DELETE', signal });
        }, () => { guard.release(); onSaved(); }) })}>Delete snapshot</Button>}
      </>}
    </Flex>
  </Drawer></>;
}
