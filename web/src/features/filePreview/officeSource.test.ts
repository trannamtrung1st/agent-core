import { beforeEach, expect, it, vi } from 'vitest';
import { readOfficePreview } from './officeSource';
import { identityReadResponse } from '../../services/identity';
import { testPolicy } from './previewTestPolicy';
import type { FileRef } from './fileSource';
vi.mock('../../services/identity', async original => ({ ...await original<typeof import('../../services/identity')>(), identityReadResponse: vi.fn() }));
beforeEach(() => vi.mocked(identityReadResponse).mockReset());
const signal = () => new AbortController().signal;
const sources: FileRef[] = [
  { kind: 'attachment', tenantId: 'tenant', sessionId: 's', attachmentId: 'a' },
  { kind: 'tenantWorkspace', owner: { kind: 'home', tenantId: 'tenant', instanceId: 'i' }, instanceId: 'i', path: '/home/x.xlsx', metadata: { displayName: 'x.xlsx', contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', byteSize: 1 } },
  { kind: 'tenantResource', tenantId: 'tenant', instanceId: 'i', resourceKey: 'resource', metadata: { displayName: 'x.docx', contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', byteSize: 1 } },
];
it.each([
  ['invalid_office_package', 400, 'malformed or unsafe'],
  ['office_feature_unsupported', 400, 'original file is still available'],
  ['office_operation_unsupported', 400, 'original file is still available'],
  ['office_processing_limit', 400, 'processing limit'],
  ['preview_structure_limit', 413, 'smaller range'],
  ['preview_too_large', 413, 'Download the original'],
])('classifies %s safely across tenant DOCX and XLSX sources', async (code, status, copy) => {
  for (const source of sources) {
    vi.mocked(identityReadResponse).mockResolvedValueOnce(new Response(JSON.stringify({ title: code, detail: 'UNTRUSTED exception secret' }), { status }));
    await expect(readOfficePreview(source, signal(), testPolicy)).rejects.toMatchObject({ code, message: expect.stringContaining(copy) });
  }
});
it.each([[403, 'preview_forbidden'], [404, 'preview_unavailable'], [409, 'preview_stale'], [412, 'preview_stale']])('retains status %s', async (status, code) => {
  vi.mocked(identityReadResponse).mockResolvedValueOnce(new Response('', { status }));
  await expect(readOfficePreview(sources[2], signal(), testPolicy)).rejects.toMatchObject({ code });
});
it.each(['{"title":"unknown","detail":"UNTRUSTED"}', '{broken', 'x'.repeat(5000)])('ignores unrecognized or oversized problem bodies', async body => {
  vi.mocked(identityReadResponse).mockResolvedValueOnce(new Response(body, { status: 400 }));
  await expect(readOfficePreview(sources[0], signal(), testPolicy)).rejects.toMatchObject({ code: 'preview_failed' });
});
