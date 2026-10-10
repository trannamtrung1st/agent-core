import { openDefinitionSettings } from "./admin-draft-editor-helpers";
import { expect, test } from "@playwright/test";

async function createInstance(page: import("@playwright/test").Page) {
  await page.goto("/admin/instances");
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "General Assistant · general-assistant" }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).press("Escape");
  await expect(page.getByRole("combobox", { name: "Definition", exact: true })).toHaveAttribute("aria-expanded", "false");
  await page.getByRole("button", { name: "Create instance", exact: true }).click();
  await expect(page.getByLabel("Persona name", { exact: true })).toBeVisible();
  return page.url();
}

test("Admin read recovery and one-time clipboard guidance stay on the affected surface", async ({ page }) => {
  await page.goto("/admin/connections/events");
  await expect(page.getByRole("button", { name: "New webhook Event", exact: true })).toBeVisible();
  expect(await page.getByRole("button", { name: "New definition", exact: true }).count()).toBe(0);
  expect(await page.getByRole("button", { name: "New instance", exact: true }).count()).toBe(0);
  let fail = true;
  await page.route("**/api/v2/admin/connections/events/catalog?kind=webhook", route => fail
    ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "Sources unavailable", status: 503 }) })
    : route.continue());
  await page.reload();
  await expect(page.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
  await expect(page.getByText("No event sources yet.", { exact: true })).toHaveCount(0);
  fail = false;
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByRole("button", { name: "New webhook Event", exact: true })).toBeVisible();
  await page.unrouteAll({ behavior: "wait" });
  await page.getByRole("button", { name: "New webhook Event", exact: true }).click();
  await page.getByLabel("Event name", { exact: true }).fill(`Polish fixture ${Date.now()}`);
  await page.getByLabel("Event key", { exact: true }).fill(`polish.${Date.now()}`);
  await page.getByRole("button", { name: "Create Event", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Copy this credential", exact: true });
  await expect(dialog).toBeVisible();
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, "writeText", {
    configurable: true, value: () => Promise.reject(new Error("Clipboard unavailable"))
  }));
  await dialog.getByRole("button", { name: "Copy credential", exact: true }).click();
  await expect(dialog.getByRole("alert")).toContainText("Select the value and copy it manually");
  await expect(dialog.getByLabel("Event credential", { exact: true })).not.toHaveValue("");
  await dialog.getByRole("button", { name: "Done", exact: true }).click();
  await expect(page.getByLabel("Event credential", { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Copy failed/)).toHaveCount(0);
});

test("Event drawer retains failed drafts, locks saving and reveals created sources through filters", async ({ page }) => {
  await page.goto("/admin/connections/events");
  const opener = page.getByRole("button", { name: "New webhook Event", exact: true });
  await expect(opener).toBeVisible();
  await page.getByRole("columnheader", { name: /State/ }).getByRole("button").click();
  await page.getByRole("menuitem").filter({ hasText: "Revoked" }).getByRole("checkbox").check();
  await page.getByRole("button", { name: "OK", exact: true }).click();
  await opener.click();
  const drawer = page.getByRole("dialog", { name: "New webhook Event", exact: true });
  const input = drawer.getByLabel("Event name", { exact: true });
  await input.fill("x".repeat(81));
  await expect(input).toHaveValue("x".repeat(80));
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();
  await expect(opener).toBeFocused();
  await opener.click();
  await expect(input).toHaveValue("");
  const sourceName = `Drawer recovery ${Date.now()}`;
  await input.fill(sourceName);
  await drawer.getByLabel("Event key", { exact: true }).fill(`recovery.${Date.now()}`);
  let fail = true;
  let secondPost!: () => void;
  const secondObserved = new Promise<void>(resolve => { secondPost = resolve; });
  let release!: () => void;
  const savingGate = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/v2/admin/connections/events", async route => {
    if (route.request().method() !== "POST") return route.continue();
    if (fail) return route.fulfill({ status: 503, contentType: "application/problem+json",
      body: JSON.stringify({ title: "Temporary creation failure", status: 503, diagnosticId: "drawer-recovery" }) });
    secondPost();
    await savingGate;
    await route.continue();
  });
  const create = drawer.getByRole("button", { name: "Create Event", exact: true });
  await create.click();
  await expect(drawer.getByRole("alert")).toContainText("Temporary creation failure");
  await expect(drawer.getByRole("button", { name: "Error details", exact: true })).toBeVisible();
  await expect(input).toHaveValue(sourceName);
  fail = false;
  try {
    await create.click();
    await secondObserved;
    await expect(create).toHaveAttribute("aria-busy", "true");
    await expect(drawer.getByRole("button", { name: "Cancel", exact: true })).toBeDisabled();
    await page.keyboard.press("Escape");
    await expect(drawer).toBeVisible();
  } finally { release(); }
  const credential = page.getByRole("dialog", { name: "Copy this credential", exact: true });
  await expect(credential).toBeVisible();
  await credential.getByRole("button", { name: "Done", exact: true }).click();
  await expect(credential).toBeHidden();
  await expect(page.getByText(sourceName, { exact: true })).toBeVisible();
  const rotate = page.getByRole("button", { name: `More actions for ${sourceName}`, exact: true });
  await rotate.click();
  await page.getByRole("menuitem", { name: "Rotate credential", exact: true }).click();
  await page.getByRole("dialog", { name: "Rotate this credential?", exact: true })
    .getByRole("button", { name: "Rotate credential", exact: true }).click();
  await expect(credential).toBeVisible();
  await credential.getByRole("button", { name: "Done", exact: true }).click();
  await expect(credential).toBeHidden();
  await expect(rotate).toBeFocused();
});

