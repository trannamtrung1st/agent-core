import { Blob as NodeBlob } from 'node:buffer';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { fileReadLost, readFile, resolveFile, sourcePath, type FileRef } from './fileSource';
import { readOfficePreview } from './officeSource';
import { testPolicy } from './previewTestPolicy';
const sources: FileRef[] = [
  { kind: 'tenantWorkspace', owner: { tenantId: 'alpha', kind: 'home', instanceId: 'agent' }, instanceId: 'agent', path: '/home/file.txt', metadata: { displayName: 'file.txt', contentType: 'text/plain', byteSize: 3 } },
  { kind: 'tenantWorkspace', owner: { tenantId: 'alpha', kind: 'working', sessionId: 'session' }, instanceId: 'agent', path: '/working/file.txt', metadata: { displayName: 'file.txt', contentType: 'text/plain', byteSize: 3 } },
];
const fetchMock = vi.fn(); const signal = () => new AbortController().signal;
beforeEach(() => { vi.clearAllMocks(); vi.stubGlobal('fetch', fetchMock); vi.stubGlobal('Blob', NodeBlob); });
afterEach(() => { vi.unstubAllGlobals(); });
it.each(sources)('refreshes current workspace metadata and downloads bounded actual bytes through tenant auth: $path', async file => {
  if (file.kind !== 'tenantWorkspace') throw new Error('Fixture source');
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ agentInstanceId: 'agent', logicalPath: file.path, contentType: 'text/plain', byteSize: 3 })))
    .mockResolvedValueOnce(new Response('abcd'));
  const metadata = await resolveFile(file, signal());
  expect(await (await readFile(file, metadata, 4, signal())).text()).toBe('abcd');
  expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([sourcePath(file) + '/metadata?' + new URLSearchParams({ path: file.path }), sourcePath(file) + '/content?' + new URLSearchParams({ path: file.path })]);
  expect(fetchMock.mock.calls.every(([, options]) => options.credentials === 'same-origin' && options.cache === 'no-store')).toBe(true);

});
it.each([401, 403, 404])('retires a workspace source on %s without legacy fallback', async status => {
  fetchMock.mockResolvedValueOnce(new Response('{}', { status })); let error: unknown;
  try { await resolveFile(sources[0], signal()); } catch (failure) { error = failure; }
  expect(fileReadLost(error)).toBe(true);
});
it.each(sources)('routes bounded Office windows to the exact workspace path: $path', async file => {
  if (file.kind !== 'tenantWorkspace') throw new Error('Fixture source');
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ format: 'xlsx', sha256: 'a'.repeat(64), warnings: [], sheets: ['Sheet'], rows: [[{ address: 'A1', value: '00123', valueStatus: 'literal' }]] })));
  expect((await readOfficePreview(file, signal(), testPolicy, 'Sheet', 'A1:A1')).rows![0][0].value).toBe('00123');
  expect(fetchMock.mock.calls[0][0]).toBe(sourcePath(file) + '/office-preview?' + new URLSearchParams({ path: file.path, sheet: 'Sheet', range: 'A1:A1' }));

});
