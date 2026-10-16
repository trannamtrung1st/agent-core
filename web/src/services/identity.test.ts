import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { identityRequest, IdentityError, resetIdentityCsrf } from './identity';
const response = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
describe('canonical identity requests', () => {
  const fetchMock = vi.fn<typeof fetch>();
  beforeEach(() => { resetIdentityCsrf(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); });
  afterEach(() => vi.unstubAllGlobals());
  it('uses same-origin cookies and fresh in-memory CSRF after identity changes', async () => {
    fetchMock.mockResolvedValueOnce(response(200, { token: 'first' })).mockResolvedValueOnce(response(204));
    await identityRequest('/api/v2/auth/logout', { method: 'POST' });
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ credentials: 'same-origin', headers: { 'X-AgentCore-CSRF': 'first' } });
    resetIdentityCsrf(); fetchMock.mockResolvedValueOnce(response(200, { token: 'second' })).mockResolvedValueOnce(response(204));
    await identityRequest('/api/v2/auth/login', { method: 'POST', body: { login: 'someone@example.test' } });
    expect(fetchMock.mock.calls[3][1]).toMatchObject({ headers: { 'X-AgentCore-CSRF': 'second' } });
  });
  it('never automatically replays a mutation after CSRF or concurrency rejection', async () => {
    fetchMock.mockResolvedValueOnce(response(200, { token: 'one' })).mockResolvedValueOnce(response(409, { reasonCode: 'stale_revision' }));
    await expect(identityRequest('/api/v2/tenants/t/roles', { method: 'POST', body: {} })).rejects.toMatchObject({ status: 409, reasonCode: 'stale_revision' });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(new IdentityError(409, 'last_owner').message).toContain('at least one active owner');
  });
  it('does not send a mutation whose caller was cancelled during CSRF preparation', async () => {
    const controller = new AbortController(); controller.abort();
    fetchMock.mockResolvedValueOnce(response(200, { token: 'one' }));
    await expect(identityRequest('/api/v2/auth/login', { method: 'POST', signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
  it('does not label a successful mutation unsaved when its following read fails', async () => {
    fetchMock.mockResolvedValueOnce(response(200, { token: 'one' })).mockResolvedValueOnce(response(204))
      .mockResolvedValueOnce(response(503, { reasonCode: 'service_unavailable' }));
    await identityRequest('/api/v2/tenants/t/role-assignments', { method: 'POST', body: {} });
    await expect(identityRequest('/api/v2/tenants/t/members')).rejects.toMatchObject({ status: 503, message: 'The service is temporarily unavailable. Check the latest information before retrying.' });
    expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
  });
});