test("Hidden automation keeps drafts, stops polling and saves a local picker time as UTC", async ({ page }) => {
  test.setTimeout(60_000);
  await createInstance(page);
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("button", { name: "New automation", exact: true }).click();
  const task = `Date picker fixture ${Date.now()}`;
  await page.getByLabel("Automation name", { exact: true }).fill(task);
  await page.getByLabel("Automation instructions", { exact: true }).fill(task);
  // Browser navigation can leave the tab while its modal drawer is open.
  // Clicking a tab behind the drawer mask would never exercise this transition.
  await page.goBack();
  await expect(page.getByRole("tab", { name: "Identity & version", exact: true })).toHaveAttribute("aria-selected", "true");
  const requests: string[] = [];
  const record = (request: import("@playwright/test").Request) => {
    if (/\/automations(?:\?|$)/.test(request.url())) requests.push(request.url());
  };
  page.on("request", record);
  await page.waitForTimeout(5500);
  page.off("request", record);
  expect(requests).toEqual([]);
  await page.goForward();
  await expect(page.getByRole("tab", { name: "Triggers", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByLabel("Automation instructions", { exact: true })).toHaveValue(task);
  await page.getByLabel("Schedule timing", { exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: /^Once$/ }).click();
  const instant = new Date(Date.now() + 86400000); instant.setSeconds(0, 0);
  const local = [instant.getFullYear(), String(instant.getMonth() + 1).padStart(2, "0"), String(instant.getDate()).padStart(2, "0")].join("-")
    + ` ${String(instant.getHours()).padStart(2, "0")}:${String(instant.getMinutes()).padStart(2, "0")}`;
  let submitted = 0;
  const countPosts = (request: import("@playwright/test").Request) => { if (request.url().endsWith("/automations") && request.method() === "POST") submitted++; };
  page.on("request", countPosts);
  await page.getByLabel("Schedule run at", { exact: true }).fill(local);
  await page.getByLabel("Schedule run at", { exact: true }).press("Enter");
  await expect(page.getByRole("status")).toContainText("Runs on");
  expect(submitted).toBe(0);
  const outgoing = page.waitForRequest(request => request.url().endsWith("/automations") && request.method() === "POST");
  await page.getByRole("button", { name: "Create automation", exact: true }).click();
  expect((await outgoing).postDataJSON().triggers[0].schedule.atUtc).toBe(instant.toISOString());
  await expect(page.getByRole("button", { name: `View automation: ${task}`, exact: true })).toBeVisible();
  page.off("request", countPosts);
});

test("Draft actions remain visible at desktop and mobile scroll positions", async ({ page }) => {
  await page.goto("/admin/definitions/examiner");
  await page.getByRole("button", { name: /^Fork v\d+ \((builtIn|durable)\)$/ }).click();
  await openDefinitionSettings(page.locator('section[aria-label="Draft editor"]'));
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
