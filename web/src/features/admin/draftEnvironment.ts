export type DraftKnowledgeSource = {
  identity: string;
  title: string;
  citation: string;
  resourcePath: string;
};

export type DraftEnvironment = {
  harness: string[];
  toolAllowlist: string[];
  workspaceTemplateId: string;
  capabilityMode?: "Selected" | "All";
  alwaysCapabilities?: string[];
  knowledgeSources: DraftKnowledgeSource[];
  allowUnreadUnsupportedAttachmentTypes: boolean;
};

export const emptyDraftEnvironment = (): DraftEnvironment => ({
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

export function readDraftEnvironment(candidate: unknown): DraftEnvironment {
  const root = asRecord(candidate);
  const environment = root ? asRecord(root.environment) : null;
  if (!environment) {
    return emptyDraftEnvironment();
  }

  const workspace = asRecord(environment.workspace);
  const attachments = asRecord(environment.attachments);
  const templateId =
    workspace && typeof workspace.templateId === "string" ? workspace.templateId : "";

  return {
    harness: readStringList(environment.harness),
    toolAllowlist: readStringList(asRecord(environment.capabilities)?.resolvedCapabilities ?? environment.toolAllowlist),
    capabilityMode: asRecord(environment.capabilities)?.mode as "Selected" | "All" | undefined,
    alwaysCapabilities: environment.projection ? readStringList(asRecord(environment.projection)?.alwaysCapabilities) : undefined,
    workspaceTemplateId: templateId,
    knowledgeSources: readKnowledgeSources(environment.knowledgeSources),
    allowUnreadUnsupportedAttachmentTypes:
      attachments?.allowUnreadUnsupportedTypes === true
  };
}

export function draftEnvironmentEquals(a: DraftEnvironment, b: DraftEnvironment): boolean {
  return JSON.stringify(normalizeDraftEnvironment(a)) === JSON.stringify(normalizeDraftEnvironment(b));
}

function normalizeDraftEnvironment(env: DraftEnvironment): DraftEnvironment {
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

export function applyDraftEnvironmentToCandidate(
  candidate: Record<string, unknown>,
  environment: DraftEnvironment
): Record<string, unknown> {
  const workspace = {
    ...(environment.workspaceTemplateId.trim().length > 0 ? { templateId: environment.workspaceTemplateId.trim() } : {})
  };

  const knowledgeSources = environment.knowledgeSources.map((item) => {
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
    environment: {
      harness: environment.harness.map((item) => item.trim()).filter(Boolean),
      knowledgeSources,
      ...(environment.capabilityMode ? { capabilities: { mode: environment.capabilityMode, resolvedCapabilities: environment.toolAllowlist }, projection: { alwaysCapabilities: environment.alwaysCapabilities ?? [] } } : { toolAllowlist: environment.toolAllowlist }),
      workspace,
      attachments: {
        allowUnreadUnsupportedTypes: environment.allowUnreadUnsupportedAttachmentTypes
      }
    }
  };
}
