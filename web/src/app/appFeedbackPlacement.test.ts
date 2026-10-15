import { describe, expect, it } from "vitest";
import { feedbackBottom } from "./appFeedbackPlacement";

const toast = { left: 400, right: 1040, top: 0, bottom: 0 };
describe("feedback action clearance", () => {
  it("uses the compact edge when no bottom actions would be covered", () => {
    expect(feedbackBottom(900, 84, toast, [], 16, 8)).toBe(16);
    expect(feedbackBottom(900, 84, toast, [{ left: 300, right: 1100, top: 500, bottom: 540 }], 16, 8)).toBe(16);
  });
  it("stays above a footer only when its horizontal and vertical bounds intersect", () => {
    const footer = { left: 300, right: 1100, top: 832, bottom: 864 };
    expect(feedbackBottom(900, 84, toast, [footer], 16, 8)).toBe(76);
    expect(feedbackBottom(900, 84, toast, [{ ...footer, left: 1100, right: 1400 }], 16, 8)).toBe(16);
  });
  it("clears a growing composer and retains the safe-area edge for other surfaces", () => {
    expect(feedbackBottom(844, 128, { ...toast, left: 16, right: 374 },
      [{ left: 12, right: 378, top: 660, bottom: 828 }], 50, 8)).toBe(192);
    expect(feedbackBottom(844, 128, toast, [], 50, 8)).toBe(50);
  });
  it("checks additional action surfaces after moving above a lower one", () => {
    const lower = { left: 300, right: 1100, top: 850, bottom: 890 };
    const upper = { left: 300, right: 1100, top: 760, bottom: 800 };
    expect(feedbackBottom(900, 84, toast, [upper, lower], 16, 8)).toBe(148);
  });
});
