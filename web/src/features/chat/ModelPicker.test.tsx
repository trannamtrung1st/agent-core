import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
  DEFAULT_MODEL_KEY,
  ModelPicker,
  nextEffortForModel,
  effortSelectValue,
  modelSelectValue,
  selectedCatalogModel,
  wireModelSelectionKey
} from "./ModelPicker";

const models = [
  {
    key: "scripted-alpha",
    displayName: "Scripted Alpha",
    tools: true,
    vision: false,
    structuredOutput: false,
    reasoning: true,
    supportedReasoningEfforts: ["low", "medium", "high"],
    defaultReasoningEffort: "medium"
  },
  {
    key: "scripted-beta",
    displayName: "Scripted Beta",
    tools: true,
    vision: false,
    structuredOutput: true,
    reasoning: false,
    supportedReasoningEfforts: [],
    defaultReasoningEffort: null
  },
  {
    key: "scripted-vision",
    displayName: "Scripted Vision",
    tools: true,
    vision: true,
    structuredOutput: false,
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
  it("shows capability icons for each enabled catalog flag", async () => {
    render(
      <ModelPicker
        models={models}
        defaultKey="scripted-alpha"
        modelValue="scripted-alpha"
        effortValue="medium"
        onModelChange={vi.fn()}
        onEffortChange={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    const listbox = await screen.findByRole("listbox");
    expect(within(listbox).getAllByRole("img", { name: "Vision" })).toHaveLength(1);
    expect(within(listbox).getAllByRole("img", { name: "Reasoning capable" })).toHaveLength(1);
    expect(within(listbox).getAllByRole("img", { name: "Tools" })).toHaveLength(3);
    expect(within(listbox).getAllByRole("img", { name: "Structured output" })).toHaveLength(1);

    const alphaOption = within(listbox).getByTitle("Scripted Alpha").closest('[role="option"]');
    expect(alphaOption).toBeTruthy();
    expect(within(alphaOption as HTMLElement).getByRole("img", { name: "Reasoning capable" })).toBeInTheDocument();
    expect(within(alphaOption as HTMLElement).queryByRole("img", { name: "Vision" })).not.toBeInTheDocument();

    const betaOption = within(listbox).getByTitle("Scripted Beta").closest('[role="option"]');
    expect(within(betaOption as HTMLElement).getByRole("img", { name: "Structured output" })).toBeInTheDocument();
  });

  it("lists each catalog model once and marks Default on the system default", async () => {
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

    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    const listbox = await screen.findByRole("listbox");
    expect(within(listbox).getAllByTitle("Scripted Alpha")).toHaveLength(1);
    expect(within(listbox).getByTitle("Scripted Beta")).toBeInTheDocument();
    expect(within(listbox).getByText("Default")).toBeInTheDocument();
    fireEvent.click(within(listbox).getByTitle("Scripted Beta"));
    expect(onModelChange).toHaveBeenCalledWith("scripted-beta");
  });

  it("shows reasoning in the chip and slider inside the model menu", async () => {
    const onEffortChange = vi.fn();
    render(
      <ModelPicker
        models={models}
        defaultKey="scripted-alpha"
        modelValue="scripted-alpha"
        effortValue="medium"
        layout="row"
        onModelChange={vi.fn()}
        onEffortChange={onEffortChange}
      />
    );

    expect(screen.getByLabelText("Reasoning effort")).toHaveTextContent("Medium");
    expect(screen.getByRole("button", { name: "Model" })).toContainElement(screen.getByLabelText("Reasoning effort"));
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    expect(await screen.findByRole("slider")).toBeInTheDocument();
    const listbox = await screen.findByRole("listbox");
    expect(within(listbox).getByTitle("Scripted Alpha")).toBeInTheDocument();
  });

  it("renders after catalog hydration without changing hook order", () => {
    const props = {
      defaultKey: "scripted-alpha" as const,
      modelValue: "scripted-alpha",
      effortValue: "medium" as const,
      onModelChange: vi.fn(),
      onEffortChange: vi.fn()
    };
    const { rerender } = render(<ModelPicker models={[]} {...props} />);
    expect(screen.queryByRole("button", { name: "Model" })).not.toBeInTheDocument();
    rerender(<ModelPicker models={models} {...props} />);
    expect(screen.getByRole("button", { name: "Model" })).toBeInTheDocument();
  });
});


describe("ascending reasoning slider", () => {
  it("maps keyboard increases to higher wire efforts with descending catalog input", async () => {
    const onEffortChange = vi.fn();
    const descending = [{ ...models[0], supportedReasoningEfforts: ["max", "xhigh", "high", "medium", "low", "minimal", "none"] }];
    render(<ModelPicker models={descending} defaultKey="scripted-alpha" modelValue="scripted-alpha"
      effortValue="low" onModelChange={vi.fn()} onEffortChange={onEffortChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    const slider = await screen.findByRole("slider");
    expect(slider).toHaveAttribute("aria-valuemin", "0");
    expect(slider).toHaveAttribute("aria-valuemax", "6");
    expect(slider).toHaveAttribute("aria-valuenow", "2");
    expect(slider).toHaveAttribute("aria-valuetext", "Low");
    fireEvent.keyDown(slider, { key: "ArrowRight", keyCode: 39 });
    fireEvent.keyUp(slider, { key: "ArrowRight", keyCode: 39 });
    expect(onEffortChange).toHaveBeenLastCalledWith("medium");
    fireEvent.keyDown(slider, { key: "End", keyCode: 35 });
    fireEvent.keyUp(slider, { key: "End", keyCode: 35 });
    expect(onEffortChange).toHaveBeenLastCalledWith("max");
    fireEvent.keyDown(slider, { key: "Home", keyCode: 36 });
    fireEvent.keyUp(slider, { key: "Home", keyCode: 36 });
    expect(onEffortChange).toHaveBeenLastCalledWith("none");
  });
  it("retains a supported value on model switch and falls back to the new default", () => {
    expect(nextEffortForModel(models, "scripted-alpha", null, "high")).toBe("high");
    expect(nextEffortForModel(models, "scripted-alpha", null, "max")).toBe("medium");
  });
  it("presents adaptive mode without pretending it is an intensity", async () => {
    render(<ModelPicker models={[{ ...models[0], supportedReasoningEfforts: ["high", "adaptive", "low"] }]}
      defaultKey="scripted-alpha" modelValue="scripted-alpha" effortValue="adaptive"
      onModelChange={vi.fn()} onEffortChange={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    expect(await screen.findByRole("combobox", { name: "Reasoning mode" })).toBeInTheDocument();
    expect(screen.queryByRole("slider")).not.toBeInTheDocument();
  });
});
