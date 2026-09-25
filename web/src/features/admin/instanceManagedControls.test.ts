import { describe, expect, it } from "vitest";
import {
  buildInstanceVersionOptions,
  isPersonaDraftDirty,
  parsePersonaJson,
  personaFieldsToJson,
  syncPersonaOnTabChange
} from "./instanceManagedControls";

const samplePersona = {
  name: "Alex",
  role: "Examiner",
  description: "Practice speaking.",
  tone: "Supportive"
};

describe("isPersonaDraftDirty", () => {
  it("detects form and JSON drafts that differ from saved persona", () => {
    expect(isPersonaDraftDirty(samplePersona, "form", { ...samplePersona, role: "Coach" }, "")).toBe(true);
    const json = personaFieldsToJson({ ...samplePersona, tone: "Calm" });
    expect(isPersonaDraftDirty(samplePersona, "json", samplePersona, json)).toBe(true);
    expect(isPersonaDraftDirty(samplePersona, "json", samplePersona, "{")).toBe(true);
    expect(isPersonaDraftDirty(samplePersona, "form", samplePersona, "")).toBe(false);
  });
});

describe("instanceManagedControls persona sync", () => {
  it("round-trips Form to JSON to Form", () => {
    const edited = { ...samplePersona, role: "Coach" };
    const toJson = syncPersonaOnTabChange("form", "json", edited, "");
    expect(toJson.ok).toBe(true);
    if (!toJson.ok) {
      return;
    }
    expect(parsePersonaJson(toJson.personaJsonDraft).role).toBe("Coach");

    const toForm = syncPersonaOnTabChange("json", "form", edited, toJson.personaJsonDraft);
    expect(toForm.ok).toBe(true);
    if (!toForm.ok) {
      return;
    }
    expect(toForm.persona.role).toBe("Coach");
  });

  it("rejects invalid JSON when leaving the JSON tab", () => {
    const result = syncPersonaOnTabChange("json", "form", samplePersona, "{ invalid");
    expect(result.ok).toBe(false);
    if (result.ok) {
      return;
    }
    expect(result.error).toContain("JSON");
  });

  it("serializes and parses persona fields", () => {
    const json = personaFieldsToJson(samplePersona);
    expect(parsePersonaJson(json)).toEqual(samplePersona);
  });
});

describe("buildInstanceVersionOptions", () => {
  it("includes built-in inventory versions when durable publications exist", () => {
    const options = buildInstanceVersionOptions(
      "examiner",
      2,
      [
        {
          definitionId: "examiner",
          version: 1,
          source: "builtIn",
          status: "published",
          displayName: "Examiner v1"
        },
        {
          definitionId: "examiner",
          version: 2,
          source: "builtIn",
          status: "published",
          displayName: "Examiner v2"
        }
      ],
      [{ definitionId: "examiner", version: 3, status: "published", metadataRevision: 1, publishedAt: "" }]
    );
    expect(options.map((item) => item.value)).toEqual([1, 2, 3]);
    expect(options[0]?.label).toContain("builtIn");
    expect(options[2]?.label).toContain("published");
  });
});
