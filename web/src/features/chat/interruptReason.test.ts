import { describe, expect, it } from "vitest";
import { interruptReasonLabel } from "./interruptReason";

describe("interruptReasonLabel", () => {
  it("maps infrastructure reasons to distinct labels", () => {
    expect(interruptReasonLabel("disconnected")).toBe("Disconnected");
    expect(interruptReasonLabel("modeChange")).toBe("Mode changed");
  });

  it("maps intentional user reasons to Interrupted", () => {
    expect(interruptReasonLabel("userSteer")).toMatch(/^Interrupted/);
    expect(interruptReasonLabel("userStop")).toMatch(/^Interrupted/);
    expect(interruptReasonLabel("userBargeIn")).toMatch(/^Interrupted/);
  });
});
