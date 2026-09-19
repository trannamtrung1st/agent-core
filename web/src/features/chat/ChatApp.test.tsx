import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ConfigProvider } from "antd";
import { afterEach, describe, expect, it, vi } from "vitest";
import { antdTheme } from "../../app/antdTheme";
import { emptySession, useSessionStore } from "../../state/sessionStore";
import { bootstrap, sendDraft, cancelRenderedResponse, resumePausedSession } from "../../services/realtime";
import { ChatApp } from "./ChatApp";

function stubMatchMedia(matches: (query: string) => boolean) {
  window.matchMedia = ((query: string) => ({
    matches: matches(query),
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
}

function renderChat() {
  return render(
    <ConfigProvider theme={antdTheme}>
      <ChatApp />
    </ConfigProvider>
  );
}

function rerenderChat(view: ReturnType<typeof render>) {
  view.rerender(
    <ConfigProvider theme={antdTheme}>
      <ChatApp />
    </ConfigProvider>
  );
}

vi.mock("../../services/realtime", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../services/realtime")>();
  return {
    ...actual,
    bootstrap: vi.fn().mockResolvedValue(""),
    reportCommittedEntries: vi.fn(),
    sendDraft: vi.fn().mockResolvedValue(undefined),
    cancelRenderedResponse: vi.fn().mockResolvedValue(undefined),
    resumePausedSession: vi.fn().mockResolvedValue(true)
  };
});

describe("ChatApp accessibility", () => {
  afterEach(() => {
    delete window.__agentCoreSpeechTest;
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
    const view = await act(async () => renderChat());
    expect(screen.getByLabelText("Identity")).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Chats" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Start conversation" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();

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
      rerenderChat(view);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Microphone permission was denied");
    expect(screen.getAllByText("Microphone permission was denied. Enable the microphone or continue in text.").length)
      .toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Attach" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Conversation actions" }));
    expect(await screen.findByRole("menuitem", { name: "End" })).toBeInTheDocument();
  });

  it("shows recoverable structured failure details from the chat owner", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        agentName: "Alex",
        agentRole: "Examiner",
        error: "Text exceeds 8000 UTF-16 code units.",
        sessionError: {
          category: "Validation",
          code: "ValidationError",
          message: "Text exceeds 8000 UTF-16 code units.",
          fatal: false,
          retryAfterMs: null,
          classId: "validation/protocol"
        },
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    const alert = screen.getByTestId("session-failure");
    expect(alert).toHaveAttribute("data-error-fatal", "false");
    fireEvent.click(screen.getByRole("button", { name: "Failure details" }));
    expect(await screen.findByTestId("session-failure-details")).toHaveTextContent("Recoverable");
  });

  it("shows fatal structured failure details on the connection owner", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "failed",
        agentName: "Alex",
        agentRole: "Examiner",
        error: "Unsupported protocol version.",
        errorFatal: true,
        sessionError: {
          category: "Protocol",
          code: "ProtocolError",
          message: "Unsupported protocol version.",
          fatal: true,
          retryAfterMs: null,
          classId: "validation/protocol"
        },
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    const alert = screen.getByTestId("session-failure");
    expect(alert).toHaveAttribute("data-error-fatal", "true");
    expect(alert.closest(".conversation-column")).not.toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Failure details" }));
    expect(await screen.findByTestId("session-failure-details")).toHaveTextContent("Fatal");
  });

  it("hides Voice in session when ready reports voice unavailable", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        voiceAvailable: false,
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      renderChat();
    });
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Attach" })).toBeInTheDocument();
  });

  it("keeps the text composer when Voice is unavailable for the speech locale", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        voiceAvailable: false,
        agentName: "Alex",
        agentRole: "Examiner",
        error: "Voice is not available for this speech locale.",
        sessionError: {
          category: "Session",
          code: "VoiceUnavailable",
          message: "Voice is not available for this speech locale.",
          fatal: false,
          retryAfterMs: null,
          classId: "speech/capture/playback"
        },
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      renderChat();
    });
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeEnabled();
    expect(screen.getByRole("button", { name: "Send" })).toBeInTheDocument();
    expect(screen.getByTestId("session-failure")).toHaveTextContent("Voice is not available for this speech locale.");
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
    const view = await act(async () => renderChat());
    expect(screen.getByTestId("connection")).toHaveTextContent("Ready");
    expect(screen.getByRole("button", { name: /^Voice$/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();

    await act(async () => {
      useSessionStore.setState({ captureLive: true, muted: false });
      rerenderChat(view);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Listening…");
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Mute" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mute" })).toHaveClass("composer-voice-live");
  });

  it("shows voice unavailable instead of listening when browser STT is blocked", async () => {
    window.__agentCoreSpeechTest = { fakeRecognizer: true };
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        mode: "voice",
        captureLive: true,
        clientTranscriptBlocked: true,
        muted: false,
        voiceAvailable: true,
        sttTransport: "clientTranscript",
        agentName: "Alex",
        agentRole: "Examiner",
        error: "Speech recognition service could not be reached.",
        sessionError: {
          category: "Speech",
          code: "SpeechRecognitionUnavailable",
          message: "Speech recognition service could not be reached.",
          fatal: false,
          retryAfterMs: null,
          classId: "speech/capture/playback",
          extensions: { recognitionError: "network" }
        },
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      renderChat();
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Voice input unavailable");
    fireEvent.click(screen.getByRole("button", { name: "Failure details" }));
    expect(await screen.findByTestId("session-failure-details")).toHaveTextContent("recognitionError");
    expect(screen.getByTestId("session-failure-details")).toHaveTextContent("network");
    expect(screen.getByRole("button", { name: "Retry voice input" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Voice$/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();
  });

  it("shows Unmute while voice mode stays active after Browser STT mute", async () => {
    window.__agentCoreSpeechTest = { fakeRecognizer: true };
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        mode: "voice",
        muted: true,
        captureLive: false,
        voiceAvailable: true,
        sttTransport: "clientTranscript",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      renderChat();
    });
    expect(screen.getByRole("button", { name: "Unmute" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Unmute" })).not.toHaveClass("composer-voice-live");
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
  });

  it("shows Voice (not Mute) when voice mode is active but capture is not live", async () => {
    window.__agentCoreSpeechTest = { fakeRecognizer: true };
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        mode: "voice",
        captureLive: false,
        muted: false,
        voiceAvailable: true,
        sttTransport: "clientTranscript",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => {
      renderChat();
    });
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Voice input inactive" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Unmute" })).not.toBeInTheDocument();
  });

  it("shows Mute while browser capture is live in voice mode", async () => {
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
      renderChat();
    });
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Mute" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Mute" })).toHaveClass("composer-voice-live");
  });

  it("renders the health profile from bootstrap", async () => {
    vi.mocked(bootstrap).mockResolvedValueOnce("Real");
    await act(async () => {
      renderChat();
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
    const view = await act(async () => renderChat());
    expect(screen.getByTestId("connection")).toHaveTextContent("Starting voice…");
    expect(screen.getByRole("button", { name: "Cancel voice" })).toBeInTheDocument();
    expect(screen.getByText("Interrupted")).toBeInTheDocument();
    expect(screen.getByLabelText("Message")).toBeEnabled();
    expect(document.querySelector(".chat-header-time time")).toHaveAttribute("dateTime", "2026-09-15T00:00:00.000Z");
    expect(screen.getByRole("combobox", { name: "Speech locale" })).toBeInTheDocument();
    expect(document.querySelector(".chat-header-copy .speech-locale-picker")).toBeNull();
    expect(document.querySelector(".chat-header-actions .speech-locale-picker")).not.toBeNull();

    fireEvent.keyDown(screen.getByLabelText("Message"), { key: "Enter" });
    expect(sendDraft).toHaveBeenCalled();

    const sendCalls = vi.mocked(sendDraft).mock.calls.length;
    fireEvent.keyDown(screen.getByLabelText("Message"), { key: "Enter", shiftKey: true });
    expect(vi.mocked(sendDraft).mock.calls.length).toBe(sendCalls);

    await act(async () => {
      useSessionStore.setState({ pendingMode: null, outputState: "waitingForAgent", liveResponseId: "r2" });
      rerenderChat(view);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Thinking…");
    expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Queue" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));
    expect(cancelRenderedResponse).toHaveBeenCalled();

    await act(async () => {
      useSessionStore.setState({ liveResponseId: "r2", outputState: "agentSpeaking" });
      rerenderChat(view);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Speaking…");

    await act(async () => {
      useSessionStore.setState({
        inputState: "userSpeaking",
        outputState: "idle",
        liveResponseId: null,
        mode: "voice",
        captureLive: true
      });
      rerenderChat(view);
    });
    expect(screen.getByTestId("connection")).toHaveTextContent("Listening…");

    await act(async () => {
      useSessionStore.setState({ liveUserTranscript: "Wait, stop" });
      rerenderChat(view);
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
    const view = await act(async () => renderChat());
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
      rerenderChat(view);
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
    await act(async () => renderChat());
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
  });

  it("shows ended history without composer or End", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "idle",
        status: "ended",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: "e1",
            role: "user",
            text: "Please explain.",
            responseId: null,
            status: "completed",
            deliveryMode: "text",
            heardTextEndExclusive: 15,
            receivedTextEndExclusive: 15,
            createdAt: "2026-09-16T00:00:00.000Z"
          }
        ]
      });
    });
    await act(async () => renderChat());
    expect(screen.getByText("Please explain.")).toBeInTheDocument();
    expect(screen.getByTestId("connection")).toHaveTextContent("Ended");
    expect(screen.getByText("This conversation has ended.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Message")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Conversation actions" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Speech locale" })).not.toBeInTheDocument();
  });

  it("names completed sessions without Resume or composer", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "idle",
        status: "ended",
        lifecycleStatus: "completed",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: "e1",
            role: "user",
            text: "Please explain.",
            responseId: null,
            status: "completed",
            deliveryMode: "text",
            heardTextEndExclusive: 15,
            receivedTextEndExclusive: 15,
            createdAt: "2026-09-16T00:00:00.000Z"
          }
        ]
      });
    });
    await act(async () => renderChat());
    expect(screen.getByTestId("connection")).toHaveTextContent("Completed");
    expect(screen.getByText("This conversation is completed.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Resume" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Message")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Speech locale" })).not.toBeInTheDocument();
  });

  it("shows reason-specific pause copy and a keyboard-operable Resume", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "idle",
        status: "paused",
        pauseReason: "silentEvaluation",
        agentName: "Alex",
        agentRole: "Examiner",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: "e1",
            role: "user",
            text: "Hello",
            responseId: null,
            status: "completed",
            deliveryMode: "text",
            heardTextEndExclusive: 5,
            receivedTextEndExclusive: 5,
            createdAt: "2026-09-16T00:00:00.000Z"
          }
        ]
      });
    });
    await act(async () => renderChat());
    expect(screen.getByTestId("connection")).toHaveTextContent("Paused");
    expect(screen.getByText(/paused after repeated quiet checks/i)).toBeInTheDocument();
    expect(screen.queryByText("This conversation has ended.")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Speech locale" })).toBeInTheDocument();
    const resume = screen.getByRole("button", { name: "Resume" });
    expect(resume).toBeEnabled();
    resume.focus();
    expect(resume).toHaveFocus();
    fireEvent.click(resume);
    expect(resumePausedSession).toHaveBeenCalled();
  });

  it("keeps the composer while the session is still ending", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "ready",
        status: "ending",
        agentName: "Alex",
        agentRole: "Examiner",
        voiceAvailable: true,
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    expect(screen.getByLabelText("Message")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Conversation actions" }));
    expect(await screen.findByRole("menuitem", { name: "End" })).toBeInTheDocument();
    expect(screen.queryByText("This conversation has ended.")).not.toBeInTheDocument();
  });

  it("shows the history-load error for a failed ended view", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "failed",
        status: "ended",
        agentName: "Alex",
        agentRole: "Examiner",
        error: "Unable to open the conversation.",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    expect(screen.getByRole("alert")).toHaveTextContent("Unable to open the conversation.");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(screen.getByText("This conversation has ended.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Message")).not.toBeInTheDocument();
  });

  it("reconnects with inline status over visible history, not an overlay", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "reconnecting",
        agentName: "Jordan",
        agentRole: "Compliance reviewer",
        entries: [
          {
            entryId: "e1",
            sequence: 1,
            sourceEventId: null,
            role: "assistant",
            text: "The document is as follows:",
            responseId: "r1",
            status: "completed",
            deliveryMode: "text",
            heardTextEndExclusive: 0,
            receivedTextEndExclusive: 26,
            createdAt: "2026-09-17T06:43:00.000Z"
          }
        ]
      });
    });
    await act(async () => renderChat());
    expect(screen.getByText("The document is as follows:")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting to Agent Core…");
    expect(screen.queryByText("Loading conversation")).not.toBeInTheDocument();
    expect(document.querySelector(".conversation-window .ant-spin-nested-loading")).toBeNull();
  });
});

