import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FilePreviewButton } from './FilePreview';
import { readFile, resolveFile } from './fileSource';
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
