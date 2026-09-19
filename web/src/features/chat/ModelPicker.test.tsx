import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
  DEFAULT_MODEL_KEY,
  ModelPicker,
  effortSelectValue,
  modelSelectValue,
  selectedCatalogModel,
  wireModelSelectionKey
} from "./ModelPicker";

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
  it("starts a new chat on the catalog default model", () => {
    expect(modelSelectValue(null, null, false, "scripted-alpha")).toBe("scripted-alpha");
    expect(modelSelectValue(null, DEFAULT_MODEL_KEY, false, "scripted-alpha")).toBe("scripted-alpha");
    expect(modelSelectValue(null, "scripted-beta", false, "scripted-alpha")).toBe("scripted-beta");
  });

  it("restores a persisted session selection instead of the global default", () => {
    expect(modelSelectValue("scripted-beta", DEFAULT_MODEL_KEY, true, "scripted-alpha")).toBe("scripted-beta");
  });
});

describe("wireModelSelectionKey", () => {
  it("keeps new-chat default as the Default sentinel", () => {
    expect(wireModelSelectionKey("scripted-alpha", "scripted-alpha", false)).toBe(DEFAULT_MODEL_KEY);
    expect(wireModelSelectionKey("scripted-beta", "scripted-alpha", false)).toBe("scripted-beta");
  });

  it("sends the catalog key for an existing session", () => {
    expect(wireModelSelectionKey("scripted-alpha", "scripted-alpha", true)).toBe("scripted-alpha");
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

describe("ModelPicker", () => {
  it("lists each catalog model once and marks Default on the system default", () => {
    const onModelChange = vi.fn();
    render(
      <ModelPicker
        models={models}
        defaultKey="scripted-alpha"
        modelValue="scripted-alpha"
        effortValue="medium"
        onModelChange={onModelChange}
        onEffortChange={vi.fn()}
      />
    );

    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Model" }));
    const listbox = screen.getByRole("listbox");
    expect(within(listbox).getAllByTitle("Scripted Alpha")).toHaveLength(1);
    expect(within(listbox).getByTitle("Scripted Beta")).toBeInTheDocument();
    expect(within(listbox).getByText("Default")).toBeInTheDocument();
    fireEvent.click(within(listbox).getByTitle("Scripted Beta"));
    expect(onModelChange).toHaveBeenCalledWith("scripted-beta");
  });
});