describe("ChatApp model selection", () => {
  afterEach(() => {
    act(() => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [],
        selectedAgentId: "examiner",
        modelCatalog: [],
        modelCatalogDefaultKey: null
      });
    });
  });

  const models = [
    {
      key: "scripted-alpha",
      displayName: "Scripted Alpha",
      tools: true,
      vision: false,
      structuredOutput: false,
      reasoning: true,
      supportedReasoningEfforts: ["low", "medium", "high"],
      defaultReasoningEffort: "medium"
    },
    {
      key: "scripted-beta",
      displayName: "Scripted Beta",
      tools: true,
      vision: false,
      structuredOutput: false,
      reasoning: false,
      supportedReasoningEfforts: [],
      defaultReasoningEffort: null
    }
  ];

  it("starts a new chat on Default and restores a persisted session model", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner",
        modelCatalog: models,
        modelCatalogDefaultKey: "scripted-alpha"
      });
    });
    const view = await act(async () => renderChat());
    const newChatModel = screen.getByRole("button", { name: "Model" });
    expect(newChatModel.closest("form.composer")).not.toBeNull();
    expect(screen.getByLabelText("Reasoning")).toBeInTheDocument();

    await act(async () => {
      useSessionStore.setState({
        sessionId: "s-model",
        connection: "ready",
        agentName: "Alex",
        agentRole: "Examiner",
        sessionModelKey: "scripted-beta",
        sessionModelDisplayName: "Scripted Beta",
        sessionModelSource: "user",
        sessionModelEffort: null
      });
      rerenderChat(view);
    });
    expect(screen.getByRole("button", { name: "Model" }).closest("form.composer")).not.toBeNull();
    expect(screen.queryByLabelText("Reasoning")).not.toBeInTheDocument();
  });

  it("disables model editing while a response is running", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s-busy",
        connection: "ready",
        agentName: "Alex",
        agentRole: "Examiner",
        liveResponseId: "r1",
        outputState: "agentGenerating",
        modelCatalog: models,
        modelCatalogDefaultKey: "scripted-alpha",
        sessionModelKey: "scripted-alpha",
        sessionModelEffort: "medium",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    expect(screen.getByRole("button", { name: "Model" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Model" }).closest("form.composer")).not.toBeNull();
    expect(screen.getByLabelText("Reasoning")).toBeInTheDocument();
  });

  it("does not allow model editing on an ended session", async () => {
    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        sessionId: "s-ended",
        connection: "idle",
        status: "ended",
        agentName: "Alex",
        agentRole: "Examiner",
        modelCatalog: models,
        modelCatalogDefaultKey: "scripted-alpha",
        sessionModelKey: "scripted-alpha",
        sessionModelEffort: "medium",
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });
    await act(async () => renderChat());
    expect(screen.getByRole("button", { name: "Model" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Model" }).closest("form.composer")).toBeNull();
    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
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

  it(
    "exposes the same session catalog from a drawer",
    async () => {
    stubMatchMedia((query) => /max-width:\s*767px/i.test(query));

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

    await act(async () => renderChat());
    expect(screen.getByRole("button", { name: "Open chats" })).toBeInTheDocument();
    expect(screen.queryByTestId("session-rail")).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open chats" }));
    await waitFor(() => {
      expect(screen.getByRole("dialog", { name: "Chats" })).toBeInTheDocument();
    });
    const rail = screen.getByTestId("session-rail");
    expect(rail).toBeInTheDocument();
    expect(within(rail).queryByText("Sessions")).not.toBeInTheDocument();
    expect(within(rail).queryByText("Chats")).not.toBeInTheDocument();
    expect(screen.getByRole("dialog", { name: "Chats" })).toBeInTheDocument();
    expect(within(rail).getByRole("button", { name: "Chat list options" })).toBeInTheDocument();
    expect(within(rail).queryByRole("button", { name: "Start a new chat" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start a new chat" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Planning notes" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Planning notes" }));
    await waitFor(() => {
      expect(screen.getByRole("menuitem", { name: "Rename" })).toBeInTheDocument();
      expect(screen.getByRole("menuitem", { name: "Delete" })).toBeInTheDocument();
    });
  },
  15_000);
});

describe("ChatApp tablet session rail", () => {
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

  it("uses a narrower persistent sider between 768px and 1199px", async () => {
    stubMatchMedia((query) => /max-width:\s*1199px/i.test(query) && !/max-width:\s*767px/i.test(query));

    await act(async () => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
        selectedAgentId: "examiner"
      });
    });

    await act(async () => renderChat());
    expect(screen.queryByRole("button", { name: "Open chats" })).not.toBeInTheDocument();
    const sider = document.querySelector(".app-sider");
    expect(sider).toHaveStyle({ width: "240px" });
    expect(screen.getByRole("navigation", { name: "Chats" })).toBeInTheDocument();
    expect(screen.getByText("Chats")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start a new chat" })).toBeInTheDocument();
  });
});
