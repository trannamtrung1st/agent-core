import { describe, expect, it, vi } from "vitest";
import {
  isSessionId,
  normalizeSessionId,
  parseSessionIdFromPath,
  sameSessionId,
  sessionPath,
  syncBrowserSessionPath
} from "./sessionRoute";

const sampleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

describe("sessionRoute", () => {
  it("parses session paths and rejects unknown routes", () => {
    expect(parseSessionIdFromPath(`/c/${sampleId}`)).toBe(sampleId);
    expect(parseSessionIdFromPath(`/c/${sampleId}/`)).toBe(sampleId);
    expect(parseSessionIdFromPath("/")).toBeNull();
    expect(parseSessionIdFromPath("/c/not-a-guid")).toBeNull();
    expect(isSessionId(sampleId)).toBe(true);
    expect(sessionPath(sampleId)).toBe(`/c/${sampleId}`);
    expect(parseSessionIdFromPath(`/c/${sampleId.toUpperCase()}`)).toBe(sampleId);
    expect(sameSessionId(sampleId, sampleId.toUpperCase())).toBe(true);
    expect(normalizeSessionId(sampleId.toUpperCase())).toBe(sampleId);
  });

  it("updates history when the path changes", () => {
    const pushState = vi.fn();
    const replaceState = vi.fn();
    vi.stubGlobal("history", { pushState, replaceState });
    vi.stubGlobal("location", { pathname: "/" });

    syncBrowserSessionPath(sampleId, "replace");
    expect(replaceState).toHaveBeenCalledWith({ sessionId: sampleId }, "", `/c/${sampleId}`);

    vi.stubGlobal("location", { pathname: `/c/${sampleId}` });
    syncBrowserSessionPath(sampleId, "replace");
    expect(replaceState).toHaveBeenCalledTimes(1);

    syncBrowserSessionPath(null, "push");
    expect(pushState).toHaveBeenCalledWith(null, "", "/");
  });
});
