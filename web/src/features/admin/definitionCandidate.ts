export type DefinitionCandidate = Record<string, unknown>;

const automationNumberRules = [
  { field: "maxActiveRegistrations", label: "Max active registrations", min: 1, max: 32 },
  { field: "oneShotHorizonDays", label: "One-shot horizon days", min: 1, max: 365 },
  { field: "minRecurrenceDays", label: "Minimum recurrence days", min: 1, max: 365 },
  { field: "minFixedIntervalSeconds", label: "Minimum fixed interval seconds", min: 60, max: 604800 }
] as const;

export function automationNumberErrors(candidate: DefinitionCandidate) {
  const policy = candidate.triggerPolicy;
  if (typeof policy !== "object" || policy === null || Array.isArray(policy)) return [];
  return automationNumberRules.flatMap(({ field, label, min, max }) => {
    const value = (policy as Record<string, unknown>)[field];
    // Only fixed interval has a constructor default; the other limits are required.
    if (value === undefined && field === "minFixedIntervalSeconds") return [];
    return typeof value === "number" && Number.isInteger(value) && value >= min && value <= max
      ? [] : [{ field, message: `${label} must be a whole number from ${min} to ${max}.` }];
  });
}

export type MetadataRow = {
  key: string;
  value: string;
};

export type VoiceAliasDefaults = {
  speechRecognizer: string | null;
  speechSynthesizer: string | null;
};

export type SkillDraft = {
  id: string;
  name: string;
  description: string;
  procedure: string;
  projection: "Always" | "OnDemand" | undefined;
  defaultEnabled: boolean | undefined;
  requiredCapabilities: string;
  resourcePaths: string;
};

export function readSkills(candidate: DefinitionCandidate): SkillDraft[] {
  const raw = candidate.skills;
  if (!Array.isArray(raw)) {
    return [];
  }

  return raw.map((item) => {
    const row = item && typeof item === "object" ? item as Record<string, unknown> : {};
    return {
      id: readSkillString(row.id),
      name: readSkillString(row.name),
      description: readSkillString(row.description),
      procedure: readSkillString(row.procedure),
      projection: row.projection === "Always" || row.projection === "OnDemand" ? row.projection : undefined,
      defaultEnabled: typeof row.defaultEnabled === "boolean" ? row.defaultEnabled : undefined,
      requiredCapabilities: readSkillListField(row.requiredCapabilities),
      resourcePaths: readSkillListField(row.resourcePaths)
    };
  });
}

export function writeSkills(candidate: DefinitionCandidate, skills: SkillDraft[]): DefinitionCandidate {
  if (skills.length === 0) {
    const next = { ...candidate };
    delete next.skills;
    return next;
  }

  const existing = Array.isArray(candidate.skills) ? candidate.skills : [];
  return {
    ...candidate,
    skills: skills.map((skill, index) => {
      const prior = existing[index] && typeof existing[index] === "object"
        ? { ...(existing[index] as Record<string, unknown>) }
        : {};
      return {
        ...prior,
        id: skill.id,
        name: skill.name,
        description: skill.description,
        procedure: skill.procedure,
        projection: skill.projection,
        defaultEnabled: skill.defaultEnabled,
        requiredCapabilities: skill.requiredCapabilities,
        resourcePaths: skill.resourcePaths
      };
    })
  };
}

function readSkillString(value: unknown) {
  return typeof value === "string" ? value : "";
}

function readSkillListField(value: unknown) {
  if (typeof value === "string") {
    return value;
  }

  return joinSkillList(value);
}

function joinSkillList(value: unknown) {
  if (!Array.isArray(value)) {
    return "";
  }

  return value.filter((item): item is string => typeof item === "string").join(", ");
}

function splitSkillList(value: string) {
  return value.split(",").map((item) => item.trim()).filter((item) => item.length > 0);
}

function normalizeSkillLists(candidate: DefinitionCandidate): DefinitionCandidate {
  const raw = candidate.skills;
  if (!Array.isArray(raw)) {
    return candidate;
  }

  return {
    ...candidate,
    skills: raw.map((item) => {
      if (item === null || typeof item !== "object" || Array.isArray(item)) {
        return item;
      }

      const row = item as Record<string, unknown>;
      return {
        ...row,
        requiredCapabilities: normalizeSkillListValue(row.requiredCapabilities),
        resourcePaths: normalizeSkillListValue(row.resourcePaths)
      };
    })
  };
}

