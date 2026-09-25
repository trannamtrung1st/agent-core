export type DraftKnowledgeSource = {
  identity: string;
  title: string;
  citation: string;
};

export type DraftEnvironment = {
  harness: string[];
  toolAllowlist: string[];
  workspaceTemplateId: string;
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
    if (!identity.trim()) {
      continue;
    }
    items.push({ identity, title, citation });
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
    toolAllowlist: readStringList(environment.toolAllowlist),
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
    knowledgeSources: env.knowledgeSources.map((item) => ({
      identity: item.identity,
      title: item.title,
      citation: item.citation
    })),
    allowUnreadUnsupportedAttachmentTypes: env.allowUnreadUnsupportedAttachmentTypes
  };
}

export function applyDraftEnvironmentToCandidate(
  candidate: Record<string, unknown>,
  environment: DraftEnvironment
): Record<string, unknown> {
  const workspace =
    environment.workspaceTemplateId.trim().length > 0
      ? { templateId: environment.workspaceTemplateId.trim() }
      : {};

  const knowledgeSources = environment.knowledgeSources
    .filter((item) => item.identity.trim().length > 0)
    .map((item) => ({
      identity: item.identity.trim(),
      title: item.title.trim(),
      citation: item.citation.trim()
    }));

  return {
    ...candidate,
    environment: {
      harness: environment.harness.map((item) => item.trim()).filter(Boolean),
      knowledgeSources,
      toolAllowlist: environment.toolAllowlist,
      workspace,
      attachments: {
        allowUnreadUnsupportedTypes: environment.allowUnreadUnsupportedAttachmentTypes
      }
    }
  };
}
