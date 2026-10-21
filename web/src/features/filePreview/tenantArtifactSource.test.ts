import { webcrypto } from 'node:crypto';
import { Blob as NodeBlob } from 'node:buffer';
import { afterEach, expect, it, vi } from 'vitest';
import { readFile, resolveFile, sourcePath, type FileRef } from './fileSource';
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });
it('refreshes owned Artifact metadata and reads exact tenant bytes without owner fallback', async () => {
  vi.stubGlobal('crypto', webcrypto); vi.stubGlobal('Blob', NodeBlob);
  const fetchMock = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ sessionId: 'session', artifactId: 'file', displayName: 'exact.txt', contentType: 'text/plain', byteSize: 3 })))
    .mockResolvedValueOnce(new Response('abc'));
  vi.stubGlobal('fetch', fetchMock);
  const ref: FileRef = { kind: 'artifact', tenantId: 'tenant', sessionId: 'session', artifactId: 'file' };
  const signal = new AbortController().signal; const metadata = await resolveFile(ref, signal);
  expect(sourcePath(ref)).toBe('/api/v2/tenants/tenant/sessions/session/artifacts/file');
  expect(await (await readFile(ref, metadata, 3, signal)).text()).toBe('abc');
  expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([sourcePath(ref), sourcePath(ref) + '/content']);

});

 it('refreshes tenant Attachment metadata and original bytes without owner fallback', async () => {
   vi.stubGlobal('crypto', webcrypto); vi.stubGlobal('Blob', NodeBlob);
   const fetchMock = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ sessionId: 's', attachmentId: 'a', displayName: 'input.txt', contentType: 'text/plain', byteSize: 3 }))).mockResolvedValueOnce(new Response('abc'));
   vi.stubGlobal('fetch', fetchMock); const ref: FileRef = { kind: 'attachment', tenantId: 't', sessionId: 's', attachmentId: 'a' }; const signal = new AbortController().signal;
   const metadata = await resolveFile(ref, signal); expect(await (await readFile(ref, metadata, 3, signal)).text()).toBe('abc');
   expect(fetchMock.mock.calls.map(([url]) => url)).toEqual(['/api/v2/tenants/t/sessions/s/attachments/a', '/api/v2/tenants/t/sessions/s/attachments/a/content']);
 });
