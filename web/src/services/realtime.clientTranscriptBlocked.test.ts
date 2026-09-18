import { beforeEach, describe, expect, it } from "vitest";
import { emptySession, useSessionStore } from "../state/sessionStore";
import { createSpeechTransportService } from "../speech/speechTransport";
import { FakeSpeechRecognizer } from "../speech/fakeSpeechAdapters";
import { ClientTranscriptLifecycle } from "../speech/clientTranscriptLifecycle";
import { conversationStatusLabel } from "../features/chat/activityState";

describe("client transcript blocked store mirror", () => {
  beforeEach(() => {
    useSessionStore.setState(emptySession());
  });

  it("keeps clientTranscriptBlocked true after clearing recoverable session errors", async () => {
    const fake = new FakeSpeechRecognizer();
    const transport = createSpeechTransportService(fake);
    transport.setActiveInputTransport("clientTranscript");
    const life = new ClientTranscriptLifecycle(
      transport,
      () => undefined,
      () => undefined,
      () => {
        useSessionStore.setState({ clientTranscriptBlocked: life.isBlocked() });
      }
    );

    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    fake.fail("SpeechRecognitionUnavailable");
    expect(life.isBlocked()).toBe(true);
    expect(useSessionStore.getState().clientTranscriptBlocked).toBe(true);

    useSessionStore.setState({
      error: null,
      sessionError: null,
      errorFatal: false
    });
    useSessionStore.setState({ clientTranscriptBlocked: life.isBlocked() });

    expect(useSessionStore.getState().clientTranscriptBlocked).toBe(true);
    expect(
      conversationStatusLabel({
        connection: "ready",
        pendingVoice: false,
        voiceLive: false,
        clientTranscriptBlocked: true,
        sessionStatus: "attached",
        inputState: "idle",
        outputState: "idle",
        liveResponseId: null
      })
    ).toBe("Voice input unavailable");
  });
});
