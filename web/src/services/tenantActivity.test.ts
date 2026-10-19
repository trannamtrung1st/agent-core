import { beforeEach, expect, it, vi } from 'vitest';
import { identityRequest, IdentityCompletionError } from './identity';
import { tenantActivitySource } from './tenantActivity';
vi.mock('./identity', async original => ({ ...await original<object>(), identityRequest: vi.fn() }));
const tenant = 'tenant-a'; const instance = 'agent-a'; const source = tenantActivitySource(tenant, instance, 'person-a');
const row = { tenantId: tenant, agentInstanceId: instance, sessionId: 'child-a', title: 'Research task', mode: 'text', revision: 4, securityRevision: 7, createdAt: '2026-10-20T00:00:00Z', updatedAt: '2026-10-20T00:00:00Z', status: 'created', lifecycleStatus: 'active', archivedAt: null,
  agentId: 'assistant', agentVersion: 32, agentName: 'Riley', agentRole: 'Assistant', canManage: true, canContinue: true, origin: 'immediateBackground', surfaces: ['BackgroundWork'], initialRunId: null };
beforeEach(() => vi.clearAllMocks());
it('keeps tenant/person context, native paging and independently unavailable initial results', async () => {
  vi.mocked(identityRequest).mockResolvedValue({ items: [{ session: row, initialRun: null, initialTitle: 'Research task', completionDelivery: null }], page: 0, pageSize: 20, total: 21 });
  const page = await source.listBackground(instance);
  expect(identityRequest).toHaveBeenCalledWith('/api/v2/tenants/tenant-a/agents/agent-a/activity/background-sessions?page=0&pageSize=20');
  expect(page.nextCursor).toBe('1'); expect(page.items[0].initialRun).toBeNull(); expect(page.items[0].origin.initialAgentRunId).toBeNull(); expect(page.items[0].artifactCount).toBeNull();
  expect(source.readScope).toBe('tenant-a:person-a');
});
it('rejects foreign rows and malformed page cursors without any fallback', async () => {
  vi.mocked(identityRequest).mockResolvedValue({ items: [{ ...row, tenantId: 'foreign' }], page: 0, pageSize: 20, total: 1 });
  await expect(source.listSessions(instance)).rejects.toThrow('does not match');
  vi.mocked(identityRequest).mockClear(); await expect(source.listSessions(instance, 'invalid')).rejects.toThrow('page is invalid'); expect(identityRequest).not.toHaveBeenCalled();
});
it('posts only the observed continuation witnesses and refuses an unconfirmed identity', async () => {
  vi.mocked(identityRequest).mockResolvedValueOnce({ session: row, initialRun: null, initialTitle: 'Research task', completionDelivery: null });
  const item = await source.getBackground('child-a');
  vi.mocked(identityRequest).mockResolvedValueOnce({ sessionId: 'foreign' });
  await expect(source.continue(item)).rejects.toBeInstanceOf(IdentityCompletionError);
  expect(identityRequest).toHaveBeenLastCalledWith('/api/v2/tenants/tenant-a/sessions/child-a/continue-in-chat', { method: 'POST', body: { expectedRevision: 7, expectedSessionRevision: 4 } });
});
it('uses current human security and exact Run revision for cancellation, then reads the exact saved Run', async () => {
  vi.mocked(identityRequest).mockResolvedValueOnce({ revision: 9 }).mockResolvedValueOnce({ context: { runId: 'run-a' } }).mockResolvedValueOnce({ runId: 'run-a', sessionId: 'child-a', agentInstanceId: instance, context: { tenantId: tenant, activationId: 'activation-a' }, revision: 6, status: 'cancelled', activationKind: 'immediateBackground' });
  const result = await source.actions!.cancel({ agentRunId: 'run-a', sessionId: 'child-a', revision: 5 } as never);
  expect(identityRequest).toHaveBeenNthCalledWith(2, '/api/v2/tenants/tenant-a/agent-runs/run-a/cancel', { method: 'POST', body: { expectedRevision: 9, expectedRunRevision: 5 } });
  expect(identityRequest).toHaveBeenLastCalledWith('/api/v2/tenants/tenant-a/agents/agent-a/activity/sessions/child-a/runs/run-a'); expect(result.status).toBe('cancelled');
});
