import { describe, expect, it } from "vitest";
import {
  applyCandidateJson,
  candidateForPersistence,
  candidateToJson,
  candidatesEqual,
  normalizeVoiceProviders,
  patchRecord,
  writeModelDefault,
  writePath
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

  it("fills default speech aliases when voice is on and clears them when voice is off", () => {
    const textOnly = {
      ...sample,
      providerPreferences: {
        languageModel: "primary-llm",
        speechRecognizer: null,
        speechSynthesizer: null,
        interruptionClassifier: "heuristic"
      }
    };
    const voiced = normalizeVoiceProviders(
      patchRecord(textOnly, ["voice"], { enabled: true })
    );
    expect(voiced.providerPreferences).toEqual({
      languageModel: "primary-llm",
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts",
      interruptionClassifier: "heuristic"
    });
    expect(candidateForPersistence(voiced).providerPreferences).toEqual(
      (voiced.providerPreferences as Record<string, unknown>)
    );

    const silenced = normalizeVoiceProviders(
      patchRecord(voiced, ["voice"], { enabled: false })
    );
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
});
