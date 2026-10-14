import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';

export type SandboxProfile = { id: string; revision: number; name: string; description: string; enabled: boolean; imageReference: string;
  initializationScript: string | null; cpu: number; memoryMiB: number; pids: number; maxCommandSeconds: number; idleTtlSeconds: number;
  maxLifetimeSeconds: number; storageBytes: number; lastVerifiedAt: string | null; verificationReason: string | null };
export type SandboxPlatform = { state: string; message: string; profiles: SandboxProfile[]; allowedImagePrefixes: string[];
  localImageIdsAllowed: boolean; securityScope: string; storageScope: string };
export type SandboxEnvironment = { sessionId: string; generation: string; state: string; profileId: string; profileRevision: number; expiresAt: string; activeExecutionId: string | null; diagnosticId: string | null; lastExecutionId: string | null };
export type SandboxExecution = { executionId: string; status: string; exitCode: number | null; stdout: string; stderr: string;
  truncated: boolean; durationMs: number; workspaceSyncStatus: string; diagnosticId: string | null; background: boolean; commandText: string };
async function request<T>(url: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await ownerFetch(url, { method, signal, ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
  if (!response.ok) throw await adminProblemMessage(response, 'Sandbox information could not be read or saved. Retry; your edits are retained.');
  return await response.json() as T;
}
export const getSandboxPlatform = (signal?: AbortSignal) => request<SandboxPlatform>('/api/v2/admin/sandboxes', 'GET', undefined, signal);
export const saveSandboxProfile = (profile: SandboxProfile) => request<SandboxProfile>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(profile.id)}`, 'PUT', { ...profile, expectedRevision: profile.revision });
export const verifySandboxProfile = (profile: SandboxProfile) => request<{ passed: boolean; message: string; profile: SandboxProfile }>(`/api/v2/admin/sandbox-profiles/${encodeURIComponent(profile.id)}/verify`, 'POST', { expectedRevision: profile.revision });
export const getInstanceSandbox = (id: string, signal?: AbortSignal) => request<{ policy: { defaultProfileId: string; allowedProfileIds: string[] } | null; profile: SandboxProfile | null; state: string; message: string }>(`/api/v2/admin/agent-instances/${encodeURIComponent(id)}/sandbox`, 'GET', undefined, signal);
export const getSessionSandbox = (id: string, signal?: AbortSignal) => request<SandboxEnvironment | null>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox`, 'GET', undefined, signal);
export const getSandboxExecution = (id: string, executionId: string, signal?: AbortSignal) => request<SandboxExecution>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/executions/${encodeURIComponent(executionId)}`, 'GET', undefined, signal);
export const stopSandboxExecution = (id: string, executionId: string) => request<SandboxExecution>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/executions/${encodeURIComponent(executionId)}/stop`, 'POST');
export const resetSessionSandbox = (id: string, expectedGeneration: string) => request<{ reset: boolean }>(`/api/v2/sessions/${encodeURIComponent(id)}/sandbox/reset`, 'POST', { expectedGeneration });
