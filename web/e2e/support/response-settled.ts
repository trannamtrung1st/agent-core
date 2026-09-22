import { expect, type Page } from "@playwright/test";

/**
 * Waits until the live assistant response has terminalized in the UI.
 * Visible streamed text alone is not sufficient: the composer may still show Stop
 * while persistence/finalization is in flight.
 */
export async function waitForResponseSettled(page: Page): Promise<void> {
  await expect(page.getByRole("button", { name: "Stop" })).toHaveCount(0, {
    timeout: 30_000
  });
  await expect(page.getByText("Finalizing response…")).toHaveCount(0, {
    timeout: 30_000
  });
  await expect(page.locator(".agent-activity")).toHaveCount(0, {
    timeout: 30_000
  });
  await expect(page.getByTestId("connection")).toHaveText("Ready", {
    timeout: 30_000
  });
}
