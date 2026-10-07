import { describe, expect, it } from "vitest";
import {
  applyCandidateJson,
  applyVoiceEnabled,
  candidateForPersistence,
  candidateToJson,
  candidatesEqual,
  patchRecord,
  readSkills,
  writeModelDefault,
  writePath,
  writeSkills
} from "./definitionCandidate";

const sample = {
  schemaVersion: 1,
  definitionId: "examiner",
  identity: { name: "Alex", role: "Examiner", description: "Practice.", tone: "Calm" },
  modelDefaults: { catalogKey: "scripted-alpha", reasoningEffort: "low" },
  metadata: { owner: "kept" },
  voice: { enabled: false, voiceId: "verse", speakingRate: 1 }
};

describe("definitionCandidate", () => {
  it("patches one field without dropping nested siblings", () => {
    const updated = patchRecord(sample, ["identity"], { name: "Guide" });
    expect(updated.identity).toEqual({
      name: "Guide",
      role: "Examiner",
      description: "Practice.",
      tone: "Calm"
    });
    expect(updated.modelDefaults).toEqual(sample.modelDefaults);
    expect(updated.metadata).toEqual(sample.metadata);
    expect(candidatesEqual(updated.voice, sample.voice)).toBe(true);
  });

  it("keeps unknown fields when a known path changes", () => {
    const withMarker = { ...sample, untouchedMarker: { nested: "preserve-me" } };
    const updated = writePath(withMarker, ["systemInstructions"], "Edited");
    expect(updated.untouchedMarker).toEqual({ nested: "preserve-me" });
    expect(updated.definitionId).toBe("examiner");
  });

  it("applies valid JSON and rejects invalid or non-object JSON", () => {
    const applied = applyCandidateJson(candidateToJson({ ...sample, systemInstructions: "From JSON" }));
    expect(applied.ok).toBe(true);
    if (applied.ok) {
      expect(applied.candidate.systemInstructions).toBe("From JSON");
      expect(applied.candidate.metadata).toEqual({ owner: "kept" });
    }

    expect(applyCandidateJson("{")).toMatchObject({ ok: false });
    expect(applyCandidateJson("[]")).toMatchObject({
      ok: false,
      error: "Advanced JSON must be an object."
    });
  });

  it("omits blank knowledge identities when preparing a save", () => {
    const candidate = {
      environment: {
        knowledgeSources: [
          { identity: "policy", title: "Policy", citation: "policy@demo" },
          { identity: "  ", title: "", citation: "" }
        ]
      }
    };
    expect(candidateForPersistence(candidate).environment).toEqual({
      knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo" }]
    });
  });

  it("selects configured speech aliases when voice turns on and clears them when voice turns off", () => {
    const textOnly = {
      ...sample,
      providerPreferences: {
        languageModel: "primary-llm",
        speechRecognizer: null,
        speechSynthesizer: null,
        interruptionClassifier: "heuristic"
      }
    };
    const voiced = applyVoiceEnabled(textOnly, true, {
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts"
    });
    expect(voiced.providerPreferences).toEqual({
      languageModel: "primary-llm",
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts",
      interruptionClassifier: "heuristic"
    });
    expect(candidateForPersistence(voiced).providerPreferences).toEqual(
      (voiced.providerPreferences as Record<string, unknown>)
    );
    const withoutDefaults = applyVoiceEnabled(textOnly, true, {
      speechRecognizer: null,
      speechSynthesizer: null
    });
    expect(withoutDefaults.providerPreferences).toMatchObject({
      speechRecognizer: null,
      speechSynthesizer: null
    });
    expect(candidateForPersistence(withoutDefaults).providerPreferences).toMatchObject({
      speechRecognizer: null,
      speechSynthesizer: null
    });

    const silenced = applyVoiceEnabled(voiced, false, {
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts"
    });
    expect(silenced.providerPreferences).toMatchObject({
      languageModel: "primary-llm",
      speechRecognizer: null,
      speechSynthesizer: null
    });
    expect(candidateForPersistence(silenced)).toBe(silenced);
    expect(candidateForPersistence({
      ...silenced,
      providerPreferences: {
        languageModel: "primary-llm",
        speechRecognizer: "  ",
        speechSynthesizer: ""
      }
    }).providerPreferences).toMatchObject({
      speechRecognizer: null,
      speechSynthesizer: null
    });
  });

  it("keeps custom speech aliases when voice stays on", () => {
    const voiced = {
      ...sample,
      voice: { enabled: true, voiceId: "verse", speakingRate: 1 },
      providerPreferences: {
        languageModel: "primary-llm",
        speechRecognizer: "custom-stt",
        speechSynthesizer: "custom-tts"
      }
    };
    expect(candidateForPersistence(voiced)).toBe(voiced);
  });

  it("clears model defaults only when both fields are blank", () => {
    const clearedKey = writeModelDefault(sample, "catalogKey", " ");
    expect(clearedKey.modelDefaults).toEqual({ catalogKey: null, reasoningEffort: "low" });
    const cleared = writeModelDefault(clearedKey, "reasoningEffort", "");
    expect(cleared.modelDefaults).toBeNull();
  });

  it("keeps a trailing comma in skill list fields while editing", () => {
    const edited = writeSkills(sample, [
      {
        id: "demo.skill",
        name: "Demo",
        description: "",
        procedure: "PROC",
        projection: "OnDemand", defaultEnabled: true,
        requiredCapabilities: "workspace.read,",
        resourcePaths: ""
      }
    ]);
    const skill = readSkills(edited)[0];
    expect(skill.projection).toBe("OnDemand");
    expect(skill.requiredCapabilities).toBe("workspace.read,");
  });

  it("does not infer missing or invalid projection and default enabled values", () => {
    const malformed = { ...sample, skills: [{ id: 'review', name: 'Review', description: 'Review', procedure: 'Review', projection: 0 }] };
    const skill = readSkills(malformed)[0];
    expect(skill.projection).toBeUndefined(); expect(skill.defaultEnabled).toBeUndefined();
    const changed = writeSkills(malformed, [{ ...skill, description: 'Edited' }]);
    expect(JSON.stringify(changed)).not.toContain('OnDemand'); expect(JSON.stringify(changed)).not.toContain('defaultEnabled');
  });

  it("round-trips skills and omits an empty list", () => {
    const withExtra = writeSkills(sample, [
      {
        id: "refund.handle",
        name: "Refund",
        description: "Handle a refund",
        procedure: "REFUND_PROCEDURE",
        projection: "OnDemand", defaultEnabled: true,
        requiredCapabilities: "workspace.read, chat.respond",
        resourcePaths: "notes/refund.md"
      }
    ]);
    const preserved = writeSkills(
      { ...withExtra, skills: [{ ...(withExtra.skills as object[])[0], marker: "keep" }] },
      [
        {
          id: "refund.handle",
          name: "Refund",
          description: "Handle a refund",
          procedure: "REFUND_PROCEDURE",
          projection: "OnDemand", defaultEnabled: true,
          requiredCapabilities: "workspace.read, chat.respond",
          resourcePaths: "notes/refund.md"
        }
      ]
    );
    const skill = (preserved.skills as Array<Record<string, unknown>>)[0];
    expect(skill).toMatchObject({
      id: "refund.handle",
      name: "Refund",
      procedure: "REFUND_PROCEDURE",
      projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: "workspace.read, chat.respond",
      resourcePaths: "notes/refund.md",
      marker: "keep"
    });
    expect(candidateForPersistence(preserved).skills).toEqual([
      {
        id: "refund.handle",
        name: "Refund",
        description: "Handle a refund",
        procedure: "REFUND_PROCEDURE",
        projection: "OnDemand", defaultEnabled: true,
        requiredCapabilities: ["workspace.read", "chat.respond"],
        resourcePaths: ["notes/refund.md"],
        marker: "keep"
      }
    ]);
    expect(candidateToJson(preserved)).toContain("REFUND_PROCEDURE");
    const applied = applyCandidateJson(candidateToJson(preserved));
    expect(applied.ok).toBe(true);
    if (applied.ok) {
      expect(applied.candidate.skills).toEqual(preserved.skills);
    }
    expect(writeSkills(preserved, []).skills).toBeUndefined();
  });
});
