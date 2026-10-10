import { expect, test, type Page } from '@playwright/test';

async function openEditor(page: Page, kind: 'schedule' | 'event') {
  await page.goto('/');
  expect((await (await page.request.get('/health')).json()).profile).toBe('Synthetic');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('agent-core.owner-capability'))).not.toBeNull();
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const instance = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'secretary', version: 9 } });
  expect(instance.ok()).toBe(true);
  const route = `/api/v2/admin/agent-instances/${(await instance.json()).instanceId}/automations`;
  const response = await page.request.post(route, { headers, data: { expectedRevision: 0, enabled: true,
    name: 'Recover held draft', instructions: 'Review the current signal quietly.',
    triggers: [{ triggerId: crypto.randomUUID(), enabled: true, revision: 1, kind,
      ...(kind === 'schedule' ? { schedule: { kind: 'daily', interval: 1, timeZone: 'UTC', localTime: '14:30' } }
        : { source: { kind: 'builtin', key: 'run.failed' } }) }],
    executionTarget: { kind: 'backgroundSession' }, completionDelivery: { kind: 'none' } } });
  expect(response.ok()).toBe(true);
  const saved = await response.json();
  await page.goto(route.replace('/api/v2/admin/agent-instances/', '/admin/instances/').replace('/automations', '/automation/automations'));
  await page.getByRole('button', { name: 'View automation: Recover held draft', exact: true }).click();
  await page.getByRole('button', { name: 'Edit automation', exact: true }).click();
  return { route, saved, drawer: page.getByRole('dialog', { name: 'Edit automation', exact: true }) };
}

test('Schedule remains saveable with an unavailable Webhook catalog', async ({ page }) => {
  await page.route('**/api/v2/admin/connections/events/catalog?kind=webhook', route => route.fulfill({ status: 503,
    contentType: 'application/problem+json', body: JSON.stringify({ title: 'Webhook catalog unavailable' }) }));
  const { drawer } = await openEditor(page, 'schedule');
  await expect(page.getByRole('button', { name: 'Reload Events and models', exact: true })).toBeVisible();
  await drawer.getByLabel('Automation instructions').fill('Persist this valid Schedule despite unrelated catalog failure.');
  await expect(drawer.getByRole('button', { name: 'Save automation', exact: true })).toBeEnabled();
  const write = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes('/automations/'));
  await drawer.getByRole('button', { name: 'Save automation', exact: true }).click();
  const saved = await (await write).json();
  expect(saved.enabled).toBe(true); expect(saved.triggers[0].kind).toBe('schedule');
  await expect(drawer).toBeHidden();
});

test('failed source and collection reads fence activation but preserve disabled Event saving', async ({ page }) => {
  await page.route('**/api/v2/admin/connections/events/catalog?kind=builtin', request => request.fulfill({ status: 503,
    contentType: 'application/problem+json', body: JSON.stringify({ title: 'Built-in catalog unavailable' }) }));
  const { route, saved, drawer } = await openEditor(page, 'event');
  await expect(drawer.getByRole('button', { name: 'Retry catalog', exact: true })).toBeVisible();
  await page.route('**' + route, request => request.request().method() === 'GET' ? request.fulfill({ status: 503,
    contentType: 'application/problem+json', body: JSON.stringify({ title: 'Collection refresh unavailable' }) }) : request.continue());
  await expect(page.getByText('Collection refresh unavailable', { exact: true })).toBeVisible({ timeout: 10_000 });
  await expect(drawer.getByRole('button', { name: 'Save automation', exact: true })).toBeDisabled();
  await drawer.getByLabel('Automation instructions').fill('Keep this known Event draft disabled until recovery.');
  const write = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes('/automations/'));
  await drawer.getByRole('button', { name: 'Save as disabled', exact: true }).click();
  const result = await (await write).json();
  expect(result.enabled).toBe(false); expect(result.triggers[0].triggerId).toBe(saved.triggers[0].triggerId);
  expect(result.triggers[0].source).toEqual(saved.triggers[0].source);
  await expect(drawer).toBeHidden();
});
