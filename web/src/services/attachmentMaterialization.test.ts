import { beforeEach, describe, expect, it, vi } from 'vitest';
import { copyAttachment, readAttachmentCopyAccess } from './attachmentMaterialization';
import { IdentityCompletionError, identityRequest } from './identity';
import { listArtifactPage } from './artifacts';
vi.mock('./identity', async original => ({ ...await original<typeof import('./identity')>(), identityRequest: vi.fn() }));
describe('tenant human materialization service', () => {
  beforeEach(() => vi.resetAllMocks());
  it('uses explicit tenant ownership and a witnessed single POST', async () => {
    const signal = new AbortController().signal;
    vi.mocked(identityRequest).mockResolvedValueOnce({ displayName: 'original.txt', securityRevision: 3, canMaterialize: true, readOnly: false })
      .mockResolvedValueOnce({ artifactId: '10000000-0000-0000-0000-000000000001', workspaceLogicalPath: '/working/original.txt' });
    await readAttachmentCopyAccess('t', 's', 'a', signal); await copyAttachment('t', 's', 'a', 3, signal);
    expect(identityRequest).toHaveBeenLastCalledWith('/api/v2/tenants/t/sessions/s/attachments/a/materialize', { method: 'POST', body: { expectedRevision: 3 }, signal });
  });
  it('treats an unreadable successful acknowledgement as uncertain without a second POST', async () => {
    vi.mocked(identityRequest).mockResolvedValue({ artifactId: 'invalid', workspaceLogicalPath: '/home/other' });
    await expect(copyAttachment('t', 's', 'a', 3, new AbortController().signal)).rejects.toBeInstanceOf(IdentityCompletionError); expect(identityRequest).toHaveBeenCalledOnce();
  });
  it('reads tenant Artifact pages through authenticated canonical scope, including exact Run filters', async () => {
    vi.mocked(identityRequest).mockResolvedValue({ items: [], nextCursor: null, hasMore: false }); const signal = new AbortController().signal;
    await listArtifactPage('s', undefined, 'run', 't', signal);
    expect(identityRequest).toHaveBeenCalledWith('/api/v2/tenants/t/sessions/s/artifacts?limit=20&agentRunId=run', { signal });
  });
});
