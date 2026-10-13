import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { DriveFilesButton } from './DriveFiles';
import * as drive from '../../services/drive';

vi.mock('../../services/drive', async (original) => ({
  ...(await original<typeof import('../../services/drive')>()),
  getDriveAccounts: vi.fn(),
  listDriveFiles: vi.fn(),
}));
const files: drive.DriveFile[] = ['q3-report.docx', 'notes.txt'].map(
  (name, i) => ({
    id: String(i),
    name,
    mimeType: 'text/plain',
    byteSize: 10,
    version: '1',
    folder: false,
    nativeDocument: false,
    trashed: false,
    md5Checksum: null,
    parents: [],
    webViewLink: null,
  }),
);
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(drive.getDriveAccounts).mockResolvedValue([
    {
      accountRef: 'work',
      displayName: 'Work',
      emailAddress: 'work@example.test',
      available: true,
      authEpoch: 1,
      bindingId: 'binding',
      recovery: null,
      status: 'Connected',
      synthetic: true,
    },
  ]);
  vi.mocked(drive.listDriveFiles).mockImplementation(
    async (_instance, _account, _folder, _cursor, query) => ({
      items: files.filter((file) => !query || file.name.includes(query)),
      nextCursor: null,
      limitedToAppAuthorizedFiles: true,
    }),
  );
});
it('clearing Drive search restores the unfiltered list using the emitted value', async () => {
  render(<DriveFilesButton instanceId="i" accountRef="work" />);
  fireEvent.click(screen.getByRole('button', { name: 'Browse Drive files' }));
  await screen.findByText('notes.txt');
  const search = screen.getByRole('searchbox', {
    name: 'Search app-authorized Drive files',
  });
  fireEvent.change(search, { target: { value: 'q3' } });
  fireEvent.keyDown(search, {
    key: 'Enter',
    code: 'Enter',
    keyCode: 13,
    charCode: 13,
  });
  await waitFor(() => expect(screen.queryByText('notes.txt')).toBeNull());
  fireEvent.click(screen.getByRole('button', { name: 'Clear Drive search' }));
  await waitFor(() =>
    expect(vi.mocked(drive.listDriveFiles).mock.lastCall?.[4]).toBe(''),
  );
  await screen.findByText('notes.txt');
  expect(search).toHaveValue('');
});
