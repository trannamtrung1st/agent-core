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

  it("appends structured failure lines after the diagnostic block", () => {
    expect(diagnosticCopyText(
      {
        diagnosticId: "diag-1",
        category: "Provider",
        code: "InvalidResponse"
      },
      [
        { label: "Class", value: "Provider or model" },
        { label: "Severity", value: "Recoverable" }
      ]
    )).toBe(
      [
        "Agent Core diagnostic",
        "Diagnostic ID: diag-1",
        "Error: Provider / InvalidResponse",
        "Class: Provider or model",
        "Severity: Recoverable"
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
    expect(screen.getByRole("button", { name: "Failed — show error details" })).toHaveClass("ant-tag-solid");
    expect(screen.getByText("Could not speak.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Failed — show error details" }));
    const details = await screen.findByTestId("diagnostic-details");
    expect(screen.getByTestId("diagnostic-id")).toHaveTextContent("diag-row");
    expect(details).toHaveTextContent("Correlation ID");
    expect(details).toHaveTextContent("corr-1");
    expect(details).toHaveTextContent("Session ID");
    expect(details).toHaveTextContent("session-1");
    expect(details).toHaveTextContent("Response ID");
    expect(details).toHaveTextContent("r2");
    expect(details).toHaveTextContent("Error");
    expect(details).toHaveTextContent("Provider / Unavailable");
    expect(details).not.toHaveTextContent("Could not speak.");
    fireEvent.click(screen.getByRole("button", { name: "Copy details" }));
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("Diagnostic ID: diag-row"));
    expect(await screen.findByRole("status")).toHaveTextContent("Copied");
  });

  it("labels a failed reply that already committed an effect", () => {
    render(
      <ChatMessage
        agentName="Alex"
        sessionId="session-1"
        entry={{
          ...failedEntry({
            diagnosticId: "diag-effect",
            correlationId: null,
            category: "Provider",
            code: "Unavailable"
          }),
          text: "",
          effectReceipts: [{ tool: "browser.close", status: "closed", label: "Browser closed" }]
        }}
      />
    );
    expect(screen.getByText("✓ Browser closed")).toBeInTheDocument();
    expect(screen.getByText("Reply failed")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reply failed — show error details" })).toBeInTheDocument();
    expect(screen.queryByText("Failed")).not.toBeInTheDocument();
  });

  it("copies allowlisted reason and channel and omits them when absent", async () => {
    const { rerender } = render(
      <ChatMessage
        agentName="Alex"
        sessionId="session-1"
        entry={failedEntry({
          diagnosticId: "diag-trunc",
          correlationId: null,
          category: "provider",
          code: "InvalidResponse",
          failureReason: "toolCallTruncated",
          providerResponseChannel: "toolCall"
        })}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Failed — show error details" }));
    expect(await screen.findByTestId("diagnostic-reason")).toHaveTextContent("toolCallTruncated");
    expect(screen.getByTestId("diagnostic-channel")).toHaveTextContent("toolCall");
    fireEvent.click(screen.getByRole("button", { name: "Copy details" }));
    const copied = String(writeText.mock.calls.at(-1)?.[0]);
    expect(copied).toContain("Reason: toolCallTruncated");
    expect(copied).toContain("Channel: toolCall");
    expect(copied).not.toContain("arguments");
    expect(copied).not.toContain("{");

    rerender(
      <ChatMessage
        agentName="Alex"
        sessionId="session-1"
        entry={failedEntry({
          diagnosticId: "diag-plain",
          correlationId: null,
          category: "provider",
          code: "InvalidResponse"
        })}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Failed — show error details" }));
    expect(screen.queryByTestId("diagnostic-reason")).not.toBeInTheDocument();
    expect(screen.queryByTestId("diagnostic-channel")).not.toBeInTheDocument();
  });

  it.each(["setupTimeout", "streamIdle", "totalTimeout"])("shows and copies timeout reason %s", async (reason) => {
    render(<ChatMessage agentName="Alex" sessionId="session-1" entry={failedEntry({
      diagnosticId: "diag-timeout", correlationId: null, category: "provider", code: "Timeout",
      failureReason: reason
    })} />);
    fireEvent.click(screen.getByRole("button", { name: "Failed — show error details" }));
    expect(await screen.findByTestId("diagnostic-reason")).toHaveTextContent(reason);
    fireEvent.click(screen.getByRole("button", { name: "Copy details" }));
    expect(String(writeText.mock.calls.at(-1)?.[0])).toContain(`Reason: ${reason}`);
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
    expect(details).toHaveTextContent("Work Item ID");
    expect(details).toHaveTextContent("work-failed");
    expect(details).toHaveTextContent("Occurrence ID");
    expect(details).toHaveTextContent("occurrence-1");
    expect(details).toHaveTextContent("Error");
    expect(details).toHaveTextContent("model-timeout");
    expect(details).not.toHaveTextContent("Trigger ID");
    expect(details).not.toHaveTextContent("The model timed out.");
  });

  it("shows admin diagnostic details only when an id exists", async () => {
    const { rerender } = render(
      <AdminDeletionBlockedAlert message="The request could not be completed." diagnosticId="diag-admin" />
    );
    fireEvent.click(screen.getByRole("button", { name: "Error details" }));
    expect(await screen.findByTestId("diagnostic-id")).toHaveTextContent(/^diag-admin$/);
    rerender(<AdminDeletionBlockedAlert message="Draft is invalid." />);
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });
});
