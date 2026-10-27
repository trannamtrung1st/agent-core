import { identityRequest, IdentityCompletionError, tenantApi } from './identity';
import type { ActivitySource, AgentRun, BackgroundSession, CursorPage, InstanceActivitySession } from './activitySource';
import type { CatalogItem } from './api';

type SessionRow = { tenantId: string; sessionId: string; agentInstanceId: string; title: string; mode: string; revision: number; createdAt: string; updatedAt: string;
  status: string; lifecycleStatus: string; archivedAt: string | null; agentId: string; agentVersion: number; agentName: string; agentRole: string;
  canManage: boolean; origin: string; surfaces: string[]; initialRunId: string | null; canContinue: boolean; securityRevision: number };
type RunRow = { goal?: AgentRun['goal']; verificationSummary?: AgentRun["verificationSummary"]; runId: string; sessionId: string; agentInstanceId: string; context: { activationId: string; tenantId: string }; status: string; revision: number;
  createdAt: string; updatedAt: string; activationKind: string; outputContract: string; modelDecision?: { basis: string; engineId: string; engineVersion: string; settingsRevision: number; preferenceSource: string; policyRevision: string; diagnosticCode: string | null } | null; reasoningEffort?: string | null; modelOfferingId: string; attemptCount: number; maxAttempts: number; progressSummary: string | null;
  objective: string | null; outcomeText: string | null; outcomeKind: string | null; failureCode: string | null; failureSummary: string | null; diagnosticId: string | null; canCancel: boolean; cancellationRequested: boolean };
type BackgroundRow = { session: SessionRow; initialRun: RunRow | null; initialTitle: string | null; completionDelivery: { status: string; targetSessionId: string | null; reason: string | null } | null };
type Page<T> = { items: T[]; total: number; page: number; pageSize: number; securityRevision: number };
const titleCase = (value: string) => value ? value[0].toUpperCase() + value.slice(1) : value;

