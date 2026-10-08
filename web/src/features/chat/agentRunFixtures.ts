import type { AgentRun, BackgroundSession } from "../../services/api";
export const fixtureRun: AgentRun = {
  agentRunId: "run-1", sessionId: "background-1", activationId: "activation-1", activationKind: "ImmediateBackground",
  status: "completed", revision: 3, attemptCount: 1, maxAttempts: 3, cancellationRequested: false, cancellationAvailable: false,
  progress: null, nextRetryAt: null, createdAt: "2026-10-08T09:00:00Z", updatedAt: "2026-10-08T09:01:00Z",
  approval: null, outcome: { kind: "Response", summary: "The background check finished.", outcomeEntryId: "result-entry", attentionRequired: false },
  failureCode: null, failureSummary: null, diagnosticId: null, knownEffectSummary: null, modelCatalogKey: "synthetic-default", responseId: "response-1", automationId: null, experienceId: null, sourceOccurrenceId: null
};
export const fixtureBackground: BackgroundSession = {
  originalTitle: "Progress check",
  session: { sessionId: "background-1", title: "Progress check", agentId: "examiner", agentVersion: 1, status: "active", archived: false, ended: false,
    workspaceOwned: true, runtimeEpoch: 1, revision: 3, createdAt: fixtureRun.createdAt, updatedAt: fixtureRun.updatedAt },
  origin: { kind: "ImmediateBackground", initialAgentRunId: "run-1", parentSessionId: "parent-1", parentAgentRunId: "parent-run",
    automationId: null, occurrenceId: null, reportCompletion: true }, surfaces: ["BackgroundWork"], latestRun: fixtureRun, canContinueInChat: true, artifactCount: 0, artifactCountHasMore: false
};
