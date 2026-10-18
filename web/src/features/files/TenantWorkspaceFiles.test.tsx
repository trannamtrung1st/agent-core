import { App, ConfigProvider } from 'antd';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { TenantWorkspaceFiles } from './TenantWorkspaceFiles';
import { IdentityError } from '../../services/identity';
import { listTenantWorkspace, mutateTenantWorkspace, readWorkspaceAccess, type TenantWorkspaceAccess, type TenantWorkspaceOwner } from '../../services/tenantWorkspace';
import { downloadFile } from '../filePreview/fileSource';
vi.mock('../../services/tenantWorkspace', async original => ({ ...await original<typeof import('../../services/tenantWorkspace')>(), readWorkspaceAccess: vi.fn(), listTenantWorkspace: vi.fn(), mutateTenantWorkspace: vi.fn() }));
vi.mock('../filePreview/fileSource', async original => ({ ...await original<typeof import('../filePreview/fileSource')>(), downloadFile: vi.fn() }));
vi.mock('../filePreview/FilePreview', () => ({ FilePreviewButton: () => <button>Preview current file</button> }));
const owner: TenantWorkspaceOwner = { tenantId: 'alpha', kind: 'home', instanceId: 'agent' };
const access: TenantWorkspaceAccess = { workspace: { tenantId: 'alpha', instanceId: 'agent', sessionId: null, logicalRoot: '/home' }, securityRevision: 7, instanceRevision: 1,
  canRead: true, canWrite: true, canDelete: true, readOnly: false, instanceName: 'Riley', sessionTitle: null };
const item = { agentInstanceId: 'agent', logicalPath: '/home/private.txt', directory: false, kind: 'file' as const, contentType: 'text/plain', byteSize: 7, updatedAt: '2026-10-19T00:00:00Z', mode: null, linkTarget: null };
const page = { items: [item], usedBytes: 7, totalItems: 1, maxFileBytes: 52428800, maxInstanceBytes: 262144000, nextPath: null, usageEnforcement: 'sampled' as const, truncated: false };
const view = (source: TenantWorkspaceOwner = owner) => <ConfigProvider><App><TenantWorkspaceFiles key={JSON.stringify(source)} owner={source} organization="Alpha" /></App></ConfigProvider>;
async function selectFile(path = '/home/supplied.txt') {
  fireEvent.click(await screen.findByRole('button', { name: 'Upload file' }));
  // AntD's test-mode ids collide between table tooltips and a native Drawer.
  // Resolve its actual title here; Playwright verifies the browser's unique dialog name.
  const editor = (await screen.findByText('Upload or replace a file', { selector: '.ant-drawer-title' })).closest('[role="dialog"]') as HTMLElement;
  const file = new File(['supplied private bytes'], 'supplied.txt', { type: 'text/plain' });
  fireEvent.change(editor.querySelector('input[type=file]')!, { target: { files: [file] } });
  await waitFor(() => expect(within(editor).getByRole('button', { name: 'Save file' })).toBeEnabled());
  fireEvent.change(within(editor).getByRole('textbox', { name: 'Destination path' }), { target: { value: path } });
  return { editor, file };
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(readWorkspaceAccess).mockResolvedValue(access); vi.mocked(listTenantWorkspace).mockResolvedValue(page); vi.mocked(mutateTenantWorkspace).mockResolvedValue(); vi.mocked(downloadFile).mockResolvedValue(); });

it('lets a writer upload supplied bytes without requesting or disclosing a file list', async () => {
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, canRead: false, canDelete: false });
  render(view()); await screen.findByText('File reading is restricted');
  expect(listTenantWorkspace).not.toHaveBeenCalled(); expect(screen.queryByText(item.logicalPath)).not.toBeInTheDocument();
  const { editor, file } = await selectFile(); fireEvent.click(within(editor).getByRole('button', { name: 'Save file' }));
  await screen.findByText('File saved.');
  expect(mutateTenantWorkspace).toHaveBeenCalledExactlyOnceWith(owner, '/home/supplied.txt', 7, expect.any(AbortSignal), file);
  expect(listTenantWorkspace).not.toHaveBeenCalled(); expect(screen.queryByRole('button', { name: /Download/ })).not.toBeInTheDocument();
});

