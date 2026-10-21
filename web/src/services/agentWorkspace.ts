export type AgentWorkspaceItem = {
  agentInstanceId: string; logicalPath: string; contentType: string; byteSize: number;
  directory: boolean; kind: 'file' | 'directory' | 'symlink' | 'special';
  updatedAt: string; linkTarget: string | null; mode: number | null;
};
export type AgentWorkspacePage = { items: AgentWorkspaceItem[]; usedBytes: number | null; totalItems: number | null;
  nextPath: string | null; maxFileBytes: number; maxInstanceBytes: number; usageEnforcement: 'sampled' | 'unknown'; truncated: boolean };
