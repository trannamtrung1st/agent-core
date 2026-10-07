import { expect, test, type Page } from '@playwright/test';

async function select(page: Page, label: string, text: string) {
  await page.getByRole('combobox', {name:label, exact:true}).click();
  if (label === 'Identity' || label === 'Published version' || label === 'Definition') await page.getByRole('combobox', {name:label, exact:true}).fill(text);
  await page.locator('.ant-select-item-option').filter({hasText:text}).last().click();
}
async function create(page: Page, name: string, mode = 'Managed', scope = 'Knowledge & resources') {
  await page.goto('/');
  await page.getByRole('button',{name:'Open Admin'}).click();
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole('button',{name:'New instance',exact:true}).click();
  await select(page,'Definition','General Assistant · general-assistant');
  await select(page,'Published version','v16 · Built-in · Published');
  const d=page.getByRole('dialog',{name:'New instance',exact:true});
  await d.getByText('Custom persona',{exact:true}).click();
  await d.getByLabel('Persona name').fill(name);await d.getByLabel('Persona role').fill('Operations');
  await d.getByLabel('Persona description').fill('Review orders safely.');await d.getByLabel('Persona tone').fill('Clear');
  await d.getByText('Harness management (optional)',{exact:true}).click();
  await select(page,'Authoring mode',mode);
  if(mode !== 'Manual (off)') {
    for(const label of ['Knowledge & resources','Skills']) await d.getByLabel(label,{exact:true}).uncheck();
    await d.getByLabel(scope,{exact:true}).check();
  }
  await d.getByRole('button',{name:'Create instance',exact:true}).click();
  await expect(page.getByRole('heading',{name,exact:true})).toBeVisible();
  const id=new URL(page.url()).pathname.split('/')[3];
  await page.locator('.admin-header').getByRole('button',{name:/Chat$/}).first().click();
  await select(page,'Identity',name);
  return id;
}
async function send(page: Page, message: string) {
  await page.getByLabel('Message',{exact:true}).fill(message);
  await page.getByRole('button',{name:'Send',exact:true}).click();
}
async function review(page: Page, id: string) {
  const owner=await page.evaluate(()=>localStorage.getItem('agent-core.owner-capability'));
  const response=await page.request.get(`/api/v2/admin/agent-instances/${id}/harness`,{headers:{'X-AgentCore-Owner-Capability':owner!}});
  expect(response.ok()).toBe(true);return response.json();
}
async function fresh(page: Page, name: string) {
  if (!(await page.getByRole('button',{name:'Start a new chat'}).isVisible())) await page.getByRole('button',{name:'Open chats'}).click();
  await page.getByRole('button',{name:'Start a new chat'}).click();
  await select(page,'Identity',name);
}
const learn='Learn this order policy for future conversations: https://example.test/p97/order-policy';

test('Managed Chat reads authorized public material, saves knowledge and retrieves it in a new Session',async({page})=>{
  const id=await create(page,'Managed learning');
  await send(page,learn);
  await expect(page.getByText(/Saved that for future conversations/).last()).toBeVisible({timeout:30000});
  const saved=await review(page,id);expect(saved.activeVersion).toBeGreaterThan(16);
  expect(saved.policy.sources).toEqual([]);expect(saved.knowledge.some((k:{identity:string})=>k.identity==='learned-orders')).toBe(true);
  const sessionId=page.url().split('/').at(-1)!;
  const owner=await page.evaluate(()=>localStorage.getItem('agent-core.owner-capability'));
  const currentResponse=await page.request.get(`/api/v2/sessions/${sessionId}`,{headers:{'X-AgentCore-Owner-Capability':owner!}});
  expect(currentResponse.ok()).toBe(true);const current=await currentResponse.json();expect(current.agentVersion).toBe(16);
  await fresh(page,'Managed learning');await send(page,'What is the learned order policy?');
  await expect(page.getByText('The saved policy says to check payment, shipping and fraud notes.',{exact:true})).toBeVisible();
});

test('Managed Chat learns a Skill and normal skills.load activates it in a new Session',async({page})=>{
  const id=await create(page,'Skill learning','Managed','Skills');
  await send(page,'Learn this order-review procedure for future conversations: Check payment, then shipping, then fraud notes.');
  await expect(page.getByText(/Saved that for future conversations/).last()).toBeVisible({timeout:30000});
  expect((await review(page,id)).skills.some((s:{id:string})=>s.id==='order-review')).toBe(true);
  await fresh(page,'Skill learning');await send(page,'Use the learned order-review procedure.');
  await expect(page.getByText('Using the learned procedure: check payment, then shipping, then fraud notes. Stop before production actions.',{exact:true})).toBeVisible();
});