it('keeps the selected file on stale authority, disables replay, and uses only an explicitly reloaded witness', async () => {
  render(view()); await screen.findByText(item.logicalPath); const { editor, file } = await selectFile();
  vi.mocked(mutateTenantWorkspace).mockRejectedValueOnce(new IdentityError(409, 'stale_revision'));
  fireEvent.click(within(editor).getByRole('button', { name: 'Save file' }));
  await within(editor).findByText('Saving is unavailable');
  expect(within(editor).getByRole('textbox', { name: 'Destination path' })).toHaveValue('/home/supplied.txt');
  expect(within(editor).getByRole('button', { name: 'supplied.txt' })).toBeInTheDocument();
  expect(within(editor).getByRole('button', { name: 'Save file' })).toBeDisabled();
  expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1); expect(screen.queryByText(item.logicalPath)).not.toBeInTheDocument();
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, securityRevision: 8 });
  fireEvent.click(within(editor).getByRole('button', { name: 'Reload workspace access' }));
  await waitFor(() => expect(within(editor).getByRole('button', { name: 'Save file' })).toBeEnabled());
  expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1);
  fireEvent.click(within(editor).getByRole('button', { name: 'Save file' })); await screen.findByText('File saved.');
  expect(mutateTenantWorkspace).toHaveBeenLastCalledWith(owner, '/home/supplied.txt', 8, expect.any(AbortSignal), file);
});

it('reports uncertain effects without replay and keeps an upload until an explicit decision', async () => {
  render(view()); await screen.findByText(item.logicalPath); const { editor } = await selectFile();
  vi.mocked(mutateTenantWorkspace).mockRejectedValueOnce(new IdentityError(409, 'workspace_effect_unknown'));
  fireEvent.click(within(editor).getByRole('button', { name: 'Save file' }));
  await within(editor).findByText('Saving is unavailable');
  expect(within(editor).getByText(/The file may have changed\. No operation was repeated/)).toBeInTheDocument();
  expect(within(editor).getByRole('button', { name: 'supplied.txt' })).toBeInTheDocument();
  expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1); expect(screen.queryByText('File saved.')).not.toBeInTheDocument();
});

it('keeps a completed save truthful when its subsequent read is denied and retires private rows', async () => {
  render(view()); await screen.findByText(item.logicalPath); const { editor } = await selectFile();
  vi.mocked(readWorkspaceAccess).mockRejectedValue(new IdentityError(403, 'resource_unavailable'));
  fireEvent.click(within(editor).getByRole('button', { name: 'Save file' }));
  await screen.findByText('File saved.'); await screen.findByText(/This item is unavailable or you no longer have access/);
  expect(screen.queryByText(item.logicalPath)).not.toBeInTheDocument(); expect(screen.queryByText('Upload or replace a file', { selector: '.ant-drawer-title' })).not.toBeInTheDocument();
  expect(screen.queryByText('Reload before another change')).not.toBeInTheDocument(); expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1);
});

it('keeps read-only downloads tenant bound and clears files after observed read loss', async () => {
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, canWrite: false, canDelete: false, readOnly: true });
  render(view()); await screen.findByText('Workspace is read-only'); await screen.findByText(item.logicalPath);
  expect(screen.queryByRole('button', { name: 'Upload file' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: /^Delete/ })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: `Download ${item.logicalPath}` }));
  await waitFor(() => expect(downloadFile).toHaveBeenCalledWith(expect.objectContaining({ kind: 'tenantWorkspace', owner, instanceId: 'agent', path: item.logicalPath }), expect.any(AbortSignal)));
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, canRead: false, canWrite: false, canDelete: false });
  fireEvent.click(screen.getByRole('button', { name: 'Reload workspace' })); await screen.findByText('File reading is restricted');
  expect(screen.queryByText(item.logicalPath)).not.toBeInTheDocument(); expect(screen.queryByText('Preview current file')).not.toBeInTheDocument(); expect(mutateTenantWorkspace).not.toHaveBeenCalled();
});

