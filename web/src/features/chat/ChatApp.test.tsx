import { act, fireEvent, render, screen, within } from "@testing-library/react";
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
    expect(screen.getByRole("navigation", { name: "Sessions" })).toBeInTheDocument();
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
    expect(screen.getByRole("button", { name: "Attach" })).toBeInTheDocument();
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

  it("blocks send while uploads are pending and allows attachment-only send", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner",
        pendingAttachments: [
          {
            localId: "l1",
            displayName: "notes.txt",
            contentType: "text/plain",
            byteSize: 4,
            status: "uploading",
            progress: 40,
            attachmentId: null,
            error: null
          }
        ]
      });
    });
    const view = await act(async () => render(<ChatApp />));
    expect(screen.getByRole("button", { name: "Attach" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
    expect(screen.getByText(/40%/)).toBeInTheDocument();

    await act(async () => {
      useSessionStore.setState({
        pendingAttachments: [
          {
            localId: "l1",
            displayName: "notes.txt",
            contentType: "text/plain",
            byteSize: 4,
            status: "ready",
            progress: 100,
            attachmentId: "a1",
            error: null
          }
        ]
      });
      view.rerender(<ChatApp />);
    });
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled();
  });

  it("renders history attachment names", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: "e1",
            role: "user",
            text: "",
            responseId: null,
            status: "completed",
            deliveryMode: "text",
            heardTextEndExclusive: 0,
            receivedTextEndExclusive: 0,
            createdAt: "2026-09-16T00:00:00.000Z",
            attachments: [{ attachmentId: "a1", displayName: "notes.txt", contentType: "text/plain" }]
          }
        ]
      });
    });
    await act(async () => render(<ChatApp />));
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
  });
});

describe("ChatApp narrow session drawer", () => {
  const matchMedia = window.matchMedia;

  afterEach(() => {
    window.matchMedia = matchMedia;
    act(() => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [],
        selectedAgentId: "examiner"
      });
    });
  });

  it("exposes the same session catalog from a drawer", async () => {
    window.matchMedia = ((query: string) => ({
      matches: /max-width:\s*767px/i.test(query),
      media: query,
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent() {
        return false;
      }
    })) as typeof window.matchMedia;

    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner",
        catalogItems: [
          {
            sessionId: "s1",
            title: "Planning notes",
            agentId: "examiner",
            agentVersion: 1,
            status: "paused",
            archived: false,
            ended: false,
            workspaceOwned: true,
            runtimeEpoch: 0,
            revision: 2,
            createdAt: "2026-09-16T00:00:00.000Z",
            updatedAt: "2026-09-16T00:01:00.000Z"
          }
        ]
      });
    });

    await act(async () => render(<ChatApp />));
    expect(screen.getByRole("button", { name: "Open sessions" })).toBeInTheDocument();
    expect(screen.queryByTestId("session-rail")).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open sessions" }));
    const rail = screen.getByTestId("session-rail");
    expect(rail).toBeInTheDocument();
    expect(within(rail).queryByText("Sessions")).not.toBeInTheDocument();
    expect(within(rail).queryByRole("button", { name: "Start a new chat" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start a new chat" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Planning notes/ })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Rename" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delete" })).toBeInTheDocument();
  });
});
