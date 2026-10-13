import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore, type PendingApproval } from "../state/sessionStore";
import { realtimeTestHooks, respondToApproval } from "./realtime";

const hooks = realtimeTestHooks!;
const pending: PendingApproval = {
  approvalId: "approval-1", responseId: "response-1", operationId: "operation-1",
  toolName: "demo.sensitive_action", effect: "sensitiveWrite", summary: "Sensitive action",
  details: {}, expiresAt: "2099-01-01T00:00:00Z"
};

beforeEach(() => {
  hooks.resetOutput();
  useSessionStore.setState({ ...emptySession(), sessionId: "session-1", attachmentId: "attachment-1", pendingApproval: pending });
});
afterEach(() => { hooks.resetOutput(); hooks.setConnection(null); });

describe("approval decision recovery", () => {
  it("does not send a queued decision to a different Session", async () => {
    let finish!: (value: unknown) => void;
    const invoke = vi.fn(() => new Promise(resolve => { finish = resolve; }));
    hooks.setConnection({ invoke } as never);
    const first = respondToApproval("reject");
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledOnce());
    const queued = respondToApproval("reject");
    useSessionStore.setState({ sessionId: "session-2", attachmentId: "attachment-2" });
    finish({ accepted: true });
    await Promise.all([first, queued]);
    expect(invoke).toHaveBeenCalledOnce();
    expect(useSessionStore.getState().pendingApproval).toEqual(pending);
  });

  it("keeps a failed transport decision reviewable and retries the exact rejection", async () => {
    const invoke = vi.fn().mockRejectedValueOnce(new Error("Connection lost.")).mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke } as never);
    await respondToApproval("reject");
    expect(useSessionStore.getState().pendingApproval).toEqual({ ...pending, decisionError: "Connection lost." });
    await respondToApproval("reject");
    expect(useSessionStore.getState().pendingApproval).toBeNull();
    expect(invoke).toHaveBeenCalledTimes(2);
    for (const [method, command] of invoke.mock.calls) {
      expect(method).toBe("RespondApproval");
      expect(command).toMatchObject({ responseId: pending.responseId, payload: { approvalId: pending.approvalId, decision: "reject" } });
    }
  });

  it.each([true, false])("does not clear a replacement approval on a delayed ACK (accepted=%s)", async accepted => {
    let finish!: (value: unknown) => void;
    const invoke = vi.fn(() => new Promise(resolve => { finish = resolve; }));
    hooks.setConnection({ invoke } as never);
    const decision = respondToApproval("reject");
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledOnce());
    const replacement = { ...pending, approvalId: "approval-2" };
    useSessionStore.setState({ pendingApproval: replacement });
    finish({ accepted, error: accepted ? undefined : { code: "StaleCommand" } });
    await decision;
    expect(useSessionStore.getState().pendingApproval).toEqual(replacement);
  });

  it("ignores a failed decision after reattachment", async () => {
    let fail!: (error: Error) => void;
    const invoke = vi.fn(() => new Promise((_, reject) => { fail = reject; }));
    hooks.setConnection({ invoke } as never);
    const decision = respondToApproval("reject");
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledOnce());
    useSessionStore.setState({ attachmentId: "attachment-2" });
    fail(new Error("Old connection closed."));
    await decision;
    expect(useSessionStore.getState().pendingApproval).toEqual(pending);
    expect(useSessionStore.getState().error).toBeNull();
  });

  it("retains a refused decision and exposes the ACK error for retry", async () => {
    hooks.setConnection({ invoke: vi.fn().mockResolvedValue({ accepted: false, error: { code: "Unavailable", message: "Retry this decision." } }) } as never);
    await respondToApproval("reject");
    expect(useSessionStore.getState().pendingApproval).toEqual({ ...pending, decisionError: "Retry this decision." });
  });
});
