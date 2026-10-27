import { IdentityCompletionError, IdentityError, identityRequest, tenantApi, type TenantContext } from './identity';
export type EvaluationCheck = { checkId: string; title: string; kind: number; required: boolean; predicate: number; expected?: string | null; prompt?: string | null; rubric?: string | null; modelOfferingId?: string | null; script?: { scriptId: string; version: number; contentHash: string } | null };
export type EvaluationSuite = { tenantId: string; suiteId: string; version: number; name: string; checks: EvaluationCheck[]; contentHash: string; draftId: string | null; archived: boolean; createdAt: string };
export type ScriptVersion = { tenantId: string; scriptId: string; version: number; name: string; kind: number; predicate: number; expected: string | null; body: string | null; hooks: number[]; contentHash: string; archived: boolean; sandboxProfileId?: string | null; sandboxTimeoutSeconds?: number; sandboxInput?: number };
export type EvaluationFinding = { checkId: string; kind: number; required: boolean; outcome: number; reasonCode: string; summary: string; reviewerId: string | null; reviewedAt: string | null; modelIdentity: string | null; sandboxExecutionId?: string | null };
export type EvaluationAttempt = { attemptId: string; tenantId: string; subjectKind: number; subjectId: string; subjectRevision: number; sourceFingerprint: string; suiteId: string; suiteVersion: number; suiteHash: string; hook: number; actorId: string; dedupKey: string; status: number; outcome: number | null; findings: EvaluationFinding[]; createdAt: string; updatedAt: string; revision: number; commitSequence: number | null; reasonCode: string | null };
export type EvaluationPage<T> = { items: T[]; total: number; page: number; pageSize: number };
export type EvaluationBinding = { suiteId: string; version: number; hook: number; policy: number };
export const outcomeLabel = (value: number | null) => value === 0 ? 'Passed' : value === 1 ? 'Failed' : 'Inconclusive';
export const statusLabel = (value: number) => ['Pending', 'Running', 'Completed', 'Cancelled', 'Unknown — inspect status'][value] ?? 'Unknown';
export const hookLabels = ['Before Run', 'After Run', 'On failure', 'Manual', 'Publication'];
export const predicates = ['Tool is offered', 'Tool is not offered', 'Resource is bound', 'Schedule is permitted', 'External action is denied', 'Run completed', 'Output is present', 'Outcome completed without attention', 'Input is present'];
export const evaluatorLabels = ['Script', 'Model judgment', 'Human assessment'];
export const freshTenantRevision = async (tenant: string, signal?: AbortSignal) => (await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal })).revision;
export function latestCheck(snapshot: { draft: { revision: number }; attempts: EvaluationAttempt[] }, suite: EvaluationSuite, check: EvaluationCheck) {
  const result = snapshot.attempts.filter(a => a.suiteId === suite.suiteId && (a.commitSequence ?? 0) > 0 && a.findings.some(f => f.checkId === check.checkId)).sort((a, b) => (b.commitSequence ?? 0) - (a.commitSequence ?? 0) || a.attemptId.localeCompare(b.attemptId))[0];
  const stale = !!result && (result.subjectRevision !== snapshot.draft.revision || result.suiteVersion !== suite.version || result.suiteHash !== suite.contentHash);
  const finding = result?.findings.find(f => f.checkId === check.checkId);
  return { result, stale, finding, label: !result ? 'Not run' : stale ? 'Stale — run again' : ![0, 2].includes(result.status) ? 'Inconclusive' : outcomeLabel(finding?.outcome ?? null) };
}

export async function evaluationCatalogWrite<T>(path: string, body: unknown, signal: AbortSignal): Promise<T> {
  try { return await identityRequest<T>(path, { method: 'POST', body, signal }); }
  catch (error) {
    if (signal.aborted || error instanceof IdentityError && error.status < 500) throw error;
    throw new IdentityCompletionError(false);
  }
}
