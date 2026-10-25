import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Breadcrumb, Button, Drawer, Flex, Form, Input, Spin, Typography, Upload } from 'antd';
import { UploadOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError } from '../../services/identity';
import { listTenantWorkspace, mutateTenantWorkspace, readWorkspaceAccess, workspaceRoot, type TenantWorkspaceAccess, type TenantWorkspaceOwner } from '../../services/tenantWorkspace';
import type { AgentWorkspaceItem, AgentWorkspacePage } from '../../services/agentWorkspace';
import { artifactSize } from '../../services/artifacts';
import { AccessError } from '../access/AccessFeedback';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { useTenantCommand } from '../access/useTenantCommand';
import { AdminCollectionToolbar, useAdminCollectionSearch } from '../admin/AdminCollectionToolbar';
import { downloadFile, type FileRef } from '../filePreview/fileSource';
import { WorkspaceFileTable } from './WorkspaceFileTable';

const maxFileBytes = 50 * 1024 * 1024;
const privateReadLost = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
function validPath(root: string, path: string) {
  return path.startsWith(root + '/') && !path.includes('\\') && !path.includes('\0') && new TextEncoder().encode(path).length <= 4095
    && path.slice(root.length + 1).split('/').every(part => part !== '' && part !== '.' && part !== '..');
}

