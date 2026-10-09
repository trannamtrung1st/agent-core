import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ExecutionModelFields } from "./ExecutionModelFields";
import type { ModelDescriptor } from "../../services/api";

const models = [
  { key: "alpha", displayName: "Alpha", supportedReasoningEfforts: ["high", "medium", "low"], defaultReasoningEffort: "medium" },
  { key: "beta", displayName: "Beta", supportedReasoningEfforts: ["max", "high"], defaultReasoningEffort: "max" }
] as ModelDescriptor[];
const props = { models, modelKey: "alpha", reasoningEffort: "high", disabled: false,
  modelLabel: "Execution model", effortLabel: "Execution reasoning", defaultLabel: "Inherited" };

describe("shared unattended and Automation model controls", () => {
  it("uses the same ascending effort policy as Chat", async () => {
    render(<ExecutionModelFields {...props} onChange={vi.fn()} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Execution reasoning" }));
    await screen.findByRole("listbox");
    expect([...document.querySelectorAll(".ant-select-item-option-content")].map(option => option.textContent)).toEqual(["Model default", "low", "medium", "high"]);
  });
  it("preserves valid effort when switching models", async () => {
    const onChange = vi.fn();
    render(<ExecutionModelFields {...props} onChange={onChange} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Execution model" }));
    fireEvent.click(await screen.findByText("Beta"));
    expect(onChange).toHaveBeenCalledWith("beta", "high");
  });
  it("uses the selected model default when the previous effort is unsupported", async () => {
    const onChange = vi.fn();
    render(<ExecutionModelFields {...props} reasoningEffort="low" onChange={onChange} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Execution model" }));
    fireEvent.click(await screen.findByText("Beta"));
    expect(onChange).toHaveBeenCalledWith("beta", "max");
  });
});
