import { afterEach, beforeEach, expect, it, vi } from 'vitest';
const identity = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./identity', () => ({ identityRequest: identity.request, tenantApi: (id: string, suffix: string) => `/api/v2/tenants/${id}${suffix}` }));
const tenant = '11111111-1111-1111-1111-111111111111';
beforeEach(() => { vi.resetModules(); identity.request.mockReset(); window.history.replaceState(null, '', `/t/${tenant}`); });
afterEach(() => { window.history.replaceState(null, '', '/'); });
it('binds the document to one tenant and retires outstanding private requests', async () => {
  const service = await import('./tenantChat');
  let release!: (value: unknown) => void;
  identity.request.mockImplementationOnce(() => new Promise(resolve => { release = resolve; }));
  const pending = service.tenantChatAccess();
  const options = identity.request.mock.calls[0][1];
  service.retireTenantChatRequests(); release({ revision: 3 });
  await expect(pending).rejects.toThrow(); expect(options.signal.aborted).toBe(true);
  window.history.replaceState(null, '', '/t/22222222-2222-2222-2222-222222222222');
  identity.request.mockResolvedValueOnce({ revision: 4 }); await service.tenantChatAccess();
  expect(identity.request.mock.lastCall?.[0]).toBe(`/api/v2/tenants/${tenant}/access/me`);
});
it('requires a current candidate, uses scoped witnesses, and clears permission hints at retirement', async () => {
  const service = await import('./tenantChat');
  await expect(service.createTenantChat('agent')).rejects.toThrow('Select an available agent');
  identity.request.mockResolvedValueOnce({ items: [{ instanceId: 'agent', revision: 7 }], canCreateSessions: true });
  await service.tenantChatCandidates(0, 'Alex & team');
  expect(identity.request.mock.lastCall?.[0]).toContain('q=Alex%20%26%20team');
  identity.request.mockResolvedValueOnce({ revision: 12 }).mockResolvedValueOnce({ sessionId: 'saved' })
    .mockResolvedValueOnce({ sessionId: 'saved' }).mockResolvedValueOnce({ sessionId: 'saved', canManage: true });
  await service.createTenantChat('agent', { offeringId: 'dca30000-0000-4000-8000-000000000002', reasoningEffort: null });
  expect(identity.request.mock.calls[2][0]).toBe(`/api/v2/tenants/${tenant}/sessions`);
  expect(identity.request.mock.calls[2][1].body).toMatchObject({ agentInstanceId: 'agent', expectedRevision: 12, expectedInstanceRevision: 7, modelPreference: { mode: 'fixed', offeringId: 'dca30000-0000-4000-8000-000000000002' }, mode: 'text' });
  expect(service.tenantMayManageSession('saved')).toBe(true);
  service.retireTenantChatRequests(); expect(service.tenantMayManageSession('saved')).toBe(false);
  await expect(service.createTenantChat('agent')).rejects.toThrow('Select an available agent');
});
