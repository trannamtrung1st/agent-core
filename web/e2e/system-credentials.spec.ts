import { test, expect, type Page } from "@playwright/test";

async function owner(page: Page, path: string, method = "GET", body?: unknown) {
  const responseText = await page.evaluate(async ({path, method, body}) => {
    const response = await fetch(`/api/v2/admin/${path}`, { method, headers: { "X-AgentCore-Owner-Capability": localStorage.getItem("agent-core.owner-capability")!, "Content-Type": "application/json" }, ...(body === undefined ? {} : {body: JSON.stringify(body)}) });
    const text = await response.text(); if (!response.ok) throw new Error(`${response.status} ${text}`);
    return text;
  }, {path, method, body});
  // Transport JSON as text so Playwright's object bridge cannot alter dynamic keys.
  return responseText ? JSON.parse(responseText) : null;
}

test("shared system credentials have safe CRUD, explicit bindings, profile reset and mobile navigation", async ({page}) => {
  test.setTimeout(120_000);
  const errors: string[] = []; page.on("pageerror", e => errors.push(e.message));
  await page.goto("/admin/credentials");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const a = await owner(page, "agent-instances", "POST", { definitionId: "secretary", version: 3 });
  const b = await owner(page, "agent-instances", "POST", { definitionId: "secretary", version: 3 });
  const name = `Shared credentials ${Date.now()}`;
  await page.getByRole("button", {name:"Create credential",exact:true}).click();
  const create = page.getByRole("dialog", {name:"Create credential",exact:true});
  await expect(create.locator(".ant-select")).toContainText("Password");
  await create.getByLabel("Display name").fill(name);
  await create.getByRole("button", {name:"Add metadata"}).click();
  await create.getByLabel("Metadata key 1").fill("username");
  await create.getByLabel("Metadata value 1").fill("operator@example.test");
  await create.getByRole("button", {name:"Add metadata"}).click();
  await create.getByLabel("Metadata key 2").fill("__proto__");
  await create.getByLabel("Metadata value 2").fill("safe metadata value");
  const longMetadata = "Safe support context ".repeat(40);
  await create.getByRole("button", {name:"Add metadata"}).click();
  await create.getByLabel("Metadata key 3").fill("support-context");
  await create.getByLabel("Metadata value 3").fill(longMetadata);
  await create.getByLabel("Allowed origins").fill("https://store.example.test");
  await create.getByLabel("Protected value", {exact:true}).fill("ui-known-private-9847");
  await expect(create.locator(".ant-input-password-icon")).toHaveCount(0);
  await create.getByRole("button", {name:/Save credential/}).click();
  await expect(create).toBeHidden();
  await page.getByRole("textbox", {name:"Search credentials",exact:true}).fill(name);
  const row = page.getByRole("row").filter({hasText:name}); await expect(row).toContainText("operator@example.test");
  await expect(row).toContainText("1 more metadata field");
  await row.getByRole("button", {name:"Expand row",exact:true}).click();
  await expect(page.getByRole("region", {name:`Details for ${name}`})).toContainText(longMetadata.trim());
  for (const width of [1440,768,390]) {
    await page.setViewportSize({width,height:900});
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  }
  await page.setViewportSize({width:1440,height:900});
  await page.getByRole("textbox", {name:"Search credentials",exact:true}).fill("unmatched-credential-search");
  await expect(page.getByText("No matches. Clear search or filters to see all results.")).toBeVisible();
  await page.getByRole("textbox", {name:"Search credentials",exact:true}).fill(name);
  const c = (await owner(page,"credentials")).items.find((x: {displayName:string}) => x.displayName === name);
  expect(JSON.stringify(c)).not.toContain("ui-known-private-9847");
  expect(Object.hasOwn(c.metadata, "__proto__")).toBe(true);
  expect(c.metadata.__proto__).toBe("safe metadata value");
  await row.getByRole("button", {name:"Replace value"}).click();
  const replace = page.getByRole("dialog", {name:"Replace protected value"});
  const input = replace.getByLabel("Protected value", {exact:true}); await expect(input).toHaveValue("");
  await page.route(`**/credentials/${c.credentialId}/value`, route => route.fulfill({status:409,json:{title:"Stale credential revision",detail:"Reload the credential.",diagnosticId:"credential-write-diagnostic"}}), {times:1});
  await input.fill("must-clear-on-failure-5937"); await replace.getByRole("button", {name:/Save credential/}).click();
  await expect(input).toHaveValue(""); await expect(replace.getByRole("alert")).toBeVisible();
  await replace.getByRole("button", {name:"Error details",exact:true}).click();
  await expect(page.getByText("credential-write-diagnostic", {exact:true})).toBeVisible();
  await replace.getByRole("button", {name:"Error details",exact:true}).click();
  await expect(replace.getByRole("button", {name:/Save credential/})).toBeEnabled();
  await expect(replace.getByRole("button", {name:/Save credential/})).not.toHaveClass(/ant-btn-loading/);
  await input.fill("rotated-private-9546"); await replace.getByRole("button", {name:/Save credential/}).click(); await expect(replace).toBeHidden();
  await row.getByRole("button", {name:"Replace value"}).click(); await expect(input).toHaveValue(""); await replace.getByRole("button", {name:"Cancel"}).click();
  for (const [instance, alias] of [[a,"primary"],[b,"shared"]] as const) {
    await page.goto(`/admin/instances/${instance.instanceId}/connections/credentials`);
    await page.getByRole("button", {name:"Bind credential",exact:true}).click();
    const bind = page.getByRole("dialog", {name:"Bind credential",exact:true});
    await bind.getByRole("combobox", {name:"System credential"}).fill(name);
    await page.getByTitle(`${name} · Password · Active`, {exact:true}).click();
    await bind.getByLabel("Reference", {exact:true}).fill("invalid alias");
    await bind.getByRole("button", {name:"Bind credential",exact:true}).click(); await expect(bind.getByText("Use 1–64 letters, digits or hyphens.")).toBeVisible();
    await bind.getByLabel("Reference", {exact:true}).fill(alias);
    if (alias === "primary") {
      await page.route(`**/agent-instances/${instance.instanceId}/credential-bindings`, route => route.fulfill({status:409,json:{title:"Binding conflict",detail:"Try again with the current instance.",diagnosticId:"binding-write-diagnostic"}}), {times:1});
      await bind.getByRole("button", {name:"Bind credential",exact:true}).click();
      await expect(bind.getByRole("alert")).toContainText("Try again with the current instance.");
      await expect(bind.getByLabel("Reference", {exact:true})).toHaveValue(alias);
    }
    await bind.getByRole("button", {name:"Bind credential",exact:true}).click();
    await expect(bind).toBeHidden(); await expect(page.getByRole("cell", {name:alias,exact:true})).toBeVisible();
  }
  await page.goto("/admin/credentials");
  await page.getByRole("textbox", {name:"Search credentials",exact:true}).fill(name);
  await expect(row.getByRole("button",{name:"Delete",exact:true})).toBeDisabled();
  await row.getByRole("button",{name:"Edit",exact:true}).click();
  const edit = page.getByRole("dialog", {name:"Edit credential"}); await expect(edit.getByLabel("Kind")).toHaveCount(0); await expect(edit.locator("input[type=password]")).toHaveCount(0);
  await expect(edit.getByLabel("Metadata key 1")).toHaveValue("username");
  await expect(edit.getByLabel("Metadata value 1")).toHaveValue("operator@example.test");
  await expect(edit.getByLabel("Metadata key 2")).toHaveValue("__proto__");
  await expect(edit.getByLabel("Metadata value 2")).toHaveValue("safe metadata value");
  await edit.getByRole("combobox", {name:"Status"}).click(); await page.getByTitle("Disabled", {exact:true}).click();
  await edit.getByRole("button", {name:/Save credential/}).click(); await expect(edit).toBeHidden(); await expect(row).toContainText("Disabled");
  await page.goto(`/admin/instances/${a.instanceId}/connections`); await expect(page).toHaveURL(new RegExp(`/instances/${a.instanceId}/connections(?:/credentials)?$`));
  await expect(page.getByText("Password · Disabled")).toBeVisible();
  await page.getByRole("button",{name:"Reset browser profile",exact:true}).click();
  await page.getByRole("dialog",{name:"Reset browser profile?"}).getByRole("button",{name:"Cancel",exact:true}).click();
  await expect(page.getByRole("dialog",{name:"Reset browser profile?"})).toBeHidden();
  await expect(page.getByRole("cell",{name:"primary",exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Reset browser profile",exact:true}).click();
  await page.getByRole("dialog",{name:"Reset browser profile?"}).getByRole("button",{name:"Reset browser profile",exact:true}).click();
  await expect(page.getByRole("cell",{name:"primary",exact:true})).toBeVisible();
  await page.getByRole("button",{name:"Unbind",exact:true}).click();
  await page.getByRole("dialog",{name:"Unbind primary?"}).getByRole("button",{name:"Unbind",exact:true}).click();
  await expect(page.getByText("No credentials bound")).toBeVisible();
  await owner(page, `agent-instances/${b.instanceId}/lifecycle`, "PATCH", {expectedRevision:1,lifecycle:"Archived"});
  await page.goto(`/admin/instances/${b.instanceId}/connections/credentials`); await expect(page.getByRole("button",{name:"Bind credential",exact:true})).toBeDisabled(); await expect(page.getByRole("button",{name:"Unbind",exact:true})).toBeDisabled();
  for (const width of [1440,768,390]) {
    await page.setViewportSize({width,height:900}); await expect(page.getByRole("heading",{name:"Credential bindings"})).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  }
  await page.goto(`/admin/instances/${a.instanceId}/connections/event-sources`); await expect(page.getByRole("tab",{name:"Event sources",exact:true})).toHaveAttribute("aria-selected","true");
  await expect(page.getByRole("region",{name:"Event sources"})).toBeVisible();
  expect(errors).toEqual([]);
});
