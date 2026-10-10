import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { AutomationTriggerPermissions, automationTriggerPermissionSummary } from "./AutomationTriggerPermissions";

function renderPermissions(initial: string[], enabled = true, disabled = false) {
  function Editor() {
    const [values, setValues] = useState(initial);
    return <><AutomationTriggerPermissions values={values} enabled={enabled} disabled={disabled} onChange={setValues} /><output aria-label="Saved permissions">{JSON.stringify(values)}</output></>;
  }
  render(<Editor />);
}
const checkbox = (name: string) => screen.getByRole("checkbox", { name });
const saved = () => JSON.parse(screen.getByLabelText("Saved permissions").textContent!) as string[];

describe("Automation trigger permissions", () => {
  it.each([
    [[], false, false, false],
    [["schedule"], true, false, false],
    [["coreEvent"], false, true, false],
    [["applicationEvent"], false, false, true],
    [["schedule", "coreEvent"], true, true, false],
    [["schedule", "applicationEvent"], true, false, true],
    [["coreEvent", "applicationEvent"], false, true, true],
    [["schedule", "coreEvent", "applicationEvent"], true, true, true]
  ] as const)("derives hierarchy from %j", (values, schedule, builtin, webhook) => {
    renderPermissions([...values]);
    expect(checkbox("Schedule")).toHaveProperty("checked", schedule);
    expect(checkbox("Built-in Events")).toHaveProperty("checked", builtin);
    expect(checkbox("Webhook Events")).toHaveProperty("checked", webhook);
    expect(checkbox("Events")).toHaveAttribute("aria-checked", builtin !== webhook ? "mixed" : String(builtin));
    expect(saved()).toEqual(values);
  });
  it("explicitly enables both families from an empty state, then removes both without changing Schedule", () => {
    renderPermissions(["schedule"]);
    fireEvent.click(checkbox("Events"));
    expect(saved()).toEqual(["schedule", "coreEvent", "applicationEvent"]);
    fireEvent.click(checkbox("Events"));
    expect(saved()).toEqual(["schedule"]);
  });
  it.each(["coreEvent", "applicationEvent"])("restores restricted paused choices for %s", kind => {
    renderPermissions(["schedule", kind]);
    fireEvent.click(checkbox("Events"));
    expect(saved()).toEqual(["schedule"]);
    fireEvent.click(checkbox("Events"));
    expect(saved()).toEqual(["schedule", kind]);
  });
  it.each([["Built-in Events", "applicationEvent"], ["Webhook Events", "coreEvent"]])("changing %s leaves the other family and Schedule intact", (label, retained) => {
    renderPermissions(["schedule", "coreEvent", "applicationEvent"]);
    fireEvent.click(checkbox(label));
    expect(saved()).toEqual(["schedule", retained]);
    expect(checkbox("Events")).toHaveAttribute("aria-checked", "mixed");
  });
  it("a new child choice replaces the paused parent undo choice", () => {
    renderPermissions(["coreEvent"]);
    fireEvent.click(checkbox("Events"));
    fireEvent.click(checkbox("Webhook Events"));
    fireEvent.click(checkbox("Webhook Events"));
    fireEvent.click(checkbox("Events"));
    expect(saved()).toEqual(["coreEvent", "applicationEvent"]);
  });
  it("keeps configured permissions visible but inactive while the overall policy is disabled", () => {
    renderPermissions(["coreEvent"], false, true);
    expect(checkbox("Built-in Events")).toBeChecked();
    expect(checkbox("Built-in Events")).toBeDisabled();
    expect(screen.getByText(/do not authorize execution/)).toBeVisible();
    expect(automationTriggerPermissionSummary(["coreEvent"])).toBe("Schedule: Not allowed · Built-in Events: Allowed · Webhook Events: Not allowed");
  });
});
