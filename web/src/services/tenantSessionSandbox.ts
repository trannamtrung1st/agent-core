import { identityRequest, tenantApi } from './identity';
import type { SandboxInitialization } from './sandboxes';

export type TenantSandboxExecution = { executionId: string; revision: number; agentRunId: string; generation: string; state: string; profileId: string | null; profileRevision: number | null; commandText: string; background: boolean; startedAt: string; finishedAt: string | null; exitCode: number | null; stdout: string; stderr: string; truncated: boolean; durationMs: number | null; filesystemEffectsMayHaveOccurred: boolean; terminationConfirmed: boolean; remoteTerminationConfirmed: boolean; diagnosticId: string | null; stopRequested: boolean; stopDispatchStarted: boolean };
export type TenantSandboxEnvironment = { generation: string; revision: number; storedState: string; profileId: string; profileRevision: number; profileName: string; imageReference: string; createdAt: string; expiresAt: string; activeExecutionId: string | null; lastExecutionId: string | null; cleanupPending: boolean; message: string | null; initialization: SandboxInitialization | null; storageEnforcement: string; selectedProfileId: string | null; selection: { profileId: string; profileRevision: number; instanceRevision: number; storedPhase: string } | null; selectionRestricted: boolean };
export type TenantSandboxObservation = { sessionId: string; environment: TenantSandboxEnvironment | null; execution: TenantSandboxExecution | null; sessionRevision: number; securityRevision: number; liveAuthorityConfirmed: false };
export type TenantSandboxControlProfile = { id: string; revision: number; name: string; imageReference: string; availability: string; canSelect: boolean };
export type TenantSandboxControls = { observation: TenantSandboxObservation; instanceRevision: number; canReset: boolean; canStop: boolean; profiles: TenantSandboxControlProfile[]; defaultProfileId: string | null };
export const tenantSessionSandboxApi = (tenant: string, session: string, suffix = '') => tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/sandbox${suffix}`);
export const readTenantSandboxControls = (tenant: string, session: string, signal?: AbortSignal) => identityRequest<TenantSandboxControls>(tenantSessionSandboxApi(tenant, session, '/controls'), { signal });
export function tenantSandboxWitness(view: TenantSandboxControls) {
  const { observation: o } = view; const environment = o.environment;
  if (!environment) throw new Error('Read an existing saved generation before requesting a Sandbox control.');
  return { expectedRevision: o.securityRevision, expectedSessionRevision: o.sessionRevision, expectedGeneration: environment.generation,
    expectedLeaseRevision: environment.revision, expectedExecutionId: environment.activeExecutionId ?? environment.lastExecutionId };
}
