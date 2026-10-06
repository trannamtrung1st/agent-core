import { describe, expect, it } from "vitest";
import {
  adminDefinitionPath,
  adminHomePath,
  adminInstancePath,
  lastChatUrl,
  parseAppRoute,
  rememberChatUrl
} from "./appRoute";

describe("appRoute", () => {
  it("parses chat and admin paths", () => {
    expect(parseAppRoute("/")).toEqual({ area: "chat" });
    expect(parseAppRoute("/c/019944af-00d1-7000-8000-000000000001")).toEqual({ area: "chat" });
    expect(parseAppRoute("/admin")).toEqual({ area: "admin", view: "home" });
    expect(parseAppRoute("/admin/instances")).toEqual({ area: "admin", view: "home", collection: "instances" });
    expect(parseAppRoute("/admin/event-sources/")).toEqual({ area: "admin", view: "home", collection: "event-sources" });
    expect(parseAppRoute("/admin/definitions")).toEqual({ area: "admin", view: "home", collection: "definitions" });
    expect(parseAppRoute("/admin/definitions/examiner")).toEqual({
      area: "admin",
      view: "definition",
      definitionId: "examiner"
    });
    expect(parseAppRoute("/admin/instances/019944af-00d1-7000-8000-000000000001")).toEqual({
      area: "admin",
      view: "instance",
      instanceId: "019944af-00d1-7000-8000-000000000001"
    });
  });

  it("builds admin paths", () => {
    expect(adminHomePath()).toBe("/admin");
    expect(adminHomePath("instances")).toBe("/admin/instances");
    expect(adminHomePath("event-sources")).toBe("/admin/event-sources");
    expect(adminDefinitionPath("examiner")).toBe("/admin/definitions/examiner");
    expect(adminInstancePath("019944AF-00D1-7000-8000-000000000001")).toBe(
      "/admin/instances/019944af-00d1-7000-8000-000000000001"
    );
  });

  it("round-trips definition and nested instance tabs", () => {
    for (const tab of ["versions", "drafts"] as const) {
      expect(parseAppRoute(adminDefinitionPath("field guide", tab))).toEqual({
        area: "admin", view: "definition", definitionId: "field guide", tab
      });
    }
    const instanceId = "019944af-00d1-7000-8000-000000000001";
    for (const tab of ["identity", "runs", "connections", "effective"] as const) {
      expect(parseAppRoute(adminInstancePath(instanceId, tab))).toEqual({
        area: "admin", view: "instance", instanceId, tab
      });
    }
    for (const section of ["memory", "experience"] as const) {
      expect(parseAppRoute(adminInstancePath(instanceId, "continuity", section))).toEqual({
        area: "admin", view: "instance", instanceId, tab: "continuity", section
      });
    }
    for (const section of ["schedules", "thoughts", "controls"] as const) {
      expect(parseAppRoute(adminInstancePath(instanceId, "automation", section))).toEqual({
        area: "admin", view: "instance", instanceId, tab: "automation", section
      });
    }
  });

  it("ignores unknown tabs and sections and handles malformed escaped identifiers", () => {
    const base = "/admin/instances/019944af-00d1-7000-8000-000000000001";
    expect(parseAppRoute(`${base}/automation/unknown`)).toEqual({
      area: "admin", view: "instance", instanceId: base.split("/").at(-1), tab: "automation"
    });
    expect(parseAppRoute(`${base}/continuity/thoughts/`)).not.toHaveProperty("section");
    expect(parseAppRoute(`${base}/unknown`)).not.toHaveProperty("tab");
    expect(parseAppRoute("/admin/definitions/examiner/unknown")).not.toHaveProperty("tab");
    expect(parseAppRoute("/admin/definitions/%E0%A4%A")).toEqual({ area: "chat" });
    expect(adminInstancePath(base.split("/").at(-1)!, "runs", "thoughts")).toBe(`${base}/runs`);
  });

  it("remembers the last chat url", () => {
    sessionStorage.clear();
    rememberChatUrl("/admin");
    expect(lastChatUrl()).toBe("/");
    rememberChatUrl("/c/019944af-00d1-7000-8000-000000000001");
    expect(lastChatUrl()).toBe("/c/019944af-00d1-7000-8000-000000000001");
  });
});
