import { expect, test } from '@playwright/test';

test('Mixed Event rows preserve drafts, samples and stable identities through nested management and reload', async ({ page }) => {
  const errors: string[]=[]; page.on('pageerror', e=>errors.push(e.message));
  await page.goto('/admin/instances');
  await expect.poll(()=>page.evaluate(()=>localStorage.getItem('agent-core.owner-capability'))).not.toBeNull();
  const headers={'X-AgentCore-Owner-Capability':(await page.evaluate(()=>localStorage.getItem('agent-core.owner-capability')))!};
  const instance=await page.request.post('/api/v2/admin/agent-instances',{headers,data:{definitionId:'secretary',version:8}}); expect(instance.ok()).toBe(true);
  const instanceId=(await instance.json()).instanceId;
  await page.goto(`/admin/instances/${instanceId}/automation/automations`);
  await page.getByRole('button',{name:'New automation',exact:true}).click();
  const drawer=page.getByRole('dialog',{name:'New automation',exact:true});
  await expect(drawer.getByRole('switch',{name:'Enable automation'})).not.toBeChecked();
  await drawer.getByLabel('Automation name').fill('Mixed independently'); await drawer.getByLabel('Automation instructions').fill('Review each source independently.');
  await drawer.getByLabel('Schedule local time').fill('14:30');
  async function mode(name:string){await drawer.getByRole('combobox',{name:'Automation trigger'}).click();await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({hasText:new RegExp('^'+name+'$')}).click();}
  async function add(name:string){await drawer.getByRole('button',{name:'Add Event',exact:true}).click(); await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({hasText:new RegExp('^'+name+' · '+name+'$')}).click();}
  await mode('Events'); await add('run.failed');
  const first=drawer.getByRole('region',{name:'Event subscription 1',exact:true});
  await first.getByLabel('Event filter expression').fill('event.data.activationKind === "UserTurn"');
  const fixture='{"schemaVersion":1,"data":{"activationKind":"UserTurn","fixture":"retained"}}';
  await first.getByLabel('Sample event JSON').fill(fixture); await first.getByRole('button',{name:'Test filter',exact:true}).click(); await expect(first.getByRole('status')).toContainText('Matched');
  await add('session.ended');
  await mode('Schedule'); await expect(drawer.getByLabel('Schedule local time')).toHaveValue('14:30'); await mode('Events');
  await drawer.getByRole('button',{name:/^run.failed Built-in/}).click(); await expect(first.getByLabel('Sample event JSON')).toHaveValue(fixture);
  const managerButton=first.getByRole('button',{name:'Create or manage Events',exact:true}); await managerButton.click();
  const manager=page.getByRole('dialog',{name:'Global Events',exact:true}); await manager.getByRole('button',{name:'New webhook Event',exact:true}).click();
  const sourceEditor=page.getByRole('dialog',{name:'New webhook Event',exact:true}); const eventName='Mixed orders '+Date.now();
  await sourceEditor.getByLabel('Event name').fill(eventName); await sourceEditor.getByLabel('Event key').fill('mixed.'+Date.now());
  await sourceEditor.getByRole('button',{name:'Create Event',exact:true}).click(); await page.getByRole('dialog',{name:'Copy this credential',exact:true}).getByRole('button',{name:'Done',exact:true}).click();
  await manager.getByRole('button',{name:'Close',exact:true}).first().click();
  await expect(drawer).toContainText('session.ended'); await expect(drawer).toContainText(eventName); await expect(drawer.getByLabel('Automation name')).toHaveValue('Mixed independently');
  await expect(drawer.getByLabel('Automation instructions')).toHaveValue('Review each source independently.');
  const response=page.waitForResponse(r=>r.request().method()==='POST' && r.url().endsWith('/automations'));
  await drawer.getByRole('button',{name:'Create automation',exact:true}).click();const saved=await(await response).json(); expect(saved.triggers).toHaveLength(2); expect(saved.enabled).toBe(false); expect(saved.triggers.some((t:{source:{kind:string}})=>t.source.kind==='webhook')).toBe(true);
  await expect(drawer).toBeHidden(); await page.reload(); await page.getByRole('button',{name:'View automation: Mixed independently',exact:true}).click(); await page.getByRole('button',{name:'Edit automation',exact:true}).click();
  const edit=page.getByRole('dialog',{name:'Edit automation',exact:true}); await expect(edit).toContainText('session.ended'); await expect(edit).toContainText(eventName);
  const listed=(await(await page.request.get(`/api/v2/admin/agent-instances/${instanceId}/automations`,{headers})).json()).items[0]; expect(listed.triggers.map((t:{triggerId:string})=>t.triggerId).sort()).toEqual(saved.triggers.map((t:{triggerId:string})=>t.triggerId).sort());
  expect(errors).toEqual([]);
});
