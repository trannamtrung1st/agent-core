import type {
  AdminDefinitionInventoryItem,
  AdminDefinitionPublicationSummary,
  AdminEffectiveConfiguration
} from "../../services/adminApi";

export type PersonaFields = AdminEffectiveConfiguration["persona"];

export const UNSAVED_PERSONA_DISCARD_MESSAGE =
  "Unsaved persona changes will be discarded when the instance reloads.";

function personaFieldsEqual(left: PersonaFields, right: PersonaFields): boolean {
  return (
    left.name === right.name &&
    left.role === right.role &&
    left.description === right.description &&
    left.tone === right.tone
  );
}

export function isPersonaDraftDirty(
  savedPersona: PersonaFields,
  activeTab: string,
  persona: PersonaFields,
  personaJsonDraft: string
): boolean {
  if (activeTab === "form") {
    return !personaFieldsEqual(persona, savedPersona);
  }
  try {
    return !personaFieldsEqual(parsePersonaJson(personaJsonDraft), savedPersona);
  } catch {
    return personaJsonDraft.trim() !== personaFieldsToJson(savedPersona).trim();
  }
}

export function personaFieldsToJson(persona: PersonaFields): string {
  return JSON.stringify(persona, null, 2);
}

export function parsePersonaJson(text: string): PersonaFields {
  const parsed = JSON.parse(text) as Record<string, unknown>;
  const keys = ["name", "role", "description", "tone"] as const;
  for (const key of keys) {
    if (typeof parsed[key] !== "string") {
      throw new Error(`Persona JSON must include string "${key}".`);
    }
  }
  const extra = Object.keys(parsed).filter((key) => !keys.includes(key as (typeof keys)[number]));
  if (extra.length > 0) {
    throw new Error(`Persona JSON has unknown fields: ${extra.join(", ")}`);
  }
  return {
    name: parsed.name as string,
    role: parsed.role as string,
    description: parsed.description as string,
    tone: parsed.tone as string
  };
}

export type PersonaTabSyncResult =
  | { ok: true; persona: PersonaFields; personaJsonDraft: string }
  | { ok: false; error: string };

export function syncPersonaOnTabChange(
  fromTab: string,
  toTab: string,
  persona: PersonaFields,
  personaJsonDraft: string
): PersonaTabSyncResult {
  if (fromTab === toTab) {
    return { ok: true, persona, personaJsonDraft };
  }
  if (fromTab === "json" && toTab === "form") {
    try {
      const parsed = parsePersonaJson(personaJsonDraft);
      return { ok: true, persona: parsed, personaJsonDraft };
    } catch (error) {
      const text = error instanceof Error ? error.message : "Invalid persona JSON.";
      return { ok: false, error: text };
    }
  }
  if (fromTab === "form" && toTab === "json") {
    return { ok: true, persona, personaJsonDraft: personaFieldsToJson(persona) };
  }
  return { ok: true, persona, personaJsonDraft };
}

export type InstanceVersionOption = { value: number; label: string };

export function managedInstanceVersionActionLabel(targetVersion: number, currentVersion: number): string {
  if (targetVersion < currentVersion) {
    return `Rollback to v${targetVersion}`;
  }
  if (targetVersion > currentVersion) {
    return `Upgrade to v${targetVersion}`;
  }
  return "Apply version";
}

export function buildInstanceVersionOptions(
  definitionId: string,
  currentVersion: number,
  inventory: AdminDefinitionInventoryItem[],
  publications: AdminDefinitionPublicationSummary[]
): InstanceVersionOption[] {
  const labels = new Map<number, string>();
  for (const item of inventory) {
    if (item.definitionId !== definitionId) {
      continue;
    }
    labels.set(item.version, `v${item.version} (${item.source} · ${item.status})`);
  }
  for (const publication of publications) {
    if (!labels.has(publication.version)) {
      labels.set(publication.version, `v${publication.version} (${publication.status})`);
    }
  }
  if (!labels.has(currentVersion)) {
    labels.set(currentVersion, `v${currentVersion} (current)`);
  }
  return [...labels.entries()]
    .sort((left, right) => left[0] - right[0])
    .map(([value, label]) => ({ value, label }));
}
