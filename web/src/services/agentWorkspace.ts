import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';

export type AgentWorkspaceItem = {
  agentInstanceId: string; logicalPath: string; contentType: string; byteSize: number;
  directory: boolean; kind: 'file' | 'directory' | 'symlink' | 'special';
  updatedAt: string; linkTarget: string | null; mode: number | null;
};
export type AgentWorkspacePage = { items: AgentWorkspaceItem[]; usedBytes: number | null; totalItems: number | null;
  nextPath: string | null; maxFileBytes: number; maxInstanceBytes: number; usageEnforcement: 'sampled' | 'unknown'; truncated: boolean };
const path = (id: string) => `/api/v2/agent-instances/${encodeURIComponent(id)}/workspace`;

export async function listAgentWorkspace(id: string, cursor?: string, prefix = '/home'): Promise<AgentWorkspacePage> {
  const query = new URLSearchParams({prefix, ...(cursor ? {cursor} : {})});
  const response = await ownerFetch(`${path(id)}?${query}`);
  if (!response.ok) throw await adminProblemMessage(response, 'Unable to load workspace. Retry to read current paths.');
  return response.json() as Promise<AgentWorkspacePage>;
}
export async function deleteAgentWorkspaceItem(id: string, item: AgentWorkspaceItem): Promise<void> {
  const response = await ownerFetch(`${path(id)}/content?${new URLSearchParams({path: item.logicalPath})}`, { method: 'DELETE' });
  if (!response.ok) throw await adminProblemMessage(response, 'The current path could not be deleted. Reload the workspace and try again.');
}
export async function downloadAgentWorkspaceItem(id: string, item: AgentWorkspaceItem, signal?: AbortSignal): Promise<void> {
  const response = await ownerFetch(`${path(id)}/content?${new URLSearchParams({path: item.logicalPath})}`, { signal });
  if (!response.ok) throw await adminProblemMessage(response, 'Download failed. Reload the current path and try again.');
  const blob = await response.blob(); signal?.throwIfAborted();
  const url = URL.createObjectURL(blob); const link = document.createElement('a');
  try {
    link.href = url; link.download = item.logicalPath.split('/').pop()!; link.hidden = true;
    document.body.append(link); link.click();
  } finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
}
