import { navigateAdmin } from "./support/admin-navigation";
import { openDefinitionSettings } from "./admin-draft-editor-helpers";
import { expect, test } from "@playwright/test";

test("Admin chrome keeps creation, scope, search and navigation usable at narrow widths", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))! };
  const response = await page.request.post("/api/v2/admin/agent-instances", { headers, data: {
    definitionId: "examiner", version: 1,
    persona: { name: "Synthetic polish · A long configuration owner name for navigation", role: "UI review", description: "Disposable layout verification fixture", tone: "Clear" }
  } });
  expect(response.ok()).toBe(true);
  const { instanceId } = await response.json();
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/admin/instances");
  await expect(page.getByRole("heading", { name: "Instances", exact: true })).toHaveCount(1);
  const create = page.getByRole("button", { name: "New instance", exact: true });
  await create.click();
  await page.getByRole("dialog", { name: "New instance", exact: true }).getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(create).toBeFocused();
  await page.getByRole("textbox", { name: "Search instances", exact: true }).fill(instanceId);
  await page.locator(`a[href="/admin/instances/${instanceId}/overview"]`).click();
  await expect(page.getByRole("region", { name: "Instance overview", exact: true })).toBeVisible();
  let sidebarInset = 0;
  for (const width of [1440, 1024, 1023, 768, 767, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.locator(".admin-sidebar")).toHaveCount(width >= 1024 ? 1 : 0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    if (width >= 1024) {
      sidebarInset = await page.getByRole("navigation", { name: "Admin navigation", exact: true }).getByRole("link", { name: "Back to Instances", exact: true }).evaluate(element => element.getBoundingClientRect().left + parseFloat(getComputedStyle(element).paddingInlineStart));
    } else {
      const menu = page.getByRole("button", { name: "Open Admin navigation", exact: true });
      await menu.click();
      const drawer = page.getByRole("dialog", { name: "Admin navigation", exact: true });
      await expect(drawer).toBeVisible();
      await expect.poll(() => drawer.getByRole("link", { name: "Back to Instances", exact: true }).evaluate(element => element.getBoundingClientRect().left + parseFloat(getComputedStyle(element).paddingInlineStart))).toBe(sidebarInset);
      await page.keyboard.press("Escape");
      await expect(menu).toBeFocused();
    }
    if (width < 768) {
      expect((await page.locator(".admin-header").boundingBox())!.height).toBeLessThan(112);
      expect(await page.locator(".admin-title").evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      expect((await page.locator(".admin-title").boundingBox())!.height).toBeLessThan(32);
      for (const control of [page.getByRole("button", { name: "Search Admin", exact: true }), page.getByRole("link", { name: "Chat", exact: true }), page.getByRole("link", { name: "Return to last chat", exact: true })]) {
        const bounds = (await control.boundingBox())!;
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(bounds.height).toBeGreaterThanOrEqual(40);
      }
    }
  }
  await page.getByRole("button", { name: "Search Admin", exact: true }).click();
  const search = page.getByRole("dialog", { name: "Search Admin", exact: true });
  await expect(search.getByRole("region", { name: "Global Admin destinations", exact: true })).toBeVisible();
  await search.getByRole("textbox", { name: "Search Admin destinations" }).fill("reasoning");
  await expect(search.getByRole("region", { name: "Global Admin destinations", exact: true })).toBeHidden();
  await search.getByRole("link", { name: /Interactive model & reasoning/ }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/instances/${instanceId}/models#modelDefaults$`));
  await expect(page.locator("#modelDefaults")).toBeFocused();
  await page.goto("/admin/not-a-real-page");
  await expect(page.getByRole("heading", { name: "Page not found", exact: true, level: 2 })).toBeVisible();
  await expect(page.getByRole("button", { name: "New definition", exact: true })).toBeHidden();
  await expect(page.getByRole("button", { name: "New instance", exact: true })).toBeHidden();
});

async function createInstance(page: import("@playwright/test").Page) {
  await page.goto("/admin/instances");
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "General Assistant · general-assistant" }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).press("Escape");
  await expect(page.getByRole("combobox", { name: "Definition", exact: true })).toHaveAttribute("aria-expanded", "false");
  await page.getByRole("button", { name: "Create instance", exact: true }).click();
  await navigateAdmin(page, "Profile");
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
  await navigateAdmin(page, "Automation");
  await page.getByRole("button", { name: "New automation", exact: true }).click();
  const task = `Date picker fixture ${Date.now()}`;
  await page.getByLabel("Automation name", { exact: true }).fill(task);
  await page.getByLabel("Automation instructions", { exact: true }).fill(task);
  // Browser navigation can leave the tab while its modal drawer is open.
  // Clicking a tab behind the drawer mask would never exercise this transition.
  await page.goBack();
  await expect(page.getByRole("heading", { name: "Profile", exact: true, level: 2 })).toBeVisible();
  const requests: string[] = [];
  const record = (request: import("@playwright/test").Request) => {
    if (/\/automations(?:\?|$)/.test(request.url())) requests.push(request.url());
  };
  page.on("request", record);
  await page.waitForTimeout(5500);
  page.off("request", record);
  expect(requests).toEqual([]);
  await page.goForward();
  await expect(page.getByRole("heading", { name: "Automations", exact: true, level: 2 })).toBeVisible();
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
