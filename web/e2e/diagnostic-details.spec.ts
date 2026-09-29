import { expect, test } from "@playwright/test";

test.use({ permissions: ["clipboard-read", "clipboard-write"] });

test("failed assistant details copy and survive reload", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("synthetic-fail-turn");
  await page.getByRole("button", { name: "Send" }).click();
  const row = page.locator(".chat-message-assistant").last();
  await expect(row.getByText("Failed")).toBeVisible({ timeout: 15_000 });
  await row.getByRole("button", { name: "Error details" }).click();
  const details = page.getByTestId("diagnostic-details");
  await expect(details).toContainText("Diagnostic ID:");
  const diagnosticId = (await page.getByTestId("diagnostic-id").innerText()).replace("Diagnostic ID: ", "").trim();
  expect(diagnosticId).toMatch(/^[0-9a-f-]{36}$/i);
  await page.getByRole("button", { name: "Copy diagnostic" }).click();
  await expect(page.getByRole("status")).toHaveText("Copied");
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied).toContain(`Diagnostic ID: ${diagnosticId}`);
  expect(copied).not.toContain("synthetic-fail-turn");

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  const reloaded = page.locator(".chat-message-assistant").last();
  await expect(reloaded.getByText("Failed")).toBeVisible();
  await reloaded.getByRole("button", { name: "Error details" }).click();
  await expect(page.getByTestId("diagnostic-id")).toHaveText(`Diagnostic ID: ${diagnosticId}`);
});

test("background work shows a seeded terminal diagnostic", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant")).toBeVisible({ timeout: 15_000 });
  await page.route("**/work-items**", async (route) => {
    await route.fulfill({
      json: {
        items: [
          {
            workItemId: "019944af-00c5-7000-8000-0000000000aa",
            status: "failed",
            revision: 4,
            origin: "Scheduled reminder",
            progress: null,
            needsApproval: false,
            approvalId: null,
            approvalRevision: null,
            approvalPreview: null,
            actionHash: null,
            cancellationAvailable: false,
            failureCode: "model-timeout",
            failureSummary: "The model timed out.",
            knownEffect: null,
            diagnosticId: "019944af-0008-7000-8000-0000000000d9",
            createdAt: "2026-09-24T09:00:00.000Z",
            updatedAt: "2026-09-24T09:01:00.000Z"
          }
        ]
      }
    });
  });
  await page.getByRole("button", { name: "Background work" }).click();
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByText("The model timed out.")).toBeVisible();
  await drawer.getByRole("button", { name: "Error details" }).click();
  const details = page.getByTestId("diagnostic-details");
  await expect(details).toContainText("Diagnostic ID: 019944af-0008-7000-8000-0000000000d9");
  await expect(details).toContainText("Work Item ID: 019944af-00c5-7000-8000-0000000000aa");
  await expect(details).toContainText("Error: model-timeout");
  await expect(details).not.toContainText("Trigger ID");
  await expect(details).not.toContainText("Occurrence ID");
});
