import { expect, test } from "@playwright/test";

test("automation drawer retains failed drafts and supports responsive create, edit and dismissal", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/admin/instances");
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  await page.getByRole("combobox", { name: "Definition", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "Secretary · secretary" }).click();
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
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Retain the closing presentation.");
  const closingPresentation = await drawer.evaluate(async element => {
    element.querySelector<HTMLButtonElement>('button[aria-label="Cancel automation edit"]')!.click();
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    return {
      title: element.querySelector(".ant-drawer-title")?.textContent,
      instructions: element.querySelector("textarea")?.value,
      action: element.querySelector('button[type="submit"]')?.textContent,
      actionDisabled: element.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled
    };
  });
  expect(closingPresentation).toEqual({ title: "New automation", instructions: "Retain the closing presentation.", action: "Save", actionDisabled: true });
  await expect(drawer).toBeHidden();
  await expect(newButton).toBeFocused();
  await page.setViewportSize({ width: 1440, height: 844 });
  await newButton.click();
  await drawer.getByRole("combobox", { name: "Automation trigger" }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: /^Events$/ }).click();
  await expect(drawer.getByRole("button", { name: "Add Event", exact: true })).toBeVisible();
  await drawer.getByRole("textbox", { name: "Automation name" }).fill("Event selection boundary");
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Review incoming orders.");
  await expect(drawer.getByRole("button", { name: "Create automation", exact: true })).toBeDisabled();
  await drawer.getByRole("button", { name: "Cancel automation edit", exact: true }).click();
  await expect(drawer).toBeHidden();
  await newButton.click();
  await drawer.getByRole("textbox", { name: "Automation name" }).fill("Mobile one-time schedule");
  await drawer.getByRole("textbox", { name: "Automation instructions" }).fill("Review pending orders once.");
  await drawer.getByRole("combobox", { name: "Schedule timing" }).click();
  await page.locator(".ant-select-dropdown:visible .ant-select-item-option").filter({ hasText: /^Once$/ }).click();
  const future = new Date(Date.now() + 2 * 86400000);
  const futureDate = `${future.getFullYear()}-${String(future.getMonth() + 1).padStart(2, "0")}-${String(future.getDate()).padStart(2, "0")}`;
  const runAt = drawer.getByRole("textbox", { name: "Schedule run at", exact: true });
  await runAt.fill(`${futureDate} 09:00`);
  await runAt.press("Enter");
  await runAt.press("Tab");
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 640 });
    await runAt.click();
    const popup = page.locator(".automation-date-picker-popup .ant-picker-panel-container");
    await expect(popup).toBeVisible();
    await expect.poll(async () => {
      const bounds = (await popup.boundingBox())!;
      return bounds.x >= 0 && bounds.x + bounds.width <= width && bounds.y >= 0 && bounds.y + bounds.height <= 640;
    }).toBe(true);
    await popup.locator(`td[title="${futureDate}"]`).click();
    await popup.locator(".ant-picker-time-panel-column").first().getByText("09", { exact: true }).click();
    await popup.locator(".ant-picker-time-panel-column").nth(1).getByText("00", { exact: true }).click();
    await popup.getByRole("button", { name: "OK", exact: true }).click();
    await expect(runAt).toHaveValue(`${futureDate} 09:00`);
    await expect(drawer.getByRole("button", { name: "Create automation", exact: true })).toBeEnabled();
  }
  await drawer.getByRole("button", { name: "Cancel automation edit", exact: true }).click();
  await expect(drawer).toBeHidden();
  expect(errors).toEqual([]);
});
