import { expect, test } from "@playwright/test";
import type { WorkItem } from "../src/services/api";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

test("long run content scrolls independently, retries and stays usable across drawer widths", async ({ page }) => {
  const errors: string[] = [];
  const failed: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("requestfailed", request => {
    if (!request.failure()?.errorText.includes("ERR_ABORTED")) failed.push(request.url());
  });
  const item: WorkItem = {
    workItemId: "90000000-0000-4000-8000-000000000001", revision: 3,
    status: "completed", origin: "Automation · Schedule", automationName: "Sample hello in 1 minute",
    triggerSummary: "Once · 2026-10-07T17:40:15.2280000+00:00", modelKey: "scripted-alpha",
    instructions: "Send a short greeting.\n" + "Keep the greeting personal and concise. Read this instruction before sending.\n".repeat(40),
    progress: null, needsApproval: false, approvalId: null, approvalRevision: null,
    approvalPreview: null, actionHash: null, cancellationAvailable: false,
    failureCode: null, failureSummary: null, knownEffect: null,
    createdAt: "2026-10-07T17:40:00Z", updatedAt: "2026-10-07T17:40:15Z", attentionRequired: true
  };
  let failResult = true;
  let longResult = true;
  await page.route("**/work-items**", async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/result")) {
      if (failResult) return route.fulfill({ status: 503, json: { detail: "Temporary result read failure" } });
      return route.fulfill({ json: { workItemId: item.workItemId,
        text: longResult ? "Sample hello!\n" + "This is the owner-facing result of the scheduled greeting.\n".repeat(70)
          : "Sample hello! This is the owner-facing message for this one-time reminder.", completedAt: item.updatedAt } });
    }
    return route.fulfill({ json: { items: [item] } });
  });
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("hello");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await page.getByRole("button", { name: "Background work, 1 need attention", exact: true }).click();
  const drawer = page.getByRole("dialog", { name: "Background work", exact: true });
  await expect(drawer.getByText("Run result could not be loaded")).toBeVisible();
  failResult = false;
  await drawer.getByRole("button", { name: "Retry result", exact: true }).click();
  const instructions = drawer.getByRole("region", { name: "Instructions for Sample hello in 1 minute", exact: true });
  const result = drawer.getByRole("region", { name: "Result for Sample hello in 1 minute", exact: true });
  await expect(result).toContainText("Sample hello!");
  await expect(drawer.getByText(/Once ·/)).not.toContainText("T17:40");
  for (const [width, height] of [[1440, 900], [768, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(async () => (await drawer.boundingBox())!.width)
      .toBe(width >= 768 ? 640 : width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const region of [instructions, result]) {
      await region.focus();
      const before = await drawer.locator(".ant-drawer-body").evaluate(e => e.scrollTop);
      await page.keyboard.press("End");
      await expect.poll(() => region.evaluate(e => e.scrollTop)).toBeGreaterThan(0);
      expect(await region.evaluate(e => e.clientHeight)).toBeLessThanOrEqual(256);
      expect(await drawer.locator(".ant-drawer-body").evaluate(e => e.scrollTop)).toBe(before);
      await page.keyboard.press("Home");
      await region.evaluate(e => { e.scrollTop = 0; });
    }
    await drawer.locator(".ant-drawer-body").evaluate(e => { e.scrollTop = 0; });
    await page.mouse.move(0, 0);
    await page.screenshot({ path: `../docs/reports/assets/background-work-polish-${width}-long.png` });
  }
  item.instructions = "Send a short greeting: Sample hello! This is a one-time reminder-style message.";
  item.revision += 1;
  longResult = false;
  await expect(instructions).toHaveText(item.instructions, { timeout: 10000 });
  await expect(result).toHaveText("Sample hello! This is the owner-facing message for this one-time reminder.");
  for (const [width, height] of [[1440, 900], [768, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    const expectedWidth = width >= 768 ? 640 : width;
    await expect.poll(async () => {
      const box = (await drawer.boundingBox())!;
      return { width: Math.round(box.width), x: Math.round(box.x) };
    }).toEqual({ width: expectedWidth, x: width - expectedWidth });
    for (const region of [instructions, result]) {
      expect(await region.evaluate(e => e.scrollHeight <= e.clientHeight)).toBe(true);
    }
    await drawer.locator(".ant-drawer-body").evaluate(e => { e.scrollTop = 0; });
    await page.screenshot({ path: `../docs/reports/assets/background-work-polish-${width}.png` });
  }
  await drawer.getByRole("button", { name: "Mark as read", exact: true }).click();
  await expect(drawer.getByText("Needs attention", { exact: true })).toHaveCount(0);
  await expect(result).toContainText("Sample hello!");
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();
  expect(errors).toEqual([]);
  expect(failed).toEqual([]);
});
