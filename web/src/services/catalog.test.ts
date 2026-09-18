import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyCatalog, useSessionStore } from "../state/sessionStore";
import {
  archiveCatalogItem,
  deleteCatalogItem,
  renameCatalogItem
} from "./catalog";
import {
  archiveSession,
  durableDeleteSession,
  listCatalog,
  renameSession
} from "./api";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    archiveSession: vi.fn(),
    durableDeleteSession: vi.fn(),
    listCatalog: vi.fn(),
    renameSession: vi.fn()
  };
});

const live = {
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
};

describe("catalog mutations", () => {
  afterEach(() => {
    useSessionStore.setState({ ...emptyCatalog(), catalogItems: [live] });
    vi.clearAllMocks();
  });

  it("surfaces rename failures and clears mutation state", async () => {
    vi.mocked(renameSession).mockRejectedValue(new Error("Rename failed."));
    const ok = await renameCatalogItem("s1", "New title");
    expect(ok).toBe(false);
    expect(useSessionStore.getState().catalogError).toBe("Rename failed.");
    expect(useSessionStore.getState().catalogMutation).toBeNull();
  });

  it("tracks rename loading while the request is in flight", async () => {
    let resolve!: () => void;
    vi.mocked(renameSession).mockImplementation(
      () =>
        new Promise((resolvePromise) => {
          resolve = () => resolvePromise(live);
        })
    );
    const pending = renameCatalogItem("s1", "New title");
    expect(useSessionStore.getState().catalogMutation).toEqual({ sessionId: "s1", kind: "rename" });
    resolve();
    await expect(pending).resolves.toBe(true);
    expect(useSessionStore.getState().catalogMutation).toBeNull();
  });

  it("surfaces delete failures and clears mutation state", async () => {
    vi.mocked(durableDeleteSession).mockRejectedValue(new Error("Storage failed."));
    const ok = await deleteCatalogItem(live);
    expect(ok).toBe(false);
    expect(useSessionStore.getState().catalogError).toBe("Storage failed.");
    expect(useSessionStore.getState().catalogMutation).toBeNull();
  });

  it("reorders the catalog after rename patches updatedAt", async () => {
    const older = {
      ...live,
      sessionId: "s-old",
      title: "Older chat",
      updatedAt: "2026-09-16T00:00:00.000Z"
    };
    const newer = {
      ...live,
      sessionId: "s-new",
      title: "Newer chat",
      updatedAt: "2026-09-16T00:02:00.000Z"
    };
    useSessionStore.setState({ ...emptyCatalog(), catalogItems: [older, newer] });
    vi.mocked(renameSession).mockResolvedValue({
      ...older,
      title: "Older chat renamed",
      updatedAt: "2026-09-16T00:03:00.000Z"
    });
    const ok = await renameCatalogItem("s-old", "Older chat renamed");
    expect(ok).toBe(true);
    expect(useSessionStore.getState().catalogItems.map((item) => item.sessionId)).toEqual(["s-old", "s-new"]);
  });

  it("refreshes after a successful archive", async () => {
    vi.mocked(archiveSession).mockResolvedValue({ ...live, archived: true });
    vi.mocked(listCatalog).mockResolvedValue({
      items: [{ ...live, archived: true }],
      nextCursor: null,
      hasMore: false
    });
    const ok = await archiveCatalogItem("s1");
    expect(ok).toBe(true);
    expect(listCatalog).toHaveBeenCalled();
    expect(useSessionStore.getState().catalogError).toBeNull();
  });
});
