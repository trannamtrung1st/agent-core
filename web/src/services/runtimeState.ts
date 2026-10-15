import { ownerFetch } from './api';

export type ResourceState = {
  kind: string; availability: string; sharing: string; observedAtUtc: string;
  liveContexts: number | null; openPages: number | null; activeGenerations: number | null;
  activeCommands: number | null; cleanupPending: number | null; code: string | null;
};
export type RuntimeState = {
  agentInstanceId: string; observedAtUtc: string; freshThroughUtc: string; availability: string;
  sessions: { open: number; paused: number; terminal: number; lastActivityAtUtc: string | null } | null;
  hostedSessions: { runtimes: number; attached: number } | null;
  runs: { queued: number; executing: number; waitingApproval: number; waitingSignal: number; retrying: number; terminal: number } | null;
  resources: ResourceState[]; issues: { kind: string; code: string }[];
};

export async function getRuntimeState(instanceId: string, signal: AbortSignal): Promise<RuntimeState> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${encodeURIComponent(instanceId)}/state`, { signal });
  if (!response.ok) throw new Error('State could not be refreshed. Retry to read a current observation.');
  return response.json() as Promise<RuntimeState>;
}
