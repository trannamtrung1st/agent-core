import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Form, Grid, Input, Select, Spin, Switch, Table, Typography, Upload, theme } from 'antd';
import { UploadOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { AdminCollectionToolbar, useAdminCollectionSearch } from './AdminCollectionToolbar';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { describeAdminError } from './adminErrors';
import { listInstanceResources, copyInstanceResource, uploadInstanceResource, toggleInstanceResource, deleteInstanceResource, downloadInstanceResource, readInstanceResourceFile, type InstanceResource, type ResourceCatalog } from '../../services/instanceConfiguration';
const kinds = [{ value: 1, label: 'Knowledge' }, { value: 2, label: 'Reference' }, { value: 3, label: 'Template' }, { value: 4, label: 'Static asset' }, { value: 5, label: 'Evaluation fixture' }];
type Editor = { resource: InstanceResource | null; logicalPath: string; kind: number; enabled: boolean; file: File | null; readOnly: boolean };
export function InstanceResourcesSection({ instanceId, archived, active = true, onUpdated }: { instanceId: string; archived: boolean; active?: boolean; onUpdated: () => void }) {
  const detailLayout = useAdminDetailLayout(); const { token } = theme.useToken(); const { modal } = App.useApp(); const compact = !Grid.useBreakpoint().md;
  const [preview, setPreview] = useState<string | null>(null);
  const [catalog, setCatalog] = useState<ResourceCatalog | null>(null); const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null); const [editor, setEditor] = useState<Editor | null>(null); const [kind, setKind] = useState<number>();
  const { search, setSearch, pagination } = useAdminCollectionSearch(); const epoch = useRef(0); const owner = useRef(instanceId); owner.current = instanceId;
  const previewEpoch = useRef(0); const opener = useRef<HTMLElement | null>(null); const drawerVisible = useRef(false);
  useEffect(() => { epoch.current++; setCatalog(null); setEditor(null); setError(null); setBusy(false); return () => { epoch.current++; }; }, [instanceId]);
  const reload = useCallback(async () => {
    const request = ++epoch.current; setLoading(true); setError(null); setCatalog(null);
    try { const result = await listInstanceResources(instanceId); if (request === epoch.current && owner.current === instanceId) { setCatalog(result); setEditor(current => current?.resource ? { ...current, resource: result.resources.find(r => r.key === current.resource!.key) ?? current.resource } : current); } }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to read resources. Retry before changing them.').message); }
    finally { if (request === epoch.current) setLoading(false); }
  }, [instanceId]);
  useEffect(() => { if (active) void reload(); }, [reload, active]);
  async function mutate(action: () => Promise<ResourceCatalog>, close = false) {
    if (!catalog || archived || busy || loading) return;
    const request = epoch.current; setBusy(true); setError(null);
    try { const result = await action(); if (request !== epoch.current || owner.current !== instanceId) return; setCatalog(result); if (close) setEditor(null); onUpdated(); }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'The resource changed or could not be saved. Reload while retaining your draft.').message); }
    finally { if (request === epoch.current) setBusy(false); }
  }
  function open(resource: InstanceResource | null, readOnly = false) {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    previewEpoch.current++; setPreview(null);
    setEditor({ resource, logicalPath: resource?.logicalPath ?? '', kind: resource?.kind ?? 1, enabled: resource?.enabled ?? true, file: null, readOnly });
  }
  async function save() {
    if (!editor || !catalog || editor.readOnly) return;
    const draft = editor; const loaded = catalog;
    await mutate(async () => {
      const file = draft.file ?? (draft.resource ? await readInstanceResourceFile(instanceId, draft.resource) : null);
      if (!file) throw new Error('Choose a resource file before saving.');
      const form = new FormData(); form.set('file', file); form.set('logicalPath', draft.logicalPath); form.set('kind', String(draft.kind)); form.set('enabled', String(draft.enabled));
      return uploadInstanceResource(instanceId, loaded, form, draft.resource ?? undefined);
    }, true);
  }
  const visible = (catalog?.resources ?? []).filter(r => (kind === undefined || r.kind === kind) && `${r.logicalPath} ${r.key} ${r.mediaType} ${r.origin}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const ready = !!catalog && !loading && !busy;
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Definition resources are inherited. Instance resources are independent files. Enabled content is frozen for each Run; edits apply to the next Run.</Typography.Paragraph>
    {archived && <Alert type="info" showIcon title="Archived resources are read-only" />}
    <Flex wrap gap={token.paddingXS}><Button type="primary" disabled={archived || !ready} onClick={() => open(null)}>New Instance resource</Button><Button disabled={busy || loading} onClick={() => void reload()}>Reload resources</Button></Flex>
    {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Retry resources</Button>} />}
    {loading && <Spin aria-label="Loading resources" />}
    {catalog && <><Flex wrap align="center" gap={token.paddingSM}><AdminCollectionToolbar value={search} onChange={setSearch} label="Resources" /><Select style={{ width: compact ? "100%" : 220 }} aria-label="Resource kind filter" allowClear placeholder="All resource kinds" options={kinds} value={kind} onChange={setKind} /></Flex>
      {(['Definition', 'Instance'] as const).map(origin => <section key={origin} className="admin-definition-panel" aria-label={`${origin} resources`}>
        <div className="admin-definition-panel-heading"><Typography.Title level={4}>{origin} resources</Typography.Title><Typography.Text type="secondary">{origin === 'Definition' ? `Published Definition v${catalog.definitionVersion} · reset restores its default` : 'Instance-owned · separate identities and paths'}</Typography.Text></div>
        <div className="admin-definition-panel-body"><Table<InstanceResource> className="admin-collection-table" size="small" rowKey="key" pagination={pagination} scroll={{ x: compact ? 760 : 1100 }} dataSource={visible.filter(r => r.origin === origin)} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search || kind ? 'No matching resources' : `No ${origin.toLowerCase()} resources`} /> }} columns={[
          { title: 'Resource', width: 260, render: (_, r) => <Flex vertical gap={token.paddingXS}><Typography.Text strong style={{ overflowWrap: 'anywhere' }}>{r.logicalPath}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>{r.key}</Typography.Text><Typography.Text type="secondary">{kinds.find(k => k.value === r.kind)?.label} · {r.mediaType} · {r.byteLength.toLocaleString()} bytes</Typography.Text></Flex> },
          { title: 'State', width: 170, render: (_, r) => <Flex vertical align="start" gap={token.paddingXS}><Switch aria-label={`Enable ${origin} resource ${r.logicalPath}`} checked={r.enabled} disabled={!ready || archived} onChange={enabled => confirmAction(modal, { title: `${enabled ? 'Enable' : 'Disable'} resource?`, content: r.dependencies.length && !enabled ? `Enabled Skills depend on this file: ${r.dependencies.join(', ')}. Disable those Skills first.` : 'Applies to the next new Run. Current Runs retain their original content.', okText: enabled ? 'Enable resource' : 'Disable resource', onOk: () => mutate(() => toggleInstanceResource(instanceId, catalog, r, enabled)) })} /><Typography.Text>{r.enabled ? 'Enabled' : 'Disabled'}</Typography.Text><Typography.Text type="secondary">{origin === 'Definition' ? r.enabledOverride === null ? 'Definition default' : 'Instance override' : 'Instance-owned'}</Typography.Text>{origin === 'Definition' && r.enabledOverride !== null && <Button disabled={!ready || archived} onClick={() => void mutate(() => toggleInstanceResource(instanceId, catalog, r, null))}>Reset to Definition default</Button>}</Flex> },
          { title: 'Actions', width: 350, render: (_, r) => <Flex className="admin-table-actions" gap={token.paddingXS}><Button disabled={!ready} onClick={() => open(r, true)}>Inspect</Button><Button disabled={!ready} onClick={() => void downloadInstanceResource(instanceId, r).catch(e => setError(describeAdminError(e, 'Download failed. Retry.').message))}>Download</Button>{origin === 'Definition' && <Button disabled={archived || !ready} onClick={() => confirmAction(modal, { title: 'Copy to this Instance?', content: 'Creates an independent file with source provenance. The inherited original stays enabled. Changes to it will not update the copy.', okText: 'Copy resource', onOk: () => mutate(() => copyInstanceResource(instanceId, catalog, r)) })}>Copy to Instance</Button>}{origin === 'Instance' && <><Button disabled={archived || !ready} onClick={() => open(r)}>Edit</Button><Button danger disabled={archived || !ready} onClick={() => confirmAction(modal, { title: 'Delete Instance resource?', content: 'Removes this file from future Runs. Admitted Runs retain their immutable content.', okText: 'Delete resource', danger: true, onOk: () => mutate(() => deleteInstanceResource(instanceId, catalog, r)) })}>Delete</Button></>}</Flex> }
        ]} /></div>
      </section>)}
    </>}
    <Drawer styles={{ body: { padding: token.padding }, footer: { padding: token.padding }, wrapper: { maxWidth: "100vw" } }} title={editor?.readOnly ? 'Resource details' : editor?.resource ? 'Edit Instance resource' : 'New Instance resource'} open={!!editor && active} size={compact ? '100%' : 640} onClose={() => { if (!busy) { previewEpoch.current++; setEditor(null); } }} afterOpenChange={open => { drawerVisible.current = open; if (!open && opener.current?.isConnected) opener.current.focus(); }} footer={editor && !editor.readOnly ? <Flex wrap gap={token.paddingXS}><Button type="primary" loading={busy} disabled={!ready || !editor.logicalPath.trim() || !editor.file && !editor.resource} onClick={() => void save()}>Save resource</Button><Button disabled={busy} onClick={() => setEditor(null)}>Cancel</Button></Flex> : undefined}>
      {editor && <Flex vertical gap={token.padding}>
        {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Reload and retain draft</Button>} />}
        {editor.readOnly && editor.resource ? <Descriptions {...detailLayout} column={1} size="small" className="admin-effective-details" items={[
          { key: 'identity', label: 'Resource key', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{editor.resource.key}</Typography.Text> },
          { key: 'path', label: 'Run path', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{editor.resource.virtualPath}</Typography.Text> },
          { key: 'hash', label: 'Content SHA-256', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{editor.resource.contentSha256}</Typography.Text> },
          { key: 'revision', label: 'Revision', children: editor.resource.revision },
          { key: 'dependencies', label: 'Enabled Skill dependencies', children: editor.resource.dependencies.join(', ') || 'None' }
        ]} /> : <Form layout="vertical">
          <Form.Item label="Logical path"><Input aria-label="Resource logical path" value={editor.logicalPath} disabled={busy} onChange={e => setEditor({ ...editor, logicalPath: e.target.value })} /></Form.Item>
          <Form.Item label="Kind"><Select aria-label="Resource kind" value={editor.kind} options={kinds} disabled={busy} onChange={kind => setEditor({ ...editor, kind })} /></Form.Item>
          <Form.Item label="Enabled"><Switch aria-label="Instance resource enabled" checked={editor.enabled} disabled={busy} onChange={enabled => setEditor({ ...editor, enabled })} /></Form.Item>
          <Form.Item label="Content" extra="Up to 8 MiB per file. Knowledge requires text; binary references and assets retain exact bytes."><Upload maxCount={1} disabled={busy} beforeUpload={file => { setEditor({ ...editor, file }); return false; }} onRemove={() => setEditor({ ...editor, file: null })}><Button icon={<UploadOutlined />}>Choose resource file</Button></Upload></Form.Item>
          {editor.resource && !editor.file && <Typography.Text type="secondary">Existing immutable bytes will be retained.</Typography.Text>}
        </Form>}
        {editor.readOnly && editor.resource && <>
          <Button style={{ alignSelf: "flex-start" }} onClick={async () => {
            const request = epoch.current; const selection = previewEpoch.current; const resource = editor.resource!;
            try { const file = await readInstanceResourceFile(instanceId, resource); const text = file.type.startsWith('text/') || file.type === 'application/json' ? (await file.text()).slice(0, 12000) : 'Binary content: use Download to inspect the exact bytes.'; if (request === epoch.current && selection === previewEpoch.current && owner.current === instanceId) setPreview(text); }
            catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to preview content. Retry.').message); }
          }}>Preview content</Button>
          {preview !== null && <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{preview}</Typography.Paragraph>}
        </>}
      </Flex>}
    </Drawer>
  </Flex>;
}
