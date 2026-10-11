import { beforeEach, describe, expect, it, vi } from "vitest";
import { ownerFetch } from "../../services/api";
import { loadComposerChoices, resourceCategories } from "./composerChoices";
vi.mock("../../services/api", () => ({ ownerFetch: vi.fn() }));
const fetch = vi.mocked(ownerFetch);

beforeEach(() => fetch.mockReset());
describe("unified composer discovery", () => {
  it("combines resource sources while retaining their individual cursors", async () => {
    fetch.mockImplementation(async (url) => {
      const category = new URL(String(url), "http://localhost").searchParams.get("category")!;
      return new Response(JSON.stringify({ items: [{ id: category, category, label: category, description: "" }], nextCursor: `next-${category}` }));
    });
    const page = await loadComposerChoices("instance", "reference", "review");
    expect(page.items.map(item => item.category)).toEqual(resourceCategories);
    expect(page.next).toEqual(Object.fromEntries(resourceCategories.map(category => [category, `next-${category}`])));
    expect(page.failed).toEqual({});
  });
  it("preserves successful results and retries only the failed source at its original cursor", async () => {
    fetch.mockImplementation(async (url) => String(url).includes("category=artifact")
      ? new Response(null, { status: 503 })
      : new Response(JSON.stringify({ items: [{ id: "chat", category: "session", label: "Chat", description: "" }], nextCursor: null })));
    const page = await loadComposerChoices("instance", "reference", "", { artifact: "page2", session: "page3" });
    expect(page.items).toHaveLength(1);
    expect(page.failed).toEqual({ artifact: "page2" });
    fetch.mockClear();
    fetch.mockResolvedValue(new Response(JSON.stringify({ items: [], nextCursor: "page4" })));
    const retry = await loadComposerChoices("instance", "reference", "", page.failed);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(String(fetch.mock.calls[0][0])).toContain("category=artifact&search=&cursor=page2");
    expect(retry.next).toEqual({ artifact: "page4" });
  });
  it("pauses a source whose cursor does not advance", async () => {
    fetch.mockResolvedValue(new Response(JSON.stringify({ items: [], nextCursor: "same" })));
    const page = await loadComposerChoices("instance", "invocation", "", { invocation: "same" });
    expect(page.next).toEqual({});
    expect(page.failed).toEqual({ invocation: "same" });
  });
  it("loads slash Skills without querying reference sources", async () => {
    fetch.mockResolvedValue(new Response(JSON.stringify({ items: [], nextCursor: null })));
    await loadComposerChoices("instance", "invocation", "review");
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(String(fetch.mock.calls[0][0])).toContain("category=invocation");
  });
});
