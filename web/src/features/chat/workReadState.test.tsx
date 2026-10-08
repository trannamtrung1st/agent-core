import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentRun } from "../../services/api";
import { WORK_READ_STORAGE_KEY, useWorkReadState } from "./workReadState";

const attention = { agentRunId: "read-test-work", status: "completed", revision: 3, updatedAt: "2026-10-05T10:00:00Z", outcome: { attentionRequired: true } } as AgentRun;

describe("background work read state", () => {
  beforeEach(() => {
    const values = new Map<string, string>();
    Object.defineProperty(window, "localStorage", { configurable: true, value: {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
      removeItem: (key: string) => { values.delete(key); }
    } });
  });
  it("updates subscribers, survives remount, and treats a newer result as unread", () => {
    const first = renderHook(useWorkReadState);
    const second = renderHook(useWorkReadState);
    expect(first.result.current.isUnread(attention)).toBe(true);
    act(() => first.result.current.markRead([attention]));
    expect(second.result.current.isUnread(attention)).toBe(false);
    first.unmount();
    const remounted = renderHook(useWorkReadState);
    expect(remounted.result.current.isUnread(attention)).toBe(false);
    expect(remounted.result.current.isUnread({ ...attention, revision: 4 })).toBe(true);
    expect(remounted.result.current.isUnread({ ...attention, agentRunId: "another-work" })).toBe(true);
  });
  it("does not let an older view overwrite a newer acknowledgement", () => {
    const view = renderHook(useWorkReadState);
    const newer = { ...attention, revision: 4 };
    act(() => view.result.current.markRead([newer]));
    expect(view.result.current.isUnread(attention)).toBe(false);
    act(() => view.result.current.markRead([attention]));
    expect(view.result.current.isUnread(newer)).toBe(false);
    expect(view.result.current.isUnread({ ...attention, revision: 5 })).toBe(true);
  });
  it("ignores quiet results and approvals and recovers from corrupt browser storage", () => {
    window.localStorage.setItem(WORK_READ_STORAGE_KEY, "invalid json");
    const view = renderHook(useWorkReadState);
    expect(view.result.current.isUnread(attention)).toBe(true);
    expect(view.result.current.isUnread({ ...attention, outcome: { ...attention.outcome!, attentionRequired: false } })).toBe(false);
    expect(view.result.current.isUnread({ ...attention, status: "needsApproval" })).toBe(false);
    act(() => view.result.current.markRead([attention]));
    expect(view.result.current.isUnread(attention)).toBe(false);
  });
  it("reports unavailable persistence without clearing unread state", () => {
    const view = renderHook(useWorkReadState);
    const spy = vi.spyOn(window.localStorage, "setItem").mockImplementation(() => { throw new Error("Full"); });
    expect(() => view.result.current.markRead([attention])).toThrow("Unable to save read status");
    expect(view.result.current.isUnread(attention)).toBe(true);
    spy.mockRestore();
  });
});
