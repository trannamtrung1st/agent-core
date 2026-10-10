import { messageText, type MessagePart } from "./messageParts";
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

  it("keeps structured chips in queued snapshots and restores them after a rejected steer", async () => {
    const parts: MessagePart[] = [{kind:"invocation",invocationKind:"skill",skillKey:"definition:review",label:"Review"},{kind:"text",text:" inspect "},{kind:"reference",reference:{kind:"session",sessionId:"11111111-1111-1111-1111-111111111111"},label:"Original chat"}];
    const invoke = vi.fn().mockResolvedValue({accepted:false,error:{message:"Selected Skill is unavailable",category:"Validation",code:"ValidationError"}});
    hooks.setConnection({invoke,send:vi.fn()} as never);
    useSessionStore.setState({...emptySession(),connection:"ready",sessionId:"s1",attachmentId:"a1",liveResponseId:"r1",draft:messageText(parts),draftParts:parts});
    await sendDraft(); parts[0] = {kind:"text",text:"mutated"};
    const queued = useSessionStore.getState().pendingSendQueue[0];
    expect(queued.parts?.[0].kind).toBe("invocation");
    await steerQueuedSend(queued.localId);
    expect(invoke).toHaveBeenCalledWith("SendText",expect.objectContaining({payload:expect.objectContaining({parts:[{kind:"invocation",invocationKind:"skill",skillKey:"definition:review"},{kind:"text",text:" inspect "},{kind:"reference",reference:{kind:"session",sessionId:"11111111-1111-1111-1111-111111111111"}}]})}));
    expect(useSessionStore.getState().pendingSendQueue[0].parts).toEqual(queued.parts);
    useSessionStore.setState({draft:queued.text,draftParts:queued.parts}); await sendDraft("interrupt");
    expect(useSessionStore.getState().draftParts).toEqual(queued.parts);
  });

  it("replays uncertain typed input unchanged while preserving a newer typed draft", async () => {
    const original: MessagePart[] = [{kind:"invocation",invocationKind:"skill",skillKey:"definition:review"},{kind:"text",text:" inspect"}];
    const newer: MessagePart[] = [{kind:"reference",reference:{kind:"session",sessionId:"22222222-2222-2222-2222-222222222222"}}];
    const invoke = vi.fn().mockRejectedValueOnce(new Error("lost ACK")).mockResolvedValue({accepted:true});
    hooks.setConnection({invoke,send:vi.fn()} as never);
    useSessionStore.setState({...emptySession(),connection:"ready",sessionId:"s1",attachmentId:"a1",liveResponseId:"r1",draft:messageText(original),draftParts:original});
    await sendDraft("interrupt");
    const first = invoke.mock.calls[0][1];
    useSessionStore.setState({connection:"ready",draft:messageText(newer),draftParts:newer});
    hooks.handleEvent({protocolVersion:1,sessionId:"s1",attachmentId:"a2",eventId:"ready",sequence:1,timestamp:new Date().toISOString(),correlationId:"ready",causationId:null,responseId:null,type:"session.ready",payload:{history:[],mode:"text",status:"attached",outputState:"idle",activeResponseId:null}});
    await vi.waitFor(()=>expect(invoke).toHaveBeenCalledTimes(2));
    expect(invoke.mock.calls[1][1].eventId).toBe(first.eventId);
    expect(invoke.mock.calls[1][1].payload.parts).toEqual(first.payload.parts);
    expect(useSessionStore.getState().draftParts).toEqual(newer);
  });

  it("preserves a newer reference revision when history confirms a lost ACK", async () => {
    const original: MessagePart[] = [{ kind: "reference", reference: { kind: "session", sessionId: "11111111-1111-1111-1111-111111111111", selectedRevision: 1 } }];
    const newer: MessagePart[] = [{ kind: "reference", reference: { kind: "session", sessionId: "11111111-1111-1111-1111-111111111111", selectedRevision: 2 } }];
    const invoke = vi.fn().mockRejectedValue(new Error("lost ACK"));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: messageText(original), draftParts: original });
    await sendDraft("interrupt");
    const eventId = invoke.mock.calls[0][1].eventId;
    useSessionStore.setState({ connection: "ready", draft: messageText(newer), draftParts: newer });
    hooks.handleEvent({ protocolVersion: 1, sessionId: "s1", attachmentId: "a2", eventId: "ready", sequence: 1, timestamp: new Date().toISOString(), correlationId: "ready", causationId: null, responseId: null, type: "session.ready", payload: { history: [{ entryId: "entry-1", sequence: 1, sourceEventId: eventId, role: "user", text: messageText(original), parts: original, status: "completed", createdAt: new Date().toISOString() }], mode: "text", status: "attached", outputState: "idle", activeResponseId: null } });
    expect(useSessionStore.getState().draftParts).toEqual(newer);
    expect(useSessionStore.getState().draft).toBe(messageText(newer));
    expect(invoke).toHaveBeenCalledTimes(1);
  });
  it("omits empty editor parts for attachment-only sends", async () => {
    const invoke=vi.fn().mockResolvedValue({accepted:true}); hooks.setConnection({invoke,send:vi.fn()} as never);
    const file={localId:"file1",displayName:"notes.txt",contentType:"text/plain",byteSize:4,status:"ready" as const,progress:100,attachmentId:"att1",error:null};
    useSessionStore.setState({...emptySession(),connection:"ready",sessionId:"s1",attachmentId:"a1",draft:"",draftParts:[],pendingAttachments:[file]});
    await sendDraft(); expect(invoke).toHaveBeenCalledWith("SendText",expect.objectContaining({payload:{text:"",attachmentIds:["att1"],behavior:"queue"}}));
  });

  it("does not send a Skill-only turn", async () => {
    const invoke = vi.fn(); hooks.setConnection({invoke,send:vi.fn()} as never);
    const parts: MessagePart[] = [{kind:"invocation",invocationKind:"skill",skillKey:"definition:review"}];
    useSessionStore.setState({...emptySession(),connection:"ready",sessionId:"s1",attachmentId:"a1",draft:messageText(parts),draftParts:parts});
    await sendDraft(); expect(invoke).not.toHaveBeenCalled(); expect(useSessionStore.getState().draftParts).toEqual(parts);
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

  it("sends a draft immediately with interrupt semantics while preserving the local FIFO", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Later" });
    await sendDraft();
    const queue = useSessionStore.getState().pendingSendQueue;
    useSessionStore.setState({ draft: "Change direction" });
    await sendDraft("interrupt");
    expect(invoke).toHaveBeenCalledWith("SendText", expect.objectContaining({ payload: expect.objectContaining({ text: "Change direction", behavior: "interrupt" }) }));
    expect(useSessionStore.getState().pendingSendQueue).toEqual(queue);
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries.some(entry => entry.role === "user" && entry.text === "Change direction")).toBe(true);
  });

  it("does not send an empty steer or consume the queue when idle", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    const queue = [{ localId: "q1", eventId: "e1", text: "Later", attachmentIds: [], attachments: [] }];
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", draft: "  ", pendingSendQueue: queue });
    await sendDraft("interrupt");
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toEqual(queue);
  });

  it("restores a rejected steer draft without queueing it", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Try again", category: "Validation", code: "ValidationError" } });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Change direction" });
    await sendDraft("interrupt");
    expect(useSessionStore.getState().draft).toBe("Change direction");
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(0);
    expect(useSessionStore.getState().entries).toHaveLength(0);
    expect(useSessionStore.getState().error).toBe("Try again");
  });

  it("preserves a new steer draft when a send acknowledgement is in flight", async () => {
    let accept!: (ack: { accepted: boolean }) => void;
    const invoke = vi.fn(() => new Promise<{ accepted: boolean }>(resolve => { accept = resolve; }));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "First" });
    const first = sendDraft("interrupt");
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledTimes(1));
    useSessionStore.setState({ draft: "Second" });
    const second = sendDraft("interrupt");
    accept({ accepted: true });
    await Promise.all([first, second]);
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().draft).toBe("Second");
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(0);
  });

  it.each(["idle", "generating"])("retries an uncertain draft steer while %s without consuming a newer draft or files", async (outputState) => {
    const invoke = vi.fn().mockRejectedValueOnce(new Error("Disconnected")).mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Steer now" });
    await sendDraft("interrupt");
    const original = invoke.mock.calls[0][1];
    const newFile = { localId: "newFile", displayName: "next.txt", contentType: "text/plain", byteSize: 4, status: "uploading" as const, progress: 20, attachmentId: null, error: null };
    useSessionStore.setState({ draft: "New unsent draft", pendingAttachments: [newFile] });
    hooks.handleEvent({ protocolVersion: 1, sessionId: "s1", attachmentId: "a2", eventId: "ready", sequence: 1, timestamp: new Date().toISOString(), correlationId: "ready", causationId: null, responseId: null, type: "session.ready", payload: { history: [], mode: "text", status: "attached", outputState, activeResponseId: outputState === "generating" ? "r1" : null } });
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledTimes(2));
    expect(invoke.mock.calls[1][1]).toMatchObject({ eventId: original.eventId, payload: { text: "Steer now", attachmentIds: [], behavior: "interrupt" } });
    expect(useSessionStore.getState().draft).toBe("New unsent draft");
    expect(useSessionStore.getState().pendingAttachments).toEqual([newFile]);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(0);
  });

  it.each(["rejected", "disconnected"])("preserves newer attachments when a steer is %s", async (outcome) => {
    let settle!: (value: unknown) => void;
    let disconnect!: (reason: Error) => void;
    const invoke = vi.fn(() => new Promise((resolve, reject) => { settle = resolve; disconnect = reject; }));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    const sentFile = { localId: "oldFile", displayName: "old.txt", contentType: "text/plain", byteSize: 4, status: "ready" as const, progress: 100, attachmentId: "oldId", error: null };
    const newFile = { ...sentFile, localId: "newFile", displayName: "new.txt", attachmentId: "newId" };
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Steer", pendingAttachments: [sentFile] });
    const sending = sendDraft("interrupt");
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledTimes(1));
    useSessionStore.setState({ draft: "Next", pendingAttachments: [newFile] });
    if (outcome === "disconnected") disconnect(new Error("Disconnected"));
    else settle({ accepted: false, error: { message: "Rejected", category: "Validation", code: "ValidationError" } });
    await sending;
    expect(useSessionStore.getState().draft).toBe("Next");
    expect(useSessionStore.getState().pendingAttachments).toEqual([newFile, sentFile]);
  });

  it("steers attachment-only content without consuming another queued message", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    const file = { localId: "file1", displayName: "notes.txt", contentType: "text/plain", byteSize: 4, status: "ready" as const, progress: 100, attachmentId: "att1", error: null };
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Later" });
    await sendDraft();
    const queue = useSessionStore.getState().pendingSendQueue;
    useSessionStore.setState({ pendingAttachments: [file] });
    await sendDraft("interrupt");
    expect(invoke).toHaveBeenCalledWith("SendText", expect.objectContaining({ payload: { text: "", attachmentIds: ["att1"], behavior: "interrupt" } }));
    expect(useSessionStore.getState().pendingAttachments).toHaveLength(0);
    expect(useSessionStore.getState().pendingSendQueue).toEqual(queue);
  });

  it("clears restored attachments after a successful retry with the same steer event ID", async () => {
    const invoke = vi.fn().mockRejectedValueOnce(new Error("Disconnected")).mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    const file = { localId: "file1", displayName: "notes.txt", contentType: "text/plain", byteSize: 4, status: "ready" as const, progress: 100, attachmentId: "att1", error: null };
    useSessionStore.setState({ ...emptySession(), connection: "ready", sessionId: "s1", attachmentId: "a1", liveResponseId: "r1", draft: "Steer", pendingAttachments: [file] });
    await sendDraft("interrupt");
    expect(useSessionStore.getState().pendingAttachments).toEqual([file]);
    await sendDraft("interrupt");
    expect(invoke.mock.calls[1][1].eventId).toBe(invoke.mock.calls[0][1].eventId);
    expect(useSessionStore.getState().pendingAttachments).toHaveLength(0);
    expect(useSessionStore.getState().entries).toHaveLength(1);
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
