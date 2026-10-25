import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ExecutionModelFields } from "./ExecutionModelFields";
import type { ModelDescriptor } from "../../services/api";

const models = [
  { offeringId: "alpha", displayName: "Alpha", supportedReasoningEfforts: ["high", "medium", "low"], defaultReasoningEffort: "medium" },
  { offeringId: "beta", displayName: "Beta", supportedReasoningEfforts: ["max", "high"], defaultReasoningEffort: "max" }
] as ModelDescriptor[];
const props = { models, offeringId: "alpha", reasoningEffort: "high", disabled: false,
  modelLabel: "Execution model", effortLabel: "Execution reasoning", defaultLabel: "Inherited" };

describe("shared unattended and Automation model controls", () => {
  it("retains an unavailable saved model and effort until an explicit model choice", () => {
    const onChange = vi.fn();
    render(<ExecutionModelFields {...props} offeringId="dca30000-0000-4000-8000-000000000099" reasoningEffort="high" onChange={onChange} />);
    expect(screen.getByText('Unavailable · dca30000-0000-4000-8000-000000000099')).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Execution reasoning' })).toBeDisabled();
    expect(screen.getByText('high · Unavailable')).toBeVisible(); expect(onChange).not.toHaveBeenCalled();
  });
  it("uses the same ascending effort policy as Chat", async () => {
    render(<ExecutionModelFields {...props} onChange={vi.fn()} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Execution reasoning" }));
    await screen.findByRole("listbox");
    expect([...document.querySelectorAll(".ant-select-item-option-content")].map(option => option.textContent)).toEqual(["Model default", "low", "medium", "high"]);
  });
  it("exposes reasoning for an inherited execution default without forcing a model override", async () => {
    const onChange = vi.fn();
    render(<ExecutionModelFields {...props} offeringId="" defaultOfferingId="alpha" onChange={onChange} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Execution reasoning" }));
    fireEvent.click(await screen.findByText("low", { selector: '.ant-select-item-option-content' }));
    expect(onChange).toHaveBeenCalledWith("", "low");
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