test('Assisted Chat rejects once then approves a fresh exact knowledge change',async({page})=>{
  const id=await create(page,'Assisted learning','Assisted');
  await send(page,learn);const approval=page.getByRole('dialog',{name:'Save this harness change?'});
  await expect(approval).toContainText('Check payment, shipping and fraud notes');
  await approval.getByRole('button',{name:'Reject',exact:true}).click();
  await expect(page.getByText(/Nothing was saved/).last()).toBeVisible();expect((await review(page,id)).activeVersion).toBe(16);
  await send(page,learn);await expect(approval).toBeVisible();await approval.getByRole('button',{name:'Approve',exact:true}).click();
  await expect(page.getByText(/Saved that for future conversations/).last()).toBeVisible();expect((await review(page,id)).activeVersion).toBeGreaterThan(16);
});

test('Managed tool and instruction proposals require exact Chat approval',async({page})=>{
  test.setTimeout(90_000);
  const id=await create(page,'Tool learning','Managed','Tool proposals');
  await send(page,'Propose disabling http.request for future conversations.');
  const approval=page.getByRole('dialog',{name:'Save this harness change?'});await expect(approval).toContainText('http.request');
  expect((await review(page,id)).activeVersion).toBe(16);await approval.getByRole('button',{name:'Approve',exact:true}).click();
  await expect(page.getByText(/Saved that for future conversations/).last()).toBeVisible();expect((await review(page,id)).selectedTools).not.toContain('http.request');
  await send(page,'Try a sensitive HTTP action now.');
  const sensitive=page.getByRole('dialog',{name:'Approve sensitive action'});await expect(sensitive).toBeVisible();
  await sensitive.getByRole('button',{name:'Reject',exact:true}).click();
  await expect(sensitive).toBeHidden({timeout:20_000});
  await expect(page.getByText('The sensitive action was not executed.',{exact:true})).toBeVisible({timeout:30_000});
  await expect(page.getByTestId('connection')).toHaveText('Ready',{timeout:20_000});
});

test('Freeze blocks durable Chat learning while normal Chat still works',async({page})=>{
  const id=await create(page,'Frozen learning');
  await page.getByRole('button',{name:'Open Admin'}).click();await page.goto(`/admin/instances/${id}`);
  await page.getByRole('tab', { name: 'Automation', exact: true }).click();
  await page.getByRole('tab', { name: 'Policies & models', exact: true }).click();
  await page.getByRole('button',{name:'Freeze self-management',exact:true}).click();
  await page.getByRole('dialog').getByRole('button',{name:'Freeze self-management',exact:true}).click();
  await expect(page.getByText('Frozen',{exact:true})).toBeVisible();await page.locator('.admin-header').getByRole('button',{name:/Chat$/}).first().click();await select(page,'Identity','Frozen learning');
  await send(page,learn);await expect(page.getByText(/cannot save a durable harness change/).last()).toBeVisible();
  expect((await review(page,id)).activeVersion).toBe(16);await send(page,'Hello');await expect(page.getByText('Hello from synthetic.',{exact:true}).last()).toBeVisible();
});


test('Managed instruction changes still require approval in Chat',async({page})=>{
  const id=await create(page,'Instruction learning','Managed','Operating instructions');
  await send(page,'Save these operating instructions for future conversations: Use concise summaries.');
  const approval=page.getByRole('dialog',{name:'Save this harness change?'});await expect(approval).toContainText('Use concise order-review summaries');
  expect((await review(page,id)).activeVersion).toBe(16);await approval.getByRole('button',{name:'Reject',exact:true}).click();
  await expect(page.getByText(/Nothing was saved/).last()).toBeVisible();expect((await review(page,id)).activeVersion).toBe(16);
});

test('A stale Chat approval cannot adopt and a fresh operation recovers', async ({ page }) => {
  // Both approval rounds must have room for their bounded completion waits.
  test.setTimeout(90_000);
  const id = await create(page, 'Conflict learning', 'Assisted');
  await send(page, learn);
  const approval = page.getByRole('dialog', { name: 'Save this harness change?' });
  await expect(approval).toBeVisible({ timeout: 30_000 });
  const policyRevision = approval.getByRole('listitem').filter({ hasText: 'Policy revision' });
  const before = await review(page, id);
  await expect(policyRevision).toHaveText(`Policy revision: ${before.policyRevision}`);
  const owner = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const changed = await page.request.put(`/api/v2/admin/agent-instances/${id}/harness/policy`, {
    headers: { 'X-AgentCore-Owner-Capability': owner! },
    data: { expectedRevision: before.instanceRevision, ...before.policy }
  });
  expect(changed.ok()).toBe(true);
  const refreshed = await review(page, id);
  expect(refreshed.policyRevision).toBeGreaterThan(before.policyRevision);
  await approval.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(approval).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText(/Nothing was saved/).last()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('connection')).toHaveText('Ready', { timeout: 20_000 });
  expect((await review(page, id)).activeVersion).toBe(16);

  await send(page, learn);
  await expect(approval).toBeVisible({ timeout: 30_000 });
  await expect(policyRevision).toHaveText(`Policy revision: ${refreshed.policyRevision}`);
  await approval.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(approval).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText(/Saved that for future conversations/).last()).toBeVisible({ timeout: 30_000 });
  expect((await review(page, id)).activeVersion).toBeGreaterThan(16);
});
