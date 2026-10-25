import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
  INHERIT_MODEL,
  ModelPicker,
  nextEffortForModel,
  effortSelectValue,
  modelSelectValue,
  selectedCatalogModel
} from "./ModelPicker";

const models = [
  {
    offeringId: "dca30000-0000-4000-8000-000000000001",
    displayName: "Scripted Alpha",
    tools: true,
    vision: false,
    structuredOutput: false,
    reasoning: true,
    supportedReasoningEfforts: ["low", "medium", "high"],
    defaultReasoningEffort: "medium"
  },
  {
    offeringId: "dca30000-0000-4000-8000-000000000002",
    displayName: "Scripted Beta",
    tools: true,
    vision: false,
    structuredOutput: true,
    reasoning: false,
    supportedReasoningEfforts: [],
    defaultReasoningEffort: null
  },
  {
    offeringId: "dca30000-0000-4000-8000-000000000003",
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
    expect(modelSelectValue(null, null, false, "dca30000-0000-4000-8000-000000000001")).toBe(INHERIT_MODEL);
    expect(modelSelectValue(null, INHERIT_MODEL, false, "dca30000-0000-4000-8000-000000000001")).toBe(INHERIT_MODEL);
    expect(modelSelectValue(null, "dca30000-0000-4000-8000-000000000002", false, "dca30000-0000-4000-8000-000000000001")).toBe("dca30000-0000-4000-8000-000000000002");
  });

  it("restores a persisted session selection instead of the global default", () => {
    expect(modelSelectValue("dca30000-0000-4000-8000-000000000002", INHERIT_MODEL, true, "dca30000-0000-4000-8000-000000000001")).toBe("dca30000-0000-4000-8000-000000000002");
  });
});

it('names an unavailable inherited default without exposing the internal selection token', () => {
  render(<ModelPicker models={models} defaultOfferingId={null} modelValue={INHERIT_MODEL} effortValue={null} onModelChange={vi.fn()} onEffortChange={vi.fn()} />);
  expect(screen.getByRole('button', { name: 'Model' })).toHaveTextContent('Organization default · Unavailable');
});

describe("effortSelectValue", () => {
  it("shows inherited tenant effort without replacing an explicit choice", () => {
    expect(effortSelectValue(models[0], null, null, false, "high")).toBe("high");
    expect(effortSelectValue(models[0], null, "low", false, "high")).toBe("low");
  });
  it("uses the selected model default before a session exists", () => {
    const alpha = selectedCatalogModel(models, INHERIT_MODEL, "dca30000-0000-4000-8000-000000000001");
    expect(effortSelectValue(alpha, null, null, false)).toBe("medium");
  });

  it("hides effort when the model does not support reasoning", () => {
    const beta = selectedCatalogModel(models, "dca30000-0000-4000-8000-000000000002", "dca30000-0000-4000-8000-000000000001");
    expect(effortSelectValue(beta, "medium", "high", true)).toBeNull();
  });

  it("keeps a persisted effort on an existing session", () => {
    const alpha = selectedCatalogModel(models, "dca30000-0000-4000-8000-000000000001", "dca30000-0000-4000-8000-000000000001");
    expect(effortSelectValue(alpha, "high", "medium", true)).toBe("high");
  });
});

