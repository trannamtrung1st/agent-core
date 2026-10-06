import { expect, test } from "@playwright/test";

async function createInstance(page: import("@playwright/test").Page) {
  await page.goto("/admin/instances");
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "General Assistant · general-assistant" }).click();
  await page.getByRole("button", { name: "Create instance", exact: true }).click();
  await expect(page.getByLabel("Persona name", { exact: true })).toBeVisible();
  return page.url();
}

test("Admin read recovery and one-time clipboard guidance stay on the affected surface", async ({ page }) => {
  await page.goto("/admin/event-sources");
  await expect(page.getByLabel("Event source name", { exact: true })).toBeVisible();
  expect(await page.getByRole("button", { name: "New definition", exact: true }).count()).toBe(0);
  expect(await page.getByRole("button", { name: "New instance", exact: true }).count()).toBe(0);
  let fail = true;
  await page.route("**/api/v2/admin/event-sources", route => fail
    ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "Sources unavailable", status: 503 }) })
    : route.continue());
  await page.reload();
  await expect(page.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
  await expect(page.getByText("No event sources yet.", { exact: true })).toHaveCount(0);
  fail = false;
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByLabel("Event source name", { exact: true })).toBeVisible();
  await page.unrouteAll({ behavior: "wait" });
  await page.getByLabel("Event source name", { exact: true }).fill(`Polish fixture ${Date.now()}`);
  await page.getByRole("button", { name: "Create event source", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Copy this credential", exact: true });
  await expect(dialog).toBeVisible();
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, "writeText", {
    configurable: true, value: () => Promise.reject(new Error("Clipboard unavailable"))
  }));
  await dialog.getByRole("button", { name: "Copy event source credential", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("Select the value and copy it manually");
  await expect(dialog.getByLabel("Event source credential", { exact: true })).not.toHaveValue("");
  await dialog.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByLabel("Event source credential", { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Copy failed/)).toHaveCount(0);
});

test("Hidden automation keeps drafts, stops polling and saves a local picker time as UTC", async ({ page }) => {
  test.setTimeout(60_000);
  await createInstance(page);
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("button", { name: "New schedule", exact: true }).click();
  const task = `Date picker fixture ${Date.now()}`;
  await page.getByLabel("Schedule task", { exact: true }).fill(task);
  await page.getByRole("tab", { name: "Thoughts", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Thoughts", exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Identity & version", exact: true }).click();
  const requests: string[] = [];
  const record = (request: import("@playwright/test").Request) => {
    if (/\/(schedules|thoughts)(?:\?|$)/.test(request.url())) requests.push(request.url());
  };
  page.on("request", record);
  await page.waitForTimeout(5500);
  page.off("request", record);
  expect(requests).toEqual([]);
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("tab", { name: "Schedules", exact: true }).click();
  await expect(page.getByLabel("Schedule task", { exact: true })).toHaveValue(task);
  await page.getByLabel("Schedule timing", { exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: /^Once$/ }).click();
  const instant = new Date(Date.now() + 86400000); instant.setSeconds(0, 0);
  const local = [instant.getFullYear(), String(instant.getMonth() + 1).padStart(2, "0"), String(instant.getDate()).padStart(2, "0")].join("-")
    + ` ${String(instant.getHours()).padStart(2, "0")}:${String(instant.getMinutes()).padStart(2, "0")}`;
  let submitted = 0;
  const countPosts = (request: import("@playwright/test").Request) => { if (request.url().endsWith("/schedules") && request.method() === "POST") submitted++; };
  page.on("request", countPosts);
  await page.getByLabel("Schedule run at", { exact: true }).fill(local);
  await page.getByLabel("Schedule run at", { exact: true }).press("Enter");
  await expect(page.getByRole("status")).toContainText("Runs on");
  expect(submitted).toBe(0);
  const outgoing = page.waitForRequest(request => request.url().endsWith("/schedules") && request.method() === "POST");
  await page.getByRole("button", { name: "Create schedule", exact: true }).click();
  expect((await outgoing).postDataJSON().schedule.atUtc).toBe(instant.toISOString());
  await expect(page.getByRole("button", { name: `View schedule: ${task}`, exact: true })).toBeVisible();
  page.off("request", countPosts);
});

test("Draft actions remain visible at desktop and mobile scroll positions", async ({ page }) => {
  await page.goto("/admin/definitions/examiner");
  await page.getByRole("button", { name: /^Fork v\d+ \((builtIn|durable)\)$/ }).click();
  await expect(page.getByLabel("System instructions", { exact: true })).toBeVisible();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await page.getByLabel("System instructions", { exact: true }).scrollIntoViewIfNeeded();
    const rect = await page.getByRole("button", { name: "Save draft", exact: true }).boundingBox();
    expect(rect!.y).toBeGreaterThanOrEqual(0);
    expect(rect!.y + rect!.height).toBeLessThan(844);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
  }
});
