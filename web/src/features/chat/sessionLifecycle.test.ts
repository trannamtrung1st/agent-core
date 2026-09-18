import { lifecycleOutcomeLabel, terminalSessionNote } from "./sessionLifecycle";

describe("session lifecycle copy", () => {
  it("keeps protocol-v1 End as Ended", () => {
    expect(lifecycleOutcomeLabel(null)).toBe("Ended");
    expect(lifecycleOutcomeLabel("ended")).toBe("Ended");
    expect(terminalSessionNote(undefined)).toBe("This conversation has ended.");
  });

  it("names additive terminal outcomes", () => {
    expect(lifecycleOutcomeLabel("completed")).toBe("Completed");
    expect(lifecycleOutcomeLabel("expired")).toBe("Expired");
    expect(lifecycleOutcomeLabel("cancelled")).toBe("Cancelled");
    expect(terminalSessionNote("completed")).toBe("This conversation is completed.");
    expect(terminalSessionNote("expired")).toBe("This conversation has expired.");
    expect(terminalSessionNote("cancelled")).toBe("This conversation was cancelled.");
  });
});
