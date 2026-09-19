import { describe, expect, it } from "vitest";
import { DEFAULT_MODEL_KEY, effortSelectValue, modelSelectValue, selectedCatalogModel } from "./ModelPicker";

const models = [
  {
    key: "scripted-alpha",
    displayName: "Scripted Alpha",
    reasoning: true,
    supportedReasoningEfforts: ["low", "medium", "high"],
    defaultReasoningEffort: "medium"
  },
  {
    key: "scripted-beta",
    displayName: "Scripted Beta",
    reasoning: false,
    supportedReasoningEfforts: [],
    defaultReasoningEffort: null
  }
];

describe("modelSelectValue", () => {
  it("starts a new chat on Default", () => {
    expect(modelSelectValue(null, null, false)).toBe(DEFAULT_MODEL_KEY);
    expect(modelSelectValue(null, "scripted-beta", false)).toBe("scripted-beta");
  });

  it("restores a persisted session selection instead of the global default", () => {
    expect(modelSelectValue("scripted-beta", DEFAULT_MODEL_KEY, true)).toBe("scripted-beta");
  });
});

describe("effortSelectValue", () => {
  it("uses the selected model default before a session exists", () => {
    const alpha = selectedCatalogModel(models, DEFAULT_MODEL_KEY, "scripted-alpha");
    expect(effortSelectValue(alpha, null, null, false)).toBe("medium");
  });

  it("hides effort when the model does not support reasoning", () => {
    const beta = selectedCatalogModel(models, "scripted-beta", "scripted-alpha");
    expect(effortSelectValue(beta, "medium", "high", true)).toBeNull();
  });

  it("keeps a persisted effort on an existing session", () => {
    const alpha = selectedCatalogModel(models, "scripted-alpha", "scripted-alpha");
    expect(effortSelectValue(alpha, "high", "medium", true)).toBe("high");
  });
});
