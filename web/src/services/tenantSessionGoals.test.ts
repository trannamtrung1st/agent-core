import { beforeEach, describe, expect, it, vi } from 'vitest';
import { IdentityError, identityRequest } from './identity';
import { readGoals, writeGoal, type GoalCommand } from './tenantSessionGoals';
vi.mock('./identity', async importOriginal => ({ ...await importOriginal<typeof import('./identity')>(), identityRequest: vi.fn() }));
const call = vi.mocked(identityRequest);
const command: GoalCommand = { kind: 3, requestKey: 'stable-request', expectedSecurityRevision: 7, expectedGoalRevision: 2, expectedPlanRevision: 1, checkpointId: 'checkpoint', note: 'Progress' };
beforeEach(() => call.mockReset());
describe('Session Goal authoritative recovery', () => {
  it('reads back an uncertain write by its exact request key without replaying the mutation', async () => {
    const saved = { goal: { goalId: 'goal' } }; call.mockRejectedValueOnce(new TypeError('connection lost')).mockResolvedValueOnce(saved);
    expect(await writeGoal('tenant', 'session', 'goal', command, new AbortController().signal)).toEqual(saved);
    expect(call.mock.calls.map(([path, options]) => [path, options?.method ?? 'GET'])).toEqual([
      ['/api/v2/tenants/tenant/sessions/session/goals/goal/checkpoints/checkpoint/progress', 'POST'],
      ['/api/v2/tenants/tenant/sessions/session/goals/recovery?requestKey=stable-request', 'GET']]);
  });
  it('does not retry a known stale mutation or an unresolved uncertain outcome', async () => {
    call.mockRejectedValueOnce(new IdentityError(409, 'stale_goal_revision'));
    await expect(writeGoal('tenant', 'session', 'goal', command, new AbortController().signal)).rejects.toBeInstanceOf(IdentityError);
    expect(call).toHaveBeenCalledTimes(1); call.mockReset(); call.mockRejectedValueOnce(new TypeError('offline')).mockResolvedValueOnce(null);
    await expect(writeGoal('tenant', 'session', 'goal', command, new AbortController().signal)).rejects.toThrow('unconfirmed'); expect(call).toHaveBeenCalledTimes(2);
  });
  it('rejects private data projected for another Session', async () => {
    call.mockResolvedValueOnce({ goals: { items: [{ tenantId: 'tenant', sessionId: 'another-session' }] } });
    await expect(readGoals('tenant', 'session')).rejects.toThrow('does not match');
  });
});
