export type DraftKnowledgeSource = {
  identity: string;
  title: string;
  citation: string;
  resourcePath: string;
};

export type DraftExecutionPolicy = {
  harness: string[];
  toolAllowlist: string[];
  workspaceTemplateId: string;
  capabilityMode?: "Selected" | "All";
  alwaysCapabilities?: string[];
  knowledgeSources: DraftKnowledgeSource[];
  allowUnreadUnsupportedAttachmentTypes: boolean;
};

export const emptyDraftExecutionPolicy = (): DraftExecutionPolicy => ({
  harness: [],
  toolAllowlist: [],
  workspaceTemplateId: "",
  knowledgeSources: [],
  allowUnreadUnsupportedAttachmentTypes: false
});

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function readStringList(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter((item): item is string => typeof item === "string" && item.trim().length > 0);
}

function readKnowledgeSources(value: unknown): DraftKnowledgeSource[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const items: DraftKnowledgeSource[] = [];
  for (const entry of value) {
    const row = asRecord(entry);
    if (!row) {
      continue;
    }
    const identity = typeof row.identity === "string" ? row.identity : "";
    const title = typeof row.title === "string" ? row.title : "";
    const citation = typeof row.citation === "string" ? row.citation : "";
    const resourcePath = typeof row.resourcePath === "string" ? row.resourcePath : "";
    items.push({ identity, title, citation, resourcePath });
  }
  return items;
}

export function readDraftExecutionPolicy(candidate: unknown): DraftExecutionPolicy {
  const root = asRecord(candidate);
  const executionPolicy = root ? asRecord(root.executionPolicy) : null;
  if (!executionPolicy) {
    return emptyDraftExecutionPolicy();
  }

  const workspace = asRecord(executionPolicy.workspace);
  const attachments = asRecord(executionPolicy.attachments);
  const templateId =
    workspace && typeof workspace.templateId === "string" ? workspace.templateId : "";

  return {
    harness: readStringList(executionPolicy.harness),
    toolAllowlist: readStringList(asRecord(executionPolicy.capabilities)?.resolvedCapabilities ?? executionPolicy.toolAllowlist),
    capabilityMode: asRecord(executionPolicy.capabilities)?.mode as "Selected" | "All" | undefined,
    alwaysCapabilities: executionPolicy.projection ? readStringList(asRecord(executionPolicy.projection)?.alwaysCapabilities) : undefined,
    workspaceTemplateId: templateId,
    knowledgeSources: readKnowledgeSources(executionPolicy.knowledgeSources),
    allowUnreadUnsupportedAttachmentTypes:
      attachments?.allowUnreadUnsupportedTypes === true
  };
}

export function draftExecutionPolicyEquals(a: DraftExecutionPolicy, b: DraftExecutionPolicy): boolean {
  return JSON.stringify(normalizeDraftExecutionPolicy(a)) === JSON.stringify(normalizeDraftExecutionPolicy(b));
}

function normalizeDraftExecutionPolicy(env: DraftExecutionPolicy): DraftExecutionPolicy {
  return {
    harness: [...env.harness].sort(),
    toolAllowlist: [...env.toolAllowlist].sort(),
    workspaceTemplateId: env.workspaceTemplateId,
    capabilityMode: env.capabilityMode,
    alwaysCapabilities: env.alwaysCapabilities?.slice().sort(),
    knowledgeSources: env.knowledgeSources.map((item) => ({
      identity: item.identity,
      title: item.title,
      citation: item.citation,
      resourcePath: item.resourcePath.trim()
    })),
    allowUnreadUnsupportedAttachmentTypes: env.allowUnreadUnsupportedAttachmentTypes
  };
}

export function applyDraftExecutionPolicyToCandidate(
  candidate: Record<string, unknown>,
  executionPolicy: DraftExecutionPolicy
): Record<string, unknown> {
  const workspace = {
    ...(executionPolicy.workspaceTemplateId.trim().length > 0 ? { templateId: executionPolicy.workspaceTemplateId.trim() } : {})
  };

  const knowledgeSources = executionPolicy.knowledgeSources.map((item) => {
    const source: { identity: string; title: string; citation: string; resourcePath?: string } = {
      identity: item.identity.trim(),
      title: item.title.trim(),
      citation: item.citation.trim()
    };
    const resourcePath = item.resourcePath.trim();
    if (resourcePath.length > 0) {
      source.resourcePath = resourcePath;
    }
    return source;
  });

  return {
    ...candidate,
    executionPolicy: {
      harness: executionPolicy.harness.map((item) => item.trim()).filter(Boolean),
      knowledgeSources,
      ...(executionPolicy.capabilityMode ? { capabilities: { mode: executionPolicy.capabilityMode, resolvedCapabilities: executionPolicy.toolAllowlist }, projection: { alwaysCapabilities: executionPolicy.alwaysCapabilities ?? [] } } : { toolAllowlist: executionPolicy.toolAllowlist }),
      workspace,
      attachments: {
        allowUnreadUnsupportedTypes: executionPolicy.allowUnreadUnsupportedAttachmentTypes
      }
    }
  };
}