export function tenantActivitySource(tenant: string, instance: string, principal: string): ActivitySource {
  const root = tenantApi(tenant, `/agents/${encodeURIComponent(instance)}/activity`);
  const request = <T,>(suffix: string) => identityRequest<T>(root + suffix);
  function session(row: SessionRow): InstanceActivitySession {
    if (row.tenantId !== tenant || row.agentInstanceId !== instance || !row.sessionId || !Array.isArray(row.surfaces)) throw new Error('The Activity response does not match this agent. Reload Activity.');
    const value: CatalogItem = { ...row, archived: row.archivedAt !== null, ended: row.status === 'ended', workspaceOwned: false, runtimeEpoch: 0 };
    return { session: value, origin: titleCase(row.origin), surfaces: row.surfaces };
  }
  function run(row: RunRow, expectedSession?: string): AgentRun {
    if (row.context?.tenantId !== tenant || row.agentInstanceId !== instance || expectedSession && row.sessionId !== expectedSession) throw new Error('The Run response does not match this context. Reload Activity.');
    return { goal: row.goal, verificationSummary: row.verificationSummary, evaluationUrl: `/t/${tenant}/admin/evaluations?subjectKind=1&subjectId=${row.runId}`, agentRunId: row.runId, sessionId: row.sessionId, activationId: row.context.activationId, activationKind: titleCase(row.activationKind),
      status: row.status, revision: row.revision, attemptCount: row.attemptCount, maxAttempts: row.maxAttempts, cancellationAvailable: row.canCancel, cancellationRequested: row.cancellationRequested,
      taskIntent: row.objective, contextProvenanceAvailable: false, progress: row.progressSummary, nextRetryAt: null, createdAt: row.createdAt, updatedAt: row.updatedAt, approval: null,
      outcome: row.outcomeKind ? { kind: titleCase(row.outcomeKind), summary: row.outcomeText ?? '', outcomeEntryId: null, attentionRequired: row.outcomeKind === 'needsAttention' } : null,
      failureCode: row.failureCode, failureSummary: row.failureSummary, diagnosticId: row.diagnosticId, knownEffectSummary: null, modelOfferingId: row.modelOfferingId, modelDecision: row.modelDecision, reasoningEffort: row.reasoningEffort,
      responseId: null, automationId: null, experienceId: null, sourceOccurrenceId: null };
  }
  function background(row: BackgroundRow): BackgroundSession {
    const projected = session(row.session);
    return { session: projected.session, origin: { kind: projected.origin, initialAgentRunId: row.session.initialRunId, parentSessionId: null, parentAgentRunId: null, automationId: null, occurrenceId: null, reportCompletion: !!row.completionDelivery && row.completionDelivery.status !== 'notRequested' },
      surfaces: row.session.surfaces, originalTitle: row.initialTitle, initialRun: row.initialRun ? run(row.initialRun, row.session.sessionId) : null, canContinueInChat: row.session.canContinue,
      artifactCount: null, artifactCountHasMore: false, completionDelivery: row.completionDelivery ? { ...row.completionDelivery, parentAgentRunId: null } : undefined,
      securityRevision: row.session.securityRevision };
  }
  async function page<T, U>(suffix: string, cursor: string | undefined, limit: number, project: (row: T) => U): Promise<CursorPage<U>> {
    const index = cursor ? Number(cursor) : 0;
    if (!Number.isSafeInteger(index) || index < 0 || index > 100_000) throw new Error('The Activity page is invalid. Reload Activity.');
    const value = await request<Page<T>>(`${suffix}${suffix.includes('?') ? '&' : '?'}page=${index}&pageSize=${limit}`);
    if (value.page !== index || value.pageSize !== limit || !Array.isArray(value.items) || !Number.isSafeInteger(value.total) || value.total < 0) throw new Error('The Activity page could not be confirmed. Reload Activity.');
    const more = (index + 1) * limit < value.total;
    return { items: value.items.map(project), nextCursor: more ? String(index + 1) : null, hasMore: more };
  }
  const readRun = async (sessionId: string, runId: string) => {
    const value = run(await request<RunRow>(`/sessions/${encodeURIComponent(sessionId)}/runs/${encodeURIComponent(runId)}`), sessionId);
    if (value.agentRunId !== runId) throw new IdentityCompletionError(); return value;
  };
  return { scope: `${tenant}:${principal}:${instance}`, tenantId: tenant, readScope: `${tenant}:${principal}`,
    listSessions: (_id, cursor) => page<SessionRow, InstanceActivitySession>('/sessions', cursor, 20, session),
    getSession: async (_id, id) => { const value = session(await request<SessionRow>(`/sessions/${encodeURIComponent(id)}`)); if (value.session.sessionId !== id) throw new IdentityCompletionError(); return value; },
    listBackground: (_id, cursor, limit = 20) => page<BackgroundRow, BackgroundSession>('/background-sessions', cursor, limit, background),
    getBackground: async id => { const value = background(await request<BackgroundRow>(`/sessions/${encodeURIComponent(id)}/background`)); if (value.session.sessionId !== id) throw new IdentityCompletionError(); return value; },
    continue: async item => {
      if (!Number.isSafeInteger(item.securityRevision) || !item.canContinueInChat) throw new Error('Read current background details before continuing.');
      const value = await identityRequest<{ sessionId: string }>(tenantApi(tenant, `/sessions/${encodeURIComponent(item.session.sessionId)}/continue-in-chat`), { method: 'POST',
        body: { expectedRevision: item.securityRevision, expectedSessionRevision: item.session.revision } });
      if (value?.sessionId !== item.session.sessionId) throw new IdentityCompletionError(); return value;
    },
    listRuns: (id, cursor, limit = 20) => page<RunRow, AgentRun>(`/sessions/${encodeURIComponent(id)}/runs`, cursor, limit, row => run(row, id)),
    listInstanceRuns: (_id, cursor, limit = 20) => page<RunRow, AgentRun>('/runs', cursor, limit, row => run(row)),
    getInstanceRun: async (_id, id) => { const value = run(await request<RunRow>(`/runs/${encodeURIComponent(id)}`)); if (value.agentRunId !== id) throw new IdentityCompletionError(); return value; },
    actions: { read: value => readRun(value.sessionId, value.agentRunId), cancel: async value => {
      const access = await identityRequest<{ revision: number }>(tenantApi(tenant, '/access/me'));
      const saved = await identityRequest<{ context?: { runId: string } }>(tenantApi(tenant, `/agent-runs/${encodeURIComponent(value.agentRunId)}/cancel`), { method: 'POST', body: { expectedRevision: access.revision, expectedRunRevision: value.revision } });
      if (saved.context?.runId !== value.agentRunId) throw new IdentityCompletionError(); return readRun(value.sessionId, value.agentRunId);
    } }
  };
}
