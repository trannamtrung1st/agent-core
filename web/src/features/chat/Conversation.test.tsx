import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { HistoryEntry } from "../../state/sessionStore";
import { Conversation } from "./Conversation";

function entry(partial: Partial<HistoryEntry> & Pick<HistoryEntry, "entryId" | "role" | "text">): HistoryEntry {
  return {
    sequence: 1,
    sourceEventId: null,
    responseId: null,
    status: "completed",
    deliveryMode: "text",
    heardTextEndExclusive: partial.text.length,
    receivedTextEndExclusive: partial.text.length,
    createdAt: "2026-09-15T00:00:00.000Z",
    ...partial
  };
}

describe("Conversation", () => {
  it("shows the empty prompt when there are no entries and no transcript chrome", () => {
    render(
      <Conversation agentName="Alex" sessionId="s1" entries={[]} activity={{ kind: "idle" }} />
    );
    expect(screen.getByRole("listitem")).toHaveClass("chat-message-empty");
    expect(screen.getByText("Send a message or start voice.")).toBeInTheDocument();
    expect(screen.queryByText(/Transcript ·/)).not.toBeInTheDocument();
  });

  it("uses text-only empty hint when voice is unavailable", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[]}
        activity={{ kind: "idle" }}
        voiceAvailable={false}
      />
    );
    expect(screen.getByText("Send a message.")).toBeInTheDocument();
    expect(screen.queryByText("Send a message or start voice.")).not.toBeInTheDocument();
  });

  it("does not describe Browser STT as offline", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[]}
        activity={{ kind: "idle" }}
        sttTransport="clientTranscript"
      />
    );
    const hint = screen.getByText(/does not send PCM to backend speech recognition/i);
    expect(hint.textContent).toMatch(/browser vendor may use a cloud recognizer/i);
    expect(hint.textContent).toMatch(/choose another speech provider/i);
    expect(hint.textContent).toMatch(/backend-controlled or local recognition/i);
    expect(hint.textContent).not.toMatch(/offline/i);
    expect(hint.textContent).not.toMatch(/on-device/i);
    expect(hint.textContent).not.toMatch(/this browser tab/i);
    expect(hint.textContent).not.toMatch(/runs in this browser/i);
  });

  it("lays out user bubbles and open assistant messages with status labels", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({ entryId: "u1", role: "user", text: "Hello" }),
          entry({
            entryId: "a1",
            role: "assistant",
            text: "Cut off.",
            responseId: "r1",
            status: "interrupted"
          }),
          entry({
            entryId: "a2",
            role: "assistant",
            text: "Could not speak.",
            responseId: "r2",
            status: "failed"
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );

    const items = screen.getAllByRole("listitem");
    expect(items[0]).toHaveAttribute("data-role", "user");
    expect(items[0]).toHaveClass("chat-message-user");
    expect(items[1]).toHaveAttribute("data-role", "assistant");
    expect(items[1]).toHaveClass("chat-message-assistant");
    expect(items[0]).toHaveTextContent("Hello");
    expect(items[1]).toHaveTextContent("Alex");
    expect(screen.getByText("Interrupted")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(items[1].querySelector(".chat-message-status")).toHaveClass("ant-tag-solid");
    expect(items[0].querySelector("time")).toHaveAttribute("dateTime", "2026-09-15T00:00:00.000Z");
    expect(items[1].querySelector("time")).toHaveAttribute("dateTime", "2026-09-15T00:00:00.000Z");
  });

  it("keeps interrupted chips in the message stack without an empty body", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "",
            responseId: "r1",
            status: "interrupted"
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );

    const item = screen.getByRole("listitem");
    expect(item.querySelector(".assistant-body")).toBeNull();
    expect(item.querySelector(".chat-message-status")).toHaveTextContent("Interrupted");
  });

  it("keeps messages readable and uses inline reconnect status instead of an overlay", () => {
    render(
      <Conversation
        agentName="Jordan"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "The document is as follows:"
          })
        ]}
        activity={{ kind: "reconnecting", label: "Reconnecting…" }}
      />
    );
    expect(screen.getByText("The document is as follows:")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting…");
    expect(screen.queryByText("Loading conversation")).not.toBeInTheDocument();
    expect(screen.queryByText("Interrupted")).not.toBeInTheDocument();
  });

  it("renders extra markdown, attachment, and artifact blocks without speech text", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "Shown display.",
            heardTextEndExclusive: 13,
            receivedTextEndExclusive: 14,
            blocks: [
              {
                blockId: "b1",
                kind: "markdown",
                text: "**Extra block**",
                fallbackText: "**Extra block**",
                attachmentId: null,
                artifactId: null
              },
              {
                blockId: "b2",
                kind: "attachment",
                text: "notes.txt",
                fallbackText: "notes.txt",
                attachmentId: "att-1",
                artifactId: null
              },
              {
                blockId: "b3",
                kind: "artifact",
                text: "fixture-artifact-1",
                fallbackText: "fixture-artifact-1",
                attachmentId: null,
                artifactId: "fixture-artifact-1"
              }
            ]
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );
    expect(screen.getByText("Shown display.")).toBeInTheDocument();
    expect(screen.getByText("Extra block")).toBeInTheDocument();
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Artifact fixture-artifact-1" })).toBeInTheDocument();
    expect(screen.queryByText("Hidden speech")).not.toBeInTheDocument();
  });

  it("renders an unknown block as unsupported content", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "Hello",
            blocks: [
              {
                blockId: "b1",
                kind: "unknown",
                text: "",
                fallbackText: "[Unsupported content]",
                attachmentId: null,
                artifactId: null
              }
            ]
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );
    expect(screen.getByText("[Unsupported content]")).toBeInTheDocument();
  });

  it("renders sanitized markdown, artifact labels, and rejects script links as text", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "Hello",
            blocks: [
              {
                blockId: "b1",
                kind: "markdown",
                text: "**Hi** [x](javascript:alert(1))",
                fallbackText: "**Hi**",
                attachmentId: null,
                artifactId: null
              },
              {
                blockId: "b2",
                kind: "artifact",
                text: "fixture-artifact-1",
                fallbackText: "fixture-artifact-1",
                attachmentId: null,
                artifactId: "fixture-artifact-1"
              }
            ]
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );
    expect(screen.getByText("Hi")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "x" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Artifact fixture-artifact-1" })).toBeInTheDocument();
    expect(screen.queryByText("Spoken hello")).not.toBeInTheDocument();
  });

  it("does not surface hidden speech text or raw tool payloads as visible messages", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({
            entryId: "a1",
            role: "assistant",
            text: "Order 91 is delayed.",
            blocks: [
              {
                blockId: "b1",
                kind: "unknown",
                text: "",
                fallbackText: "[Unsupported content]",
                attachmentId: null,
                artifactId: null
              }
            ]
          })
        ]}
        activity={{ kind: "idle" }}
      />
    );
    expect(screen.getByText("Order 91 is delayed.")).toBeInTheDocument();
    expect(screen.queryByText(/tool_call/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Spoken hello/)).not.toBeInTheDocument();
    expect(screen.getByText("[Unsupported content]")).toBeInTheDocument();
  });

  it("shows transient activity instead of persisting it as a message", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[entry({ entryId: "u1", role: "user", text: "Hello" })]}
        activity={{ kind: "thinking", label: "Thinking…" }}
      />
    );
    expect(screen.getByRole("status")).toHaveTextContent("Thinking…");
    expect(screen.getAllByRole("listitem")).toHaveLength(1);
  });

  it("groups live agent meta with thinking instead of splitting an empty streaming row", () => {
    render(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({ entryId: "u1", role: "user", text: "hi" }),
          entry({
            entryId: "a1",
            role: "assistant",
            text: "",
            responseId: "r1",
            status: "streaming"
          })
        ]}
        activity={{ kind: "thinking", label: "Thinking…" }}
      />
    );

    expect(screen.getAllByRole("listitem")).toHaveLength(1);
    expect(screen.getByText("Alex")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Thinking…");
    const group = document.querySelector(".agent-turn-activity");
    expect(group).toContainElement(screen.getByText("Alex"));
    expect(group).toContainElement(screen.getByRole("status"));
  });

  it("reserves reply space after the latest user send, not on empty chat", () => {
    const { rerender } = render(
      <Conversation agentName="Alex" sessionId="s1" entries={[]} activity={{ kind: "idle" }} />
    );
    expect(screen.queryByTestId("conversation-reply-space")).not.toBeInTheDocument();

    rerender(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[entry({ entryId: "u1", role: "user", text: "now?" })]}
        activity={{ kind: "thinking", label: "Thinking…" }}
      />
    );
    expect(screen.getByTestId("conversation-reply-space")).toBeInTheDocument();
    expect(screen.getByRole("listitem")).toHaveAttribute("data-turn-anchor", "true");
    expect(screen.getByRole("status")).toHaveTextContent("Thinking…");

    rerender(
      <Conversation
        agentName="Alex"
        sessionId="s1"
        entries={[
          entry({ entryId: "u1", role: "user", text: "now?" }),
          entry({ entryId: "a1", role: "assistant", text: "Soon." }),
          entry({ entryId: "u2", role: "user", text: "and now?" })
        ]}
        activity={{ kind: "idle" }}
      />
    );
    const items = screen.getAllByRole("listitem");
    expect(items[0]).not.toHaveAttribute("data-turn-anchor");
    expect(items[2]).toHaveAttribute("data-turn-anchor", "true");
  });
});
