import type { Page } from "@playwright/test";

/** Matches the queue `<section aria-label="Queued messages">`, not "Expand queued messages". */
export function queuedMessages(page: Page) {
  return page.getByRole("region", { name: "Queued messages", exact: true });
}
