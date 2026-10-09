import { describe, expect, it } from "vitest";
import { orderedReasoningEfforts, reasoningEffortChoices, retainedReasoningEffort } from "./reasoningEfforts";

describe("reasoning presentation", () => {
  it.each([
    [["high", "medium", "low"], ["low", "medium", "high"]],
    [["low", "medium", "high"], ["low", "medium", "high"]],
    [["max", "none", "xhigh", "minimal", "low"], ["none", "minimal", "low", "xhigh", "max"]],
    [["high", "low", "high"], ["low", "high"]]
  ])("orders only supported levels without changing saved input", (input, expected) => {
    const original = [...input];
    expect(orderedReasoningEfforts(input)).toEqual(expected);
    expect(input).toEqual(original);
  });
  it("keeps nonlinear and future modes outside the intensity scale", () => {
    expect(reasoningEffortChoices(["adaptive", "high", "future", "auto", "low"]))
      .toEqual({ levels: ["low", "high"], modes: ["adaptive", "future", "auto"] });
  });
  it("preserves valid selections and uses the next model default for unsupported ones", () => {
    const model = { supportedReasoningEfforts: ["high", "low"], defaultReasoningEffort: "low" };
    expect(retainedReasoningEffort(model, "high")).toBe("high");
    expect(retainedReasoningEffort(model, "medium")).toBe("low");
    expect(retainedReasoningEffort(undefined, "high")).toBe("");
  });
});
