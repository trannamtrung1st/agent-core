import { sessionPath } from "../../app/sessionRoute";
import { navigateToAppPath } from "../../app/appRoute";
import { createSessionForInstance } from "../../services/api";
import { createAdminAgentInstance } from "../../services/adminApi";
import { openSessionById, suspendLiveSessionForNavigation } from "../../services/realtime";

export async function startManagedPublicationChat(definitionId: string, version: number): Promise<void> {
  const instance = await createAdminAgentInstance(definitionId, version);
  const session = await createSessionForInstance(instance.instanceId);
  await suspendLiveSessionForNavigation();
  const opened = await openSessionById(session.sessionId, { syncUrl: false });
  if (opened === "failed") {
    throw new Error("Managed chat session could not be opened.");
  }

  navigateToAppPath(sessionPath(session.sessionId), true);
}
