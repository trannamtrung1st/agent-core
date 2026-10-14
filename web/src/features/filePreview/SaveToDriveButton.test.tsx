import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SaveToDriveButton } from './SaveToDriveButton';
import * as drive from '../../services/drive';
import { resolveFile } from './fileSource';

vi.mock('../../services/drive', async (original) => ({
  ...(await original<typeof import('../../services/drive')>()),
  getDriveOptions: vi.fn(),
  getSaveAccounts: vi.fn(),
  listDriveFiles: vi.fn(),
  saveToDrive: vi.fn(),
  inspectDriveOperation: vi.fn(),
}));
vi.mock('./fileSource', async (original) => ({
  ...(await original<typeof import('./fileSource')>()),
  resolveFile: vi.fn(),
}));
const file = { kind: 'artifact' as const, sessionId: 's', artifactId: 'a' };
const accounts: drive.DriveAccount[] = ['Work', 'Personal'].map((name) => ({
  accountRef: name.toLowerCase(),
  displayName: name,
  emailAddress: `${name}@example.test`,
  bindingId: name,
  authEpoch: 1,
  available: true,
  recovery: null,
  status: 'Connected',
  synthetic: true,
}));
const folder: drive.DriveFile = {
  id: 'old-folder',
  name: 'Work-only folder',
  folder: true,
  mimeType: 'application/vnd.google-apps.folder',
  byteSize: 0,
  version: '1',
  md5Checksum: null,
  parents: [],
  trashed: false,
  webViewLink: null,
  nativeDocument: false,
};
beforeEach(() => {
  vi.resetAllMocks();
  sessionStorage.clear();
  vi.mocked(drive.getDriveOptions).mockResolvedValue({
    enabled: true,
    configured: true,
    synthetic: true,
    scope: 'drive.file',
  });
  vi.mocked(resolveFile).mockResolvedValue({
    displayName: 'report.docx',
    contentType: 'application/octet-stream',
    byteSize: 10,
    sha256: 'sha',
  });
  vi.mocked(drive.getSaveAccounts).mockResolvedValue({
    instanceId: 'i',
    accounts,
  });
  vi.mocked(drive.listDriveFiles).mockResolvedValue({
    items: [],
    nextCursor: null,
    limitedToAppAuthorizedFiles: true,
  });
});
async function open() {
  render(<SaveToDriveButton file={file} displayName="report.docx" />);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Save report.docx to Drive' }),
  );
}
describe('Drive save destination recovery', () => {
  it('shows terminal recovery when a read-only operation check proves non-execution', async () => {
    vi.mocked(drive.getSaveAccounts).mockResolvedValue({
      instanceId: 'i',
      accounts: [accounts[0]],
    });
    vi.mocked(drive.saveToDrive).mockRejectedValueOnce(
      new drive.DrivePendingError('op', 'remote'),
    );
    vi.mocked(drive.inspectDriveOperation).mockRejectedValueOnce(
      new drive.DriveOperationFailedError('No effect. Check permission.'),
    );
    await open();
    const save = await screen.findByRole('button', { name: 'Save new file' });
    await waitFor(() => expect(save).toBeEnabled());
    fireEvent.click(save);
    fireEvent.click(
      await screen.findByRole('button', { name: 'Check operation' }),
    );
    await screen.findByRole('button', { name: 'Start new save' });
    expect(
      screen.getByRole('textbox', { name: 'Drive file name' }),
    ).toBeDisabled();
    expect(drive.saveToDrive).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByRole('button', { name: 'Check operation' }),
    ).toBeNull();
  });
  it('continues only the same pending operation and keeps the immutable destination locked', async () => {
    vi.mocked(drive.getSaveAccounts).mockResolvedValue({
      instanceId: 'i',
      accounts: [accounts[0]],
    });
    vi.mocked(drive.saveToDrive).mockRejectedValueOnce(
      new drive.DrivePendingError('saved-op', 'remote', true),
    );
    vi.mocked(drive.saveToDrive).mockResolvedValueOnce({
      operationId: 'saved-op',
      status: 'Succeeded',
      reconciled: true,
      synthetic: true,
      limitation: null,
      file: { ...folder, name: 'report.docx', folder: false },
    });
    await open();
    const save = await screen.findByRole('button', { name: 'Save new file' });
    await waitFor(() => expect(save).toBeEnabled());
    fireEvent.click(save);
    const resume = await screen.findByRole('button', {
      name: 'Continue upload',
    });
    expect(
      screen.getByRole('combobox', { name: 'Drive folder' }),
    ).toBeDisabled();
    expect(
      screen.getByRole('textbox', { name: 'Drive file name' }),
    ).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save new file' })).toBeNull();
    fireEvent.click(resume);
    await screen.findByText('Verified in Synthetic Drive');
    const [first, second] = vi.mocked(drive.saveToDrive).mock.calls;
    expect(second[1]).toEqual({ ...first[1], operationId: 'saved-op' });
    expect(drive.saveToDrive).toHaveBeenCalledTimes(2);
  });
  it('requires an explicit new-save action after proven non-execution and generates a fresh ID', async () => {
    vi.mocked(drive.getSaveAccounts).mockResolvedValue({
      instanceId: 'i',
      accounts: [accounts[0]],
    });
    vi.mocked(drive.saveToDrive).mockRejectedValueOnce(
      new drive.DriveOperationFailedError(
        'The operation did not execute. Check permission.',
      ),
    );
    vi.mocked(drive.saveToDrive).mockRejectedValueOnce(
      new drive.DrivePendingError('unknown-op', 'remote'),
    );
    await open();
    const save = await screen.findByRole('button', { name: 'Save new file' });
    await waitFor(() => expect(save).toBeEnabled());
    fireEvent.click(save);
    const fresh = await screen.findByRole('button', { name: 'Start new save' });
    expect(
      screen.getByRole('textbox', { name: 'Drive file name' }),
    ).toBeDisabled();
    expect(drive.saveToDrive).toHaveBeenCalledTimes(1);
    fireEvent.click(fresh);
    fireEvent.click(
      await screen.findByRole('button', { name: 'Save new file' }),
    );
    await screen.findByRole('button', { name: 'Check operation' });
    expect(vi.mocked(drive.saveToDrive).mock.calls[0][1].operationId).not.toBe(
      vi.mocked(drive.saveToDrive).mock.calls[1][1].operationId,
    );
    expect(
      screen.queryByRole('button', { name: 'Continue upload' }),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: 'Start new save' })).toBeNull();
    expect(
      screen.getByRole('textbox', { name: 'Drive file name' }),
    ).toBeDisabled();
  });
  it('locks destination controls during upload, uncertain recovery and verified completion', async () => {
    vi.mocked(drive.getSaveAccounts).mockResolvedValue({
      instanceId: 'i',
      accounts: [accounts[0]],
    });
    let reject!: (reason: unknown) => void;
    vi.mocked(drive.saveToDrive).mockReturnValue(
      new Promise((_resolve, fail) => {
        reject = fail;
      }),
    );
    vi.mocked(drive.inspectDriveOperation).mockResolvedValue({
      operationId: 'op',
      status: 'Succeeded',
      reconciled: true,
      synthetic: true,
      limitation: null,
      file: { ...folder, id: 'remote', name: 'report.docx', folder: false },
    });
    await open();
    const save = await screen.findByRole('button', { name: 'Save new file' });
    await waitFor(() => expect(save).toBeEnabled());
    fireEvent.click(save);
    await waitFor(() => expect(drive.saveToDrive).toHaveBeenCalledTimes(1));
    expect(
      screen.getByRole('combobox', { name: 'Drive folder' }),
    ).toBeDisabled();
    await act(async () => reject(new drive.DrivePendingError('op', 'remote')));
    const inspect = await screen.findByRole('button', {
      name: /Check operation/,
    });
    expect(inspect).toHaveAccessibleName('Check operation');
    expect(
      screen.getByRole('combobox', { name: 'Drive folder' }),
    ).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save new file' })).toBeNull();
    fireEvent.click(inspect);
    await screen.findByText('Verified in Synthetic Drive');
    expect(
      screen.getByRole('combobox', { name: 'Drive folder' }),
    ).toBeDisabled();
    expect(drive.saveToDrive).toHaveBeenCalledTimes(1);
    expect(drive.inspectDriveOperation).toHaveBeenCalledWith(
      'i',
      'work',
      'op',
      expect.any(AbortSignal),
    );
  });
  it('retries a failed destination read in the same dialog without enabling an upload', async () => {
    vi.mocked(drive.getSaveAccounts).mockRejectedValueOnce(
      new Error('Destinations unavailable'),
    );
    await open();
    fireEvent.click(
      await screen.findByRole('button', { name: 'Retry destinations' }),
    );
    await waitFor(() => expect(drive.getSaveAccounts).toHaveBeenCalledTimes(2));
    await screen.findByRole('combobox', { name: 'Drive account' });
    expect(
      screen.getByRole('button', { name: 'Save new file' }),
    ).toBeDisabled();
    expect(screen.queryByText('Destinations unavailable')).toBeNull();
  });
  it('clears a previous binding’s folders and ignores its late read after account change', async () => {
    let finish!: (value: drive.DrivePage) => void;
    vi.mocked(drive.listDriveFiles).mockImplementation((_instance, account) =>
      account === 'work'
        ? new Promise((resolve) => {
            finish = resolve;
          })
        : Promise.resolve({
            items: [],
            nextCursor: null,
            limitedToAppAuthorizedFiles: true,
          }),
    );
    await open();
    fireEvent.mouseDown(
      await screen.findByRole('combobox', { name: 'Drive account' }),
    );
    fireEvent.click(await screen.findByText('Work · Work@example.test'));
    await waitFor(() => expect(drive.listDriveFiles).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(drive.listDriveFiles).mock.calls[0][5];
    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: 'Drive account' }),
    );
    fireEvent.click(
      await screen.findByText('Personal · Personal@example.test'),
    );
    expect(signal?.aborted).toBe(true);
    await act(async () =>
      finish({
        items: [folder],
        nextCursor: null,
        limitedToAppAuthorizedFiles: true,
      }),
    );
    await waitFor(() => expect(drive.listDriveFiles).toHaveBeenCalledTimes(2));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Drive folder' }));
    expect(screen.queryByText('Work-only folder')).toBeNull();
    expect(screen.getByRole('button', { name: 'Save new file' })).toBeEnabled();
  });
});
