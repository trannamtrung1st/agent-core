import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ArtifactView } from './ArtifactView';
import { IdentityError } from '../../services/identity';
import {
  artifactKind,
  artifactSize,
  downloadArtifact,
  getArtifact,
} from '../../services/artifacts';
vi.mock('../../services/artifacts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../services/artifacts')>()),
  getArtifact: vi.fn(),
  downloadArtifact: vi.fn(),
}));
const file = {
  artifactId: 'id',
  sessionId: 's1',
  displayName: 'scrum-project-plan.md',
  contentType: 'text/markdown',
  byteSize: 18842,
};
beforeEach(() => {
  vi.mocked(getArtifact).mockReset().mockResolvedValue(file);
  vi.mocked(downloadArtifact).mockReset().mockResolvedValue();
});

describe('Artifact card', () => {
  it('binds tenant metadata and download to the same source and removes private names after read loss', async () => {
    vi.mocked(downloadArtifact).mockRejectedValueOnce(new IdentityError(403, 'resource_unavailable'));
    render(<ArtifactView tenantId="tenant-a" sessionId="s1" artifactId="id" />);
    const button = await screen.findByRole('button', { name: 'Download scrum-project-plan.md' });
    expect(getArtifact).toHaveBeenCalledWith('s1', 'id', 'tenant-a', expect.any(AbortSignal));
    fireEvent.click(button);
    await waitFor(() => expect(screen.queryByText('scrum-project-plan.md')).not.toBeInTheDocument());
    expect(screen.getByText('File unavailable')).toBeInTheDocument();
    expect(downloadArtifact).toHaveBeenCalledWith('s1', 'id', expect.any(AbortSignal), 'tenant-a');
  });

  it.each([401, 403, 404])('keeps the refusal reason after retiring protected metadata (%i)', async (status) => {
    vi.mocked(downloadArtifact).mockRejectedValueOnce(new IdentityError(status, 'resource_unavailable'));
    render(<ArtifactView tenantId="tenant-a" sessionId="s1" artifactId="id" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Download scrum-project-plan.md' }));
    await screen.findByText('File unavailable');
    expect(screen.getByRole('status')).toHaveTextContent(status === 401 ? 'session has expired' : status === 403 ? 'no longer have permission' : 'Reload the source list');
    expect(screen.queryByText('scrum-project-plan.md')).not.toBeInTheDocument();
  });

  it('loads quietly then shows Core metadata and an accessible Download without fetching content', async () => {
    let resolve!: (value: typeof file) => void;
    vi.mocked(getArtifact).mockReturnValue(
      new Promise((done) => {
        resolve = done;
      }),
    );
    render(<ArtifactView tenantId="tenant" sessionId="s1" artifactId="id" />);
    expect(screen.getByText('Preparing file…')).toBeInTheDocument();
    expect(screen.getByRole('group')).toHaveAttribute('aria-busy', 'true');
    await act(async () => resolve(file));
    expect(screen.getByText('Markdown · 18.4 KB')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Download scrum-project-plan.md' }),
    ).toBeEnabled();
    expect(downloadArtifact).not.toHaveBeenCalled();
  });

  it('keeps canonical metadata during download, prevents duplicates and retries failures locally', async () => {
    let reject!: (value: Error) => void;
    vi.mocked(downloadArtifact).mockReturnValueOnce(
      new Promise((_, fail) => {
        reject = fail;
      }),
    );
    render(<ArtifactView tenantId="tenant" sessionId="s1" artifactId="id" />);
    const button = await screen.findByRole('button', {
      name: 'Download scrum-project-plan.md',
    });
    fireEvent.click(button);
    fireEvent.click(button);
    expect(button).toBeDisabled();
    expect(screen.getByRole('group')).toHaveAttribute('aria-busy', 'true');
    expect(downloadArtifact).toHaveBeenCalledTimes(1);
    await act(async () => reject(new Error('/private/secret uuid')));
    expect(screen.getByText('scrum-project-plan.md')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent(
      'Download failed. Try again.',
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Retry download scrum-project-plan.md',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Download scrum-project-plan.md' }),
      ).toBeEnabled(),
    );
    expect(downloadArtifact).toHaveBeenCalledTimes(2);
  });

  it('fails without UUID or raw exception fallback and retries metadata', async () => {
    vi.mocked(getArtifact).mockRejectedValueOnce(new Error('/private/uuid'));
    render(<ArtifactView tenantId="tenant" sessionId="s1" artifactId="secret-id" />);
    await screen.findByText('File unavailable');
    expect(screen.queryByText(/secret-id|private|uuid/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Retry file' }));
    await screen.findByRole('button', {
      name: 'Download scrum-project-plan.md',
    });
    expect(getArtifact).toHaveBeenCalledTimes(2);
  });

  it('retains the full long accessible filename and supports read-only session downloads', async () => {
    const longName = 'quarterly-review-'.repeat(20) + '.md';
    vi.mocked(getArtifact).mockResolvedValue({
      ...file,
      displayName: longName,
    });
    render(<ArtifactView tenantId="tenant" sessionId="ended-session" artifactId="id" />);
    const button = await screen.findByRole('button', {
      name: `Download ${longName}`,
    });
    expect(screen.getByText(longName)).toHaveAttribute('title', longName);
    fireEvent.click(button);
    await waitFor(() =>
      expect(downloadArtifact).toHaveBeenCalledWith(
        'ended-session',
        'id',
        expect.any(AbortSignal),
        'tenant',
      ),
    );
  });

  it('ignores late metadata after switching sessions', async () => {
    let resolveOld!: (value: typeof file) => void;
    vi.mocked(getArtifact)
      .mockReturnValueOnce(
        new Promise((resolve) => {
          resolveOld = resolve;
        }),
      )
      .mockResolvedValueOnce({
        ...file,
        sessionId: 's2',
        displayName: 'current-session.md',
      });
    const { rerender } = render(
      <ArtifactView tenantId="tenant" sessionId="s1" artifactId="id" />,
    );
    rerender(<ArtifactView tenantId="tenant" sessionId="s2" artifactId="id" />);
    await screen.findByRole('button', { name: 'Download current-session.md' });
    await act(async () => resolveOld(file));
    expect(screen.queryByText(file.displayName)).toBeNull();
    fireEvent.click(
      screen.getByRole('button', { name: 'Download current-session.md' }),
    );
    await waitFor(() =>
      expect(downloadArtifact).toHaveBeenCalledWith(
        's2',
        'id',
        expect.any(AbortSignal),
        'tenant',
      ),
    );
  });

  it('cancels an in-flight download on unmount', async () => {
    const { unmount } = render(<ArtifactView tenantId="tenant" sessionId="s1" artifactId="id" />);
    vi.mocked(downloadArtifact).mockImplementation(async (_, __, signal) => {
      await new Promise<void>((resolve) =>
        signal!.addEventListener('abort', () => resolve()),
      );
    });
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Download scrum-project-plan.md',
      }),
    );
    const signal = vi.mocked(downloadArtifact).mock.calls[0][2]!;
    unmount();
    expect(signal.aborted).toBe(true);
  });

  it('uses safe friendly labels and binary size units', () => {
    expect(
      [
        'application/pdf',
        'text/markdown; charset=utf-8',
        'text/csv',
        'application/json',
        'text/plain',
        'image/png',
        'application/zip',
        'unknown',
      ].map(artifactKind),
    ).toEqual([
      'PDF',
      'Markdown',
      'CSV',
      'JSON',
      'Text',
      'Image',
      'ZIP',
      'File',
    ]);
    expect([0, 820, 18842, 1887436].map(artifactSize)).toEqual([
      '0 B',
      '820 B',
      '18.4 KB',
      '1.8 MB',
    ]);
  });
});

it('withholds metadata and download requests when no organization scope is supplied', async () => {
  render(<ArtifactView sessionId="s1" artifactId="id" />);
  expect(await screen.findByText('File unavailable')).toBeVisible();
  expect(screen.queryByText(file.displayName)).not.toBeInTheDocument();
  expect(getArtifact).not.toHaveBeenCalled(); expect(downloadArtifact).not.toHaveBeenCalled();
});
