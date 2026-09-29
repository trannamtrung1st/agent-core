import { render, screen } from "@testing-library/react";
import { App } from "antd";
import { describe, expect, it } from "vitest";
import { AdminDeletionBlockedAlert } from "./adminDeletionBlocked";

describe("AdminDeletionBlockedAlert", () => {
  it("structures blocked deletion messages with a list and footer", () => {
    render(
      <App>
        <AdminDeletionBlockedAlert
          message={[
            "Cannot delete this instance.",
            "",
            "It is still referenced by:",
            "• 1 session",
            "",
            "Archive keeps it inactive without breaking history."
          ].join("\n")}
        />
      </App>
    );

    expect(screen.getByText("Cannot delete this instance.")).toBeInTheDocument();
    expect(screen.getByText("It is still referenced by:")).toBeInTheDocument();
    expect(screen.getByText("1 session")).toBeInTheDocument();
    expect(screen.getByText("Archive keeps it inactive without breaking history.")).toBeInTheDocument();
  });

  it("falls back to a single-line alert for generic errors", () => {
    render(
      <App>
        <AdminDeletionBlockedAlert message="Network error." />
      </App>
    );

    expect(screen.getByText("Network error.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });
});
