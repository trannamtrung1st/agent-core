export type AgentWorkspaceItem = {
  workspaceOwner: import('./tenantWorkspace').WorkspaceReference | null; resourceInstanceId?: string | null; resourceSpaceSource?: { tenantId: string; spaceId: string; resourceId: string; revision: number } | null; logicalPath: string; contentType: string; byteSize: number;
  directory: boolean; kind: 'file' | 'directory' | 'symlink' | 'special';
  updatedAt: string; linkTarget: string | null; mode: number | null;
};
export type AgentWorkspacePage = { items: AgentWorkspaceItem[]; usedBytes: number | null; totalItems: number | null;
  nextPath: string | null; maxFileBytes: number; maxSpaceBytes: number; usageEnforcement: 'sampled' | 'unknown'; truncated: boolean };
