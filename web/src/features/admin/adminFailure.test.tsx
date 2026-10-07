import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { AdminErrorNotice, AdminRetryAction } from "./adminFailure";

describe("admin failure details", () => {
  it("shows error details beside the message when a diagnostic id exists", () => {
    render(
      <AdminErrorNotice
        message="The request could not be completed."
        diagnosticId="019944af-0008-7000-8000-0000000000e1"
        tone="danger"
      />
    );

    expect(screen.getByText("The request could not be completed.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Error details" })).toBeInTheDocument();
  });

  it("keeps the retry action without an error details control when the id is absent", () => {
    render(<AdminRetryAction onRetry={() => undefined} />);

    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });

  it("labels alert diagnostics and opens the existing copyable details", async () => {
    render(<AdminErrorNotice message="Retrospective output or model is invalid."
      diagnosticId="019944af-0008-7000-8000-0000000000e1" showDetailsLabel />);
    const details = screen.getByRole("button", { name: "Error details" });
    expect(details).toHaveTextContent("Error details");
    fireEvent.click(details);
    await waitFor(() => expect(screen.getByText("019944af-0008-7000-8000-0000000000e1")).toBeVisible());
    expect(screen.getByRole("button", { name: "Copy details" })).toBeVisible();
  });
});
