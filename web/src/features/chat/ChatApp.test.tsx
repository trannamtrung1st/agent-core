import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore } from "../../state/sessionStore";
import { bootstrap, sendDraft } from "../../services/realtime";
import { ChatApp } from "./ChatApp";

vi.mock("../../services/realtime", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../services/realtime")>();
  return {
    ...actual,
    bootstrap: vi.fn().mockResolvedValue(""),
    reportCommittedEntries: vi.fn(),
    sendDraft: vi.fn().mockResolvedValue(undefined)
  };
});

describe("ChatApp accessibility", () => {
  afterEach(() => {
    act(() => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [],
        selectedAgentId: "examiner"
      });
    });
  });

  it("labels identity and surfaces connection and microphone errors", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    const view = await act(async () => render(<ChatApp />));
    expect(screen.getByLabelText("Identity")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start conversation" })).toBeInTheDocument();

    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "failed",
        agentName: "Alex",
        agentRole: "Examiner",
        voiceAvailable: true,
        error: "Microphone permission was denied. Enable the microphone or continue in text.",
        agents: [],
        selectedAgentId: "examiner"
      });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Connection failed");
    expect(screen.getByRole("alert")).toHaveTextContent("Microphone permission was denied");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Voice" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "End" })).toBeInTheDocument();
  });

  it("shows Voice after reconnect when durable mode is voice until capture is live", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        mode: "voice",
        captureLive: false,
        voiceAvailable: true,
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    const view = await act(async () => render(<ChatApp />));
    expect(screen.getByTestId("connection")).toHaveTextContent("Ready");
    expect(screen.getByRole("button", { name: "Voice" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();

    await act(async () => {
      useSessionStore.setState({ captureLive: true, muted: false });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Listening");
    expect(screen.getByRole("button", { name: "Mute" })).toBeInTheDocument();
  });

  it("shows Mute while voice is prepared even if capture is not streaming", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        mode: "voice",
        captureLive: true,
        muted: false,
        voiceAvailable: true,
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      render(<ChatApp />);
    });
    expect(screen.getByRole("button", { name: "Mute" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
  });

  it("renders the health profile from bootstrap", async () => {
    vi.mocked(bootstrap).mockResolvedValueOnce("Real");
    await act(async () => {
      render(<ChatApp />);
    });
    expect(screen.getByTestId("profile")).toHaveTextContent("Real");
  });

  it("shows pending voice, thinking, interrupted entries, and Enter send", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        pendingMode: "voice",
        voiceAvailable: true,
        agentName: "Alex",
        agentRole: "Examiner",
        draft: "Hello",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: null,
            role: "assistant",
            text: "Cut off mid sentence.",
            responseId: "r1",
            status: "interrupted",
            deliveryMode: "text",
            heardTextEndExclusive: 8,
            receivedTextEndExclusive: 21,
            createdAt: "2026-09-15T00:00:00.000Z"
          }
        ]
      });
    });
    const view = await act(async () => render(<ChatApp />));
    expect(screen.getByTestId("connection")).toHaveTextContent("Starting voice…");
    expect(screen.getByRole("button", { name: "Cancel voice" })).toBeInTheDocument();
    expect(screen.getByText("interrupted")).toBeInTheDocument();
    expect(screen.getByPlaceholderText("Type your message here...")).toBeEnabled();

    fireEvent.keyDown(screen.getByPlaceholderText("Type your message here..."), { key: "Enter" });
    expect(sendDraft).toHaveBeenCalled();

    const sendCalls = vi.mocked(sendDraft).mock.calls.length;
    fireEvent.keyDown(screen.getByPlaceholderText("Type your message here..."), { key: "Enter", shiftKey: true });
    expect(vi.mocked(sendDraft).mock.calls.length).toBe(sendCalls);

    await act(async () => {
      useSessionStore.setState({ pendingMode: null, outputState: "waitingForAgent", liveResponseId: "r2" });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Thinking");

    await act(async () => {
      useSessionStore.setState({ liveResponseId: null, outputState: "agentSpeaking" });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Agent speaking");

    await act(async () => {
      useSessionStore.setState({ inputState: "userSpeaking", outputState: "idle" });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("User speaking");
  });
});
