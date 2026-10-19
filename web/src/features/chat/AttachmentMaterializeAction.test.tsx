import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { AttachmentMaterializeAction } from './AttachmentMaterializeAction';
import { copyAttachment, readAttachmentCopyAccess } from '../../services/attachmentMaterialization';
import { IdentityError } from '../../services/identity';
import { listArtifactPage } from '../../services/artifacts';
import { listTenantWorkspace } from '../../services/tenantWorkspace';
import { confirmAction } from '../../app/confirmAction';
vi.mock('../../services/attachmentMaterialization');
vi.mock('../../app/confirmAction');
vi.mock('../../services/artifacts', () => ({ listArtifactPage: vi.fn() }));
vi.mock('../../services/tenantWorkspace', () => ({ listTenantWorkspace: vi.fn() }));
vi.mock('./TenantSessionFilesDrawer', () => ({ TenantSessionFilesDrawer: ({ open, children, onClose }: { open: boolean; children: ReactNode; onClose: () => void }) => open ? <section aria-label="Session files">{children}<button onClick={onClose}>Close files</button></section> : null }));
const hints = { displayName: 'Exact original 試験.txt', securityRevision: 4, canMaterialize: true, readOnly: false };
const result = { artifactId: '10000000-0000-0000-0000-000000000001', workspaceLogicalPath: '/working/Exact_original.txt' };
function show() { return render(<App><AttachmentMaterializeAction tenantId="tenant" sessionId="session" attachmentId="source" displayName="History name.txt" /></App>); }
async function open() { fireEvent.click(screen.getByRole('button', { name: 'Copy History name.txt to working files' })); await screen.findByText(hints.displayName); }
async function confirm() { fireEvent.click(screen.getByRole('button', { name: 'Create copies' })); const options = vi.mocked(confirmAction).mock.lastCall![1]; await act(async () => { await options.onOk?.(); options.afterClose?.(); }); }
describe('exact Attachment working copy action', () => {
  beforeEach(() => {
    vi.resetAllMocks(); vi.mocked(readAttachmentCopyAccess).mockResolvedValue(hints); vi.mocked(copyAttachment).mockResolvedValue(result);
    vi.mocked(confirmAction).mockReturnValue({ destroy: vi.fn(), update: vi.fn(), then: vi.fn() } as never);
    vi.mocked(listArtifactPage).mockResolvedValue({ items: [], nextCursor: null, hasMore: false });
    vi.mocked(listTenantWorkspace).mockResolvedValue({ items: [], nextCursor: null, hasMore: false } as never);
  });
  it('loads exact current source access on demand, confirms and reports factual complete copies once', async () => {
    show(); expect(readAttachmentCopyAccess).not.toHaveBeenCalled(); await open(); expect(copyAttachment).not.toHaveBeenCalled();
    await confirm(); expect(copyAttachment).toHaveBeenCalledExactlyOnceWith('tenant', 'session', 'source', 4, expect.any(AbortSignal));
    expect(screen.getByText('Copies created')).toBeInTheDocument(); expect(screen.getByText(/\/working\/Exact_original.txt/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create copies' })).toBeDisabled();
  });
  it('preserves unknown outcome across closing and blocks replay until explicit successful file review and new confirmation', async () => {
    vi.mocked(copyAttachment).mockRejectedValueOnce(new TypeError('private network details')); show(); await open(); await confirm();
    expect(screen.getByText(/copy outcome could not be confirmed/i)).toBeInTheDocument(); expect(copyAttachment).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Close files' })); await open(); expect(screen.getByText(/copy outcome could not be confirmed/i)).toBeInTheDocument();
    vi.mocked(listTenantWorkspace).mockRejectedValueOnce(new IdentityError(403, 'resource_unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Reload files' })); await screen.findByText(/Ask someone with read access/);
    expect(screen.getByRole('button', { name: 'Create copies' })).toBeDisabled(); expect(copyAttachment).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Reload files' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Create copies' })).toBeEnabled());
    expect(copyAttachment).toHaveBeenCalledTimes(1); await confirm(); expect(copyAttachment).toHaveBeenCalledTimes(2);
    expect(listArtifactPage).toHaveBeenCalledWith('session', undefined, undefined, 'tenant', expect.any(AbortSignal)); expect(screen.queryByText(/private/)).not.toBeInTheDocument();
  });
  it('distinguishes known partial completion from unknown and rechecks both collections before another explicit action', async () => {
    vi.mocked(copyAttachment).mockRejectedValueOnce(new IdentityError(409, 'attachment_materialization_incomplete')); show(); await open(); await confirm();
    expect(screen.getByText(/working copy completed, but Artifact publication did not/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reload files' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Create copies' })).toBeEnabled());
    expect(listTenantWorkspace).toHaveBeenCalledOnce(); expect(copyAttachment).toHaveBeenCalledOnce();
  });
  it('retains refusal and reloads fresh access without resending a stale command', async () => {
    vi.mocked(copyAttachment).mockRejectedValueOnce(new IdentityError(409, 'stale_revision')); show(); await open(); await confirm();
    expect(screen.getByText(/copy was refused/)).toBeInTheDocument(); expect(screen.getByText(hints.displayName)).toBeInTheDocument(); vi.mocked(readAttachmentCopyAccess).mockResolvedValueOnce({ ...hints, securityRevision: 12 });
    fireEvent.click(screen.getByRole('button', { name: 'Reload files' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Create copies' })).toBeEnabled());
    expect(copyAttachment).toHaveBeenCalledTimes(1); expect(listTenantWorkspace).not.toHaveBeenCalled(); await confirm(); expect(vi.mocked(copyAttachment).mock.lastCall![3]).toBe(12);
  });
  it('withholds copy from read-only or unauthorized sources without fetching destination bodies', async () => {
    vi.mocked(readAttachmentCopyAccess).mockResolvedValue({ ...hints, canMaterialize: false, readOnly: true }); show(); await open();
    expect(screen.getByText('This Session is read-only')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Create copies' })).toBeDisabled();
    expect(copyAttachment).not.toHaveBeenCalled(); expect(listTenantWorkspace).not.toHaveBeenCalled();
  });
  it('clears current source detail after read loss and aborts the retired owner', async () => {
    const view = show(); await open(); vi.mocked(readAttachmentCopyAccess).mockRejectedValueOnce(new IdentityError(403, 'resource_unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Reload files' })); await screen.findByText('This source file is no longer available to you.');
    expect(screen.queryByText(hints.displayName)).not.toBeInTheDocument(); const signal = vi.mocked(readAttachmentCopyAccess).mock.lastCall![3]; view.unmount(); expect(signal.aborted).toBe(true);
  });
});
