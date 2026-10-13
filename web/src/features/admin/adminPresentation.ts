import type { AdminDefinitionInventoryItem, AdminEffectiveConfiguration } from "../../services/adminApi";
export type LoadState<T> =
  | { kind: "loading" }
  | { kind: "error"; message: string; unauthorized?: boolean; diagnosticId?: string }
  | { kind: "ready"; data: T };

export type EffectiveConfigLoadState = LoadState<AdminEffectiveConfiguration> & {
  data?: AdminEffectiveConfiguration;
};

export type DefinitionInventoryGroup = {
  definitionId: string;
  logicalName: string;
  defaultPersona: string;
  latestVersion: number;
  latestStatus: string;
  versions: AdminDefinitionInventoryItem[];
  draftCount: number;
  draftOnly: boolean;
};

export function logicalDefinitionName(definitionId: string) {
  return definitionId
    .split("-")
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
    .join(" ");
}

export function formatAdminTimestamp(value: string) {
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) {
    return value;
  }
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(timestamp);
}

export function formatInventorySource(source: string) {
  return source === "builtIn" ? "Built-in" : "Durable";
}

export function formatInventoryStatus(status: string) {
  if (!status) {
    return status;
  }
  const normalized = status.toLowerCase();
  return `${normalized.charAt(0).toUpperCase()}${normalized.slice(1)}`;
}

export function formatDraftSource(sourceKind: string) {
  if (sourceKind === "ForkBuiltIn") {
    return "Built-in source";
  }
  if (sourceKind === "New") {
    return "New definition";
  }
  return "Durable source";
}

export function latestActiveVersion(versions: AdminDefinitionInventoryItem[]): number | null {
  const active = [...versions]
    .filter((row) => row.status.toLowerCase() !== "deprecated")
    .sort((left, right) => right.version - left.version)[0];
  return active?.version ?? null;
}

export function formatDefinitionVersionSummary(group: {
  latestVersion: number;
  latestStatus: string;
  versions: AdminDefinitionInventoryItem[];
  draftOnly?: boolean;
}): string {
  if (group.draftOnly) {
    return "Draft only · Never published";
  }
  const active = latestActiveVersion(group.versions);
  const latestStatus = formatInventoryStatus(group.latestStatus);
  if (active !== null && active !== group.latestVersion) {
    return `Latest v${group.latestVersion} · ${latestStatus} · Latest active v${active}`;
  }
  return `Latest v${group.latestVersion} · ${latestStatus}`;
}

export function formatDefinitionInventoryCounts(group: {
  versions: AdminDefinitionInventoryItem[];
  draftCount: number;
  draftOnly?: boolean;
}): string {
  const drafts = `${group.draftCount} ${group.draftCount === 1 ? "draft" : "drafts"}`;
  if (group.draftOnly) {
    return drafts;
  }
  const versions = `${group.versions.length} ${group.versions.length === 1 ? "version" : "versions"}`;
  return group.draftCount > 0 ? `${drafts} · ${versions}` : versions;
}

export function defaultForkSourceVersion(versions: AdminDefinitionInventoryItem[]): number | null {
  if (versions.length === 0) {
    return null;
  }
  const ordered = [...versions].sort((left, right) => right.version - left.version);
  const eligible = ordered.find((row) => row.status.toLowerCase() !== "deprecated");
  return (eligible ?? ordered[0]).version;
}

export function formatForkSourceOptionLabel(row: AdminDefinitionInventoryItem) {
  return `v${row.version} · ${formatInventorySource(row.source)} · ${formatInventoryStatus(row.status)}`;
}

export function forkSourceKey(row: AdminDefinitionInventoryItem) {
  return `${row.source}:${row.version}`;
}

export function instanceVersionRows(versions: AdminDefinitionInventoryItem[]) {
  // Exact-version resolution prefers a built-in definition over a durable publication.
  return [...new Map([...versions]
    .sort((a, b) => Number(a.source === "builtIn") - Number(b.source === "builtIn"))
    .map(row => [row.version, row])).values()];
}

export function groupDefinitionInventory(
  items: AdminDefinitionInventoryItem[]
): DefinitionInventoryGroup[] {
  const groups = new Map<string, AdminDefinitionInventoryItem[]>();
  for (const item of items) {
    const versions = groups.get(item.definitionId) ?? [];
    versions.push(item);
    groups.set(item.definitionId, versions);
  }

  return Array.from(groups, ([definitionId, versions]) => {
    const orderedVersions = [...versions].sort((left, right) => right.version - left.version);
    const published = orderedVersions.filter((row) => row.status.toLowerCase() !== "draftonly");
    const draftCount = orderedVersions.reduce((count, row) => Math.max(count, row.draftCount ?? 0), 0);
    if (published.length === 0) {
      const draftRow = orderedVersions[0];
      return {
        definitionId,
        logicalName: logicalDefinitionName(definitionId),
        defaultPersona: draftRow?.displayName ?? logicalDefinitionName(definitionId),
        latestVersion: 0,
        latestStatus: "draftOnly",
        versions: [],
        draftCount,
        draftOnly: true
      };
    }
    const latest = published[0];
    return {
      definitionId,
      logicalName: logicalDefinitionName(definitionId),
      defaultPersona: latest.displayName,
      latestVersion: latest.version,
      latestStatus: latest.status,
      versions: published,
      draftCount,
      draftOnly: false
    };
  });
}
