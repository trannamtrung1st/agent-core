import { expect, test, type Page } from '@playwright/test';
import { completeDefinitionDraftPublishGate, publishDraftFromInstructions } from './admin-definition-gate-helpers';
import { draftEditorSection } from './admin-draft-editor-helpers';
import { selectManagedIdentityOption } from './admin-managed-helpers';
import { waitForResponseSettled } from './support/response-settled';

const knownAntDMigrationNotices = new Set([
  "Warning: [antd: List] The `List` component is deprecated and will be removed in the next major version. If you're using version 6.6.0 or later, please use `Listy` instead.",
  'Warning: [antd: Alert] `message` is deprecated. Please use `title` instead.'
]);

// Synthetic proves the real runtime and durable owner flows. It does not prove
// hosted-model judgement, natural-language memory admission, or lesson fidelity.
test.describe('Morgan secretary Synthetic journey', () => {
  test.describe.configure({ mode: 'serial' });
  test.use({ actionTimeout: 15_000 });
  const definitionId = `executive-secretary-${Date.now()}`;
  let instanceId: string;
  let sourceSessionId: string;

  async function openInstance(page: Page, automation = false) {
    await page.goto(`/admin/instances/${instanceId}`);
    await expect(page.getByRole('heading', { name: 'Morgan', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: automation ? 'Automation' : 'Continuity', exact: true }).click();
    if (!automation) await page.getByRole('tab', { name: 'Experience', exact: true }).click();
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
    await expect(page.locator('.chat-message-assistant')).toHaveCount(before + 1, { timeout: 30_000 });
    await expect(page.locator('.chat-message-assistant .assistant-body').last()).toHaveText(answer, { timeout: 30_000 });
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
      if (message.type() === 'error' && !knownAntDMigrationNotices.has(message.text())) failures.push(message.text());
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
    for (const tool of ['knowledge.retrieve', 'automation.create', 'automation.inspect', 'automation.disable', 'automation.run', 'automation.list', 'automation.update', 'automation.delete',
      'browser.navigate', 'browser.snapshot', 'browser.click', 'browser.type']) {
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
    await page.getByRole('tab', { name: 'Instances', exact: true }).click();
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
    instanceId = new URL(page.url()).pathname.split('/')[3];
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Morgan', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: 'Continuity', exact: true }).click();
    await page.getByRole('tab', { name: 'Experience', exact: true }).click();
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
    await expect(experience.getByText(/No experience yet/)).toBeVisible();
    await experience.getByRole("button", { name: "Enter Session ID", exact: true }).click();
    await experience.getByLabel('Session ID', { exact: true }).fill(sourceSessionId);
    const retrospect = () => Promise.all([
      page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/experience/checkpoints') && r.ok()),
      experience.getByRole('button', { name: 'Retrospect now', exact: true }).click()
    ]);
    await retrospect();
    await expect(experience.getByRole('button', { name: 'Retrospect now', exact: true })).toBeEnabled();
    await retrospect();
    await expect(experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true })).toHaveCount(1);
    await experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true }).click();
    await expect(experience.getByText('The user supplied a correction', { exact: true })).toBeVisible();
    await expect(experience.getByRole('region', { name: 'Experience details', exact: true }).getByText(sourceSessionId, { exact: true })).toBeVisible();
    await newChat(page);
    await send(page, 'Use my recent experience before acting.', 'I will observe current page state before acting, based on earlier experience. Current policy still controls every action.');
    expect(page.url()).not.toContain(sourceSessionId);
    await endChat(page);
    await openInstance(page);
    await experience.getByRole('button', { name: 'View experience: Review observable completed work', exact: true }).first().click();
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

  test('Admin and Chat share Automations, provenance, quiet/attention outcomes and mobile controls', async ({ page }) => {
    test.setTimeout(150_000);
    await openInstance(page, true);
    const automations = page.getByRole('region', { name: 'Automations', exact: true });
    await automations.getByRole('button', { name: 'New automation', exact: true }).click();
    await automations.getByLabel('Automation name', { exact: true }).fill('Atlas follow-up review');
    await automations.getByLabel('Automation instructions', { exact: true }).fill('Review Atlas follow-up obligations; do nothing when nothing needs action.');
    await automations.getByLabel('Schedule maximum occurrences', { exact: true }).fill('5');
    await automations.getByRole('button', { name: 'Create automation', exact: true }).click();
    const source = automations.getByRole('button', { name: 'View automation: Atlas follow-up review', exact: true });
    await expect(source).toBeVisible();
    if (await source.getAttribute('aria-expanded') !== 'true') await source.click();
    await expect(automations.getByRole('region', { name: 'Automation details', exact: true }).getByText('Admin owner', { exact: true })).toBeVisible();
    await automations.getByRole('button', { name: 'Run automation now', exact: true }).click();
    await expect(automations.getByText(/Completed · No action/).first()).toBeVisible({ timeout: 30_000 });
    await automations.getByRole('button', { name: 'View last run: Atlas follow-up review', exact: true }).click();
    const work = page.getByRole('dialog', { name: 'Run details', exact: true });
    await expect(work.locator('.agent-run-details').getByText('No action', { exact: true })).toBeVisible();
    await work.getByRole('button', { name: 'View Automation', exact: true }).click();
    await automations.getByRole('button', { name: 'Disable automation', exact: true }).click();
    const ownerHeaders = await headers(page);
    const profile = await (await page.request.get('/api/v2/profile', { headers: ownerHeaders })).json();
    expect((await page.request.patch('/api/v2/profile', { headers: ownerHeaders, data: {
      expectedRevision: profile.revision, values: { timeZone: 'Asia/Ho_Chi_Minh' }
    } })).ok()).toBe(true);
    await newChat(page);
    await send(page, 'remind me tomorrow', 'Scheduled Call John.');
    const chatId = page.url().split('/').at(-1)!;
    await page.getByRole('button', { name: 'Automations', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Automations', exact: true });
    await expect(drawer.getByText('Call John', { exact: true })).toBeVisible();
    await expect(drawer.getByText('Disabled', { exact: true })).toBeVisible();
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await endChat(page);
    await openInstance(page, true);
    await automations.getByRole('button', { name: 'View automation: Call John', exact: true }).click();
    await expect(automations.getByText(new RegExp(`Chat user request.*${chatId}`))).toBeVisible();
    await automations.getByRole('button', { name: 'Edit automation', exact: true }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await automations.getByLabel('Automation instructions', { exact: true }).fill('synthetic-automation-attention: review unresolved Atlas decisions.');
    await automations.getByRole('button', { name: 'Save automation', exact: true }).click();
    await automations.getByRole('button', { name: 'Run automation now', exact: true }).click();
    await expect(automations.getByText(/Completed · Needs attention/).first()).toBeVisible({ timeout: 30_000 });
    await automations.getByRole('button', { name: 'View last run: Call John', exact: true }).click();
    await expect(work.getByRole('region', { name: 'Needs attention', exact: true })).toContainText("An unresolved checkpoint needs the owner's attention.");
    await work.getByRole('button', { name: 'View Automation', exact: true }).click();
    await automations.getByRole('button', { name: 'Delete automation', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete automation', exact: true }).click();
    await expect(automations.getByRole('button', { name: 'View automation: Call John', exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    const rows = (await (await page.request.get(`/api/v2/admin/agent-instances/${instanceId}/automations`, { headers: ownerHeaders })).json()).items;
    expect(rows[0].authorizationOrigin).toBe('AdminOwner'); expect(rows[0].status).toBe('Disabled');
    expect(rows[0].trigger.schedule.maxOccurrences).toBe(5); expect(rows[0].lastAgentRunId).toBeTruthy();
  });

  test('uses the configured browser for observed record lookup and keeps progress separate from the answer', async ({ page }) => {
    test.setTimeout(150_000);
    await newChat(page);
    await send(page, 'Please look up record AC-1042.', 'AC-1042 is In review.');
    await expect(page.locator('.chat-message-application')).toHaveCount(1);
    await expect(page.locator('.chat-message-application')).toContainText("I found the record. I'm checking the details now.");
    await expect(page.locator('.agent-activity')).toHaveCount(0);
    await page.reload();
    await expect(page.locator('.chat-message-assistant .assistant-body').last()).toHaveText('AC-1042 is In review.');
    await expect(page.locator('.chat-message-application')).toHaveCount(1);
    await endChat(page);
  });
});
