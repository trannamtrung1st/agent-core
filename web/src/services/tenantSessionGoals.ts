import { IdentityError, identityRequest, tenantApi } from './identity';
export type GoalVerification = { kind: number; suiteId?: string | null; suiteVersion?: number | null; suiteHash?: string | null };
export type GoalCheckpointPlan = { checkpointId: string; title: string; expectedOutcome: string; required: boolean; dependencies: string[]; verification: GoalVerification[] };
export type GoalPlan = { title: string; objective: string; successCriteria: string; checkpoints: GoalCheckpointPlan[]; deadlineAt?: string | null; requireFinalAcceptance: boolean; autoContinuation: { enabled: boolean; maxRuns: number; until: string | null } };
export type GoalEvidence = { kind: number; sourceId: string; runId?: string | null; principalId?: string | null; acceptedAt?: string | null; reason?: string | null };
export type SessionGoal = { goalId: string; tenantId: string; sessionId: string; spaceId: string; revision: number; planRevision: number; status: number; plan: GoalPlan; pendingPlan: GoalPlan | null; autoRunsUsed: number; finalAcceptedBy: string | null; checkpoints: { checkpointId: string; status: number; note: string | null; sourceRunId: string | null; evidence: GoalEvidence[] | null }[] };
export type GoalsView = { goals: { items: SessionGoal[]; total: number; page: number; pageSize: number }; focusedGoalId: string | null; focusRevision: number; sessionRevision: number; securityRevision: number; canManage: boolean; canAccept: boolean; currentSpaceId: string | null };
export type GoalCommand = { kind: number; requestKey: string; expectedSecurityRevision: number; expectedGoalRevision: number; expectedPlanRevision: number; plan?: GoalPlan; checkpointId?: string; note?: string; action?: string; evidence?: GoalEvidence; confirm?: boolean };
export const goalsApi = (tenant: string, session: string, suffix = '') => tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/goals${suffix}`);
export const goalStatus = ['Proposed', 'Active', 'Paused', 'Completed', 'Cancelled'];
export const checkpointStatus = ['Pending', 'Reported', 'Blocked', 'Verification pending', 'Verified', 'Skipped'];
export const verificationLabel = ['Exact Evaluation pass', 'Artifact publication receipt', 'Human acceptance'];
export async function readGoals(tenant: string, session: string, signal?: AbortSignal): Promise<GoalsView> {
  const view = await identityRequest<GoalsView>(goalsApi(tenant, session), { signal });
  if (!view.goals || view.goals.items.some(g => g.tenantId !== tenant || g.sessionId !== session)) throw new Error('Goal response does not match this conversation. Reload it.');
  return view;
}
export async function writeGoal(tenant: string, session: string, goalId: string, command: GoalCommand, signal: AbortSignal) {
  const suffix = command.kind === 0 ? '' : `/${goalId}/${command.kind === 1 ? 'plan' : command.kind === 2 ? 'plan-decision' : command.kind === 5 ? 'acceptance' : command.kind === 6 ? 'control' : `checkpoints/${command.checkpointId}/${command.kind === 3 ? 'progress' : 'verify'}`}`;
  try {
    return await identityRequest<{ goal: SessionGoal }>(goalsApi(tenant, session, suffix), { method: command.kind === 1 ? 'PUT' : 'POST', body: command.kind === 0 ? { goalId, command } : command, signal });
  } catch (error) {
    if (signal.aborted || error instanceof IdentityError && error.status < 500) throw error;
    // Outcome uncertain: look up the exact saved request. Never issue another mutation here.
    const receipt = await identityRequest<{ goal: SessionGoal } | null>(goalsApi(tenant, session, `/recovery?requestKey=${encodeURIComponent(command.requestKey)}`), { signal });
    if (receipt?.goal.goalId === goalId) return receipt;
    throw new Error('The outcome is unconfirmed. Reload Goal activity before making another change.');
  }
}
