import { continueInChat, getBackgroundSession, getInstanceAgentRun, getInstanceSession, listAgentRuns, listBackgroundSessions, listInstanceAgentRuns, listInstanceSessions,
  type AgentRun, type BackgroundSession, type CursorPage, type InstanceActivitySession } from './api';

export type RunActions = { cancel: (run: AgentRun) => Promise<AgentRun>; read: (run: AgentRun) => Promise<AgentRun> };
export type ActivitySource = {
  scope: string; tenantId?: string; readScope?: string;
  listSessions: typeof listInstanceSessions; getSession: typeof getInstanceSession;
  listBackground: typeof listBackgroundSessions; getBackground: typeof getBackgroundSession;
  continue: (item: BackgroundSession) => Promise<{ sessionId: string }>;
  listRuns: typeof listAgentRuns; listInstanceRuns: typeof listInstanceAgentRuns;
  getInstanceRun: typeof getInstanceAgentRun;
  actions?: RunActions;
};
export const legacyActivitySource: ActivitySource = { scope: 'legacy', listSessions: listInstanceSessions, getSession: getInstanceSession,
  listBackground: listBackgroundSessions, getBackground: getBackgroundSession, continue: item => continueInChat(item.session.sessionId),
  listRuns: listAgentRuns, listInstanceRuns: listInstanceAgentRuns, getInstanceRun: getInstanceAgentRun };
export type { AgentRun, BackgroundSession, CursorPage, InstanceActivitySession };
