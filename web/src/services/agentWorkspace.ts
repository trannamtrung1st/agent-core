import { ownerFetch } from "./api";
import { adminProblemMessage } from "./adminApi";

export type AgentWorkspaceItem = {
  itemId: string; agentInstanceId: string; logicalPath: string; contentType: string; byteSize: number;
  directory?: boolean;
  sha256Hex: string; revision: number; createdAt: string; updatedAt: string; sourceSessionId: string | null;
};
export type AgentWorkspacePage = { items: AgentWorkspaceItem[]; usedBytes: number; totalItems: number;
  treeSha256?: string; nextPath: string | null; maxFileBytes: number; maxInstanceBytes: number };
const path = (id: string) => `/api/v2/agent-instances/${encodeURIComponent(id)}/workspace`;

export async function listAgentWorkspace(id: string, afterPath?: string): Promise<AgentWorkspacePage> {
  const response = await ownerFetch(`${path(id)}${afterPath ? `?afterPath=${encodeURIComponent(afterPath)}` : ""}`);
  if (!response.ok) throw await adminProblemMessage(response, "Unable to load workspace. Retry to reload the files.");
  return response.json() as Promise<AgentWorkspacePage>;
}
export async function deleteAgentWorkspaceItem(id: string, item: AgentWorkspaceItem): Promise<void> {
  const response = await ownerFetch(`${path(id)}/${encodeURIComponent(item.itemId)}?expectedRevision=${item.revision}`, { method: "DELETE" });
  if (!response.ok) throw await adminProblemMessage(response, "The file could not be deleted. Reload the workspace and try again.");
}
export async function downloadAgentWorkspaceItem(id: string, item: AgentWorkspaceItem, signal?: AbortSignal): Promise<void> {
  const response = await ownerFetch(`${path(id)}/${encodeURIComponent(item.itemId)}/content`, { signal });
  if (!response.ok) throw await adminProblemMessage(response, "Download failed. Try again.");
  const blob = await response.blob(); signal?.throwIfAborted();
  const url = URL.createObjectURL(blob); const link = document.createElement("a");
  try {
    link.href = url; link.download = item.logicalPath.split("/").pop()!; link.hidden = true;
    document.body.append(link); link.click();
  } finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
}
