import { ownerFetch } from "../../services/api";
import type { ComposerChoice } from "../../services/messageParts";

export const resourceCategories = ["homeFile", "session", "backgroundSession", "artifact", "agentRun", "skill"] as const;
export type ChoiceCursors = Record<string, string | null>;

// Keep each source's bounded cursor independent when presenting one resource list.
export async function loadComposerChoices(instance: string, mode: "invocation" | "reference", search: string, cursors?: ChoiceCursors) {
  const categories = cursors ? Object.keys(cursors) : mode === "invocation" ? ["invocation"] : resourceCategories;
  const results = await Promise.allSettled(categories.map(async (category) => {
    const cursor = cursors?.[category];
    const params = new URLSearchParams({ category, search, ...(cursor ? { cursor } : {}) });
    const response = await ownerFetch(`/api/v2/agent-instances/${instance}/composer?${params}`);
    if (!response.ok) throw new Error(category);
    return await response.json() as { items: ComposerChoice[]; nextCursor: string | null };
  }));
  const items: ComposerChoice[] = [];
  const next: ChoiceCursors = {};
  const failed: ChoiceCursors = {};
  results.forEach((result, i) => {
    const category = categories[i];
    if (result.status === "fulfilled") {
      items.push(...result.value.items);
      if (result.value.nextCursor) next[category] = result.value.nextCursor;
    } else failed[category] = cursors?.[category] ?? null;
  });
  return { items, next, failed };
}
