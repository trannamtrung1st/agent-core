import { beforeEach, expect, it, vi } from 'vitest';
import { ownerFetch } from './api';
import {
  DriveOperationFailedError,
  DrivePendingError,
  getDriveAccounts,
  saveToDrive,
} from './drive';

vi.mock('./api', () => ({ ownerFetch: vi.fn() }));
const file = { kind: 'artifact' as const, sessionId: 's', artifactId: 'a' };
const body = {
  accountRef: 'work',
  authEpoch: 1,
  bindingId: 'binding',
  expectedSha256: 'sha',
  operationId: 'op',
  folderId: 'root',
  name: 'report.txt',
};
beforeEach(() => vi.resetAllMocks());

it.each(['transport', 'server', 'malformed'])(
  'fences an upload with an unknown %s response using its original operation ID',
  async (failure) => {
    if (failure === 'transport')
      vi.mocked(ownerFetch).mockRejectedValue(new TypeError('Failed to fetch'));
    else
      vi.mocked(ownerFetch).mockResolvedValue(
        failure === 'server'
          ? new Response('unavailable', { status: 502 })
          : new Response('invalid JSON'),
      );
    await expect(saveToDrive(file, body)).rejects.toMatchObject({
      operationId: 'op',
      resumeAllowed: false,
    });
  },
);
it('distinguishes proven non-execution from a saved resumable operation', async () => {
  vi.mocked(ownerFetch).mockResolvedValueOnce(
    Response.json(
      {
        title: 'drive_operation_failed',
        detail: 'No effect. Check permission.',
      },
      { status: 409 },
    ),
  );
  await expect(saveToDrive(file, body)).rejects.toBeInstanceOf(
    DriveOperationFailedError,
  );
  vi.mocked(ownerFetch).mockResolvedValueOnce(
    Response.json(
      {
        error: 'drive_effect_indeterminate',
        operationId: 'op',
        fileId: 'remote',
        resumeAllowed: true,
      },
      { status: 409 },
    ),
  );
  await expect(saveToDrive(file, body)).rejects.toMatchObject({
    operationId: 'op',
    resumeAllowed: true,
  });
});
it('does not turn read failures or intentional aborts into pending writes', async () => {
  vi.mocked(ownerFetch).mockRejectedValue(new TypeError('Failed to fetch'));
  await expect(getDriveAccounts('i')).rejects.not.toBeInstanceOf(
    DrivePendingError,
  );
  const abort = new AbortController();
  abort.abort();
  await expect(
    saveToDrive(file, body, abort.signal),
  ).rejects.not.toBeInstanceOf(DrivePendingError);
});
