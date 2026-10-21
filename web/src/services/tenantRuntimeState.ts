import type { TenantAgentEditor } from './tenantAgentConfiguration';
export type RuntimeResourceState = { kind: 'browser' | 'sandbox'; availability: string; sharing: string; observedAtUtc: string;
  liveContexts?: number | null; openPages?: number | null; activeGenerations?: number | null; activeCommands?: number | null; cleanupPending?: number | null; code?: string | null };
export type TenantRuntimeState = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { observation: {
  agentInstanceId: string; observedAtUtc: string; freshThroughUtc: string; availability: string; scope: string;
  sessions: { open: number; paused: number; terminal: number; lastActivityAtUtc: string | null } | null;
  hostedSessions: { runtimes: number; attached: number } | null;
  runs: { queued: number; executing: number; waitingApproval: number; waitingSignal: number; retrying: number; terminal: number } | null;
  resources: RuntimeResourceState[]; issues: { kind: string; code: string }[];
} };
