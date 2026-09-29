import { describe, expect, it } from "vitest";
import { AdminRequestError } from "../../services/adminApi";
import { OwnerCapabilityError } from "../../services/api";
import { describeAdminError, formatAdminLoadError, parseAdminDeletionBlockedMessage } from "./adminErrors";

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

describe("formatAdminLoadError", () => {
  it("keeps a diagnostic id from an admin request error", () => {
    const formatted = formatAdminLoadError(
      new AdminRequestError("The request could not be completed.", "019944af-0008-7000-8000-0000000000e1")
    );

    expect(formatted).toEqual({
      message: "The request could not be completed.",
      diagnosticId: "019944af-0008-7000-8000-0000000000e1",
      unauthorized: false
    });
  });

  it("omits the diagnostic id when the failure has none", () => {
    expect(describeAdminError(new Error("Request failed."), "Request failed.")).toEqual({
      message: "Request failed."
    });
    expect(formatAdminLoadError(new Error("Admin definitions failed (500)")).diagnosticId).toBeUndefined();
    expect(formatAdminLoadError(new OwnerCapabilityError("missing")).diagnosticId).toBeUndefined();
  });
});
