import { expect, test, type Page } from '@playwright/test';
import { completeDefinitionDraftPublishGate, publishDraftFromInstructions } from './admin-definition-gate-helpers';
import { draftEditorSection } from './admin-draft-editor-helpers';
import { selectManagedIdentityOption } from './admin-managed-helpers';
import { waitForResponseSettled } from './support/response-settled';

// Synthetic proves the real runtime and durable owner flows. It does not prove
// hosted-model judgement, natural-language memory admission, or lesson fidelity.
test.describe('Morgan secretary Synthetic journey', () => {
  test.describe.configure({ mode: 'serial' });
  test.use({ actionTimeout: 15_000 });
  const definitionId = `executive-secretary-${Date.now()}`;
  let instanceId: string;
  let sourceSessionId: string;

  async function openInstance(page: Page) {
    await page.goto(`/admin/instances/${instanceId}`);
    await expect(page.getByRole('heading', { name: 'Morgan', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: 'Behavior & continuity', exact: true }).click();
  }

  async function newChat(page: Page) {
    await page.goto('/');
    await page.getByRole('button', { name: 'Start a new chat', exact: true }).click();
    await selectManagedIdentityOption(page, instanceId);
  }

  async function send(page: Page, text: string, answer: string) {
    const before = await page.locator('.chat-message-assistant').count();
    await page.getByLabel('Message', { exact: true }).fill(text);
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.locator('.chat-message-assistant')).toHaveCount(before + 1);
    await expect(page.locator('.assistant-body').last()).toHaveText(answer, { timeout: 30_000 });
    await waitForResponseSettled(page);
  }

  async function endChat(page: Page) {
    await page.getByRole('button', { name: 'End', exact: true }).click();
    await page.getByRole('dialog', { name: 'End this conversation?', exact: true })
      .getByRole('button', { name: 'End', exact: true }).click();
    await expect(page.getByLabel('Message', { exact: true })).toBeHidden();
  }

  async function headers(page: Page) {
    await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
    return { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  }

  async function saveDraft(page: Page) {
    await Promise.all([
      page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes('/definition-drafts/') && r.ok()),
      draftEditorSection(page).getByRole('button', { name: 'Save draft', exact: true }).click()
    ]);
    await expect(draftEditorSection(page).getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  }

  // Real page errors, transport failures, and server errors always fail the journey.
  let failures: string[];
  test.beforeEach(async ({ page }) => {
    failures = [];
    page.on('pageerror', error => failures.push(error.message));
    page.on('console', message => {
      // Existing AntD migration notices are tracked separately from runtime errors.
      if (message.type() === 'error' && !/^Warning: \[antd: (List|Alert)\]/.test(message.text())) failures.push(message.text());
    });
    page.on('requestfailed', request => {
      if (!request.failure()?.errorText.includes('ERR_ABORTED')) failures.push(`${request.url()}: ${request.failure()?.errorText}`);
    });
    page.on('response', response => { if (response.status() >= 500) failures.push(`${response.status()} ${response.url()}`); });
  });
  test.afterEach(() => expect(failures).toEqual([]));

  test('authors and publishes a fresh Definition, rejects invalid input, and creates Morgan', async ({ page }) => {
    test.setTimeout(150_000);
    await page.goto('/admin');
    await page.getByRole('button', { name: 'New definition', exact: true }).click();
    const modal = page.getByRole('dialog', { name: 'New definition', exact: true });
    await modal.getByLabel('Definition ID', { exact: true }).fill('Executive Secretary');
    await expect(modal.getByLabel('Definition ID', { exact: true })).toHaveAttribute('aria-invalid', 'true');
    await expect(modal.getByRole('button', { name: 'Create draft', exact: true })).toBeDisabled();
    await modal.getByLabel('Definition ID', { exact: true }).fill(definitionId);
    await modal.getByRole('button', { name: 'Create draft', exact: true }).click();
    const editor = draftEditorSection(page);
    await expect(editor.getByLabel('System instructions', { exact: true })).toBeVisible();
    for (const [label, value] of [
      ['Definition name', 'Executive Secretary'], ['Definition role', 'Executive Secretary'],
      ['Definition description', 'A dependable secretary who organizes follow-ups and checks prior context.'],
      ['Definition tone', 'Concise, calm, practical'],
      ['System instructions', 'You are Morgan. Organize Atlas follow-ups, use relevant Memory and Experience, put blockers first, and obey current policy and approvals.']
    ]) await editor.getByLabel(label, { exact: true }).fill(value);
    for (const label of ['Session memory', 'Identity user promotion', 'Identity user retrieval', 'Automation enabled',
      'Allow user scheduling', 'Allow one-shot', 'Allow daily', 'Allow weekly', 'Allow fixed interval']) {
      await editor.getByRole('switch', { name: label, exact: true }).click();
    }
    await editor.getByLabel('Max active registrations', { exact: true }).fill('4');
    await editor.getByLabel('One-shot horizon days', { exact: true }).fill('30');
    await editor.getByRole('combobox', { name: 'Allowed source kinds', exact: true }).click();
    await page.locator('.ant-select-item-option').filter({ hasText: 'Schedule' }).last().click();
    await page.keyboard.press('Escape');
    await saveDraft(page);

    // The trusted allowlist picker must not advertise context-owned capabilities
    // which the publication validator intentionally refuses as Definition grants.
    const tools = await page.request.get('/api/v2/admin/tools', { headers: await headers(page) });
    expect(tools.ok()).toBe(true);
    const names = (await tools.json()).toolNames;
    expect(names).not.toContain('continuity.search');
    expect(names).not.toContain('experience.recent');
    await editor.getByRole('tab', { name: 'Capabilities', exact: true }).click();
    for (const tool of ['knowledge.retrieve', 'trigger.schedule_once', 'trigger.schedule_recurring', 'trigger.list', 'trigger.update', 'trigger.cancel',
      'browser.navigate', 'browser.observe', 'browser.act']) {
      const select = editor.getByRole('combobox', { name: 'Tool allowlist', exact: true });
      await select.click(); await select.fill(tool);
      await page.locator('.ant-select-item-option').filter({ hasText: tool }).last().click();
      await page.keyboard.press('Escape');
    }
    await saveDraft(page);
    await page.reload();
    await expect(editor.getByLabel('Definition name', { exact: true })).toHaveValue('Executive Secretary');
    await expect(editor.getByLabel('Max active registrations', { exact: true })).toHaveValue('4');
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await completeDefinitionDraftPublishGate(page, editor);
    await publishDraftFromInstructions(page, editor);
    await page.goto('/admin');
    await page.getByRole('button', { name: 'New instance', exact: true }).click();
    const create = page.getByRole('dialog', { name: 'New instance', exact: true });
    await create.getByRole('combobox', { name: 'Definition', exact: true }).click();
    await create.getByRole('combobox', { name: 'Definition', exact: true }).fill(definitionId);
    await page.locator('.ant-select-item-option').filter({ hasText: definitionId }).click();
    await create.getByText('Custom persona', { exact: true }).click();
    for (const [label, value] of [['Persona name', 'Morgan'], ['Persona role', 'Executive Secretary'],
      ['Persona description', 'A dependable secretary for Atlas stakeholder reviews.'], ['Persona tone', 'Concise, calm, practical']]) {
      await create.getByLabel(label, { exact: true }).fill(value);
    }
    await create.getByRole('button', { name: 'Create instance', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Morgan', exact: true })).toBeVisible();
    instanceId = page.url().split('/').at(-1)!;
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Morgan', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: 'Behavior & continuity', exact: true }).click();
    const experience = page.getByRole('region', { name: 'Experience', exact: true });
    await experience.getByRole('switch', { name: 'Enable experience', exact: true }).click();
    await expect(experience.getByText('Enabled · completed work may be retrospected', { exact: true })).toBeVisible();
  });

  test('retrospects actual completed work, dedupes the checkpoint, and recalls/suppresses Experience across chats', async ({ page }) => {
    test.setTimeout(120_000);
    await newChat(page);
    await send(page, 'Prepare an Atlas stakeholder review checklist: unresolved decisions, agenda confirmation, and meeting follow-up.', 'Hello from synthetic.');
    await send(page, 'A correction: for future Atlas stakeholder reviews put unresolved decisions before agenda confirmation.', 'Hello from synthetic.');
    sourceSessionId = page.url().split('/').at(-1)!;
    await endChat(page);
    await openInstance(page);
    const experience = page.getByRole('region', { name: 'Experience', exact: true });
    await expect(experience.getByText('Review observable completed work', { exact: true })).toHaveCount(1, { timeout: 30_000 });
    await experience.getByLabel('Retrospection source Session', { exact: true }).fill(sourceSessionId);
    const retrospect = () => Promise.all([
      page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/experience/checkpoints') && r.ok()),
      experience.getByRole('button', { name: 'Retrospect now', exact: true }).click()
    ]);
    await retrospect();
    await expect(experience.getByRole('button', { name: 'Retrospect now', exact: true })).toBeEnabled();
    await retrospect();
    await expect(experience.getByText('Review observable completed work', { exact: true })).toHaveCount(1);
    await experience.getByText('Review observable completed work', { exact: true }).click();
    await expect(experience.getByText('The user supplied a correction', { exact: true })).toBeVisible();
    await expect(experience.getByText(new RegExp(`Source Session ${sourceSessionId}`))).toBeVisible();
    await newChat(page);
    await send(page, 'Use my recent experience before acting.', 'I will observe current page state before acting, based on earlier experience. Current policy still controls every action.');
    expect(page.url()).not.toContain(sourceSessionId);
    await endChat(page);
    await openInstance(page);
    await experience.getByText('Review observable completed work', { exact: true }).first().click();
    await experience.getByRole('button', { name: 'Suppress experience', exact: true }).first().click();
    await expect(experience.getByText('Suppressed', { exact: true }).first()).toBeVisible();
    // Suppress every eligible source in this disposable instance, including the
    // second session's independently admitted stable checkpoint.
    const rows = await page.request.get(`/api/v2/admin/agent-instances/${instanceId}/experience`, { headers: await headers(page) });
    expect(rows.ok()).toBe(true);
    await experience.getByRole('button', { name: 'Reset experience', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Reset experience', exact: true }).click();
    await expect(experience.getByText('No experience yet. Enable experience and retrospect a completed task.', { exact: true })).toBeVisible();
    await newChat(page);
    await send(page, 'Use my recent experience before acting.', 'Hello from synthetic.');
    await endChat(page);
  });

  test('Admin and Chat schedule parity, pinned durable runs, quiet/attention Thought, and mobile controls', async ({ page }) => {
    test.setTimeout(180_000);
    await openInstance(page);
    const schedules = page.getByRole('region', { name: 'Scheduled work', exact: true });
    const task = 'Review Atlas follow-up obligations and prepare a concise status summary.';
    await schedules.getByRole('button', { name: 'New schedule', exact: true }).click();
    await schedules.getByLabel('Schedule task', { exact: true }).fill(task);
    await schedules.getByLabel('Schedule time zone', { exact: true }).fill('Asia/Ho_Chi_Minh');
    await schedules.getByLabel('Schedule maximum occurrences', { exact: true }).fill('0');
    await expect(schedules.getByRole('button', { name: 'Create schedule', exact: true })).toBeDisabled();
    await schedules.getByLabel('Schedule maximum occurrences', { exact: true }).fill('5');
    await schedules.getByRole('button', { name: 'Create schedule', exact: true }).click();
    await expect(schedules.getByLabel('Schedule task', { exact: true })).toBeHidden();
    await schedules.getByText(task, { exact: true }).click();
    await expect(schedules.getByText('Admin owner', { exact: true })).toBeVisible();
    const path = `/api/v2/admin/agent-instances/${instanceId}/schedules`;
    const before = (await (await page.request.get(path, { headers: await headers(page) })).json()).items[0];
    let runCalls = 0;
    // Return stale status after acceptance to exercise the actual admission race.
    const oldStatus = await (await page.request.get(path, { headers: await headers(page) })).text();
    const statusPattern = `**/agent-instances/${instanceId}/schedules`;
    await page.route(statusPattern, route => route.request().method() === 'GET'
      ? route.fulfill({ status: 200, contentType: 'application/json', body: oldStatus }) : route.continue());
    const runPattern = `**/agent-instances/${instanceId}/schedules/*/run`;
    await page.route(runPattern, async route => { runCalls++; await route.continue(); });
    const run = schedules.getByRole('button', { name: 'Run schedule now', exact: true });
    await run.evaluate(button => { (button as HTMLButtonElement).click(); (button as HTMLButtonElement).click(); });
    await expect(run).toBeDisabled();
    await expect(run).toHaveText('Starting…');
    await expect.poll(() => runCalls).toBe(1);
    await expect(schedules.getByRole('button', { name: 'Refresh schedules', exact: true })).toBeEnabled();
    await run.evaluate(button => (button as HTMLButtonElement).click());
    expect(runCalls).toBe(1);
    await page.unroute(statusPattern);
    await schedules.getByRole('button', { name: 'Refresh schedules', exact: true }).click();
    await expect(schedules.getByText('Completed', { exact: true })).toBeVisible({ timeout: 30_000 });
    await schedules.getByRole('button', { name: 'Inspect scheduled execution', exact: true }).click();
    const work = page.getByRole('dialog', { name: 'Background work', exact: true });
    await expect(work.getByText('Scheduled reminder', { exact: true })).toBeVisible();
    await expect(work.getByRole('button', { name: /Create/ })).toHaveCount(0);
    await work.getByRole('button', { name: 'Close', exact: true }).click();
    await schedules.getByRole('button', { name: 'Disable schedule', exact: true }).click();
    await expect(schedules.getByRole('button', { name: 'Enable schedule', exact: true })).toBeVisible();
    await expect(run).toBeDisabled();
    // A supported host prerequisite has no first-party timezone editing surface.
    const ownerHeaders = await headers(page);
    const profile = await (await page.request.get('/api/v2/profile', { headers: ownerHeaders })).json();
    const setZone = await page.request.patch('/api/v2/profile', { headers: ownerHeaders, data: { expectedRevision: profile.revision, values: { timeZone: 'Asia/Ho_Chi_Minh' } } });
    expect(setZone.ok()).toBe(true);
    await newChat(page);
    await send(page, 'remind me tomorrow', 'Scheduled Call John.');
    const chatId = page.url().split('/').at(-1)!;
    await page.getByRole('button', { name: 'Schedules', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Schedules', exact: true });
    await expect(drawer.getByText('Call John', { exact: true })).toBeVisible();
    await expect(drawer.getByText('Disabled', { exact: true })).toBeVisible();
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await endChat(page);
    await openInstance(page);
    await schedules.getByText('Call John', { exact: true }).click();
    await expect(schedules.getByText(new RegExp(`Chat user request.*${chatId}`))).toBeVisible();
    await schedules.getByRole('button', { name: 'Edit schedule', exact: true }).click();
    const futureTask = 'Review unresolved Atlas decisions before agenda confirmation and prepare a concise blocker-first status summary.';
    await page.setViewportSize({ width: 390, height: 844 });
    await schedules.getByLabel('Schedule task', { exact: true }).fill(futureTask);
    await schedules.getByRole('button', { name: 'Save schedule', exact: true }).click();
    await expect(schedules.getByLabel('Schedule task', { exact: true })).toBeHidden();
    await expect(schedules.getByText(new RegExp(`Chat user request.*${chatId}`))).toBeVisible();
    await schedules.getByRole('button', { name: 'Cancel schedule', exact: true }).click();
    await page.getByRole('dialog', { name: 'Cancel this schedule?', exact: true }).getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeHidden();
    await expect(schedules.getByRole('button', { name: 'Cancel schedule', exact: true })).toBeFocused();
    await schedules.getByRole('button', { name: 'Cancel schedule', exact: true }).click();
    await page.getByRole('dialog', { name: 'Cancel this schedule?', exact: true }).getByRole('button', { name: 'Cancel schedule', exact: true }).click();
    await expect(schedules.getByText('Cancelled', { exact: true })).toBeVisible();
    await expect(schedules.getByText('Next: Not scheduled', { exact: true })).toHaveCount(2);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);

    const initiative = page.getByRole('region', { name: 'Initiative', exact: true });
    const prompt = 'Review current secretary responsibilities and relevant Memory and Experience. Do nothing when there is no meaningful action.';
    await initiative.getByLabel('Thinking prompt', { exact: true }).fill(prompt);
    await initiative.getByRole('switch', { name: 'Enable thought activation', exact: true }).click();
    await initiative.getByRole('button', { name: 'Create thought', exact: true }).click();
    await expect(initiative.getByText('Every 1 hour', { exact: true })).toBeVisible();
    await initiative.getByText(prompt, { exact: true }).click();
    await initiative.getByRole('button', { name: 'Run now', exact: true }).click();
    await expect(initiative.getByText('NoAction', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await initiative.getByRole('button', { name: 'Edit thought', exact: true }).click();
    await initiative.getByLabel('Thinking prompt', { exact: true }).fill('synthetic-thought-attention: review an unresolved Atlas checkpoint.');
    await initiative.getByRole('button', { name: 'Save thought', exact: true }).click();
    await expect(initiative.getByRole('button', { name: 'Save thought', exact: true })).toBeHidden();
    await initiative.getByRole('button', { name: 'Run now', exact: true }).click();
    await expect(initiative.getByText('AttentionRequested', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await initiative.getByRole('button', { name: 'Inspect execution', exact: true }).click();
    await expect(work.getByText('Thought activation', { exact: true })).toHaveCount(2);
    await expect(work.getByText('Needs attention', { exact: true })).toBeVisible();
    await work.getByRole('button', { name: 'Close', exact: true }).click();
    await page.reload();
    await page.getByRole('tab', { name: 'Behavior & continuity', exact: true }).click();
    await expect(initiative.getByText('AttentionRequested', { exact: true }).first()).toBeVisible();
    const after = (await (await page.request.get(path, { headers: await headers(page) })).json()).items;
    const adminRow = after.find((row: { registrationId: string }) => row.registrationId === before.registrationId);
    expect(adminRow.authorizationOrigin).toBe('AdminOwner'); expect(adminRow.status).toBe('Disabled');
    expect(adminRow.schedule.maxOccurrences).toBe(5); expect(adminRow.lastWorkItemId).toBeTruthy();
    const chatRow = after.find((row: { sourceSessionId: string }) => row.sourceSessionId === chatId);
    expect(chatRow.authorizationOrigin).toBe('CurrentUserTurn'); expect(chatRow.status).toBe('Cancelled'); expect(chatRow.intent).toBe(futureTask);
  });

  test('uses the configured browser for observed record lookup and keeps progress separate from the answer', async ({ page }) => {
    test.setTimeout(150_000);
    await newChat(page);
    await send(page, 'Please look up record AC-1042.', 'AC-1042 is In review.');
    await expect(page.locator('.chat-message-application')).toHaveCount(1);
    await expect(page.locator('.chat-message-application')).toContainText("I found the record. I'm checking the details now.");
    await expect(page.locator('.agent-activity')).toHaveCount(0);
    await page.reload();
    await expect(page.locator('.assistant-body').last()).toHaveText('AC-1042 is In review.');
    await expect(page.locator('.chat-message-application')).toHaveCount(1);
    await endChat(page);
  });
});
