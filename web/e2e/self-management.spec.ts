import { expect, test, type Page } from '@playwright/test';
async function select(page: Page, name: string, text: string, search = false) {
  const field = page.getByRole('combobox', {name,exact:true}); await field.click();
  if (search) await field.fill(text);
  await page.locator('.ant-select-item-option').filter({hasText:text}).last().click();
}
async function send(page: Page, message: string) {
  await page.getByLabel('Message',{exact:true}).fill(message); await page.getByRole('button',{name:'Send',exact:true}).click();
}
test('Admin enable, bounded save, exact rejection and approval, next Run readback, freeze and Admin unfreeze', async({page})=>{
  test.setTimeout(120000);
  const name = 'Self management UAT';
  await page.goto('/');await page.getByRole('button',{name:'Open Admin'}).click();
  await page.getByRole('tab',{name:'Instances',exact:true}).click();await page.getByRole('button',{name:'New instance',exact:true}).click();
  await select(page,'Definition','General Assistant · general-assistant',true);
  await select(page,'Published version','v23 · Built-in · Published',true);
  const create=page.getByRole('dialog',{name:'New instance',exact:true});
  await create.getByText('Custom persona',{exact:true}).click();
  for(const [label,value] of [['Persona name',name],['Persona role','Operations'],['Persona description','Verify owned configuration.'],['Persona tone','Clear']])await create.getByLabel(label).fill(value);
  await create.getByRole('button',{name:'Create instance',exact:true}).click();await expect(page.getByRole('heading',{name,exact:true})).toBeVisible();
  const id=new URL(page.url()).pathname.split('/')[3];
  await page.goto('/admin/instances/'+id+'/identity/settings');
  const panel=page.getByRole('region',{name:'Agent self-management',exact:true});
  await expect(panel).toContainText('Definition defaults');
  await select(page,'Self-management mode','Managed');
  await panel.getByRole('button',{name:'Customize areas'}).click();
  for(const area of ['Settings','Skills','Resources'])await panel.getByRole('checkbox',{name:area,exact:true}).check();
  await panel.getByRole('button',{name:'Customize automatic changes'}).click();
  await panel.getByRole('checkbox',{name:'Conversation preferences',exact:true}).check();
  await panel.getByRole('button',{name:'Save self-management'}).click();await expect(panel).toContainText('policy revision 2');
  await page.locator('.admin-header').getByRole('button',{name:/Chat$/}).first().click();await select(page,'Identity',name,true);
  await send(page,'Always answer briefly.');await expect(page.getByText('Saved for my next Run. The current Run remains unchanged.',{exact:true}).last()).toBeVisible();
  const chat=page.url();
  await send(page,'Turn on user memory retrieval.');
  const approval=page.getByRole('dialog',{name:'Apply configuration to this Instance?',exact:true});
  await expect(approval).toContainText('userRetrieval');await expect(approval).toContainText('Next new Run');
  await page.keyboard.press('Escape');await expect(approval).toBeHidden();
  await expect(page.getByText('Nothing was saved. Inspect the current revision and policy before retrying.',{exact:true}).last()).toBeVisible();
  await send(page,'Turn on user memory retrieval.');await expect(approval).toBeVisible();
  await approval.getByRole('button',{name:'Apply to this Instance',exact:true}).click();await expect(approval).toBeHidden();
  await expect(page.getByText('Saved for my next Run. The current Run remains unchanged.',{exact:true}).last()).toBeVisible();
  await send(page,'Verify my saved configuration.');await expect(page.getByText('Current response length: concise. Configuration applies to this fresh Run.',{exact:true})).toBeVisible();
  await send(page,'Freeze self-management.');
  const freeze=page.getByRole('dialog',{name:'Freeze self-management?',exact:true});await expect(freeze).toContainText('Immediate management revocation');
  await freeze.getByRole('button',{name:'Freeze self-management',exact:true}).click();await expect(page.getByText('Self-management frozen. Normal tasks remain available.',{exact:true})).toBeVisible();
  await send(page,'Hello');await expect(page.getByText('Hello from synthetic.',{exact:true})).toBeVisible();
  await send(page,'Inspect my configuration.');await expect(page.getByText('Self-management is unavailable under the current policy. Use Admin to enable it.',{exact:true})).toBeVisible();
  await page.goto('/admin/instances/'+id+'/identity/settings');await expect(panel).toContainText('Self-management is frozen');
  await panel.getByRole('button',{name:'Unfreeze self-management',exact:true}).click();await page.getByRole('dialog').getByRole('button',{name:'Unfreeze',exact:true}).click();
  await expect(panel.getByText('Self-management is frozen',{exact:true})).toHaveCount(0);
  for(const [width,height] of [[1440,900],[768,900],[390,844]]) {await page.setViewportSize({width,height});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);}
  await page.goto(chat);await send(page,'Verify my saved configuration.');await expect(page.getByText('Current response length: concise. Configuration applies to this fresh Run.',{exact:true}).last()).toBeVisible();
});
