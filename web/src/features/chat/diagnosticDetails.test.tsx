import { fireEvent, render, screen } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AdminDeletionBlockedAlert } from "../admin/adminDeletionBlocked";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { ChatMessage } from "./ChatMessage";
import { diagnosticCopyText } from "./diagnosticCopy";
import type { WorkItem } from "../../services/api";
import type { HistoryEntry } from "../../state/sessionStore";

const writeText = vi.fn().mockResolvedValue(undefined);

const failedEntry = (failure?: HistoryEntry["failure"]): HistoryEntry => ({
  entryId: "a1",
  sequence: 2,
  sourceEventId: null,
  role: "assistant",
  text: "Could not speak.",
  responseId: "r2",
  status: "failed",
  deliveryMode: "text",
  heardTextEndExclusive: 0,
  receivedTextEndExclusive: 16,
  createdAt: "2026-09-18T00:00:00.000Z",
  failure
});

describe("diagnostic copy", () => {
  it("omits absent lines and refuses an empty id", () => {
    expect(diagnosticCopyText({ category: "Provider", code: "Unavailable" })).toBeNull();
    expect(diagnosticCopyText({
      diagnosticId: "019944af-0008-7000-8000-0000000000d5",
      sessionId: "session-1",
      responseId: "response-1",
      category: "Provider",
      code: "Unavailable"
    })).toBe(
      [
        "Agent Core diagnostic",
        "Diagnostic ID: 019944af-0008-7000-8000-0000000000d5",
        "Session ID: session-1",
        "Response ID: response-1",
        "Error: Provider / Unavailable"
      ].join("\n")
    );
  });

  it("includes work ids only when the projection has them", () => {
    expect(diagnosticCopyText({
      diagnosticId: "diag-1",
      sessionId: "session-1",
      workItemId: "work-1",
      code: "model-timeout",
      triggerOccurrenceId: "occurrence-1"
    })).toBe(
      [
        "Agent Core diagnostic",
        "Diagnostic ID: diag-1",
        "Session ID: session-1",
        "Work Item ID: work-1",
        "Occurrence ID: occurrence-1",
        "Error: model-timeout"
      ].join("\n")
    );
  });
});

describe("failed assistant details", () => {
  beforeEach(() => {
    writeText.mockClear();
    Object.assign(navigator, { clipboard: { writeText } });
  });

  it("keeps Failed and copies only the allowlisted diagnostic", async () => {
    render(
      <ChatMessage
        agentName="Alex"
        sessionId="session-1"
        entry={failedEntry({
          diagnosticId: "diag-row",
          correlationId: "corr-1",
          category: "Provider",
          code: "Unavailable"
        })}
      />
    );
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("Could not speak.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Error details" }));
    const details = await screen.findByTestId("diagnostic-details");
    expect(details).toHaveTextContent("Diagnostic ID: diag-row");
    expect(details).toHaveTextContent("Correlation ID: corr-1");
    expect(details).toHaveTextContent("Session ID: session-1");
    expect(details).toHaveTextContent("Response ID: r2");
    expect(details).toHaveTextContent("Error: Provider / Unavailable");
    expect(details).not.toHaveTextContent("Could not speak.");
    fireEvent.click(screen.getByRole("button", { name: "Copy diagnostic" }));
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("Diagnostic ID: diag-row"));
    expect(await screen.findByRole("status")).toHaveTextContent("Copied");
  });

  it("does not invent details for a legacy failed row", () => {
    render(<ChatMessage agentName="Alex" sessionId="session-1" entry={failedEntry(null)} />);
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });
});

describe("diagnosed work and admin errors", () => {
  const failedWork: WorkItem = {
    workItemId: "work-failed",
    status: "failed",
    revision: 3,
    origin: "Scheduled reminder",
    progress: null,
    needsApproval: false,
    approvalId: null,
    approvalRevision: null,
    approvalPreview: null,
    actionHash: null,
    cancellationAvailable: false,
    failureCode: "model-timeout",
    failureSummary: "The model timed out.",
    knownEffect: null,
    createdAt: "2026-09-24T09:00:00.000Z",
    updatedAt: "2026-09-24T09:01:00.000Z",
    diagnosticId: "diag-work",
    sourceOccurrenceId: "occurrence-1"
  };

  it("shows work diagnostic copy without a trigger id the item does not have", async () => {
    render(
      <AntApp>
        <BackgroundWorkDrawer
          sessionId="session-1"
          open
          wide
          onClose={() => undefined}
          load={async () => [failedWork]}
          loadResult={async () => {
            throw new Error("unused");
          }}
          cancel={async () => failedWork}
          approve={async () => failedWork}
          reject={async () => failedWork}
        />
      </AntApp>
    );
    expect(await screen.findByText("The model timed out.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Error details" }));
    const details = await screen.findByTestId("diagnostic-details");
    expect(details).toHaveTextContent("Work Item ID: work-failed");
    expect(details).toHaveTextContent("Occurrence ID: occurrence-1");
    expect(details).toHaveTextContent("Error: model-timeout");
    expect(details).not.toHaveTextContent("Trigger ID");
    expect(details).not.toHaveTextContent("The model timed out.");
  });

  it("shows admin diagnostic details only when an id exists", async () => {
    const { rerender } = render(
      <AdminDeletionBlockedAlert message="The request could not be completed." diagnosticId="diag-admin" />
    );
    fireEvent.click(screen.getByRole("button", { name: "Error details" }));
    expect(await screen.findByTestId("diagnostic-id")).toHaveTextContent("diag-admin");
    rerender(<AdminDeletionBlockedAlert message="Draft is invalid." />);
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });
});
