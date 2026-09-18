import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  composerSendLabel,
  composerSteerEnabled,
  composerStopEnabled,
  realtimeTestHooks,
  removeQueuedSend,
  sendDraft,
  steerQueuedSend,
  cancelRenderedResponse
} from "./realtime";
import { emptySession, useSessionStore } from "../state/sessionStore";

const hooks = realtimeTestHooks!;

describe("Codex-style pending send queue", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hooks.resetOutput();
    useSessionStore.setState(emptySession());
  });

  it("queues locally during a live response without SendText or history entries", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      liveResponseId: "r1",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
    expect(useSessionStore.getState().pendingSendQueue[0].text).toBe("Hello");
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries).toHaveLength(0);
    expect(composerSendLabel()).toBe("Queue");
  });

  it("preserves FIFO for multiple queued sends", async () => {
    hooks.setConnection({ invoke: vi.fn(), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      draft: "U2",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    useSessionStore.setState({ draft: "U3" });
    await sendDraft();
    expect(useSessionStore.getState().pendingSendQueue.map((item) => item.text)).toEqual(["U2", "U3"]);
  });

  it("dispatches only the queue head after natural completion", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "U2",
          attachmentIds: [],
          attachments: []
        },
        {
          localId: "q2",
          eventId: "e2",
          text: "U3",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "evt",
      sequence: 2,
      timestamp: new Date().toISOString(),
      correlationId: "evt",
      causationId: null,
      responseId: "r1",
      type: "agent.response.completed",
      payload: { status: "completed" }
    });
    await vi.waitFor(() => expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1));
    const sendCall = invoke.mock.calls.find((call) => call[0] === "SendText");
    expect(sendCall?.[1]).toMatchObject({
      type: "user.text",
      eventId: "e1",
      payload: { text: "U2", attachmentIds: [] }
    });
    expect(useSessionStore.getState().pendingSendQueue[0].text).toBe("U3");
    expect(useSessionStore.getState().entries.some((entry) => entry.role === "user" && entry.text === "U2")).toBe(true);
  });

  it("does not dispatch the queue head after Stop", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "U2",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "evt",
      sequence: 2,
      timestamp: new Date().toISOString(),
      correlationId: "evt",
      causationId: null,
      responseId: "r1",
      type: "agent.response.interrupted",
      payload: { reason: "userStop" }
    });
    await Promise.resolve();
    expect(invoke.mock.calls.some((call) => call[0] === "SendText")).toBe(false);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("does not dispatch the queue head after failed completion", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "U2",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "evt",
      sequence: 2,
      timestamp: new Date().toISOString(),
      correlationId: "evt",
      causationId: null,
      responseId: "r1",
      type: "agent.response.completed",
      payload: { status: "failed" }
    });
    await Promise.resolve();
    expect(invoke.mock.calls.some((call) => call[0] === "SendText")).toBe(false);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("does not dispatch the queue head after non-terminal interruption", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "U2",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "evt",
      sequence: 2,
      timestamp: new Date().toISOString(),
      correlationId: "evt",
      causationId: null,
      responseId: "r1",
      type: "agent.response.interrupted",
      payload: { reason: "newText" }
    });
    await Promise.resolve();
    expect(invoke.mock.calls.some((call) => call[0] === "SendText")).toBe(false);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("steers with interrupt semantics and leaves later queued items", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "U2",
          attachmentIds: [],
          attachments: []
        },
        {
          localId: "q2",
          eventId: "e2",
          text: "U3",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    expect(composerSteerEnabled("q1")).toBe(true);
    await steerQueuedSend("q1");
    expect(invoke).toHaveBeenCalledWith(
      "SendText",
      expect.objectContaining({
        type: "user.text",
        eventId: "e1",
        payload: expect.objectContaining({ text: "U2", behavior: "interrupt" })
      })
    );
    expect(useSessionStore.getState().pendingSendQueue.map((item) => item.text)).toEqual(["U3"]);
  });

  it("steers a middle queue item without reordering the rest", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        { localId: "qa", eventId: "ea", text: "A", attachmentIds: [], attachments: [] },
        { localId: "qb", eventId: "eb", text: "B", attachmentIds: [], attachments: [] },
        { localId: "qc", eventId: "ec", text: "C", attachmentIds: [], attachments: [] }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    await steerQueuedSend("qb");
    expect(invoke).toHaveBeenCalledWith(
      "SendText",
      expect.objectContaining({
        eventId: "eb",
        payload: expect.objectContaining({ text: "B", behavior: "interrupt" })
      })
    );
    expect(useSessionStore.getState().pendingSendQueue.map((item) => item.text)).toEqual(["A", "C"]);
  });

  it("disables steer on other rows while any item is dispatching", () => {
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      liveResponseId: "r1",
      pendingSendQueue: [
        { localId: "qa", eventId: "ea", text: "A", attachmentIds: [], attachments: [], dispatching: true },
        { localId: "qb", eventId: "eb", text: "B", attachmentIds: [], attachments: [] }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    expect(composerSteerEnabled("qa")).toBe(false);
    expect(composerSteerEnabled("qb")).toBe(false);
  });

  it("ignores a second steer while the first send is in flight", async () => {
    let resolveSend: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () =>
        new Promise<{ accepted: boolean }>((resolve) => {
          resolveSend = resolve;
        })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        { localId: "qa", eventId: "ea", text: "A", attachmentIds: [], attachments: [] },
        { localId: "qb", eventId: "eb", text: "B", attachmentIds: [], attachments: [] }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    const first = steerQueuedSend("qa");
    await Promise.resolve();
    await steerQueuedSend("qb");
    expect(invoke).toHaveBeenCalledTimes(1);
    resolveSend({ accepted: true });
    await first;
  });

  it("removing a queued item releases its attachment snapshot", async () => {
    useSessionStore.setState({
      ...emptySession(),
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e1",
          text: "file",
          attachmentIds: ["att-1"],
          attachments: [
            {
              localId: "local-1",
              displayName: "notes.txt",
              contentType: "text/plain",
              byteSize: 4,
              status: "ready",
              progress: 100,
              attachmentId: "att-1",
              error: null
            }
          ]
        }
      ]
    });
    await removeQueuedSend("q1");
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(0);
  });

  it("Stop captures responseId and ignores stale completion for a newer live response", async () => {
    let resolveAck: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      agents: [],
      selectedAgentId: "examiner"
    });
    const stopping = cancelRenderedResponse();
    await Promise.resolve();
    expect(invoke).toHaveBeenCalledWith(
      "CancelResponse",
      expect.objectContaining({ responseId: "r1", type: "agent.response.cancel" })
    );
    useSessionStore.setState({ liveResponseId: "r2" });
    resolveAck({ accepted: false });
    await stopping;
    expect(useSessionStore.getState().liveResponseId).toBe("r2");
    expect(composerStopEnabled()).toBe(true);
  });

  it("reconciles an uncertain queued steer without duplicating through sendDraft", async () => {
    const invoke = vi.fn().mockRejectedValue(new Error("network"));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e-steer",
          text: "Steer me",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    await steerQueuedSend("q1");
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
    expect(useSessionStore.getState().pendingSendQueue[0]?.dispatching).toBe(false);
    invoke.mockClear();
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: new Date().toISOString(),
      correlationId: "ready",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: { history: [] }
    });
    await Promise.resolve();
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("drops the queue item when reconnect history proves a lost steer ACK succeeded", async () => {
    const invoke = vi.fn().mockRejectedValue(new Error("network"));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      pendingSendQueue: [
        {
          localId: "q1",
          eventId: "e-steer",
          text: "Steer me",
          attachmentIds: [],
          attachments: []
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    await steerQueuedSend("q1");
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: new Date().toISOString(),
      correlationId: "ready",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        history: [
          {
            entryId: "entry-1",
            sequence: 1,
            sourceEventId: "e-steer",
            role: "user",
            text: "Steer me",
            status: "completed",
            createdAt: "2026-09-18T00:00:00.000Z"
          }
        ]
      }
    });
    await Promise.resolve();
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(0);
    invoke.mockClear();
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "ready-2",
      sequence: 2,
      timestamp: new Date().toISOString(),
      correlationId: "ready-2",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: { history: [] }
    });
    await Promise.resolve();
    expect(invoke).not.toHaveBeenCalled();
  });
});
