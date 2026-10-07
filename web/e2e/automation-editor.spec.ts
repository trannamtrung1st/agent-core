import { expect, test } from "@playwright/test";

test("automation drawer retains failed drafts and supports responsive create, edit and dismissal", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/admin/instances");
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "General Assistant · general-assistant" }).click();
  await page.getByRole("button", { name: "Create instance", exact: true }).click();
  await expect(page).toHaveURL(/admin\/instances\/.+/);
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  const newButton = page.getByRole("button", { name: "New automation", exact: true });
  await newButton.click();
  const drawer = page.getByRole("dialog", { name: "New automation", exact: true });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByRole("button", { name: "Create automation", exact: true })).toBeDisabled();
  const name = `Drawer review ${Date.now()}`;
  await drawer.getByRole("textbox", { name: "Automation name" }).fill(name);
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Review pending orders.");
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await expect.poll(async () => Math.round((await drawer.boundingBox())!.width)).toBe(width < 768 ? width : 640);
    const body = drawer.locator(".ant-drawer-body");
    expect(await body.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    const submit = drawer.getByRole("button", { name: "Create automation", exact: true });
    expect((await submit.boundingBox())!.y).toBeLessThan(844);
    if (width === 390) expect((await submit.boundingBox())!.height).toBeGreaterThanOrEqual(40);
  }
  await page.route("**/automations", async route => {
    if (route.request().method() === "POST") await route.fulfill({ status: 409, contentType: "application/problem+json",
      body: JSON.stringify({ detail: "Automation revision is stale. Reload and try again." }) });
    else await route.continue();
  });
  await drawer.getByRole("button", { name: /Create automation/ }).click();
  await expect(drawer.getByRole("alert")).toContainText("Automation revision is stale.");
  await expect(drawer.getByRole("textbox", { name: "Automation instructions" })).toHaveValue("Review pending orders.");
  await page.unroute("**/automations");
  let posted = false;
  let releaseRefresh!: () => void;
  const refreshGate = new Promise<void>(resolve => { releaseRefresh = resolve; });
  let refreshStarted!: () => void;
  const refreshObserved = new Promise<void>(resolve => { refreshStarted = resolve; });
  await page.route("**/automations", async route => {
    if (route.request().method() === "POST") { posted = true; await route.continue(); }
    else if (posted) { refreshStarted(); await refreshGate; await route.continue(); }
    else await route.continue();
  });
  await drawer.getByRole("button", { name: /Create automation/ }).click();
  await refreshObserved;
  await expect(drawer).toBeVisible();
  await expect(drawer.getByRole("button", { name: /Create automation/ })).toBeDisabled();
  releaseRefresh();
  await expect(drawer).toBeHidden();
  await page.unroute("**/automations");
  const source = page.getByRole("button", { name: `View automation: ${name}`, exact: true });
  await expect(source).toBeFocused();
  await page.getByRole("textbox", { name: "Search automations" }).fill("no matching automation");
  await newButton.click();
  await drawer.getByRole("textbox", { name: "Automation name" }).fill(`${name} filtered`);
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Review filtered orders.");
  let releaseFilteredRefresh: () => void = () => {};
  const filteredRefreshGate = new Promise<void>(resolve => { releaseFilteredRefresh = resolve; });
  let saved = false;
  await page.route("**/automations", async route => {
    if (route.request().method() === "POST") {
      await route.continue();
      saved = true;
    } else if (saved) { await filteredRefreshGate; await route.continue(); }
    else await route.continue();
  });
  await drawer.getByRole("button", { name: "Create automation", exact: true }).click();
  await expect(drawer.getByRole("button", { name: "Create automation", exact: true })).toBeDisabled();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeVisible();
  releaseFilteredRefresh();
  await expect(drawer).toBeHidden();
  await page.unroute("**/automations");
  await expect(page.getByRole("textbox", { name: "Search automations" })).toHaveValue("");
  await expect(page.getByRole("button", { name: `View automation: ${name} filtered`, exact: true })).toBeFocused();
  await source.click();
  const edit = page.getByRole("button", { name: "Edit automation", exact: true });
  await edit.click();
  const editor = page.getByRole("dialog", { name: "Edit automation", exact: true });
  await editor.getByRole("textbox", { name: "Automation instructions" }).fill("Updated review instructions.");
  await editor.getByRole("button", { name: "Save automation", exact: true }).click();
  await expect(editor).toBeHidden();
  await expect(source).toBeFocused();
  await edit.click();
  await expect(editor.getByRole("textbox", { name: "Automation instructions" })).toHaveValue("Updated review instructions.");
  await page.keyboard.press("Escape");
  await expect(editor).toBeHidden();
  await expect(edit).toBeFocused();
  await newButton.click();
  await drawer.getByRole("button", { name: "Cancel automation edit", exact: true }).click();
  await expect(drawer).toBeHidden();
  await expect(newButton).toBeFocused();
  await page.setViewportSize({ width: 1440, height: 844 });
  await newButton.click();
  await drawer.getByRole("combobox", { name: "Automation trigger" }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: /^Event$/ }).click();
  await expect(drawer.getByRole("combobox", { name: "Automation event source" })).toBeVisible();
  await drawer.getByRole("textbox", { name: "Automation name" }).fill("Event source boundary");
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Review incoming orders.");
  await expect(drawer.getByRole("button", { name: "Create automation", exact: true })).toBeDisabled();
  await drawer.getByRole("button", { name: "Cancel automation edit", exact: true }).click();
  await expect(drawer).toBeHidden();
  expect(errors).toEqual([]);
});
