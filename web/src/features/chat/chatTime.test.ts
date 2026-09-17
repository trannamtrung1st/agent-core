import { describe, expect, it } from "vitest";
import { formatChatTime, statusLabel } from "./chatTime";

describe("formatChatTime", () => {
  const now = new Date(2026, 8, 17, 12, 51);

  it("returns null for invalid timestamps", () => {
    expect(formatChatTime("not-a-date", now, "en-US")).toBeNull();
  });

  it("shows time only on the same calendar day", () => {
    const sameDay = new Date(2026, 8, 17, 9, 5).toISOString();
    expect(formatChatTime(sameDay, now, "en-US")).toMatch(/\d{1,2}:\d{2}/);
    expect(formatChatTime(sameDay, now, "en-US")).not.toMatch(/Sep/);
  });

  it("includes the month for earlier days in the same year", () => {
    const earlier = new Date(2026, 8, 15, 7, 0).toISOString();
    expect(formatChatTime(earlier, now, "en-US")).toMatch(/Sep/);
  });
});

describe("statusLabel", () => {
  it("labels terminal entry statuses", () => {
    expect(statusLabel("interrupted")).toBe("Interrupted");
    expect(statusLabel("failed")).toBe("Failed");
    expect(statusLabel("completed")).toBeNull();
  });
});
