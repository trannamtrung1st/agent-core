import type { AgentRun, BackgroundSession, CursorPage, InstanceActivitySession } from './api';
export type RunActions = { cancel: (run: AgentRun) => Promise<AgentRun>; read: (run: AgentRun) => Promise<AgentRun> };
export type ActivitySource = {
  scope: string; tenantId: string; readScope: string;
  listSessions: (instance: string, cursor?: string) => Promise<CursorPage<InstanceActivitySession>>;
  getSession: (instance: string, session: string) => Promise<InstanceActivitySession>;
  listBackground: (instance: string, cursor?: string, limit?: number) => Promise<CursorPage<BackgroundSession>>;
  getBackground: (session: string) => Promise<BackgroundSession>;
  continue: (item: BackgroundSession) => Promise<{ sessionId: string }>;
  listRuns: (session: string, cursor?: string, limit?: number) => Promise<CursorPage<AgentRun>>;
  listInstanceRuns: (instance: string, cursor?: string, limit?: number) => Promise<CursorPage<AgentRun>>;
  getInstanceRun: (instance: string, run: string) => Promise<AgentRun>;
  actions: RunActions;
};
export type { AgentRun,BackgroundSession,CursorPage,InstanceActivitySession };
