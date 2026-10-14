import { testPolicy } from './previewTestPolicy';
import { loadPreviewOptions } from './previewOptions';
vi.mock('./previewOptions', async original => ({ ...(await original<typeof import('./previewOptions')>()), loadPreviewOptions: vi.fn() }));
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FilePreviewButton } from './FilePreview';
import { readFile, resolveFile, PreviewError } from './fileSource';
import { readOfficePreview } from './officeSource';
vi.mock('./officeSource', () => ({ readOfficePreview: vi.fn() }));
vi.mock('./fileSource', async (original) => ({
  ...(await original<typeof import('./fileSource')>()),
  readFile: vi.fn(),
  resolveFile: vi.fn(),
}));
vi.mock('./previewPolicy', async (original) => ({
  ...(await original<typeof import('./previewPolicy')>()),
  decodeText: vi.fn().mockResolvedValue('current content'),
}));
const file = { kind: 'artifact' as const, sessionId: 's1', artifactId: 'a1' };
const metadata = {
  displayName: 'readme.txt',
  contentType: 'text/plain',
  byteSize: 15,
};
beforeEach(() => {
  vi.mocked(readOfficePreview).mockReset();
  vi.mocked(loadPreviewOptions).mockReset().mockResolvedValue(testPolicy);
  vi.mocked(resolveFile).mockReset().mockResolvedValue(metadata);
  vi.mocked(readFile)
    .mockReset()
    .mockResolvedValue(new Blob(['current content']));
});
describe('Shared preview lifecycle', () => {
  it('keeps content lazy, closes and aborts on source/owner changes, and ignores late results', async () => {
    let finish!: (value: Blob) => void;
    vi.mocked(readFile).mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const view = render(<FilePreviewButton file={file} />);
    expect(resolveFile).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Preview file' }));
    await waitFor(() => expect(readFile).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(readFile).mock.calls[0][3];
    view.rerender(<FilePreviewButton file={{ ...file, sessionId: 's2' }} />);
    expect(signal.aborted).toBe(true);
    await act(async () => finish(new Blob(['old private content'])));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText('old private content')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Preview file' }));
    await screen.findByText('current content');
  });
  it('offers truthful unsupported fallback without fetching any body', async () => {
    vi.mocked(resolveFile).mockResolvedValue({
      ...metadata,
      displayName: 'report.pdf',
      contentType: 'application/pdf',
    });
    render(<FilePreviewButton file={file} />);
    fireEvent.click(screen.getByRole('button', { name: 'Preview file' }));
    await screen.findByText(/Preview is unavailable for this format/);
    expect(readFile).not.toHaveBeenCalled();
    expect(
      screen.getByRole('button', { name: 'Download report.pdf' }),
    ).toBeEnabled();
  });
});

it('fails closed when policy is unavailable and recovers with Retry while preserving Download', async () => {
  vi.mocked(loadPreviewOptions).mockRejectedValueOnce(new Error('offline'));
  render(<FilePreviewButton file={file} />);
  fireEvent.click(screen.getByRole('button', { name: 'Preview file' }));
  await screen.findByRole('button', { name: 'Retry' });
  expect(readFile).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: 'Download readme.txt' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByText('current content');
});
it('uses the effective limit before reading oversized content', async () => {
  vi.mocked(resolveFile).mockResolvedValue({ ...metadata, byteSize: testPolicy.limits.textBytes + 1 });
  render(<FilePreviewButton file={file} />);
  fireEvent.click(screen.getByRole('button', { name: 'Preview file' }));
  await screen.findByText(/preview limit is 2 MiB/);
  expect(readFile).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: 'Download readme.txt' })).toBeEnabled();
});

it.each([['docx', false], ['xlsx', false], ['docx', true], ['xlsx', true]] as const)('Office %s preview failures keep original Download available (Drive: %s)', async (extension, drive) => {
  vi.mocked(resolveFile).mockResolvedValue({ displayName: 'original.' + extension, contentType: 'application/vnd.openxmlformats-officedocument.' + (extension === 'docx' ? 'wordprocessingml.document' : 'spreadsheetml.sheet'), byteSize: 15 });
  vi.mocked(readOfficePreview).mockRejectedValue(new PreviewError('office_feature_unsupported', 'This Office file contains a feature the preview does not support. The original file is still available to download.'));
  const source = drive ? { kind: 'drive' as const, instanceId: 'i', accountRef: 'work', fileId: 'f', version: '2', authEpoch: 3, bindingId: 'b', synthetic: true, metadata: { displayName: 'original.' + extension, contentType: 'application/octet-stream', byteSize: 15 } } : file;
  render(<FilePreviewButton file={source} />);
  fireEvent.click(screen.getByRole('button', { name: drive ? 'Preview original.' + extension : 'Preview file' }));
  await waitFor(() => expect(screen.getByText(/The original file is still available to download/)).toBeVisible());
  expect(screen.getByRole('button', { name: 'Download original.' + extension })).toBeEnabled();
  expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
});
