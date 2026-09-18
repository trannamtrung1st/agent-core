import { describe, expect, it } from "vitest";
import { speechLocaleSelectValue } from "./SpeechLocalePicker";

describe("speechLocaleSelectValue", () => {
  it("uses the pending override before a session exists", () => {
    expect(speechLocaleSelectValue(null, null, "fr-FR", false)).toBe("fr-FR");
    expect(speechLocaleSelectValue("agentDefault", null, null, false)).toBe("");
  });

  it("uses the persisted override only when the source is sessionOverride", () => {
    expect(speechLocaleSelectValue("sessionOverride", "vi-VN", null, true)).toBe("vi-VN");
    expect(speechLocaleSelectValue("agentDefault", null, "fr-FR", true)).toBe("");
  });
});
