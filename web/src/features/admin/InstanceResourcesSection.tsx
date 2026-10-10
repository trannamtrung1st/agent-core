import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Form, Grid, Input, Select, Spin, Switch, Table, Tooltip, Typography, Upload, theme } from 'antd';
import { UndoOutlined, UploadOutlined } from '@ant-design/icons';
import { ResourceDeletionContent, ResourceSelectionToolbar } from './ResourceSelectionToolbar';
import { confirmAction } from '../../app/confirmAction';
import { AdminCollectionToolbar, useAdminCollectionSearch } from './AdminCollectionToolbar';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { ResourceFilePicker, ResourceImportPreview } from './ResourceImportFields';
import { isTextualResource, RESOURCE_FILE_HELP, RESOURCE_KINDS, createPreviewItem, resourceBatchLimitProblem, resourceMediaType, resourcePreviewProblem, type ResourcePreviewItem } from './resourcePreview';
import { describeAdminError } from './adminErrors';
import { listInstanceResources, copyInstanceResource, uploadInstanceResource, toggleInstanceResource, deleteInstanceResource, downloadInstanceResource, readInstanceResourceFile, type InstanceResource, type ResourceCatalog } from '../../services/instanceConfiguration';
const kinds = [{ value: 1, label: 'Knowledge' }, { value: 2, label: 'Reference' }, { value: 3, label: 'Template' }, { value: 4, label: 'Static asset' }];
type Editor = { resource: InstanceResource | null; logicalPath: string; kind: number; enabled: boolean; file: File | null; items: ResourcePreviewItem[]; readOnly: boolean };
export function InstanceResourcesSection({ instanceId, archived, active = true, onUpdated }: { instanceId: string; archived: boolean; active?: boolean; onUpdated: () => void }) {
  const detailLayout = useAdminDetailLayout(); const { token } = theme.useToken(); const { modal } = App.useApp(); const compact = !Grid.useBreakpoint().md;
  const [selectedResources, setSelectedResources] = useState<React.Key[]>([]);
  const [preview, setPreview] = useState<string | null>(null);
  const [catalog, setCatalog] = useState<ResourceCatalog | null>(null); const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null); const [editor, setEditor] = useState<Editor | null>(null); const [kind, setKind] = useState<number>();
  const [progress, setProgress] = useState<string | null>(null);
  const { search, setSearch, pagination } = useAdminCollectionSearch(); const epoch = useRef(0); const owner = useRef(instanceId); owner.current = instanceId;
  const previewEpoch = useRef(0); const opener = useRef<HTMLElement | null>(null); const drawerVisible = useRef(false);
  useEffect(() => { epoch.current++; setCatalog(null); setSelectedResources([]); setEditor(null); setError(null); setBusy(false); return () => { epoch.current++; }; }, [instanceId]);
  const reload = useCallback(async () => {
    const request = ++epoch.current; setLoading(true); setError(null); setSelectedResources([]); setCatalog(null);
    try { const result = await listInstanceResources(instanceId); if (request === epoch.current && owner.current === instanceId) { setCatalog(result); setEditor(current => current?.resource ? { ...current, resource: result.resources.find(r => r.key === current.resource!.key) ?? current.resource } : current); } }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to read resources. Retry before changing them.').message); }
    finally { if (request === epoch.current) setLoading(false); }
  }, [instanceId]);
  useEffect(() => { if (active) void reload(); }, [reload, active]);
  async function mutate(action: () => Promise<ResourceCatalog>, close = false) {
    if (!catalog || archived || busy || loading) return;
    const request = epoch.current; setBusy(true); setError(null);
    try { const result = await action(); if (request !== epoch.current || owner.current !== instanceId) return; setCatalog(result); setSelectedResources([]); if (close) setEditor(null); onUpdated(); }
    catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'The resource changed or could not be saved. Reload while retaining your draft.').message); }
    finally { if (request === epoch.current) setBusy(false); }
  }
  function confirmDeleteSelected() {
    if (!catalog || !ready || archived) return;
    const selected = catalog.resources.filter(r => r.origin === 'Instance' && selectedResources.includes(r.key));
    const loaded = catalog; const request = epoch.current;
    confirmAction(modal, {
      title: `Delete ${selected.length} Instance resources?`,
      content: <ResourceDeletionContent notice="Removes these files from future Runs. Admitted Runs retain their immutable content. Deletion stops if the Instance changes." paths={selected.map(r => r.logicalPath)} />,
      okText: 'Delete resources', danger: true,
      onOk: async () => {
        if (request !== epoch.current || owner.current !== instanceId || busy || loading) return;
        setBusy(true); setError(null);
        let next = loaded; let deleted = 0;
        try {
          for (const resource of selected) {
            if (request !== epoch.current || owner.current !== instanceId) return;
            // Advance only by our own committed revision, so concurrent edits stop the batch.
            const result = await deleteInstanceResource(instanceId, next, resource);
            next = { ...result, instanceRevision: next.instanceRevision + 1 };
            deleted++;
            if (request === epoch.current && owner.current === instanceId) setCatalog(result);
          }
          if (request === epoch.current) { setSelectedResources([]); onUpdated(); }
        } catch (e) {
          if (request === epoch.current) {
            setSelectedResources([]); setCatalog(null);
            setError(`${deleted} of ${selected.length} resources deleted. ${describeAdminError(e, 'Deletion stopped. Reload resources before retrying.').message}`);
            if (deleted) onUpdated();
          }
        } finally { if (request === epoch.current) setBusy(false); }
      }
    });
  }
  function open(resource: InstanceResource | null, readOnly = false) {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    previewEpoch.current++; setPreview(null);
    setError(null); setProgress(null);
    setEditor({ resource, logicalPath: resource?.logicalPath ?? '', kind: resource?.kind ?? 1, enabled: resource?.enabled ?? true, file: null, items: [], readOnly });
  }
  function updateItems(update: (current: ResourcePreviewItem[]) => ResourcePreviewItem[]) {
    setEditor(current => current ? { ...current, items: update(current.items) } : current);
  }
  async function importResources() {
    if (!editor || !catalog || archived || busy || loading || importBlocked) return;
    const request = epoch.current; const draft = editor;
    let latest = catalog; let saved = 0;
    setBusy(true); setError(null);
    try {
      for (const item of draft.items) {
        if (request !== epoch.current || owner.current !== instanceId) return;
        setProgress(`Adding ${saved + 1} of ${draft.items.length}…`);
        const form = new FormData();
        form.set('file', new File([item.file], item.file.name, { type: item.mediaType! }));
        form.set('logicalPath', item.logicalPath.trim()); form.set('kind', String(RESOURCE_KINDS.indexOf(item.kind as typeof RESOURCE_KINDS[number]) + 1)); form.set('enabled', String(draft.enabled));
        latest = await uploadInstanceResource(instanceId, latest, form);
        if (request !== epoch.current || owner.current !== instanceId) return;
        saved++;
        setCatalog(latest);
        updateItems(current => current.filter(candidate => candidate.key !== item.key));
      }
      setEditor(null);
    } catch (e) {
      if (request === epoch.current && owner.current === instanceId) {
        const problem = describeAdminError(e, 'Unable to add this resource. Retry, or reload and retain the remaining files.').message;
        setError(`${saved} of ${draft.items.length} resources added. ${problem} Remaining files are kept for retry.`);
        const failed = draft.items[saved];
        updateItems(current => current.map(item => item.key === failed.key ? { ...item, uploadError: problem } : item));
      }
    } finally {
      if (request === epoch.current && owner.current === instanceId) { setBusy(false); setProgress(null); if (saved) onUpdated(); }
    }
  }
  async function save() {
    if (!editor || !catalog || editor.readOnly) return;
    const draft = editor; const loaded = catalog;
    await mutate(async () => {
      const file = draft.file ?? (draft.resource ? await readInstanceResourceFile(instanceId, draft.resource) : null);
      if (!file) throw new Error('Choose a resource file before saving.');
      const form = new FormData(); form.set('file', new File([file], file.name, { type: draft.file ? resourceMediaType(file, draft.logicalPath) ?? file.type : draft.resource!.mediaType })); form.set('logicalPath', draft.logicalPath.trim()); form.set('kind', String(draft.kind)); form.set('enabled', String(draft.enabled));
      return uploadInstanceResource(instanceId, loaded, form, draft.resource ?? undefined);
    }, true);
  }
  const visible = (catalog?.resources ?? []).filter(r => (kind === undefined || r.kind === kind) && `${r.logicalPath} ${r.key} ${r.mediaType} ${r.origin}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const ready = !!catalog && !loading && !busy;
  const localResources = (catalog?.resources ?? []).filter(resource => resource.origin === 'Instance');
  const incoming = editor?.items ?? [];
  const importProblems = incoming.map(item => resourcePreviewProblem({ ...item, uploadError: undefined }, incoming.map(candidate => candidate.logicalPath), localResources.map(resource => resource.logicalPath), 'Instance'));
  const limitProblem = resourceBatchLimitProblem(incoming, localResources.length, localResources.reduce((sum, resource) => sum + resource.byteLength, 0), 'Instance');
  const importBlocked = !incoming.length || !!limitProblem || importProblems.some(Boolean);
  const editProblem = editor?.resource && !editor.readOnly ? resourcePreviewProblem({ logicalPath: editor.logicalPath, kind: RESOURCE_KINDS[editor.kind - 1], byteLength: editor.file?.size ?? editor.resource.byteLength, mediaType: editor.file ? resourceMediaType(editor.file, editor.logicalPath) : editor.resource.mediaType }, [editor.logicalPath], localResources.filter(resource => resource.key !== editor.resource!.key).map(resource => resource.logicalPath), 'Instance') : null;
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Definition resources are inherited. Instance resources are independent files. Enabled content is frozen for each Run; edits apply to the next Run.</Typography.Paragraph>
    {archived && <Alert type="info" showIcon title="Archived resources are read-only" />}
    <Flex wrap gap={token.paddingXS}><Button type="primary" disabled={archived || !ready} onClick={() => open(null)}>New Instance resource</Button><Button disabled={busy || loading} onClick={() => void reload()}>Reload resources</Button></Flex>
    {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Retry resources</Button>} />}
    {loading && <Spin aria-label="Loading resources" />}
    {catalog && <><Flex wrap align="center" gap={token.paddingSM}><AdminCollectionToolbar value={search} onChange={value => { setSelectedResources([]); setSearch(value); }} label="Resources" /><Select style={{ width: compact ? "100%" : 220 }} aria-label="Resource kind filter" allowClear placeholder="All resource kinds" options={kinds} value={kind} onChange={value => { setSelectedResources([]); setKind(value); }} /></Flex>
      {(['Definition', 'Instance'] as const).map(origin => <AgentConfigurationPanel key={origin} title={<>{origin} resources</>} label={`${origin} resources`}
        description={origin === 'Definition' ? `Published Definition v${catalog.definitionVersion} · reset restores its default` : 'Instance-owned · separate identities and paths'} bodyGap="default">
          {origin === 'Instance' && <ResourceSelectionToolbar count={selectedResources.length} disabled={archived || !ready} busy={busy} onClear={() => setSelectedResources([])} onDelete={confirmDeleteSelected} />}
          <Table<InstanceResource> className="admin-collection-table" size="small" rowKey="key" tableLayout="fixed" rowSelection={origin === 'Instance' ? { selectedRowKeys: selectedResources, onChange: setSelectedResources,
            getCheckboxProps: r => ({ disabled: archived || !ready, 'aria-label': `Select resource ${r.logicalPath}` }) } : undefined} pagination={pagination} scroll={{ x: 1695 }} dataSource={visible.filter(r => r.origin === origin)} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search || kind ? 'No matching resources' : `No ${origin.toLowerCase()} resources`} /> }} columns={[
          { title: 'Resource path', dataIndex: 'logicalPath', width: 240, ellipsis: true, render: (path: string) => <Typography.Text strong title={path}>{path}</Typography.Text> },
          { title: 'Resource key', dataIndex: 'key', width: 240, ellipsis: true, render: (key: string) => <Typography.Text type="secondary" title={key}>{key}</Typography.Text> },
          { title: 'Kind', dataIndex: 'kind', width: 130, render: (kind: number) => kinds.find(k => k.value === kind)?.label },
          { title: 'Media type', dataIndex: 'mediaType', width: 180 },
          { title: 'Size (bytes)', dataIndex: 'byteLength', width: 125, align: 'right', render: (bytes: number) => bytes.toLocaleString() },
          { title: 'State', width: 130, render: (_, r) => <Flex align="center" gap={token.paddingXS} style={{ whiteSpace: 'nowrap' }}><Switch aria-label={`Enable ${origin} resource ${r.logicalPath}`} checked={r.enabled} disabled={!ready || archived} onChange={enabled => confirmAction(modal, { title: `${enabled ? 'Enable' : 'Disable'} resource?`, content: r.dependencies.length && !enabled ? `Enabled Skills depend on this file: ${r.dependencies.join(', ')}. Disable those Skills first.` : 'Applies to the next new Run. Current Runs retain their original content.', okText: enabled ? 'Enable resource' : 'Disable resource', onOk: () => mutate(() => toggleInstanceResource(instanceId, catalog, r, enabled)) })} /><Typography.Text>{r.enabled ? 'Enabled' : 'Disabled'}</Typography.Text></Flex> },
          { title: 'Enablement source', width: 200, render: (_, r) => <Flex align="center" gap={token.paddingXS}><Typography.Text type="secondary">{origin === 'Instance' ? 'Instance-owned' : r.enabledOverride === null ? 'Definition default' : 'Instance override'}</Typography.Text>{origin === 'Definition' && r.enabledOverride !== null && <Tooltip title="Reset to Definition default"><Button aria-label="Reset to Definition default" icon={<UndoOutlined />} disabled={!ready || archived} onClick={() => void mutate(() => toggleInstanceResource(instanceId, catalog, r, null))} /></Tooltip>}</Flex> },
          { title: 'Actions', width: 350, render: (_, r) => <Flex className="admin-table-actions" gap={token.paddingXS}><Button disabled={!ready} onClick={() => open(r, true)}>Inspect</Button><Button disabled={!ready} onClick={() => void downloadInstanceResource(instanceId, r).catch(e => setError(describeAdminError(e, 'Download failed. Retry.').message))}>Download</Button>{origin === 'Definition' && <Button disabled={archived || !ready} onClick={() => confirmAction(modal, { title: 'Copy to this Instance?', content: 'Creates an independent file with source provenance. The inherited original stays enabled. Changes to it will not update the copy.', okText: 'Copy resource', onOk: () => mutate(() => copyInstanceResource(instanceId, catalog, r)) })}>Copy to Instance</Button>}{origin === 'Instance' && <><Button disabled={archived || !ready} onClick={() => open(r)}>Edit</Button><Button danger disabled={archived || !ready} onClick={() => confirmAction(modal, { title: 'Delete Instance resource?', content: 'Removes this file from future Runs. Admitted Runs retain their immutable content.', okText: 'Delete resource', danger: true, onOk: () => mutate(() => deleteInstanceResource(instanceId, catalog, r)) })}>Delete</Button></>}</Flex> }
         ]} /></AgentConfigurationPanel>)}
    </>}
    <Drawer styles={{ body: { padding: token.padding }, footer: { padding: token.padding }, wrapper: { maxWidth: "100vw" } }} title={editor?.readOnly ? 'Resource details' : editor?.resource ? 'Edit Instance resource' : 'Add Instance resources'} open={!!editor && active} size={compact ? '100%' : editor?.resource || editor?.readOnly ? 640 : 880} onClose={() => { if (!busy) { previewEpoch.current++; setEditor(null); } }} afterOpenChange={open => { drawerVisible.current = open; if (!open && opener.current?.isConnected) opener.current.focus(); }} footer={editor && !editor.readOnly ? <Flex vertical gap={token.paddingXS}>
      {progress && <Typography.Text role="status">{progress}</Typography.Text>}
      <Flex wrap gap={token.paddingXS}>
        <Button type="primary" loading={busy} disabled={!ready || (editor.resource ? !!editProblem : importBlocked)} onClick={() => void (editor.resource ? save() : importResources())}>
          {editor.resource ? 'Save resource' : incoming.length ? `Add ${incoming.length} ${incoming.length === 1 ? 'resource' : 'resources'}` : 'Add resources'}
        </Button>
        <Button disabled={busy} onClick={() => setEditor(null)}>Cancel</Button>
      </Flex>
    </Flex> : undefined}>
      {editor && <Flex vertical gap={token.padding}>
        {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => void reload()}>Reload and retain draft</Button>} />}
        {editor.readOnly && editor.resource ? <Descriptions {...detailLayout} column={1} size="small" className="admin-effective-details" items={[
          { key: 'identity', label: 'Resource key', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{editor.resource.key}</Typography.Text> },
          { key: 'path', label: 'Run path', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{editor.resource.virtualPath}</Typography.Text> },
          { key: 'hash', label: 'Content SHA-256', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{editor.resource.contentSha256}</Typography.Text> },
          { key: 'revision', label: 'Revision', children: editor.resource.revision },
          { key: 'dependencies', label: 'Enabled Skill dependencies', children: editor.resource.dependencies.join(', ') || 'None' }
        ]} /> : !editor.resource ? <Flex vertical gap={token.padding}>
          <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Choose files or a folder, then review each path and kind before adding. Resources apply to the next Run.</Typography.Paragraph>
          <ResourceFilePicker disabled={busy} onFiles={files => updateItems(current => [...current, ...files.map(createPreviewItem)])} onError={setError} />
          {incoming.length > 0 && <Flex vertical gap={token.paddingSM}>
            <Flex wrap justify="space-between" align="center" gap={token.paddingXS}>
              <Typography.Text strong>{incoming.length} {incoming.length === 1 ? 'file' : 'files'} to add</Typography.Text>
              <Button type="text" disabled={busy} onClick={() => updateItems(() => [])}>Clear files</Button>
            </Flex>
            <ResourceImportPreview items={incoming} existingPaths={localResources.map(resource => resource.logicalPath)} scope="Instance" disabled={busy} onChange={updateItems} />
          </Flex>}
          {limitProblem && <Alert type="warning" showIcon title={limitProblem} />}
          <Flex align="center" gap={token.paddingXS}><Switch aria-label="Enable imported resources" checked={editor.enabled} disabled={busy} onChange={enabled => setEditor({ ...editor, enabled })} /><Typography.Text>Enable added resources</Typography.Text></Flex>
        </Flex> : <Form layout="vertical">
          <Flex vertical gap={token.padding}>
            <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Update the path, kind or availability. Changes apply to the next Run.</Typography.Paragraph>
            <Form.Item label="Logical path" style={{ margin: 0 }} extra="A relative path, such as references/guide.pdf.">
              <Input aria-label="Resource logical path" value={editor.logicalPath} disabled={busy} onChange={e => setEditor({ ...editor, logicalPath: e.target.value })} />
            </Form.Item>
            <Flex gap={token.padding} wrap>
              <Form.Item label="Kind" style={{ margin: 0, flex: '1 1 220px' }}><Select aria-label="Resource kind" value={editor.kind} options={kinds} disabled={busy} onChange={kind => setEditor({ ...editor, kind })} /></Form.Item>
              <Form.Item label="Availability" style={{ margin: 0 }}><Flex align="center" gap={token.paddingXS} style={{ minHeight: token.controlHeight }}><Switch aria-label="Instance resource enabled" checked={editor.enabled} disabled={busy} onChange={enabled => setEditor({ ...editor, enabled })} /><Typography.Text>{editor.enabled ? 'Enabled' : 'Disabled'}</Typography.Text></Flex></Form.Item>
            </Flex>
            <Flex vertical gap={token.paddingXS}>
              <Typography.Text strong>Content</Typography.Text>
              <Typography.Text style={{ overflowWrap: 'anywhere' }}>{editor.resource.logicalPath.split('/').at(-1)} · {editor.resource.byteLength.toLocaleString()} bytes</Typography.Text>
              <Typography.Text type="secondary">{editor.file ? 'The selected file will replace the current content.' : 'Current content is kept unless you choose a replacement.'}</Typography.Text>
              <Upload.Dragger className="admin-resource-upload" maxCount={1} showUploadList={false} disabled={busy} beforeUpload={file => { setEditor(current => current ? { ...current, file } : current); return false; }}>
                <p className="ant-upload-drag-icon"><UploadOutlined /></p>
                <p className="ant-upload-text">Choose a replacement file</p>
                <p className="ant-upload-hint">Or drop one file here · up to 8 MiB</p>
              </Upload.Dragger>
              {editor.file && <Flex gap={token.paddingXS} align="center" justify="space-between">
                <Typography.Text style={{ overflowWrap: 'anywhere', minWidth: 0 }}>{editor.file.name} · {editor.file.size.toLocaleString()} bytes</Typography.Text>
                <Button type="text" disabled={busy} onClick={() => setEditor({ ...editor, file: null })}>Keep current file</Button>
              </Flex>}
              <Typography.Text type="secondary">Knowledge requires a supported text or data format. {RESOURCE_FILE_HELP}</Typography.Text>
            </Flex>
            {editProblem && <Alert type="error" showIcon title={editProblem} />}
          </Flex>
        </Form>}
        {editor.readOnly && editor.resource && <>
          <Button style={{ alignSelf: "flex-start" }} onClick={async () => {
            const request = epoch.current; const selection = previewEpoch.current; const resource = editor.resource!;
            try { const file = await readInstanceResourceFile(instanceId, resource); const text = isTextualResource(file.type) ? (await file.text()).slice(0, 12000) : 'Binary content: use Download to inspect the exact bytes.'; if (request === epoch.current && selection === previewEpoch.current && owner.current === instanceId) setPreview(text); }
            catch (e) { if (request === epoch.current) setError(describeAdminError(e, 'Unable to preview content. Retry.').message); }
          }}>Preview content</Button>
          {preview !== null && <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{preview}</Typography.Paragraph>}
        </>}
      </Flex>}
    </Drawer>
  </Flex>;
}
