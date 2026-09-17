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
    expect(screen.getByText("interrupted")).toBeInTheDocument();
    expect(screen.getByText("failed")).toBeInTheDocument();
  });

  it("shows connecting as conversation loading, distinct from interrupted entries", () => {
    render(
      <Conversation agentName="Alex" sessionId="s1" entries={[]} connection="connecting" activity={{ kind: "idle" }} />
    );
    expect(screen.getByText("Send a message or start voice.")).toBeInTheDocument();
    expect(screen.getByText("Loading conversation")).toBeInTheDocument();
    expect(screen.queryByText("interrupted")).not.toBeInTheDocument();
  });

  it("renders unknown blocks as sanitized fallback text", () => {
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
    expect(screen.getByText("Artifact · fixture-artifact-1")).toBeInTheDocument();
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
});
