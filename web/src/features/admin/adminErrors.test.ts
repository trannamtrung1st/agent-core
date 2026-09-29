import { describe, expect, it } from "vitest";
import { parseAdminDeletionBlockedMessage } from "./adminErrors";

describe("parseAdminDeletionBlockedMessage", () => {
  it("parses instance deletion refusals", () => {
    const message = [
      "Cannot delete this instance.",
      "",
      "It is still referenced by:",
      "• 1 session",
      "",
      "Archive keeps it inactive without breaking history."
    ].join("\n");

    expect(parseAdminDeletionBlockedMessage(message)).toEqual({
      headline: "Cannot delete this instance.",
      bullets: ["1 session"],
      footer: "Archive keeps it inactive without breaking history."
    });
  });

  it("parses multiple reference lines", () => {
    const message = [
      "Cannot delete this definition.",
      "",
      "It is still referenced by:",
      "• 2 agent instances",
      "• 3 sessions",
      "",
      "Deleting a definition does not remove instances, sessions, or their history."
    ].join("\n");

    expect(parseAdminDeletionBlockedMessage(message)).toEqual({
      headline: "Cannot delete this definition.",
      bullets: ["2 agent instances", "3 sessions"],
      footer: "Deleting a definition does not remove instances, sessions, or their history."
    });
  });

  it("returns null for unrelated errors", () => {
    expect(parseAdminDeletionBlockedMessage("Instance could not be deleted.")).toBeNull();
  });
});
