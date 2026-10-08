import { expect, type Page } from "@playwright/test";

// The header includes response activity; Ready is an idle label, not an attachment signal.
export async function waitForSessionAttached(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => ({
    connected: window.__agentCore?.hubConnected?.(),
    state: window.__agentCore?.sessionConnection?.()
  })), { timeout: 15_000 }).toEqual({ connected: true, state: "ready" });
}