describe("ModelPicker", () => {
  it("distinguishes inheritance from explicitly selecting the same default offering", async () => {
    const onModelChange = vi.fn();
    const { rerender } = render(<ModelPicker models={models} defaultOfferingId={models[0].offeringId}
      defaultSource="agentDefault" modelValue={INHERIT_MODEL} effortValue="high" onModelChange={onModelChange} onEffortChange={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Model' }));
    expect(await screen.findByTitle('Use Agent default')).toHaveAttribute('aria-selected', 'true');
    const explicit = screen.getByTitle('Scripted Alpha');
    expect(explicit).toHaveAttribute('aria-selected', 'false');
    fireEvent.click(explicit);
    expect(onModelChange).toHaveBeenLastCalledWith(models[0].offeringId);
    rerender(<ModelPicker models={models} defaultOfferingId={models[0].offeringId}
      defaultSource="agentDefault" modelValue={models[0].offeringId} effortValue="medium" onModelChange={onModelChange} onEffortChange={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Model' }));
    fireEvent.click(await screen.findByTitle('Use Agent default'));
    expect(onModelChange).toHaveBeenLastCalledWith(INHERIT_MODEL);
  });

  it("distinguishes the same model through separate connections", async () => {
    const onModelChange = vi.fn();
    render(<ModelPicker models={[{ ...models[0], displayName: 'Shared model', connectionLabel: 'Production' },
      { ...models[0], offeringId: models[1].offeringId, displayName: 'Shared model', connectionLabel: 'Research' }]}
      defaultOfferingId={models[0].offeringId} modelValue={models[0].offeringId} effortValue="high"
      onModelChange={onModelChange} onEffortChange={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Model' })).toHaveTextContent('Shared model · Production');
    fireEvent.click(screen.getByRole('button', { name: 'Model' }));
    fireEvent.click(await screen.findByTitle('Shared model · Research'));
    expect(onModelChange).toHaveBeenCalledWith(models[1].offeringId);
  });

  it("keeps an unavailable saved choice visible without selecting another model", () => {
    const onModelChange = vi.fn();
    render(<ModelPicker models={models} defaultOfferingId={models[0].offeringId} modelValue="dca30000-0000-4000-8000-000000000099"
      effortValue="high" onModelChange={onModelChange} onEffortChange={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Model' })).toHaveTextContent('Unavailable');
    expect(onModelChange).not.toHaveBeenCalled();
  });
  it("shows capability icons for each enabled catalog flag", async () => {
    render(
      <ModelPicker
        models={models}
        defaultOfferingId="dca30000-0000-4000-8000-000000000001"
        modelValue="dca30000-0000-4000-8000-000000000001"
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
        defaultOfferingId="dca30000-0000-4000-8000-000000000001"
        modelValue="dca30000-0000-4000-8000-000000000001"
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
    expect(onModelChange).toHaveBeenCalledWith("dca30000-0000-4000-8000-000000000002");
  });

  it("shows reasoning in the chip and slider inside the model menu", async () => {
    const onEffortChange = vi.fn();
    render(
      <ModelPicker
        models={models}
        defaultOfferingId="dca30000-0000-4000-8000-000000000001"
        modelValue="dca30000-0000-4000-8000-000000000001"
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
      defaultOfferingId: "dca30000-0000-4000-8000-000000000001" as const,
      modelValue: "dca30000-0000-4000-8000-000000000001",
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
    render(<ModelPicker models={descending} defaultOfferingId="dca30000-0000-4000-8000-000000000001" modelValue="dca30000-0000-4000-8000-000000000001"
      effortValue="low" onModelChange={vi.fn()} onEffortChange={onEffortChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    const slider = await screen.findByRole("slider");
    expect(slider).toHaveAttribute("aria-valuemin", "0");
    expect(slider).toHaveAttribute("aria-valuemax", "6");
    expect(slider).toHaveAttribute("aria-valuenow", "2");
    expect(slider).toHaveAttribute("aria-valuetext", "Low");
    fireEvent.keyDown(slider, { offeringId: "ArrowRight", keyCode: 39 });
    fireEvent.keyUp(slider, { offeringId: "ArrowRight", keyCode: 39 });
    expect(onEffortChange).toHaveBeenLastCalledWith("medium");
    fireEvent.keyDown(slider, { offeringId: "End", keyCode: 35 });
    fireEvent.keyUp(slider, { offeringId: "End", keyCode: 35 });
    expect(onEffortChange).toHaveBeenLastCalledWith("max");
    fireEvent.keyDown(slider, { offeringId: "Home", keyCode: 36 });
    fireEvent.keyUp(slider, { offeringId: "Home", keyCode: 36 });
    expect(onEffortChange).toHaveBeenLastCalledWith("none");
  });
  it("retains a supported value on model switch and falls back to the new default", () => {
    expect(nextEffortForModel(models, "dca30000-0000-4000-8000-000000000001", null, "high")).toBe("high");
    expect(nextEffortForModel(models, "dca30000-0000-4000-8000-000000000001", null, "max")).toBe("medium");
  });
  it("presents adaptive mode without pretending it is an intensity", async () => {
    render(<ModelPicker models={[{ ...models[0], supportedReasoningEfforts: ["high", "adaptive", "low"] }]}
      defaultOfferingId="dca30000-0000-4000-8000-000000000001" modelValue="dca30000-0000-4000-8000-000000000001" effortValue="adaptive"
      onModelChange={vi.fn()} onEffortChange={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    expect(await screen.findByRole("combobox", { name: "Reasoning mode" })).toBeInTheDocument();
    expect(screen.queryByRole("slider")).not.toBeInTheDocument();
  });
});