/** The mounted owner is keyed by tenant and actual Space/Session in each caller. */
export function TenantWorkspaceFiles({ owner, organization, onBusy }: { owner: TenantWorkspaceOwner; organization: string; onBusy?: (busy: boolean) => void }) {
  const root = workspaceRoot(owner); const ownerKey = JSON.stringify(owner);
  const [access, setAccess] = useState<TenantWorkspaceAccess>(); const [page, setPage] = useState<AgentWorkspacePage>();
  const [prefix, setPrefix] = useState(root); const [loading, setLoading] = useState(true); const [error, setError] = useState<unknown>();
  const [notice, setNotice] = useState<string>(); const [blocked, setBlocked] = useState(false); const [editing, setEditing] = useState(false);
  const [deletePath, setDeletePath] = useState(''); const [confirming, setConfirming] = useState(false); const [downloading, setDownloading] = useState(false);
  const command = useTenantCommand(); const { modal } = App.useApp(); const { search, setSearch, pagination } = useAdminCollectionSearch();
  const generation = useRef(0); const request = useRef<AbortController | null>(null); const download = useRef<AbortController | null>(null);
  const active = useRef(true); const confirmation = useRef<ReturnType<typeof modal.confirm> | null>(null); const opener = useRef<HTMLElement | null>(null);
  const reloadAction = useRef<HTMLButtonElement>(null); const restoreFocus = useRef(false);
  const pending = command.busy || downloading || confirming;
  useEffect(() => {
    if (!loading && !editing && !confirming && restoreFocus.current) { restoreFocus.current = false; requestAnimationFrame(() => reloadAction.current?.focus({ preventScroll: true })); }
  }, [loading, editing, confirming, blocked]);
  useEffect(() => { onBusy?.(pending || editing); return () => onBusy?.(false); }, [pending, editing, onBusy]);
  const clearPrivate = () => { restoreFocus.current = true; setPage(undefined); setAccess(undefined); setPrefix(root); };
  const reload = useCallback(async (directory: string, cursor?: string) => {
    const current = ++generation.current; request.current?.abort(); const controller = new AbortController(); request.current = controller;
    setLoading(true); setError(undefined);
    if (!cursor) { setPage(undefined); setAccess(undefined); }
    try {
      const view = await readWorkspaceAccess(owner, controller.signal);
      const result = view.canRead ? await listTenantWorkspace(owner, directory, controller.signal, cursor) : undefined;
      if (controller.signal.aborted || current !== generation.current) return;
      setAccess(view); setBlocked(false); setPage(previous => cursor && previous && result
        ? { ...result, items: [...new Map([...previous.items, ...result.items].map(item => [item.logicalPath, item])).values()] } : result);
      if (!view.canRead) setPrefix(root);
    } catch (failure) {
      if (!controller.signal.aborted && current === generation.current) { setError(failure); setPage(undefined); setAccess(undefined); if (privateReadLost(failure)) setPrefix(root); }
    } finally { if (!controller.signal.aborted && current === generation.current) setLoading(false); }
  }, [ownerKey, root]);
  useEffect(() => { active.current = true; void reload(root); return () => {
    active.current = false; generation.current++; request.current?.abort(); download.current?.abort(); confirmation.current?.destroy();
  }; }, [reload, root]);
  const refresh = () => { command.clearError(); void reload(prefix); };
  const fileRef = (item: AgentWorkspaceItem): FileRef => ({ kind: 'tenantWorkspace', owner, path: item.logicalPath,
    metadata: { displayName: item.logicalPath.split('/').at(-1)!, contentType: item.contentType, byteSize: item.byteSize } });
  const mutate = async (path: string, file?: File) => {
    if (!access || blocked || loading || command.busy || (file ? !access.canWrite : !access.canDelete)) return;
    const revision = access.securityRevision;
    setNotice(undefined);
    await command.run(async signal => {
      try { await mutateTenantWorkspace(owner, path, revision, signal, file); }
      catch (failure) { if (!signal.aborted) { setBlocked(true); setPage(undefined); if (privateReadLost(failure)) clearPrivate(); } throw failure; }
    }, () => {
      closeEditor(); setDeletePath(''); setBlocked(false); restoreFocus.current = true; setNotice(file ? 'File saved.' : 'Path deleted. Conversation Artifacts are kept.');
      void reload(prefix);
    });
  };
  const remove = (path: string, directory = false) => {
    if (!access?.canDelete || pending || editing || blocked || loading || confirmation.current || !validPath(root, path)) return;
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: directory ? 'Delete empty folder?' : 'Delete workspace path?', danger: true,
      okText: directory ? 'Delete folder' : 'Delete path', content: `Delete the current path ${path} in ${organization}. This is nonrecursive; nonempty folders remain. Existing conversation Artifacts are kept.`,
      afterClose: () => { confirmation.current = null; if (active.current) setConfirming(false); }, onOk: () => mutate(path) });
  };
  const save = async (item: AgentWorkspaceItem) => {
    if (!access?.canRead || pending || loading || download.current) return;
    const controller = new AbortController(); download.current = controller; setDownloading(true); setError(undefined);
    try { await downloadFile(fileRef(item), controller.signal); }
    catch (failure) { if (!controller.signal.aborted) { setError(failure); clearPrivate(); setBlocked(true); } }
    finally { if (download.current === controller) download.current = null; if (!controller.signal.aborted) setDownloading(false); }
  };
  const closeEditor = () => { setEditing(false); requestAnimationFrame(() => {
    const target = opener.current?.isConnected ? opener.current : reloadAction.current; target?.focus({ preventScroll: true });
  }); };
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>{root} · {owner.kind === 'home' ? 'Live files owned by this Space.' : 'Working files owned by this Session.'} {organization}.</Typography.Paragraph>
    <Flex wrap align="center" justify="space-between" gap="var(--ac-space-compact)">
      <Typography.Text>{access ? owner.kind === 'home' ? access.workspaceName : access.sessionTitle ?? 'Session files' : 'Workspace files'}</Typography.Text>
      <Flex wrap gap="var(--ac-space-compact)"><Button ref={reloadAction} aria-label="Reload workspace" loading={loading} disabled={pending} onClick={refresh}>Reload workspace</Button>
        {access?.canWrite && <Button type="primary" icon={<UploadOutlined aria-hidden />} disabled={pending || loading || blocked || editing} onClick={event => { opener.current = event.currentTarget; setEditing(true); }}>Upload file</Button>}
      </Flex>
    </Flex>
    {notice && <Alert type="success" showIcon title={notice} />}
    {error !== undefined && <AccessError error={error} />}
    {command.error !== undefined && <AccessError error={command.error} />}
    {blocked && <Alert type="warning" showIcon title="Reload before another change" description="The file may have changed. No operation was repeated. Reload current files and access before deciding what to do next. Your selected upload is kept until you save or discard it." />}
    {loading && !access && <Spin aria-label="Loading workspace" />}
    {access?.readOnly && <Alert type="info" showIcon title="Workspace is read-only" description="Files remain available under current read access. Restore the owning lifecycle before making changes." />}
    {access && !access.canRead && <Alert type="info" showIcon title="File reading is restricted" description={access.canWrite || access.canDelete ? 'You can change a known path under your current permissions. Listing, preview and download require separate read access.' : 'You do not have access to list, preview, download or change these files. Contact an organization owner if you need access.'} />}
    {access?.canRead && page && <>
      <Breadcrumb aria-label="Workspace directory" items={prefix.split('/').filter(Boolean).map((part, index, parts) => ({ title: index === parts.length - 1 ? part : <Button type="text" size="small" disabled={pending || editing || loading} onClick={() => { const path = '/' + parts.slice(0, index + 1).join('/'); setPrefix(path); setSearch(''); void reload(path); }}>{part}</Button> }))} />
      <Typography.Text type="secondary">{page.usedBytes === null ? 'Usage unknown' : artifactSize(page.usedBytes)} sampled usage. Search filters loaded paths in this folder. Reload to observe current files.</Typography.Text>
      {page.usageEnforcement === 'unknown' && <Alert type="warning" showIcon title="Storage usage could not be measured" description="Reload to recheck current usage." />}
      <AdminCollectionToolbar value={search} onChange={setSearch} label="workspace items" showScrollHint={false} />
      <WorkspaceFileTable items={page.items.filter(item => `${item.logicalPath} ${item.contentType}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()))}
        compact={owner.kind === 'working'} loading={loading} busy={pending || editing || blocked} canDelete={access.canDelete} emptyText={search ? 'No matching items' : 'No workspace items yet'} pagination={pagination}
        previewFile={fileRef} onOpen={path => { setPrefix(path); setSearch(''); void reload(path); }} onDownload={item => void save(item)} onRemove={item => remove(item.logicalPath, item.directory)}
        onReadLost={() => { clearPrivate(); setBlocked(true); setError(new IdentityError(403, 'resource_unavailable')); }} />
      {page.nextPath && <Button disabled={pending || editing || loading} onClick={() => void reload(prefix, page.nextPath!)}>Load more items</Button>}
    </>}
    {access?.canDelete && !access.canRead && <Flex vertical gap="var(--ac-space-compact)"><Form.Item label="Path to delete" style={{ marginBottom: 0 }} htmlFor="workspace-delete-path"><Input id="workspace-delete-path" value={deletePath} maxLength={4095} disabled={pending || loading || editing || blocked} onChange={event => setDeletePath(event.target.value)} placeholder={root + '/filename'} /></Form.Item>
      <Button danger style={{ alignSelf: 'flex-start' }} disabled={pending || loading || editing || blocked || !validPath(root, deletePath)} onClick={() => remove(deletePath)}>Delete a path</Button></Flex>}
    {editing && <UploadEditor root={root} prefix={prefix} canSave={!!access?.canWrite && !loading && !blocked} busy={command.busy} error={command.error} blocked={blocked} onReload={refresh} onClose={closeEditor} onSave={(path, file) => void mutate(path, file)} />}
  </Flex>;
}

function UploadEditor({ root, prefix, canSave, busy, error, blocked, onReload, onClose, onSave }: { root: string; prefix: string; canSave: boolean; busy: boolean; error?: unknown; blocked: boolean; onReload: () => void; onClose: () => void; onSave: (path: string, file: File) => void }) {
  const [file, setFile] = useState<File>(); const [path, setPath] = useState('');
  const guard = useAccessDraftGuard(!!file || path !== '', busy, onClose);
  return <>{guard.holder}<Drawer open title="Upload or replace a file" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }}
    onClose={guard.close} keyboard={!busy} maskClosable={!busy} closable={!busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>Cancel</Button><Button type="primary" aria-label="Save file" loading={busy} disabled={!canSave || !file || file.size > maxFileBytes || !validPath(root, path)} onClick={() => file && onSave(path, file)}>Save file</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      <Typography.Paragraph style={{ margin: 0 }}>Save one file, up to 50 MiB, at an exact {root} path. If the path exists, its current bytes are replaced. Other authorized writers can change it at any time.</Typography.Paragraph>
      {error !== undefined && <AccessError error={error} />}
      {!canSave && <Flex vertical gap="var(--ac-space-compact)"><Alert type="warning" showIcon title="Saving is unavailable" description={blocked ? 'The file may have changed. No operation was repeated. Your selected file and path are kept.' : 'Your selected file and path are kept. Reload workspace access before deciding whether to save.'} /><Button style={{ alignSelf: 'flex-start' }} disabled={busy} onClick={onReload}>Reload workspace access</Button></Flex>}
      <Form layout="vertical"><Form.Item label="File"><Upload maxCount={1} fileList={file ? [{ uid: 'selected', name: file.name }] : []} disabled={busy}
        beforeUpload={value => { setFile(value); setPath(prefix + '/' + value.name); return false; }} onRemove={() => { setFile(undefined); return true; }}><Button disabled={busy}>Choose file</Button></Upload></Form.Item>
        {file && file.size > maxFileBytes && <Alert type="error" showIcon title="File exceeds 50 MiB" description="Choose a smaller file. No bytes were uploaded." />}
        <Form.Item label="Destination path" htmlFor="workspace-upload-path" help={`Use an exact absolute child path under ${root}. No file version is implied.`}><Input id="workspace-upload-path" value={path} maxLength={4095} disabled={busy} onChange={event => setPath(event.target.value)} placeholder={root + '/filename'} /></Form.Item></Form>
    </Flex>
  </Drawer></>;
}