function normalizeSkillListValue(value: unknown): string[] {
  if (typeof value === "string") {
    return splitSkillList(value);
  }

  if (!Array.isArray(value)) {
    return [];
  }

  return value.filter((item): item is string => typeof item === "string").map((item) => item.trim()).filter((item) => item.length > 0);
}

export function candidateForPersistence(candidate: DefinitionCandidate): DefinitionCandidate {
  return clearSpeechAliasesWhenVoiceOff(omitBlankKnowledgeSources(normalizeSkillLists(candidate)));
}

function clearSpeechAliasesWhenVoiceOff(candidate: DefinitionCandidate): DefinitionCandidate {
  if (readBoolean(candidate, ["voice", "enabled"])) {
    return candidate;
  }
  const rawRecognizer = readPath(candidate, ["providerPreferences", "speechRecognizer"]);
  const rawSynthesizer = readPath(candidate, ["providerPreferences", "speechSynthesizer"]);
  if (rawRecognizer == null && rawSynthesizer == null) {
    return candidate;
  }
  return patchRecord(candidate, ["providerPreferences"], {
    speechRecognizer: null,
    speechSynthesizer: null
  });
}

export function applyVoiceEnabled(
  candidate: DefinitionCandidate,
  enabled: boolean,
  defaults: VoiceAliasDefaults
): DefinitionCandidate {
  const next = patchRecord(candidate, ["voice"], { enabled });
  if (!enabled) {
    return patchRecord(next, ["providerPreferences"], {
      speechRecognizer: null,
      speechSynthesizer: null
    });
  }

  const recognizer = readString(next, ["providerPreferences", "speechRecognizer"]).trim();
  const synthesizer = readString(next, ["providerPreferences", "speechSynthesizer"]).trim();
  return patchRecord(next, ["providerPreferences"], {
    speechRecognizer: recognizer || defaults.speechRecognizer,
    speechSynthesizer: synthesizer || defaults.speechSynthesizer
  });
}

function omitBlankKnowledgeSources(candidate: DefinitionCandidate): DefinitionCandidate {
  const environment = readRecord(candidate, ["environment"]);
  const sources = environment?.knowledgeSources;
  if (!Array.isArray(sources)) {
    return candidate;
  }

  const kept = sources.filter((item) => {
    if (item === null || typeof item !== "object" || Array.isArray(item)) {
      return false;
    }
    const identity = (item as DefinitionCandidate).identity;
    return typeof identity === "string" && identity.trim().length > 0;
  });
  if (kept.length === sources.length) {
    return candidate;
  }
  return writePath(candidate, ["environment", "knowledgeSources"], kept);
}

export function cloneCandidate(candidate: DefinitionCandidate): DefinitionCandidate {
  return structuredClone(candidate);
}

export function candidateToJson(candidate: DefinitionCandidate): string {
  return JSON.stringify(candidate, null, 2);
}

export function candidatesEqual(left: unknown, right: unknown): boolean {
  return stableStringify(left) === stableStringify(right);
}

export function applyCandidateJson(
  text: string
): { ok: true; candidate: DefinitionCandidate } | { ok: false; error: string } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text) as unknown;
  } catch (error) {
    const message = error instanceof Error ? error.message : "Advanced JSON could not be parsed.";
    return { ok: false, error: message };
  }

  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return { ok: false, error: "Advanced JSON must be an object." };
  }

  return { ok: true, candidate: parsed as DefinitionCandidate };
}

export function readPath(candidate: DefinitionCandidate, path: string[]): unknown {
  let cursor: unknown = candidate;
  for (const key of path) {
    if (cursor === null || typeof cursor !== "object" || Array.isArray(cursor)) {
      return undefined;
    }
    cursor = (cursor as DefinitionCandidate)[key];
  }
  return cursor;
}

export function readRecord(candidate: DefinitionCandidate, path: string[]): DefinitionCandidate | null {
  const value = readPath(candidate, path);
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as DefinitionCandidate;
  }
  return null;
}

export function readString(candidate: DefinitionCandidate, path: string[]): string {
  const value = readPath(candidate, path);
  return typeof value === "string" ? value : "";
}

export function readBoolean(candidate: DefinitionCandidate, path: string[]): boolean {
  return readPath(candidate, path) === true;
}

