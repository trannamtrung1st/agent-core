import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { HistoryEntry } from "../../state/sessionStore";
import { Transcript } from "./Transcript";

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

describe("Transcript", () => {
  it("shows the empty prompt when there are no entries", () => {
    render(<Transcript agentName="Alex" sessionId="s1" entries={[]} />);
    expect(screen.getByRole("listitem")).toHaveClass("entry-empty");
    expect(screen.getByText("Send a message or start voice.")).toBeInTheDocument();
    expect(screen.getByText(/Transcript · 0 entries/)).toBeInTheDocument();
  });

  it("distinguishes user and agent lines and keeps interrupted status in type", () => {
    render(
      <Transcript
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
      />
    );

    const items = screen.getAllByRole("listitem");
    expect(items[0]).toHaveAttribute("data-role", "user");
    expect(items[1]).toHaveAttribute("data-role", "assistant");
    expect(items[0]).toHaveTextContent("You");
    expect(items[0]).toHaveTextContent("Hello");
    expect(items[1]).toHaveTextContent("Alex");
    expect(screen.getByText("interrupted")).toBeInTheDocument();
    expect(screen.getByText("failed")).toBeInTheDocument();
    expect(screen.getByText(/Transcript · 3 entries/)).toBeInTheDocument();
  });

  it("renders unknown blocks as sanitized fallback text", () => {
    render(
      <Transcript
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
      />
    );
    expect(screen.getByText("[Unsupported content]")).toBeInTheDocument();
  });

  it("renders sanitized markdown, artifact labels, and rejects script links as text", () => {
    render(
      <Transcript
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
      />
    );
    expect(screen.getByText("Hi")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "x" })).not.toBeInTheDocument();
    expect(screen.getByText("Artifact · fixture-artifact-1")).toBeInTheDocument();
  });
});
