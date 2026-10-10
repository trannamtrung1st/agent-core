import { expect, test } from '@playwright/test';

test('Skill summaries are bounded and row actions remain reachable while scrolling', async ({ page }) => {
  const errors: string[] = [];
  const failed: string[] = [];
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('requestfailed', request => failed.push(request.url()));
  await page.goto('/');
  await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const created = await page.request.post('/api/v2/admin/agent-instances', {
    headers, data: { definitionId: 'general-assistant', version: 22,
      persona: { name: 'Skills layout fixture', role: 'Inspector', description: 'Synthetic layout check', tone: 'Clear' } }
  });
  expect(created.ok()).toBeTruthy();
  const { instanceId } = await created.json();
  const name = 'Read asset time-series data and verify the selected project before reporting';
  const description = 'Open the named project and asset, read its time-series values, preserve source timestamps, and verify the selected project before reporting the observed values. Keep this full description available when the table summary truncates.';
  const skill = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/skills`, {
    headers, data: { id: 'asset-time-series-with-a-long-stable-skill-identifier', name, description,
      procedure: 'Preserve supporting evidence.', projection: 'OnDemand', enabled: true,
      requiredCapabilities: ['workspace.read', 'workspace.search'] }
  });
  expect(skill.ok()).toBeTruthy();
  await page.goto(`/admin/instances/${instanceId}/skills`);
  const local = page.getByRole('region', { name: 'Instance Skills', exact: true });
  const row = local.getByRole('row').filter({ hasText: name });
  await expect(row).toBeVisible();
  for (const width of [1920, 1440, 768, 767, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const summary = row.locator('.admin-skill-summary').filter({ hasText: name });
    await expect(summary).toHaveCSS('text-overflow', 'ellipsis');
    expect(await summary.evaluate(el => el.scrollWidth > el.clientWidth)).toBe(true);
    expect((await summary.boundingBox())!.width).toBeLessThanOrEqual(320);
    const edit = row.getByRole('button', { name: 'Edit', exact: true });
    const viewport = local.locator('.ant-table-content');
    for (const position of ['start', 'end']) {
      await viewport.evaluate((el, edge) => { el.scrollLeft = edge === 'start' ? 0 : el.scrollWidth; }, position);
      const viewportBox = (await viewport.boundingBox())!;
      const editBox = (await edit.boundingBox())!;
      expect(editBox.x).toBeGreaterThanOrEqual(viewportBox.x);
      expect(editBox.x + editBox.width).toBeLessThanOrEqual(viewportBox.x + viewportBox.width + 1);
      await edit.click();
      await expect(page.getByLabel('Procedure', { exact: true })).toHaveValue('Preserve supporting evidence.');
      await page.getByRole('button', { name: 'Cancel editing', exact: true }).click();
      await expect(page.getByRole('dialog')).toBeHidden();
    }
    await viewport.evaluate(el => { el.scrollLeft = 0; });
    await summary.hover();
    await expect(page.getByRole('tooltip')).toHaveText(name);
    await page.mouse.move(0, 0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
  await row.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(row).toBeVisible();
  await expect(local.getByRole('button', { name: 'Delete', exact: true })).toBeFocused();
  expect(failed).toEqual([]);
  expect(errors.filter(error => !error.includes('favicon'))).toEqual([]);
});
