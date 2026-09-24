import { expect, test } from "@playwright/test";

const resultText = "Oven timer finished.";

test("background work stays out of the transcript at wide and narrow widths", async ({ page }) => {
  test.setTimeout(60_000);
  const consoleErrors: string[] = [];
  const serverErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("response", (response) => {
    if (response.status() >= 500) {
      serverErrors.push(`${response.status()} ${response.url()}`);
    }
  });

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await page.getByRole("combobox", { name: "Identity" }).click();
  await page.locator(".ant-select-item-option", { hasText: "Riley — General assistant" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Background work" })).toBeVisible({ timeout: 15_000 });

  const opener = page.getByRole("button", { name: "Background work" });
  await opener.focus();
  await expect(opener).toBeFocused();
  await page.keyboard.press("Enter");
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByText("No background work yet")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();

  let approved = false;
  await page.route("**/work-items**", async (route) => {
    const url = route.request().url();
    if (url.includes("/result")) {
      await route.fulfill({
        json: {
          workItemId: "019944af-00c5-7000-8000-000000000010",
          text: resultText,
          completedAt: "2026-09-24T09:01:00.000Z"
        }
      });
      return;
    }

    if (url.includes("/approve") && route.request().method() === "POST") {
      approved = true;
      await route.fulfill({
        json: {
          workItemId: "019944af-00c5-7000-8000-000000000011",
          status: "queued",
          revision: 5,
          origin: "Application event",
          progress: null,
          needsApproval: false,
          approvalId: null,
          approvalRevision: null,
          approvalPreview: null,
          actionHash: null,
          cancellationAvailable: true,
          failureCode: null,
          failureSummary: null,
          knownEffect: null,
          createdAt: "2026-09-24T09:00:00.000Z",
          updatedAt: "2026-09-24T09:02:00.000Z"
        }
      });
      return;
    }

    const completed = {
      workItemId: "019944af-00c5-7000-8000-000000000010",
      status: "completed",
      revision: 4,
      origin: "Scheduled reminder",
      progress: "Checking the oven",
      needsApproval: false,
      approvalId: null,
      approvalRevision: null,
      approvalPreview: null,
      actionHash: null,
      cancellationAvailable: false,
      failureCode: null,
      failureSummary: null,
      knownEffect: null,
      createdAt: "2026-09-24T09:00:00.000Z",
      updatedAt: "2026-09-24T09:01:00.000Z"
    };
    const waiting = {
      workItemId: "019944af-00c5-7000-8000-000000000011",
      status: approved ? "queued" : "needsApproval",
      revision: approved ? 5 : 4,
      origin: "Application event",
      progress: null,
      needsApproval: !approved,
      approvalId: approved ? null : "019944af-00c5-7000-8000-000000000012",
      approvalRevision: approved ? null : 1,
      approvalPreview: approved ? null : "POST https://example.com/items",
      actionHash: approved ? null : "a".repeat(64),
      cancellationAvailable: true,
      failureCode: null,
      failureSummary: null,
      knownEffect: null,
      createdAt: "2026-09-24T08:59:00.000Z",
      updatedAt: "2026-09-24T09:00:00.000Z"
    };
    await route.fulfill({ json: { items: [completed, waiting] } });
  });

  await opener.click();
  await expect(drawer.getByText(resultText)).toBeVisible();
  await expect(drawer.getByText("Needs approval")).toBeVisible();
  await expect(drawer.getByText("POST https://example.com/items")).toBeVisible();
  await expect(page.getByRole("region", { name: "Conversation" })).not.toContainText(resultText);
  await expect(drawer).not.toContainText("SECRET_BODY");
  await expect(drawer).not.toContainText("checkpoint");

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText(resultText)).toBeVisible();
  await expect(page.getByRole("region", { name: "Conversation" })).not.toContainText(resultText);

  await page.setViewportSize({ width: 390, height: 800 });
  await expect(page.getByRole("navigation", { name: "Chats" })).toBeHidden();
  await expect(drawer.getByText(resultText)).toBeVisible();
  const approve = drawer.getByRole("button", { name: "Approve Application event" });
  await approve.focus();
  await expect(approve).toBeFocused();
  await approve.click();
  await page.getByRole("button", { name: "Approve action" }).click();
  await expect(drawer.getByText("Queued").first()).toBeVisible();

  const unexpectedConsole = consoleErrors.filter(
    (message) => !message.includes("[antd: List]")
  );
  expect(unexpectedConsole).toEqual([]);
  expect(serverErrors).toEqual([]);
});
