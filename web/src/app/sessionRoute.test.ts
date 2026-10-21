import { beforeEach, describe, expect, it, vi } from "vitest";
import { isSessionId, normalizeSessionId, parseSessionIdFromPath, sameSessionId, sessionPath, syncBrowserSessionPath } from "./sessionRoute";

const sampleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

const tenant = "11111111-1111-1111-1111-111111111111";
describe("sessionRoute", () => {
 beforeEach(() => { vi.unstubAllGlobals(); window.history.replaceState(null, "", `/t/${tenant}`); });
  it("parses session paths and rejects unknown routes", () => {
    expect(parseSessionIdFromPath(`/t/${tenant}/c/${sampleId}`)).toBe(sampleId);
    expect(parseSessionIdFromPath(`/t/${tenant}/c/${sampleId}/`)).toBe(sampleId);
    expect(parseSessionIdFromPath("/")).toBeNull();
    expect(parseSessionIdFromPath(`/c/${sampleId}`)).toBeNull();
    expect(sessionPath(sampleId, null)).toBe("/account/organizations");
    expect(parseSessionIdFromPath("/c/not-a-guid")).toBeNull();
    expect(isSessionId(sampleId)).toBe(true);
    expect(sessionPath(sampleId)).toBe(`/t/${tenant}/c/${sampleId}`);
    expect(parseSessionIdFromPath(`/t/${tenant}/c/${sampleId.toUpperCase()}`)).toBe(sampleId);
    expect(sameSessionId(sampleId, sampleId.toUpperCase())).toBe(true);
    expect(normalizeSessionId(sampleId.toUpperCase())).toBe(sampleId);
  });

  it("updates history when the path changes", () => {
    const pushState = vi.fn();
    const replaceState = vi.fn();
    vi.stubGlobal("history", { pushState, replaceState });
    vi.stubGlobal("location", { pathname: `/t/${tenant}` });

    syncBrowserSessionPath(sampleId, "replace");
    expect(replaceState).toHaveBeenCalledWith({ sessionId: sampleId }, "", `/t/${tenant}/c/${sampleId}`);

    vi.stubGlobal("location", { pathname: `/t/${tenant}/c/${sampleId}` });
    syncBrowserSessionPath(sampleId, "replace");
    expect(replaceState).toHaveBeenCalledTimes(1);

    syncBrowserSessionPath(null, "push");
    expect(pushState).toHaveBeenCalledWith(null, "", `/t/${tenant}`);
  });
});
