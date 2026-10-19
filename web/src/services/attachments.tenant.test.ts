import { afterEach, beforeEach, expect, it, vi } from 'vitest';
const calls = vi.hoisted(() => ({ witness: vi.fn(), upload: vi.fn(), request: vi.fn() }));
vi.mock('./tenantChat', async original => ({ ...await original<typeof import('./tenantChat')>(), chatTenantId: 'tenant-one', tenantTextWitness: calls.witness }));
vi.mock('./identity', async original => ({ ...await original<typeof import('./identity')>(), identityUpload: calls.upload, identityRequest: calls.request }));
import { abortPendingAttachment, releaseAllPendingFiles, releasePendingFile, uploadAttachment } from './attachments';
beforeEach(() => { calls.witness.mockReset(); calls.upload.mockReset(); calls.request.mockReset(); });
afterEach(() => releaseAllPendingFiles());
it('serializes actual uploads with fresh witnesses and preserves the upload identity for explicit retry', async () => {
  calls.witness.mockResolvedValueOnce({ expectedRevision: 4, expectedSessionRevision: 2 }).mockResolvedValueOnce({ expectedRevision: 5, expectedSessionRevision: 2 });
  let complete!: (value: unknown) => void;
  calls.upload.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; })).mockResolvedValueOnce({ attachmentId: 'saved-two' });
  const a = uploadAttachment('session-one', 'upload-one', new File(['a'], 'first.txt'), vi.fn());
  const b = uploadAttachment('session-one', 'upload-two', new File(['b'], 'second.txt'), vi.fn());
  await vi.waitFor(() => expect(calls.upload).toHaveBeenCalledTimes(1)); expect(calls.witness).toHaveBeenCalledTimes(1);
  complete({ attachmentId: 'saved-one' });
  await expect(a).resolves.toMatchObject({ attachmentId: 'saved-one', displayName: 'first.txt' }); await b;
  expect(calls.upload.mock.calls[0][0]).toContain('/tenants/tenant-one/sessions/session-one/attachments?uploadId=upload-one&expectedRevision=4&expectedSessionRevision=2');
  expect(calls.upload.mock.calls[1][0]).toContain('uploadId=upload-two&expectedRevision=5');
  expect(calls.upload.mock.calls[0][1]).toBeInstanceOf(FormData);
  expect(calls.request).not.toHaveBeenCalled(); // Upload acknowledgement does not imply a metadata read.
});
it('aborts an upload queued behind another mutation before dispatching its bytes', async () => {
  calls.witness.mockResolvedValue({ expectedRevision: 4, expectedSessionRevision: 2 });
  let complete!: (value: unknown) => void;
  calls.upload.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
  const a = uploadAttachment('s', 'first', new File(['a'], 'a.txt'), vi.fn());
  const b = uploadAttachment('s', 'second', new File(['b'], 'b.txt'), vi.fn());
  const refused = expect(b).rejects.toMatchObject({ name: 'AbortError' });
  await vi.waitFor(() => expect(calls.upload).toHaveBeenCalledTimes(1)); releasePendingFile('second');
  complete({ attachmentId: 'saved' }); await a; await refused; expect(calls.upload).toHaveBeenCalledTimes(1);
});
it('does not replay a rejected upload and uses witnessed tenant abort instead of owner access', async () => {
  calls.witness.mockResolvedValue({ expectedRevision: 8, expectedSessionRevision: 3 }); calls.upload.mockRejectedValue(new Error('Access changed'));
  await expect(uploadAttachment('s', 'same-upload', new File(['a'], 'a.txt'), vi.fn())).rejects.toThrow('Access changed'); expect(calls.upload).toHaveBeenCalledTimes(1);
  calls.request.mockResolvedValue(undefined); await abortPendingAttachment('s', 'saved');
  expect(calls.request).toHaveBeenCalledWith(expect.stringContaining('/api/v2/tenants/tenant-one/sessions/s/attachments/saved?requestId='), { method: 'DELETE' });
  expect(calls.request.mock.calls[0][0]).toContain('expectedRevision=8&expectedSessionRevision=3');
});
