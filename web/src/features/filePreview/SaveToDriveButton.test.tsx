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
