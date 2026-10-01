import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { HistoryEntry } from "../../state/sessionStore";
import { ChatMessageStatus } from "./ChatMessageStatus";

function entry(overrides: Partial<HistoryEntry> & Pick<HistoryEntry, "status">): HistoryEntry {
  return {
    entryId: "e1",
    sequence: 1,
    sourceEventId: null,
    role: "assistant",
    text: "",
    responseId: "r1",
    deliveryMode: "text",
    heardTextEndExclusive: 0,
    receivedTextEndExclusive: 0,
    createdAt: "2026-09-15T00:00:00.000Z",
    finishReason: null,
    interruptReason: null,
    ...overrides
  };
}

describe("ChatMessageStatus", () => {
  it("renders a solid red tag for failed rows without a diagnostic reference", () => {
    const { container } = render(
      <ChatMessageStatus entry={entry({ status: "failed" })} sessionId="s1" label="Failed" />
    );
    const tag = container.querySelector(".chat-message-status");
    expect(tag).toHaveClass("ant-tag-solid");
    expect(tag).toHaveTextContent("Failed");
    expect(tag).not.toHaveClass("chat-message-status-interactive");
    expect(screen.queryByRole("button", { name: "Failed — show error details" })).not.toBeInTheDocument();
  });

  it("renders a gold tag for interrupted rows with interrupt tooltip", () => {
    const { container } = render(
      <ChatMessageStatus
        entry={entry({ status: "interrupted", interruptReason: "disconnected" })}
        sessionId="s1"
        label="Disconnected"
      />
    );
    const tag = container.querySelector(".chat-message-status");
    expect(tag).toHaveClass("ant-tag-solid");
    expect(tag).toHaveAttribute("title", "disconnected");
    expect(tag).toHaveTextContent("Disconnected");
  });

  it("renders a gold tag for completed length-limit labeling", () => {
    const { container } = render(
      <ChatMessageStatus
        entry={entry({ status: "completed", finishReason: "lengthLimit" })}
        sessionId="s1"
        label="Output limit reached"
      />
    );
    const tag = container.querySelector(".chat-message-status");
    expect(tag).toHaveClass("ant-tag-solid");
    expect(tag).toHaveTextContent("Output limit reached");
  });

  it("uses the same tag styling and opens details for diagnosed failures", async () => {
    render(
      <ChatMessageStatus
        entry={entry({
          status: "failed",
          failure: {
            diagnosticId: "diag-1",
            correlationId: null,
            category: "provider",
            code: "Unavailable"
          }
        })}
        sessionId="s1"
        label="Failed"
      />
    );
    const trigger = screen.getByRole("button", { name: "Failed — show error details" });
    expect(trigger).toHaveClass("ant-tag-solid");
    expect(trigger).toHaveClass("chat-message-status-interactive");
    fireEvent.click(trigger);
    expect(await screen.findByTestId("diagnostic-details")).toBeInTheDocument();
    expect(screen.getByTestId("diagnostic-id")).toHaveTextContent("diag-1");
  });
});
