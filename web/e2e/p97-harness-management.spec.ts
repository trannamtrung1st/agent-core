import { expect, test, type Page } from "@playwright/test";

async function select(page: Page, label: string, text: string) {
  await page.getByLabel(label, { exact: true }).click();
  await page.locator('.ant-select-item-option').filter({ hasText: text }).last().click();
}
async function createManaged(page: Page) {
  await page.goto('/');
  await page.getByLabel('Message', {exact:true}).fill('Hello');
  await page.getByRole('button', {name:'Send',exact:true}).click();
  await expect(page.getByText('Hello from synthetic.', {exact:true}).last()).toBeVisible({timeout:15000});
  await page.getByRole('button', {name:'Open Admin'}).click();
  await page.getByRole('button', {name:'New instance',exact:true}).click();
  await select(page, 'Definition', 'General Assistant · general-assistant');
  await select(page, 'Published version', 'v7 · Built-in · Published');
  const dialog = page.getByRole('dialog', {name:'New instance',exact:true});
  await dialog.getByText('Custom persona',{exact:true}).click();
  await dialog.getByLabel('Persona name').fill('P97 Store Operations Assistant');
  await dialog.getByLabel('Persona role').fill('Store operations');
  await dialog.getByLabel('Persona description').fill('Review orders and flag owner attention.');
  await dialog.getByLabel('Persona tone').fill('Clear');
  await dialog.getByText('Harness management (optional)',{exact:true}).click();
  await select(page, 'Authoring mode', 'Managed');
  await dialog.getByRole('button', {name:'Create instance',exact:true}).click();
  await expect(page.getByRole('heading', {name:'P97 Store Operations Assistant',exact:true})).toBeVisible();
  const section = page.locator('section[aria-label="Harness management"]');
  await expect(section.getByText('Managed',{exact:true})).toBeVisible();
  await section.getByText('Configure authoring policy',{exact:true}).click();
  await section.getByLabel('Tool proposals',{exact:true}).check();
  await section.getByLabel('Permitted sources',{exact:true}).fill('knowledge:support-order-policy');
  await section.getByLabel('Eligible tools',{exact:true}).fill('web.fetch');
  await page.locator('.ant-select-item-option').filter({hasText:'web.fetch'}).last().click();
  await section.getByLabel('Eligible tools',{exact:true}).press('Escape');
  await section.getByRole('button',{name:'Save authoring policy'}).click();
  await section.getByLabel('Preparation purpose').fill('Prepare knowledge and a safe order-review procedure for store operations.');
  await section.getByRole('button',{name:'Prepare harness',exact:true}).click();
  await expect(section.getByText('Awaiting approval',{exact:true})).toBeVisible({timeout:30000});
  return section;
}
async function approveAndVerify(page: Page) {
  const section = page.locator('section[aria-label="Harness management"]');
  await expect(section.getByRole('button',{name:'Publish & adopt',exact:true})).toBeDisabled();
  await section.getByRole('button',{name:'Review & approve change'}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Approve change',exact:true}).click();
  await section.getByRole('button',{name:'Continue preparation',exact:true}).click();
  await expect(section.getByText('Ready for review',{exact:true})).toBeVisible({timeout:30000});
  await section.getByText(/Verification & limitations/).click();
  await expect(section.getByText('Candidate knowledge readback',{exact:true})).toBeVisible();
  await expect(section.getByText('Partially verified',{exact:true})).toBeVisible();
  await expect(section.getByText(/Production refunds and customer messages require external owner evidence/)).toBeVisible();
  return section;
}

test('P9.7 owner configures, prepares, verifies, approves, publishes, adopts and freezes', async ({ page }, info) => {
  const errors: string[] = [];
  const failures: string[] = [];
  page.on('console', message => { if(message.type()==='error' && !message.text().includes('[antd:')) errors.push(message.text()); });
  page.on('requestfailed', request => failures.push(request.url()));
  const section = await createManaged(page);
  await expect(section.getByText('Active version 7',{exact:true})).toBeVisible();
  await approveAndVerify(page);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({width, height:900});
    await expect(section.getByRole('button',{name:'Publish & adopt',exact:true})).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.evaluate(async () => { await Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => {}))); });
    await page.screenshot({path: info.outputPath(`p97-review-${width}.png`), fullPage:true});
  }
  await section.getByRole('button',{name:'Publish & adopt',exact:true}).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toBeHidden();
  await section.getByRole('button',{name:'Publish & adopt',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Publish & adopt',exact:true}).click();
  await expect(section.getByText('Published & adopted',{exact:true})).toBeVisible({timeout:15000});
  await expect(section.getByText('Active version 7',{exact:true})).toBeHidden();
  await section.getByRole('button',{name:'Freeze self-management',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Freeze self-management',exact:true}).click();
  await expect(section.getByText('Frozen',{exact:true})).toBeVisible();
  await expect(section.getByText('Published & adopted',{exact:true})).toBeVisible();
  await expect(section.getByRole('button',{name:'Prepare harness',exact:true})).toHaveCount(0);
  await section.getByText(/Verification & limitations/).click();
  await expect(section.getByText('Partially verified',{exact:true})).toBeVisible();
  await page.screenshot({path:info.outputPath('p97-frozen-mobile.png'),fullPage:true});
  expect(errors).toEqual([]); expect(failures).toEqual([]);
});

test('P9.7 stale review cannot promote and can recover with fresh evidence', async ({page}) => {
  const section = await createManaged(page);
  await approveAndVerify(page);
  const id = page.url().split('/').at(-1)!;
  const owner = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': owner! };
  // Concurrent owner edit exercises the real draft CAS path; it does not touch the active version.
  const reviewResponse = await page.request.get(`/api/v2/admin/agent-instances/${id}/harness`, {headers});
  expect(reviewResponse.ok()).toBe(true);
  const review = await reviewResponse.json();
  const draftResponse = await page.request.get(`/api/v2/admin/definition-drafts/${review.preparation.draftId}`, {headers});
  const draft = await draftResponse.json();
  const edited = await page.request.put(`/api/v2/admin/definition-drafts/${draft.draftId}`, {headers,
    data: { expectedRevision: draft.revision, candidate: {...draft.candidate, systemInstructions: draft.candidate.systemInstructions + '\nUse concise operations summaries.'} } });
  expect(edited.ok()).toBe(true);
  await section.getByRole('button',{name:'Publish & adopt',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Publish & adopt',exact:true}).click();
  await expect(section.getByRole('alert').filter({hasText:/stale|revision|candidate/i})).toContainText(/stale|revision|candidate/i);
  await expect(section.getByText('Active version 7',{exact:true})).toBeVisible();
  await expect(section.getByRole('button',{name:'Publish & adopt',exact:true})).toBeDisabled();
  await section.getByRole('button',{name:'Continue preparation',exact:true}).click();
  await expect(section.getByRole('button',{name:'Publish & adopt',exact:true})).toBeEnabled({timeout:30000});
  await expect(section.getByText('Active version 7',{exact:true})).toBeVisible();
  await section.getByRole('button',{name:'Cancel preparation',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Cancel preparation',exact:true}).click();
  await expect(section.getByText('Cancelled',{exact:true})).toBeVisible();
});
