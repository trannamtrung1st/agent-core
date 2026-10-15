import { expect, test } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
import { instancePages, draftPages } from '../src/features/admin/adminNavigation';

for (const width of [1440, 390]) test(`canonical Admin routes, drafts and history at ${width}px`, async ({ page }) => {
  test.setTimeout(180_000);
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message));
  await page.setViewportSize({ width, height: 900 });
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1, persona: { name: 'Canonical navigation fixture with a deliberately long operator name', role: 'Inspector', description: 'Synthetic route fixture', tone: 'Clear' } } });
  expect(created.ok(), await created.text()).toBe(true); const { instanceId } = await created.json(); const base = `/admin/instances/${instanceId}`;
  await page.goto('/admin'); await expect(page).toHaveURL(/\/admin\/instances$/);
  await page.goto(base); await expect(page).toHaveURL(`${new URL(base, page.url())}/overview`);
  await navigateAdmin(page, 'Profile'); await page.getByLabel('Persona name', { exact: true }).fill('Unsaved profile fixture');
  await page.waitForLoadState('networkidle');
  let reads = 0; page.on('request', r => { if (r.url().endsWith(`${instanceId}/effective-config`)) reads++; });
  await navigateAdmin(page, 'Instructions');
  await page.getByRole('textbox', { name: 'Agent instructions', exact: true }).fill('Unsaved instructions fixture');
  await navigateAdmin(page, 'Conversation');
  await page.getByRole('textbox', { name: 'Language', exact: true }).fill('vi');
  await navigateAdmin(page, 'Models'); await navigateAdmin(page, 'Instructions');
  await expect(page.getByRole('textbox', { name: 'Agent instructions', exact: true })).toHaveValue('Unsaved instructions fixture');
  await navigateAdmin(page, 'Conversation');
  await expect(page.getByRole('textbox', { name: 'Language', exact: true })).toHaveValue('vi');
  expect(reads).toBe(0);
  await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save configuration', exact: true })).toBeDisabled();
  await expect(page.getByRole('status').filter({ hasText: 'Configuration saved.' })).toBeVisible();
  await page.getByRole('textbox', { name: 'Language', exact: true }).fill('vi-VN');
  await page.getByRole('link', { name: 'Chat', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Stay', exact: true }).click();
  await expect(page).toHaveURL(`${new URL(base, page.url())}/conversation`);
  await navigateAdmin(page, 'Profile'); await expect(page.getByLabel('Persona name', { exact: true })).toHaveValue('Unsaved profile fixture');
  await page.getByRole('link', { name: 'Chat', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Discard and leave', exact: true }).click();
  await expect(page).toHaveURL(/\/$/);
  await page.goto(`${base}/overview`);
  for (const p of instancePages) {
    await navigateAdmin(page, p.label); await expect(page).toHaveURL(new URL(`${base}/${p.path}`, page.url()).href);
    await page.reload(); await expect(page.getByRole('heading', { name: p.label, exact: true, level: 2 })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
  }
  await navigateAdmin(page, 'Sessions'); await navigateAdmin(page, 'Runs'); await page.goBack();
  await expect(page).toHaveURL(new URL(`${base}/activity/sessions`, page.url()).href);
  await page.goForward(); await expect(page).toHaveURL(new URL(`${base}/activity/runs`, page.url()).href);
  await page.goto('/admin/definitions/examiner'); await navigateAdmin(page, 'Drafts'); await navigateAdmin(page, 'Versions'); await page.goBack();
  await expect(page).toHaveURL(/\/drafts$/); await page.goForward(); await expect(page).toHaveURL(/\/versions$/);
  await page.getByRole('button', { name: /Fork v1 \(builtIn\)/ }).click(); await expect(page).toHaveURL(/\/drafts\/[0-9a-f-]+\/overview$/);
  const draftBase = new URL(page.url()).pathname.replace(/\/overview$/, '');
  await navigateAdmin(page, 'Instructions'); await page.getByLabel('Agent instructions', { exact: true }).fill('Candidate retained through draft pages');
  await navigateAdmin(page, 'Models'); await navigateAdmin(page, 'Instructions');
  await expect(page.getByLabel('Agent instructions', { exact: true })).toHaveValue('Candidate retained through draft pages');
  await navigateAdmin(page, 'Advanced JSON'); await page.getByRole('textbox', { name: 'Advanced JSON', exact: true }).fill('{ invalid');
  await navigateAdmin(page, 'Skills'); await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  await navigateAdmin(page, 'Advanced JSON'); await expect(page.getByRole('textbox', { name: 'Advanced JSON', exact: true })).toHaveValue('{ invalid');
  await page.getByRole('link', { name: 'Chat', exact: true }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Stay', exact: true }).click();
  // Repair through the same candidate JSON, save explicitly, then reload every draft route.
  const draftId = draftBase.split('/').at(-1)!;
  const saved = await (await page.request.get(`/api/v2/admin/definition-drafts/${draftId}`, { headers })).json();
  await page.getByRole('textbox', { name: 'Advanced JSON', exact: true }).fill(JSON.stringify({ ...saved.candidate, agentInstructions: 'Saved canonical draft fixture' }, null, 2));
  await page.getByRole('button', { name: 'Save draft', exact: true }).click(); await expect(page.getByText('Draft saved.', { exact: true })).toBeVisible();
  for (const p of draftPages) { await navigateAdmin(page, p.label); await page.reload(); await expect(page).toHaveURL(new URL(`${draftBase}/${p.path}`, page.url()).href); await expect(page.getByRole('heading', { name: p.label, exact: true, level: 2 })).toBeVisible(); }
  await page.goto('/admin/instances/not-valid'); await expect(page.getByText('Admin page not found', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('all legacy leaves preserve queries, fragments and exact owner destinations', async ({ page }) => {
  test.setTimeout(120_000); await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } }); expect(created.ok()).toBe(true);
  const { instanceId } = await created.json(); const base = `/admin/instances/${instanceId}`;
  for (const [old, next] of [['identity','profile'], ['identity/profile','profile'], ['identity/settings','instructions'], ['identity/workspace','workspace'], ['skills/skills','skills'], ['skills/resources','resources'], ['continuity','continuity/memory'], ['automation','automations'], ['automation/automations','automations'], ['automation/controls','unattended#execution-defaults'], ['automation/harness','self-management'], ['activity','activity/sessions'], ['runs','activity/runs'], ['access','capabilities']]) {
    await page.goto(`${base}/${old}?q=kept&token=removed`); const [path, hash] = next.split('#');
    await expect(page).toHaveURL(new URL(`${base}/${path}?q=kept${hash ? `#${hash}` : ''}`, page.url()).href);
    await expect(page.getByRole('heading', { level: 2 }).first()).toBeVisible();
  }
  await page.goto(`${base}/lifecycle`); await page.getByRole('button', { name: 'Archive instance', exact: true }).click(); await page.getByRole('dialog').getByRole('button', { name: 'Archive', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Unarchive instance', exact: true })).toBeVisible();
  await page.goto(`${base}/automation/controls`); await expect(page).toHaveURL(new RegExp(`${base}/lifecycle\\?notice=archived-automations$`));
  await expect(page.getByText('Automations are unavailable for this archived Instance', { exact: true })).toBeVisible();
});

test('Search Admin scopes approved labels, focuses anchors and recovers after deleting a dirty draft', async ({ page }) => {
  await page.goto('/');
  await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const response = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } });
  expect(response.ok()).toBe(true);
  const { instanceId } = await response.json();
  await page.goto(`/admin/instances/${instanceId}/overview`);
  const opener = page.getByRole('button', { name: 'Search Admin', exact: true });
  await opener.focus(); await page.keyboard.press('Enter');
  const search = page.getByRole('dialog', { name: 'Search Admin', exact: true });
  await search.getByRole('textbox').fill('private-content-not-in-index');
  await expect(search.getByText(/No destinations found/)).toBeVisible();
  await page.keyboard.press('Escape'); await expect(opener).toBeFocused();
  await opener.click(); await search.getByRole('textbox').fill('reasoning effort');
  await search.getByRole('link', { name: /Interactive model & reasoning/ }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/instances/${instanceId}/models#modelDefaults$`));
  await expect(page.locator('#modelDefaults')).toBeFocused();
  await opener.click(); await search.getByRole('textbox').fill('provider preferences');
  await search.getByRole('link', { name: /Provider preferences/ }).click();
  await expect(page.locator('#providerPreferences')).toBeFocused();
  await page.setViewportSize({ width: 390, height: 900 });
  await page.getByRole('button', { name: 'Open Admin navigation', exact: true }).click();
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  await nav.getByRole('link', { name: 'Instructions', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(nav).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Instructions', exact: true, level: 2 })).toBeFocused();
  await page.goto('/admin/definitions/examiner/versions');
  await page.getByRole('button', { name: /Fork v1 \(builtIn\)/ }).click();
  await expect(page).toHaveURL(/\/drafts\/[0-9a-f-]+\/overview$/);
  const draftUrl = page.url();
  await navigateAdmin(page, 'Instructions');
  await page.getByLabel('Agent instructions', { exact: true }).fill('Unsaved draft being explicitly deleted');
  await page.getByRole('button', { name: 'Search Admin', exact: true }).click();
  await search.getByRole('textbox').fill('browser privacy');
  await search.getByRole('link', { name: /Browser privacy/ }).click();
  await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Stay', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page.getByRole('button', { name: 'Search Admin', exact: true })).toBeFocused();
  await expect(page.getByLabel('Agent instructions', { exact: true })).toHaveValue('Unsaved draft being explicitly deleted');
  await page.getByRole('button', { name: 'Delete draft', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete draft', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/definitions\/examiner\/drafts$/);
  await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toHaveCount(0);
  await page.goto(draftUrl);
  await expect(page.getByText(/Failed to load draft|Draft could not be loaded|Draft not found|not found|404/i).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toHaveCount(0);
});

test('Admin navigation changes at exactly 1024px', async ({ page }) => {
  await page.goto('/admin/instances');
  for (const width of [1024, 1023, 1000, 992, 1024]) {
    await page.setViewportSize({ width, height: 900 });
    const opener = page.getByRole('button', { name: 'Open Admin navigation', exact: true });
    if (width >= 1024) {
      await expect(page.locator('.admin-sidebar')).toBeVisible();
      await expect(opener).toHaveCount(0);
    } else {
      await expect(opener).toBeVisible();
      await expect(page.locator('.admin-sidebar')).toHaveCount(0);
      await opener.click();
      await expect(page.getByRole('navigation', { name: 'Admin navigation', exact: true })).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(opener).toBeFocused();
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
  }
});
