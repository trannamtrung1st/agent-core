import { beforeEach, describe, expect, it, vi } from 'vitest';
import { readFile, resolveFile, sourcePath } from './fileSource';
import { ownerFetch } from '../../services/api';
vi.mock('../../services/api', () => ({ ownerFetch: vi.fn() }));
const ref = { kind: 'attachment' as const, sessionId: 's', attachmentId: 'a' };
const signal = () => new AbortController().signal;
beforeEach(() => vi.mocked(ownerFetch).mockReset());
describe('Owner protected bounded reads', () => {
  it('maps closed source identities to only current protected routes', () => {
    expect(
      sourcePath({
        kind: 'resource',
        instanceId: 'i',
        resourceKey: 'r/a',
        metadata: { displayName: 'x', contentType: 'text/plain', byteSize: 1 },
      }),
    ).toBe('/api/v2/admin/agent-instances/i/resources/r%2Fa');
    expect(sourcePath(ref)).toBe('/api/v2/sessions/s/attachments/a');
  });
  it('rejects wrong metadata identity, revoked access and stale source length', async () => {
    vi.mocked(ownerFetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          sessionId: 'other',
          attachmentId: 'a',
          displayName: 'x',
          contentType: 'text/plain',
          byteSize: 1,
        }),
      ),
    );
    await expect(resolveFile(ref, signal())).rejects.toMatchObject({
      code: 'preview_unavailable',
    });
    vi.mocked(ownerFetch).mockResolvedValueOnce(
      new Response('', { status: 403 }),
    );
    await expect(
      readFile(
        ref,
        { displayName: 'x', contentType: 'text/plain', byteSize: 1 },
        8,
        signal(),
      ),
    ).rejects.toMatchObject({ code: 'preview_forbidden' });
    vi.mocked(ownerFetch).mockResolvedValueOnce(new Response('longer'));
    await expect(
      readFile(
        ref,
        { displayName: 'x', contentType: 'text/plain', byteSize: 1 },
        8,
        signal(),
      ),
    ).rejects.toMatchObject({ code: 'preview_stale' });
  });
  it('stops a chunked body at the limit, without trusting declared length', async () => {
    let cancelled = false;
    const body = new ReadableStream({
      start(c) {
        c.enqueue(new TextEncoder().encode('too long'));
      },
      cancel() {
        cancelled = true;
      },
    });
    vi.mocked(ownerFetch).mockResolvedValueOnce(new Response(body));
    await expect(
      readFile(
        ref,
        { displayName: 'x', contentType: 'text/plain', byteSize: 1 },
        4,
        signal(),
      ),
    ).rejects.toMatchObject({ code: 'preview_too_large' });
    expect(cancelled).toBe(true);
  });
});
