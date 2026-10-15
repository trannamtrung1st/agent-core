import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';

export type SandboxProfile = { id: string; revision: number; name: string; description: string; enabled: boolean; imageReference: string;
  initializationScript: string | null; cpu: number; memoryMiB: number; pids: number; maxCommandSeconds: number; idleTtlSeconds: number;
  maxLifetimeSeconds: number; storageBytes: number; lastVerifiedAt: string | null; verificationReason: string | null; verification?:SandboxVerification|null; registryCredentialId?:string|null; availability?:string; availabilityMessage?:string|null };
export type SandboxPlatform = { state: string; message: string; profiles: SandboxProfile[]; allowedImagePrefixes: string[];
  localImageIdsAllowed: boolean; securityScope: string; storageScope: string; maxWorkingStorageBytes: number };
export type SandboxEnvironment = { sessionId: string; generation: string; state: string; profileId: string; profileRevision: number; expiresAt: string; activeExecutionId: string | null; diagnosticId: string | null; lastExecutionId: string | null; message?: string | null; createdAt?: string | null; lastActivityAt?: string | null; maximumLifetimeAt?: string | null;
  cleanupReason?: string | null; cleanupPending?: boolean; homeAccess?: 'ReadWrite' | 'ReadOnly' | 'Disabled' | null; storageEnforcement?: string; cleanupPasses?: number; lastCleanupAttemptAt?: string | null; initialization?: SandboxInitialization | null; profileName?:string|null;imageReference?:string|null; selectedProfileId?:string|null; transitionProfileId?:string|null; transitionPhase?:string|null };
export type SandboxExecution = { executionId: string; status: string; exitCode: number | null; stdout: string; stderr: string;
  truncated: boolean; durationMs: number; filesystemEffectsMayHaveOccurred: boolean; diagnosticId: string | null; background: boolean; commandText: string; profileId?:string|null;profileRevision?:number|null;generation?:string|null;phase?:string|null;failureCategory?:string|null;cancellationReason?:string|null;terminationConfirmed?:boolean; generationRecycled?:boolean; remoteTerminationConfirmed?:boolean };
async function request<T>(url: string, method = 'GET', body?: unknown, signal?: AbortSignal, emptyResponse?: T): Promise<T> {
  const response = await ownerFetch(url, { method, signal, ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
  if (!response.ok) throw await adminProblemMessage(response, 'Sandbox information could not be read or saved. Retry; your edits are retained.');
  // ASP.NET Results.Json(null) returns a successful empty body for an unprovisioned Session.
  if (emptyResponse !== undefined) {
    const text = await response.text();
    return text.trim() ? JSON.parse(text) as T : emptyResponse;
  }
  return await response.json() as T;
}
export const getSandboxPlatform = (signal?: AbortSignal) => request<SandboxPlatform>('/api/v2/admin/sandboxes', 'GET', undefined, signal);
export const saveSandboxProfile = (profile: SandboxProfile) => {
  const {name, description, enabled, imageReference, registryCredentialId, initializationScript, cpu, memoryMiB, pids, maxCommandSeconds, idleTtlSeconds, maxLifetimeSeconds, storageBytes} = profile;
  return request<SandboxProfile>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(profile.id)}`, 'PUT', {expectedRevision:profile.revision, name, description, enabled, imageReference, registryCredentialId:registryCredentialId??null, initializationScript, cpu, memoryMiB, pids, maxCommandSeconds, idleTtlSeconds, maxLifetimeSeconds, storageBytes});
};
export const getSandboxProfile = (id:string, revision:number) => request<SandboxProfile>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(id)}?revision=${revision}`);
export const sandboxSourceLabel = (p:SandboxProfile) => p.imageReference.startsWith('sha256:') ? 'Local image' : 'Remote OCI';
export const sandboxImageRegistry = (value: string) => {
  const image = value.trim();
  if (!image || image.startsWith('sha256:')) return null;
  const first = image.split('/')[0];
  return image.includes('/') && (first.includes('.') || first.includes(':') || first === 'localhost') ? first : 'docker.io';
};
export const sandboxStatusLabel = (p:SandboxProfile) => ({Eligible:'Verified · eligible', NeedsVerification:'Needs verification', VerificationFailed:'Verification failed', SourceUnavailable:'Source unavailable', Forbidden:'Forbidden by host', ServerUnavailable:'Server unavailable', Disabled:'Disabled'}[p.availability??''] ?? (p.enabled?'Needs verification':'Disabled'));
export const sandboxEligible = (p:SandboxProfile) => p.availability==='Eligible';
export const verifySandboxProfile = (profile: SandboxProfile) => request<{ passed: boolean; message: string; profile: SandboxProfile; exitCode?:number|null; stdout?:string; stderr?:string; truncated?:boolean; durationMs?:number; diagnosticId?:string|null }>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(profile.id)}/verify`, 'POST', { expectedRevision: profile.revision });
export const getInstanceSandbox = (id: string, signal?: AbortSignal) => request<{ policy: { defaultProfileId: string; allowedProfileIds: string[] } | null; profile: SandboxProfile | null; profiles?: SandboxProfile[]; state: string; message: string; instanceRevision:number; instanceInitializationScript:string|null; verification:{result:SandboxVerification}|null }>(`/api/v2/admin/agent-instances/${encodeURIComponent(id)}/sandbox`, 'GET', undefined, signal);
export const getSessionSandbox = (id: string, signal?: AbortSignal) => request<SandboxEnvironment | null>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox`, 'GET', undefined, signal, null);
export const getSandboxExecution = (id: string, executionId: string, signal?: AbortSignal) => request<SandboxExecution>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/executions/${encodeURIComponent(executionId)}`, 'GET', undefined, signal);
export const stopSandboxExecution = (id: string, executionId: string) => request<SandboxExecution>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/executions/${encodeURIComponent(executionId)}/stop`, 'POST');
export const resetSessionSandbox = (id: string, expectedGeneration: string, expectedExecutionId?:string|null) => request<{ reset: boolean }>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/reset`, 'POST', { expectedGeneration, expectedExecutionId:expectedExecutionId??null });

export type SandboxInitialization = { state:string; profileId:string; profileRevision:number; instanceRevision:number|null; startedAt:string|null; finishedAt:string|null; exitCode:number|null; stdout:string; stderr:string; truncated:boolean; durationMs:number; diagnosticId:string|null; phase:string|null };
export type SandboxVerification = { passed:boolean; message:string; profileId:string; profileRevision:number; instanceRevision:number|null; verifiedAt:string; exitCode:number|null; stdout:string; stderr:string; truncated:boolean; durationMs:number; diagnosticId:string|null };
export const checkSandboxImage = (profile:SandboxProfile) => request<SandboxVerification>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(profile.id)}/check-image`, 'POST', {expectedRevision:profile.revision});
export const verifyInstanceSandbox = (id:string, revision:number, profileId?:string) => request<SandboxVerification>(`/api/v2/admin/agent-instances/${encodeURIComponent(id)}/sandbox/verify`, 'POST', {expectedInstanceRevision:revision, ...(profileId?{profileId}:{})});
