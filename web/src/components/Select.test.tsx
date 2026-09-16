import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Select } from "./Select";

const options = [
  { value: "examiner", label: "Alex — Speaking examiner" },
  { value: "support", label: "Sam — Customer support representative" }
];

describe("Select", () => {
  it("exposes the identity label and selects an option", () => {
    const onChange = vi.fn();
    render(<Select aria-label="Identity" value="examiner" options={options} onChange={onChange} />);

    expect(screen.getByLabelText("Identity")).toHaveTextContent("Alex — Speaking examiner");

    fireEvent.click(screen.getByLabelText("Identity"));
    fireEvent.pointerDown(screen.getByRole("option", { name: "Sam — Customer support representative" }));
    expect(onChange).toHaveBeenCalledWith("support");
  });

  it("selects an option even if the trigger blurs before click", () => {
    const onChange = vi.fn();
    render(<Select aria-label="Identity" value="examiner" options={options} onChange={onChange} />);

    const trigger = screen.getByLabelText("Identity");
    fireEvent.click(trigger);
    const option = screen.getByRole("option", { name: "Sam — Customer support representative" });
    fireEvent.pointerDown(option);
    fireEvent.focusOut(trigger, { relatedTarget: null });

    expect(onChange).toHaveBeenCalledWith("support");
  });

  it("supports keyboard selection", () => {
    const onChange = vi.fn();
    render(<Select aria-label="Identity" value="examiner" options={options} onChange={onChange} />);

    const trigger = screen.getByLabelText("Identity");
    fireEvent.keyDown(trigger, { key: "ArrowDown" });
    fireEvent.keyDown(trigger, { key: "ArrowDown" });
    fireEvent.keyDown(trigger, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("support");
  });

  it("closes on escape and outside click", () => {
    render(<Select aria-label="Identity" value="examiner" options={options} onChange={vi.fn()} />);

    const trigger = screen.getByLabelText("Identity");
    fireEvent.click(trigger);
    expect(screen.getByRole("listbox", { name: "Identity" })).toBeInTheDocument();

    fireEvent.keyDown(trigger, { key: "Escape" });
    expect(screen.queryByRole("listbox", { name: "Identity" })).not.toBeInTheDocument();

    fireEvent.click(trigger);
    expect(screen.getByRole("listbox", { name: "Identity" })).toBeInTheDocument();
    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole("listbox", { name: "Identity" })).not.toBeInTheDocument();
  });
});
