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

  it("remembers the last chat url", () => {
    sessionStorage.clear();
    rememberChatUrl("/admin");
    expect(lastChatUrl()).toBe("/");
    rememberChatUrl("/c/019944af-00d1-7000-8000-000000000001");
    expect(lastChatUrl()).toBe("/c/019944af-00d1-7000-8000-000000000001");
  });
});
