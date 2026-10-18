import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf } from './identity';
import { mutateTenantWorkspace, readWorkspaceAccess, tenantWorkspaceApi, type TenantWorkspaceOwner } from './tenantWorkspace';
const owner: TenantWorkspaceOwner = { kind: 'working', tenantId: 'alpha', sessionId: 'session' };
const fetchMock = vi.fn();
beforeEach(() => { resetIdentityCsrf(); vi.clearAllMocks(); vi.stubGlobal('fetch', fetchMock); });
afterEach(() => { resetIdentityCsrf(); vi.unstubAllGlobals(); });
it('sends original supplied bytes once with CSRF and an exact current security witness', async () => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ token: 'local-test-token' }))).mockResolvedValueOnce(new Response(null, { status: 204 }));
  const bytes = new Blob(['exact supplied bytes']); await mutateTenantWorkspace(owner, '/working/試 nghiệm.txt', 9, new AbortController().signal, bytes);
  const [path, options] = fetchMock.mock.calls[1];
  expect(path).toBe(tenantWorkspaceApi(owner, '/content') + '?' + new URLSearchParams({ path: '/working/試 nghiệm.txt', expectedRevision: '9' }));
  expect(options).toMatchObject({ method: 'PUT', body: bytes, credentials: 'same-origin', cache: 'no-store', headers: { 'X-AgentCore-CSRF': 'local-test-token', 'Content-Type': 'application/octet-stream' } });
  expect(fetchMock).toHaveBeenCalledTimes(2);
});
it.each([409, 503, 202])('never replays a %s mutation response or treats an uncertain acknowledgement as completed', async status => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ token: 'local-test-token' })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ reasonCode: 'workspace_effect_unknown', mutationsMayHaveOccurred: true }), { status }));
  await expect(mutateTenantWorkspace(owner, '/working/file', 9, new AbortController().signal)).rejects.toMatchObject({ reasonCode: 'workspace_effect_unknown' });
  expect(fetchMock).toHaveBeenCalledTimes(2); expect(fetchMock.mock.calls[1][1].method).toBe('DELETE');
});
it('rejects an incoherent access projection without using the returned owner for any file operation', async () => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ workspace: { tenantId: 'beta', instanceId: 'agent', sessionId: 'session', logicalRoot: '/working' }, securityRevision: 1, instanceRevision: 1, canRead: true, canWrite: true, canDelete: true, readOnly: false })));
  await expect(readWorkspaceAccess(owner, new AbortController().signal)).rejects.toThrow('Current workspace access is unavailable'); expect(fetchMock).toHaveBeenCalledTimes(1);
});

it('accepts Session-bound hints without live Instance configuration and rejects cross-parent facts', async () => {
  const value = { workspace: { tenantId: 'alpha', instanceId: 'agent', sessionId: 'session', logicalRoot: '/working' }, securityRevision: 9, instanceRevision: null,
    instanceName: null, sessionTitle: 'Retained Session', canRead: false, canWrite: true, canDelete: false, readOnly: false };
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(value))).mockResolvedValueOnce(new Response(JSON.stringify({ ...value, instanceName: 'Private current persona', instanceRevision: 77 })));
  expect(await readWorkspaceAccess(owner, new AbortController().signal)).toEqual(value);
  await expect(readWorkspaceAccess(owner, new AbortController().signal)).rejects.toThrow('Current workspace access is unavailable');
});