export function readNumber(candidate: DefinitionCandidate, path: string[]): number | null {
  const value = readPath(candidate, path);
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export function readCandidateStringList(candidate: DefinitionCandidate, path: string[]): string[] {
  const value = readPath(candidate, path);
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter((item): item is string => typeof item === "string");
}

export function writePath(
  candidate: DefinitionCandidate,
  path: string[],
  value: unknown
): DefinitionCandidate {
  if (path.length === 0) {
    return candidate;
  }
  const [head, ...rest] = path;
  if (rest.length === 0) {
    const next = { ...candidate };
    if (value === undefined) {
      delete next[head];
    } else {
      next[head] = value;
    }
    return next;
  }

  const current = candidate[head];
  const child =
    current !== null && typeof current === "object" && !Array.isArray(current)
      ? (current as DefinitionCandidate)
      : {};
  return { ...candidate, [head]: writePath(child, rest, value) };
}

export function patchRecord(
  candidate: DefinitionCandidate,
  path: string[],
  patch: Record<string, unknown>,
  defaults?: Record<string, unknown>
): DefinitionCandidate {
  const current = readRecord(candidate, path);
  const base = current ? { ...current } : { ...(defaults ?? {}) };
  return writePath(candidate, path, { ...base, ...patch });
}

export function writeNullableString(
  candidate: DefinitionCandidate,
  path: string[],
  value: string
): DefinitionCandidate {
  return writePath(candidate, path, value.trim().length === 0 ? null : value);
}

export function writeModelDefault(
  candidate: DefinitionCandidate,
  field: "catalogKey" | "reasoningEffort",
  value: string
): DefinitionCandidate {
  const current = readRecord(candidate, ["modelDefaults"]);
  const next = {
    catalogKey:
      field === "catalogKey" ? blankToNull(value) : blankToNull(readString(current ?? {}, ["catalogKey"])),
    reasoningEffort:
      field === "reasoningEffort"
        ? blankToNull(value)
        : blankToNull(readString(current ?? {}, ["reasoningEffort"]))
  };
  if (next.catalogKey === null && next.reasoningEffort === null) {
    return writePath(candidate, ["modelDefaults"], null);
  }
  return writePath(candidate, ["modelDefaults"], next);
}

export function readMetadataRows(candidate: DefinitionCandidate): MetadataRow[] {
  const current = readRecord(candidate, ["metadata"]);
  if (!current) {
    return [];
  }
  return Object.entries(current)
    .filter((entry): entry is [string, string] => typeof entry[1] === "string")
    .map(([key, value]) => ({ key, value }));
}

export function writeMetadataRows(
  candidate: DefinitionCandidate,
  rows: MetadataRow[]
): DefinitionCandidate {
  const current = readRecord(candidate, ["metadata"]);
  const metadata: Record<string, unknown> = {};
  if (current) {
    for (const [key, value] of Object.entries(current)) {
      if (typeof value !== "string") {
        metadata[key] = value;
      }
    }
  }
  for (const row of rows) {
    metadata[row.key] = row.value;
  }
  return writePath(candidate, ["metadata"], metadata);
}

export const memoryPolicyDefaults: Record<string, unknown> = {
  sessionMemory: false,
  identityUserPromotion: false,
  identityUserRetrieval: false,
  userPromotion: false,
  userRetrieval: false
};

export const triggerPolicyDefaults: Record<string, unknown> = {
  enabled: false,
  allowUserScheduling: false,
  allowOneShot: false,
  allowDaily: false,
  allowWeekly: false,
  allowIndefiniteRecurrence: false,
  maxActiveRegistrations: 1,
  oneShotHorizonDays: 1,
  minRecurrenceDays: 1,
  allowedSourceKinds: [],
  allowFixedInterval: false,
  minFixedIntervalSeconds: 60
};

function blankToNull(value: string): string | null {
  return value.trim().length === 0 ? null : value;
}

function stableStringify(value: unknown): string {
  if (value === undefined) {
    return "undefined";
  }
  if (value === null || typeof value !== "object") {
    return JSON.stringify(value) ?? "null";
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableStringify(item)).join(",")}]`;
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  return `{${keys.map((key) => `${JSON.stringify(key)}:${stableStringify(record[key])}`).join(",")}}`;
}

export function skillIdFromName(name: string, existing: string[]): string {
  let stem = name.replace(/[A-Z]/g, character => character.toLowerCase()).replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  if (!/^[a-z]/.test(stem)) stem = `skill-${stem}`;
  stem = stem.slice(0, 64).replace(/-+$/g, '');
  if (!existing.includes(stem)) return stem;
  for (let suffix = 2; suffix <= 999; suffix++) {
    const tail = `-${suffix}`;
    const id = stem.slice(0, 64 - tail.length).replace(/-+$/g, '') + tail;
    if (!existing.includes(id)) return id;
  }
  throw new Error('No available Skill identity remains for this name.');
}
