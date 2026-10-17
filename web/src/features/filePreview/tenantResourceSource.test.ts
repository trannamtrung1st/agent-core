import { webcrypto } from 'node:crypto';
import { Blob as NodeBlob } from 'node:buffer';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ownerFetch } from '../../services/api';
import { fileReadLost, readFile, resolveFile, sourcePath, type FileRef } from './fileSource';
import { readOfficePreview } from './officeSource';
import { loadPreviewOptions } from './previewOptions';
import { testPolicy } from './previewTestPolicy';
vi.mock('../../services/api', () => ({ ownerFetch: vi.fn() }));
const fetchMock = vi.fn<typeof fetch>(); const signal = () => new AbortController().signal;
const metadata = { displayName: 'old.txt', contentType: 'text/plain', byteSize: 3 };
const file: FileRef = { kind: 'tenantResource', tenantId: 'organization', instanceId: 'agent', resourceKey: 'instance:file', metadata };
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
const hash = async (bytes: Uint8Array) => Array.from(new Uint8Array(await webcrypto.subtle.digest('SHA-256', bytes)), byte => byte.toString(16).padStart(2, '0')).join('');
beforeEach(() => { vi.mocked(ownerFetch).mockReset(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); vi.stubGlobal('crypto', webcrypto); vi.stubGlobal('Blob', NodeBlob); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });
it('refreshes exact scoped metadata and verifies the authorized bounded content with no owner fallback', async () => {
  const sha256 = await hash(new TextEncoder().encode('new'));
  fetchMock.mockResolvedValueOnce(json({ agent: { instance: { instanceId: 'agent' } }, item: { resource: { key: 'instance:file', logicalPath: 'references/new.txt', mediaType: 'text/plain', byteLength: 3, contentSha256: sha256 } } })).mockResolvedValueOnce(new Response('new'));
  const fresh = await resolveFile(file, signal()); expect(fresh).toEqual({ displayName: 'new.txt', contentType: 'text/plain', byteSize: 3, sha256 });
  expect((await readFile(file, fresh, 3, signal())).size).toBe(3); expect(sourcePath(file)).toBe('/api/v2/tenants/organization/agents/agent/resources/instance%3Afile');
  expect(fetchMock.mock.calls.map(([path]) => String(path))).toEqual([sourcePath(file) + '/editor', sourcePath(file) + '/content']);
  expect(fetchMock.mock.calls.every(([, options]) => options?.credentials === 'same-origin' && options.cache === 'no-store')).toBe(true); expect(ownerFetch).not.toHaveBeenCalled();
});
it('rejects wrong metadata, changed bytes and over-limit streams without releasing stale content', async () => {
  fetchMock.mockResolvedValueOnce(json({ agent: { instance: { instanceId: 'other' } }, item: { resource: { key: 'instance:file' } } }));
  await expect(resolveFile(file, signal())).rejects.toMatchObject({ code: 'preview_unavailable' });
  fetchMock.mockResolvedValueOnce(new Response('bad'));
  await expect(readFile(file, { ...metadata, sha256: await hash(new TextEncoder().encode('new')) }, 3, signal())).rejects.toMatchObject({ code: 'preview_stale' });
  let cancelled = false; fetchMock.mockResolvedValueOnce(new Response(new ReadableStream({ start(c) { c.enqueue(new TextEncoder().encode('oversized')); }, cancel() { cancelled = true; } })));
  await expect(readFile(file, metadata, 3, signal())).rejects.toMatchObject({ code: 'preview_too_large' }); expect(cancelled).toBe(true); expect(ownerFetch).not.toHaveBeenCalled();
});
it.each([401, 403, 404])('maps %s to definitive read loss without falling back to legacy authentication', async status => {
  const expired = vi.fn(); window.addEventListener('agentcore:authentication-expired', expired);
  try { fetchMock.mockResolvedValueOnce(json({}, status)); let denied: unknown; try { await resolveFile(file, signal()); } catch (error) { denied = error; } expect(fileReadLost(denied)).toBe(true); expect(expired).toHaveBeenCalledTimes(status === 401 ? 1 : 0); expect(ownerFetch).not.toHaveBeenCalled(); }
  finally { window.removeEventListener('agentcore:authentication-expired', expired); }
});
it('reads scoped Office windows and uncached policy with each owner signal and preserves safe recovery', async () => {
  fetchMock.mockResolvedValueOnce(json(testPolicy)).mockResolvedValueOnce(json(testPolicy)).mockResolvedValueOnce(json({ format: 'xlsx', sha256: 'a'.repeat(64), warnings: [], sheets: ['Review'], rows: [[{ address: 'A1', value: '00123', valueStatus: 'literal' }]] })).mockResolvedValueOnce(json({ title: 'preview_structure_limit' }, 413));
  await loadPreviewOptions('organization', signal()); await loadPreviewOptions('organization', signal());
  const result = await readOfficePreview(file, signal(), testPolicy, 'Review', 'A1:A1'); expect(result.rows![0][0].value).toBe('00123');
  expect(String(fetchMock.mock.calls[2][0])).toBe(sourcePath(file) + '/office-preview?sheet=Review&range=A1%3AA1');
  await expect(readOfficePreview(file, signal(), testPolicy, 'Review', 'A1:A999')).rejects.toMatchObject({ code: 'preview_structure_limit' });
  expect(fetchMock.mock.calls.slice(0, 2).every(([path]) => String(path) === '/api/v2/tenants/organization/file-preview/options')).toBe(true); expect(ownerFetch).not.toHaveBeenCalled();
});
