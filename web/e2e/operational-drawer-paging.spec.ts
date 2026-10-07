import { expect, test } from "@playwright/test";
import type { WorkItem, SessionAutomation } from "../src/services/api";

// Controlled owner-scoped lists exercise paging independently of reminder wall clocks.
test("operational drawers scroll pages, retry, and persist background read status", async ({ page }) => {
  test.setTimeout(60_000);
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  const work: WorkItem[] = Array.from({ length: 45 }, (_, index) => ({
    workItemId: `10000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    status: "completed", revision: 3, origin: `Review ${index}`, progress: "Background review completed",
    needsApproval: false, approvalId: null, approvalRevision: null, approvalPreview: null, actionHash: null,
    cancellationAvailable: false, failureCode: null, failureSummary: null, knownEffect: null,
    createdAt: "2026-10-05T09:00:00Z", updatedAt: "2026-10-05T10:00:00Z", attentionRequired: [0, 25, 44].includes(index)
  }));
  const schedules: SessionAutomation[] = work.map((row, index) => ({
    automationId: row.workItemId, instructions: `Reminder ${index}`, status: "completed", triggerKind: "oneShot",
    timeZone: "Asia/Ho_Chi_Minh", when: "Once on October 5, 2026 at 17:00", nextOccurrenceAt: null, revision: 2
  }));
  let failNext = true;
  let listRefreshes = 0;
  let release: () => void = () => undefined;
  const pendingPage = new Promise<void>(resolve => { release = resolve; });
  const resultRequests: string[] = [];
  await page.route("**/work-items**", async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/result")) {
      const id = url.pathname.split("/").at(-2)!;
      resultRequests.push(id);
      await route.fulfill({ json: { workItemId: id, text: `Result for ${id}`, completedAt: "2026-10-05T10:00:00Z" } });
      return;
    }
    const attentionOnly = url.searchParams.has("attentionOnly");
    const before = url.searchParams.get("before");
    if (!before && !attentionOnly) listRefreshes++;
    if (before && !attentionOnly) {
      if (failNext) {
        failNext = false;
        await route.fulfill({ status: 503, json: { detail: "Unable to load older work. Try again." } });
        return;
      }
      await pendingPage;
    }
    const source = attentionOnly ? work.filter(item => item.attentionRequired) : work;
    const start = before ? source.findIndex(item => item.workItemId === before) + 1 : 0;
    await route.fulfill({ json: { items: source.slice(start, start + Number(url.searchParams.get("limit") ?? 50)) } });
  });
  await page.route("**/automations?**", async route => {
    const url = new URL(route.request().url());
    const before = url.searchParams.get("before");
    const start = before ? schedules.findIndex(item => item.automationId === before) + 1 : 0;
    await route.fulfill({ json: { items: schedules.slice(start, start + Number(url.searchParams.get("limit") ?? 50)) } });
  });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("hello");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const badge = page.getByRole("button", { name: "Background work, 3 need attention", exact: true });
  await badge.click();
  const drawer = page.getByRole("dialog", { name: "Background work", exact: true });
  await expect(drawer.locator(".background-work-item")).toHaveCount(20);
  await expect.poll(() => resultRequests.length).toBe(20);
  // Bulk read must also clear attention on pages that have not been opened.
  await drawer.getByRole("button", { name: "Mark all as read", exact: true }).click();
  await expect(page.getByRole("button", { name: "Background work", exact: true })).toBeVisible();
  for (const index of [0, 25, 44]) work[index] = { ...work[index], revision: 4 };
  await expect(badge).toBeVisible({ timeout: 10_000 });

  await drawer.locator(".ant-drawer-body").evaluate(body => { body.scrollTop = body.scrollHeight; });
  await expect(drawer.getByText("Unable to load older work. Try again.")).toBeVisible();
  await expect(drawer.locator(".background-work-item")).toHaveCount(20);
  const beforePoll = listRefreshes;
  await expect.poll(() => listRefreshes, { timeout: 10_000 }).toBeGreaterThan(beforePoll);
  await expect(drawer.getByText("Unable to load older work. Try again.")).toBeVisible();
  await drawer.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(drawer.getByText("Loading more…")).toBeVisible();
  release();
  await expect(drawer.locator(".background-work-item")).toHaveCount(40);
  await drawer.locator(".ant-drawer-body").evaluate(body => { body.scrollTop = body.scrollHeight; });
  await expect(drawer.locator(".background-work-item")).toHaveCount(45);
  await expect(drawer.getByText("You’re all caught up")).toBeVisible();
  await drawer.locator(".ant-drawer-body").evaluate(body => { body.scrollTop = 0; });
  await drawer.getByRole("button", { name: "Mark as read", exact: true }).first().click();
  await expect(page.getByRole("button", { name: "Background work, 2 need attention", exact: true })).toBeVisible();
  await drawer.getByRole("button", { name: "Mark all as read", exact: true }).click();
  await expect(page.getByRole("button", { name: "Background work", exact: true })).toBeVisible();
  await drawer.getByRole("button", { name: "Close", exact: true }).click();
  await page.reload();
  await expect(page.getByRole("button", { name: "Background work", exact: true })).toBeVisible();
  await page.setViewportSize({ width: 390, height: 800 });
  await page.getByRole("button", { name: "Background work", exact: true }).click();
  await expect(drawer.locator(".background-work-item")).toHaveCount(20);
  await expect(drawer.getByText("Needs attention", { exact: true })).toHaveCount(0);
  await drawer.getByRole("button", { name: "Close", exact: true }).click();
  work[0] = { ...work[0], revision: 4, updatedAt: "2026-10-05T10:01:00Z" };
  await expect(page.getByRole("button", { name: "Background work, 1 need attention", exact: true })).toBeVisible({ timeout: 10_000 });
  await page.getByRole("button", { name: "Automations", exact: true }).click();
  const scheduleDrawer = page.getByRole("dialog", { name: "Automations", exact: true });
  await expect(scheduleDrawer.locator(".schedule-item")).toHaveCount(20);
  await scheduleDrawer.locator(".ant-drawer-body").evaluate(body => { body.scrollTop = body.scrollHeight; });
  await expect(scheduleDrawer.locator(".schedule-item")).toHaveCount(40);
  await scheduleDrawer.locator(".ant-drawer-body").evaluate(body => { body.scrollTop = body.scrollHeight; });
  await expect(scheduleDrawer.locator(".schedule-item")).toHaveCount(45);
  await expect(scheduleDrawer.getByText("You’re all caught up")).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  expect(errors).toEqual([]);
});


test("Admin background pages survive resizing and refresh", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const instanceId = await page.evaluate(async () => {
    const response = await fetch("/api/v2/admin/agent-instances", {
      method: "POST",
      headers: { "content-type": "application/json", "X-AgentCore-Owner-Capability": localStorage.getItem("agent-core.owner-capability")! },
      body: JSON.stringify({ definitionId: "examiner", version: 1 })
    });
    if (!response.ok) throw new Error(await response.text());
    return (await response.json()).instanceId as string;
  });
  const work: WorkItem[] = Array.from({ length: 45 }, (_, index) => ({
    workItemId: `20000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
    status: "completed", revision: 3, origin: `Admin review ${index}`, progress: "Review completed",
    needsApproval: false, approvalId: null, approvalRevision: null, approvalPreview: null, actionHash: null,
    cancellationAvailable: false, failureCode: null, failureSummary: null, knownEffect: null,
    createdAt: "2026-10-05T09:00:00Z", updatedAt: "2026-10-05T10:00:00Z", attentionRequired: false
  }));
  let headRequests = 0;
  let resultRequests = 0;
  await page.route(`**/agent-instances/${instanceId}/work-items**`, async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/result")) {
      resultRequests++;
      await route.fulfill({ json: { workItemId: url.pathname.split("/").at(-2), text: "Admin result", completedAt: work[0].updatedAt } });
      return;
    }
    const before = url.searchParams.get("before");
    if (!before) headRequests++;
    const start = before ? work.findIndex(item => item.workItemId === before) + 1 : 0;
    await route.fulfill({ json: { items: work.slice(start, start + Number(url.searchParams.get("limit") ?? 50)) } });
  });
  await page.goto(`/admin/instances/${instanceId}`);
  await page.getByRole("tab", { name: "Runs", exact: true }).click();
  const drawer = page.getByRole("region", { name: "Runs", exact: true });
  await expect(drawer.locator("[data-run-id]")).toHaveCount(20);
  await drawer.getByRole("button", { name: "Load more", exact: true }).click();
  await expect(drawer.locator("[data-run-id]")).toHaveCount(40);
  await expect.poll(() => resultRequests).toBe(0);
  const beforeResize = headRequests;
  await page.setViewportSize({ width: 390, height: 800 });
  expect(headRequests).toBe(beforeResize);
  await expect(drawer.locator("[data-run-id]")).toHaveCount(40);
  expect(resultRequests).toBe(0);
  await drawer.getByRole("button", { name: "Load more", exact: true }).click();
  await expect(drawer.locator("[data-run-id]")).toHaveCount(45);
  await expect(drawer.getByText("You’re all caught up")).toBeVisible();
});
