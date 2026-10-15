import {expect,test} from '@playwright/test';

// A disposable owner publishes an explicit policy for these two private fixtures first.
// No credentials, catalog entry or authority is synthesized by the application.
test('two private OCI choices use distinct owned generations and retain Office delivery',async({page},info)=>{
  test.skip(!process.env.AGENTCORE_OCI_DEFINITION_VERSION,'Requires explicit disposable registry/catalog/Definition setup.');
  await page.goto('/admin/platform/sandboxes');
  await page.waitForFunction(()=>localStorage.getItem('agent-core.owner-capability'));
  const headers={'X-AgentCore-Owner-Capability':(await page.evaluate(()=>localStorage.getItem('agent-core.owner-capability')))!};
  const environments=[];
  for(const [id,label] of [['private-workbench','Private workbench'],['private-alternate','Private alternate']]) {
    const instance=await page.request.post('/api/v2/admin/agent-instances',{headers,data:{definitionId:'general-assistant',version:Number(process.env.AGENTCORE_OCI_DEFINITION_VERSION)}});
    expect(instance.ok(),await instance.text()).toBe(true);const instanceId=(await instance.json()).instanceId;
    await page.goto(`/admin/instances/${instanceId}/sandbox`);
    const selector=page.getByRole('combobox',{name:'Sandbox profile',exact:true});await expect(selector).toBeEnabled();await selector.click();
    await page.locator('.ant-select-dropdown').getByText(label+' · Verified · eligible',{exact:true}).click();
    await page.getByRole('button',{name:'Save configuration',exact:true}).click();
    await expect(page.getByRole('button',{name:'Save configuration',exact:true})).toBeDisabled();
    await expect(page.getByRole('region',{name:'Effective sandbox configuration'})).toContainText(label);
    const session=await page.request.post('/api/v2/sessions',{headers,data:{agentInstanceId:instanceId,mode:'text'}});expect(session.ok()).toBe(true);const sessionId=(await session.json()).sessionId;
    await page.goto(`/c/${sessionId}`);await expect(page.getByTestId('connection')).toHaveText('Ready');
    await page.getByRole('textbox',{name:'Message',exact:true}).fill('synthetic-sandbox:demo');await page.getByRole('button',{name:'Send',exact:true}).click();
    await expect(page.locator('.chat-message-assistant').last()).toContainText('Python failure fixed, Node test passed, Excel and Word files validated and published.',{timeout:120000});
    await expect(page.getByRole('button',{name:'Download report.xlsx',exact:true}).first()).toBeVisible();await expect(page.getByRole('button',{name:'Download report.docx',exact:true}).first()).toBeVisible();
    const environment=await (await page.request.get(`/api/v2/sessions/${sessionId}/sandbox`,{headers})).json();expect(environment.profileId).toBe(id);expect(environment.profileRevision).toBe(1);environments.push(environment);
    await page.getByRole('button',{name:'Session sandbox',exact:true}).click();await expect(page.getByRole('dialog',{name:'Session sandbox',exact:true})).toContainText(id);
    await page.screenshot({path:info.outputPath(id+'.png'),fullPage:true});await page.keyboard.press('Escape');
    expect((await page.request.post(`/api/v2/sessions/${sessionId}/sandbox/reset`,{headers,data:{expectedGeneration:environment.generation,expectedExecutionId:environment.activeExecutionId??environment.lastExecutionId}})).ok()).toBe(true);
    expect((await page.request.delete(`/api/v2/sessions/${sessionId}`,{headers})).ok()).toBe(true);
  }
  expect(environments[0].generation).not.toBe(environments[1].generation);
});
