import { tenantComposerChoices } from "../../services/tenantChat";
import type { ComposerChoice } from "../../services/messageParts";

export const resourceCategories = ["homeFile", "session", "backgroundSession", "artifact", "agentRun", "skill"] as const;
export type ChoiceCursors = Record<string, string | null>;

// Keep each source's bounded cursor independent when presenting one resource list.
export async function loadComposerChoices(instance: string, mode: "invocation" | "reference", search: string, cursors?: ChoiceCursors) {
  const categories = cursors ? Object.keys(cursors) : mode === "invocation" ? ["invocation"] : resourceCategories;
  const results = await Promise.allSettled(categories.map(async (category) => {
    const cursor = cursors?.[category];
    const params = new URLSearchParams({ category, search, ...(cursor ? { cursor } : {}) });
    const page = await tenantComposerChoices(instance, params);
    if (cursor && page.nextCursor === cursor) throw new Error(category);
    return page;
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