it('requires confirmation to delete, deduplicates a pending request and restores a surviving control', async () => {
  render(view()); await screen.findByText(item.logicalPath);
  fireEvent.click(screen.getByRole('button', { name: `Delete ${item.logicalPath}` })); fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));
  expect(mutateTenantWorkspace).not.toHaveBeenCalled();
  let finish!: () => void; vi.mocked(mutateTenantWorkspace).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  fireEvent.click(screen.getByRole('button', { name: `Delete ${item.logicalPath}` })); fireEvent.click(await screen.findByRole('button', { name: 'Delete path' }));
  await waitFor(() => expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1));
  vi.mocked(listTenantWorkspace).mockResolvedValue({ ...page, items: [] }); await act(async () => finish());
  await screen.findByText('Path deleted. Conversation Artifacts are kept.');
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reload workspace' })).toHaveFocus());
});

it('lets delete-only users name an exact path without loading names or content', async () => {
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, canRead: false, canWrite: false });
  render(view()); const path = await screen.findByRole('textbox', { name: 'Path to delete' });
  fireEvent.change(path, { target: { value: '/home/known.txt' } }); fireEvent.click(screen.getByRole('button', { name: 'Delete a path' }));
  expect(mutateTenantWorkspace).not.toHaveBeenCalled(); fireEvent.click(await screen.findByRole('button', { name: 'Delete path' }));
  await waitFor(() => expect(mutateTenantWorkspace).toHaveBeenCalledExactlyOnceWith(owner, '/home/known.txt', 7, expect.any(AbortSignal), undefined));
  expect(listTenantWorkspace).not.toHaveBeenCalled(); expect(downloadFile).not.toHaveBeenCalled();
});

it('keeps Session file actions visible in the bounded inspector even on a desktop viewport', async () => {
  const source: TenantWorkspaceOwner = { tenantId: 'alpha', kind: 'working', sessionId: 'session' };
  vi.mocked(readWorkspaceAccess).mockResolvedValue({ ...access, instanceName: null, instanceRevision: null, workspace: { ...access.workspace, logicalRoot: '/working', sessionId: 'session' } });
  vi.mocked(listTenantWorkspace).mockResolvedValue({ ...page, items: [{ ...item, logicalPath: '/working/private.txt' }] });
  render(view(source)); await screen.findByText('/working/private.txt');
  expect(screen.getByRole('columnheader', { name: 'File' })).toBeInTheDocument();
  expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeInTheDocument();
  expect(screen.queryByRole('columnheader', { name: 'Updated' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Download /working/private.txt' })).toBeEnabled();
});

it('restores Reload when revoked upload authority removes the original opener', async () => {
  render(view()); await screen.findByText(item.logicalPath); const { editor } = await selectFile();
  vi.mocked(mutateTenantWorkspace).mockRejectedValueOnce(new IdentityError(403, 'resource_unavailable'));
  fireEvent.click(within(editor).getByRole('button', { name: 'Save file' })); await within(editor).findByText('Saving is unavailable');
  expect(screen.queryByRole('button', { name: 'Upload file' })).not.toBeInTheDocument();
  fireEvent.click(within(editor).getByRole('button', { name: 'Cancel' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Discard changes' }));
  await waitFor(() => expect(screen.queryByText('Upload or replace a file', { selector: '.ant-drawer-title' })).not.toBeInTheDocument());
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reload workspace' })).toHaveFocus());
  expect(mutateTenantWorkspace).toHaveBeenCalledTimes(1);
});
