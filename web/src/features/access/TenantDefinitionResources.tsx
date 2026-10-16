import { useEffect, useState } from 'react';
import { Alert, App, Button, Drawer, Empty, Flex, Form, Input, Select, Table, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { identityBlob } from '../../services/identity';
import { definitionApi, draftApi, resourceKinds, type DefinitionResourceAccess, type TenantResource } from '../../services/tenantDefinitions';
import { resourceMediaType } from '../admin/resourcePreview';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';
export type DraftMutation = (suffix: string, method: string, body?: Record<string, unknown>, committed?: () => void) => void;

function ResourceDownload({ path, resource, disabled }: { path: string; resource: TenantResource; disabled?: boolean }) {
  const command = useTenantCommand();
  return <Flex vertical gap="var(--ac-space-compact)"><Button disabled={disabled} loading={command.busy} aria-label={`Download ${resource.logicalPath}`} onClick={() => void command.run(signal => identityBlob(path, signal), blob => {
    const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = resource.logicalPath.split('/').at(-1)!; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  })}>Download</Button>{command.error !== undefined && <AccessError error={command.error} />}</Flex>;
}
export function TenantPublicationResources({ tenant, id, version }: { tenant: string; id: string; version: number }) {
  const state = useIdentityQuery<{ items: TenantResource[]; allRead: boolean }>(definitionApi(tenant, id, `/publications/${version}/resources`));
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={state.reload} />;
  return <Flex vertical gap="var(--ac-space-section)">{!state.data?.allRead && <ResourceAccessNotice />}<ResourceTable items={state.data?.items ?? []} emptyDescription={state.data?.allRead ? 'No resources bound to this version.' : 'No resources are available with your current access.'} actions={r => <ResourceDownload path={definitionApi(tenant, id, `/publications/${version}/resources/${r.resourceId}/content`)} resource={r} />} /></Flex>;
}
function ResourceAccessNotice() { return <Alert type="info" showIcon title="Resource access is limited" description="Only resources you can read are listed." />; }
function ResourceTable({ items, actions, emptyDescription }: { items: TenantResource[]; actions: (resource: TenantResource) => React.ReactNode; emptyDescription: string }) {
  return <Table className="admin-collection-table" rowKey="resourceId" dataSource={items} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyDescription} /> }} columns={[
    { title: 'Resource', render: (_, item) => <Flex vertical><Typography.Text style={{ overflowWrap: 'anywhere' }}>{item.logicalPath}</Typography.Text><Typography.Text type="secondary">{resourceKinds[item.kind - 1]} · {(item.byteLength / 1024).toFixed(1)} KB</Typography.Text></Flex> },
    { title: 'Actions', render: (_, item) => actions(item) }
  ]} />;
}
export function TenantDraftResources({ tenant, draftId, items, access, busy, mutate, error }: { tenant: string; draftId: string; items: TenantResource[]; access: DefinitionResourceAccess; busy: boolean; mutate: DraftMutation; error?: unknown }) {
  const { modal } = App.useApp(); const [editing, setEditing] = useState<TenantResource | null | undefined>();
  useEffect(() => { if (editing !== undefined && (editing === null ? !access.canCreate : !access.items.some(r => r.resourceId === editing.resourceId && r.canManage))) setEditing(undefined); }, [access, editing]);
  return <Flex vertical gap="var(--ac-space-section)">
    {!access.allRead && <ResourceAccessNotice />}
    {access.canCreate && <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Text type="secondary">Upload actual files. Existing content is reusable only through a resource already bound to this draft.</Typography.Text><Button disabled={busy} onClick={() => setEditing(null)}>Upload resources</Button></Flex>}
    <ResourceTable items={items} emptyDescription={access.allRead ? 'No resources bound to this draft.' : 'No resources are available with your current access.'} actions={resource => <Flex wrap gap="var(--ac-space-compact)">
      <ResourceDownload path={draftApi(tenant, draftId, `/resources/${resource.resourceId}/content`)} resource={resource} disabled={busy} />
      {access.items.some(r => r.resourceId === resource.resourceId && r.canManage) && <Button disabled={busy} aria-label={`Edit ${resource.logicalPath}`} onClick={() => setEditing(resource)}>Edit</Button>}
      {access.items.some(r => r.resourceId === resource.resourceId && r.canDelete) && <Button danger disabled={busy} aria-label={`Remove ${resource.logicalPath}`} onClick={() => confirmAction(modal, { title: `Remove ${resource.logicalPath}?`, content: 'This removes its binding and direct access grants from this draft. Required references must be updated before publication.', danger: true, okText: 'Remove resource', onOk: () => mutate(`/resources/${resource.resourceId}`, 'DELETE') })}>Remove</Button>}
    </Flex>} />
    {editing !== undefined && <ResourceEditor existing={editing} busy={busy} error={error} onClose={() => setEditing(undefined)} onSave={body => mutate('/resources/batch', 'POST', body, () => setEditing(undefined))} />}
  </Flex>;
}
function ResourceEditor({ existing, busy, error, onClose, onSave }: { existing: TenantResource | null; busy: boolean; error?: unknown; onClose: () => void; onSave: (body: Record<string, unknown>) => void }) {
  const [files, setFiles] = useState<File[]>([]); const [paths, setPaths] = useState<string[]>([]); const [kind, setKind] = useState(existing?.kind ?? 2); const [path, setPath] = useState(existing?.logicalPath ?? ''); const [localError, setError] = useState<string>(); const [preparing, setPreparing] = useState(false);
  const guard = useAccessDraftGuard(files.length > 0 || path !== (existing?.logicalPath ?? '') || kind !== (existing?.kind ?? 2), busy || preparing, onClose);
  const save = async () => {
    setError(undefined); setPreparing(true);
    try {
      if (!existing && !files.length) throw new Error('Choose at least one file.');
      if (files.length > 64 || files.some(f => f.size < 1 || f.size > 8 * 1024 * 1024) || files.reduce((sum, f) => sum + f.size, 0) > 16 * 1024 * 1024) throw new Error('Use non-empty files up to 8 MiB each and 16 MiB per upload.');
      const content = async (file: File) => { const bytes = new Uint8Array(await file.arrayBuffer()); let binary = ''; for (let offset = 0; offset < bytes.length; offset += 8192) binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192)); return btoa(binary); };
      const items = existing ? [{ resourceId: existing.resourceId, logicalPath: path, kind, mediaType: files[0] ? resourceMediaType(files[0], path) : existing.mediaType, ...(files[0] ? { content: await content(files[0]) } : {}) }] : await Promise.all(files.map(async (file, index) => ({ logicalPath: paths[index], kind, mediaType: resourceMediaType(file, paths[index]), content: await content(file) })));
      if (items.some(item => !item.logicalPath?.trim() || !item.mediaType)) throw new Error('Provide a logical path and supported file type for every resource.');
      onSave({ items });
    } catch (failure) { setError((failure as Error).message); } finally { setPreparing(false); }
  };
  return <>{guard.holder}<Drawer open title={existing ? 'Edit resource' : 'Upload resources'} className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!busy && !preparing} maskClosable={!busy && !preparing} closable={!busy && !preparing}
    footer={<Flex gap="var(--ac-space-compact)" justify="end"><Button disabled={busy || preparing} onClick={guard.close}>Cancel</Button><Button type="primary" loading={busy || preparing} onClick={() => void save()}>Save resources</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">{error !== undefined && <AccessError error={error} />}{localError && <Typography.Paragraph role="alert" type="danger">{localError}</Typography.Paragraph>}
      <Form layout="vertical"><Form.Item label="Resource kind"><Select aria-label="Resource kind" value={kind} disabled={busy || preparing} onChange={setKind} options={resourceKinds.map((label, index) => ({ label, value: index + 1 }))} /></Form.Item>
      <Form.Item label={existing ? 'Replace file (optional)' : 'Choose files'} extra="Up to 8 MiB per file, 16 MiB per upload. Uploaded data is inert and scoped to this draft."><Input type="file" aria-label={existing ? 'Replace file' : 'Choose resource files'} multiple={!existing} disabled={busy || preparing} onChange={event => { const chosen = Array.from(event.target.files ?? []); setFiles(chosen); setPaths(chosen.map(file => file.name)); }} /></Form.Item>
      {existing ? <Form.Item label="Logical path"><Input aria-label="Resource logical path" value={path} disabled={busy || preparing} onChange={event => setPath(event.target.value)} /></Form.Item> : files.map((file, index) => <Form.Item key={`${file.name}:${index}`} label={`Logical path for ${file.name}`}><Input aria-label={`Logical path for ${file.name}`} value={paths[index]} disabled={busy || preparing} onChange={event => setPaths(current => current.map((value, i) => i === index ? event.target.value : value))} /></Form.Item>)}
      </Form>
    </Flex>
  </Drawer></>;
}
