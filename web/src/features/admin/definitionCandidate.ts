export type DefinitionCandidate = Record<string, unknown>;

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
  activationKeywords: string;
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
      activationKeywords: joinSkillList(row.activationKeywords),
      requiredCapabilities: joinSkillList(row.requiredCapabilities),
      resourcePaths: joinSkillList(row.resourcePaths)
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
        activationKeywords: splitSkillList(skill.activationKeywords),
        requiredCapabilities: splitSkillList(skill.requiredCapabilities),
        resourcePaths: splitSkillList(skill.resourcePaths)
      };
    })
  };
}

function readSkillString(value: unknown) {
  return typeof value === "string" ? value : "";
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

export function candidateForPersistence(candidate: DefinitionCandidate): DefinitionCandidate {
  return clearSpeechAliasesWhenVoiceOff(omitBlankKnowledgeSources(candidate));
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
