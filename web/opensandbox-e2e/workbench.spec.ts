import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';
import crypto from 'node:crypto';
import { selectSpreadsheetCell } from '../e2e/support/spreadsheet';

const stateFile = process.env.AGENTCORE_OSB_UI_STATE!;
async function send(page: Page, scenario: string) {
  const messages = page.locator('.chat-message-assistant'); const count = await messages.count();
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('synthetic-sandbox:' + scenario);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(messages).toHaveCount(count + 1, { timeout: 120000 });
  await expect(messages.last()).toContainText('Real sandbox', { timeout: 120000 });
  await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeHidden();
  return messages.last();
}
async function setup(page: Page) {
  await page.goto('/admin/platform/sandboxes');
  await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  return { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
}
async function environment(page: Page, id: string, headers: Record<string, string>) {
  const response = await page.request.get(`/api/v2/sessions/${id}/sandbox`, { headers });
  expect(response.ok()).toBe(true); return await response.json();
}

async function previewOfficeArtifacts(page: Page) {
  const workbookOpen = page.getByRole('button', { name: 'Preview report.xlsx', exact: true }).first();
  await workbookOpen.click();
  const workbook = page.getByRole('dialog', { name: 'report.xlsx', exact: true });
  await selectSpreadsheetCell(workbook, 'B1', '5');
  await page.keyboard.press('Escape');
  await expect(workbook).toHaveCount(0);
  await expect(workbookOpen).toBeFocused();
  const documentOpen = page.getByRole('button', { name: 'Preview report.docx', exact: true }).first();
  await documentOpen.click();
  const document = page.getByRole('dialog', { name: 'report.docx', exact: true });
  await expect(page.frameLocator('iframe[title="Word document preview"]').getByText('Total: 5', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(document).toHaveCount(0);
  await expect(documentOpen).toBeFocused();
}

test('actual Workbench artifacts, stream, recovery and process recreation', async ({ page }, info) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
  const headers = await setup(page);
  await expect.poll(async () => (await (await page.request.get('/api/v2/admin/sandboxes', { headers })).json()).state,
    { timeout: 30000 }).toBe('Ready');
  let sessionId: string;
  if (process.env.AGENTCORE_OSB_UI_PHASE === 'after') {
    const saved = JSON.parse(fs.readFileSync(stateFile, 'utf8')); sessionId = saved.sessionId;
    expect((await environment(page, sessionId, headers)).generation).toBe(saved.generation);
    await page.goto(`/c/${sessionId}`); await expect(page.getByTestId('connection')).toHaveText('Ready');
    await expect(await send(page, 'retain')).toContainText('"exitCode":0');
    await expect(await send(page, 'init')).toContainText('"stdout":"I"');
    expect((await environment(page, sessionId, headers)).generation).toBe(saved.generation);
    for (const file of saved.artifacts) {
      const request = page.waitForEvent('download');
      await page.getByRole('button', { name: `Download ${file.name}`, exact: true }).first().click();
      const download = await request; const bytes = fs.readFileSync((await download.path())!);
      expect(crypto.createHash('sha256').update(bytes).digest('hex')).toBe(file.sha256);
    }
    await previewOfficeArtifacts(page);
    // The observed partial output must arrive while the actual command runs.
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('synthetic-sandbox:long');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await page.getByRole('button', { name: 'Session sandbox', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Session sandbox', exact: true });
    await expect(drawer.getByText('Running', { exact: true })).toBeVisible();
    await expect(drawer.getByRole('region', { name: 'Standard output', exact: true })).toContainText('started');
    await expect(drawer.getByRole('region', { name: 'Standard output', exact: true })).toContainText('beyond-30-seconds', { timeout: 60000 });
    await expect(drawer.getByText('Completed', { exact: true })).toBeVisible({ timeout: 60000 });
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop', exact: true })).toBeHidden();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('synthetic-sandbox:cancel');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await page.getByRole('button', { name: 'Session sandbox', exact: true }).click();
    await expect(drawer.getByText('Running', { exact: true })).toBeVisible();
    await expect(drawer.getByRole('region', { name: 'Standard output', exact: true })).toContainText('started');
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await page.getByRole('button', { name: 'Session sandbox', exact: true }).click();
    await expect(drawer).toContainText('NeedsResync');
    await drawer.getByRole('button', { name: 'Reset sandbox', exact: true }).click();
    await page.getByRole('button', { name: 'Reset sandbox', exact: true }).last().click();
    await expect(drawer).toContainText('Terminated');
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(await send(page, 'retain')).toContainText('"exitCode":0');
    expect((await environment(page, sessionId, headers)).generation).not.toBe(saved.generation);
    const sessionMetadata = await (await page.request.get(`/api/v2/sessions/${sessionId}`, { headers })).json();
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('[test:background-start] [test:opensandbox-background]');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    let child: { session: { sessionId: string }; origin: { parentSessionId: string }; initialRun: { status: string; outcome: { summary: string } }; artifactCount: number } | undefined;
    await expect.poll(async () => {
      const response = await page.request.get(`/api/v2/agent-instances/${sessionMetadata.agentInstanceId}/background-sessions`, { headers });
      expect(response.ok()).toBe(true);
      child = (await response.json()).items.find((item: { origin: { parentSessionId: string } }) => item.origin.parentSessionId === sessionId);
      return child?.initialRun?.status;
    }, { timeout: 90000 }).toBe('completed');
    expect(child!.artifactCount).toBe(2); expect(child!.initialRun.outcome.summary).toContain('Real background Workbench completed');
    const childId = child!.session.sessionId;
    expect((await environment(page, childId, headers)).generation).not.toBe((await environment(page, sessionId, headers)).generation);
    await page.goto(`/c/${childId}`); await expect(page.getByTestId('connection')).toHaveText('Ready');
    await expect(await send(page, 'init')).toContainText('"stdout":"I"');
    expect((await page.request.delete(`/api/v2/sessions/${childId}`, { headers })).ok()).toBe(true);
    expect((await page.request.delete(`/api/v2/sessions/${sessionId}`, { headers })).ok()).toBe(true);
  } else {
    await expect(page.getByLabel('Sandbox initialization script')).toBeVisible();
    await page.getByLabel('Sandbox initialization script').fill('printf private-verification-sentinel >&2; exit 7');
    const brokenSave = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith('/sandbox-profiles/general-workbench'));
    await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    const brokenRevision = (await (await brokenSave).json()).revision;
    await expect(page.getByRole('button', { name: 'Save profile', exact: true })).toBeDisabled();
    const brokenVerify = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/sandbox-profiles/general-workbench/verify'));
    await page.getByRole('button', { name: 'Verify profile', exact: true }).click();
    const failedObservation = await (await brokenVerify).json();
    expect(failedObservation.passed).toBe(false); expect(failedObservation.profile.revision).toBe(brokenRevision);
    expect(failedObservation.message).toContain('Initialization failed with exit code 7');
    expect(failedObservation.message).not.toContain('private-verification-sentinel');
    await expect(page.getByRole('status')).toContainText('Initialization failed with exit code 7');

    await page.getByLabel('Sandbox initialization script').fill('printf I >> init-count.txt');
    const savedProfile = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith('/sandbox-profiles/general-workbench'));
    await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    expect((await savedProfile).ok()).toBe(true);
    await expect(page.getByRole('button', { name: 'Save profile', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Verify profile', exact: true }).click();
    await expect(page.getByText(/Non-root Python readiness, initialization and scratch validation passed/).first()).toBeVisible({ timeout: 60000 });
    const instance = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'general-assistant', version: 30 } });
    expect(instance.ok(), await instance.text()).toBe(true);
    const instanceId=(await instance.json()).instanceId;
    // The dedicated destination preserves the old link and the sparse/CAS save owner.
    await page.goto(`/admin/instances/${instanceId}/execution#sandboxPolicy`);
    await expect(page).toHaveURL(new RegExp(`/instances/${instanceId}/sandbox#sandboxPolicy`));
    await expect(page.getByRole('textbox',{name:'Instance initialization script',exact:true})).toBeVisible();
    const setupScript='echo "Hello, world!"; printf instance-stderr >&2; test "$(tail -c 1 init-count.txt)" = I';
    await page.getByRole('textbox',{name:'Instance initialization script',exact:true}).fill(setupScript);
    await page.getByRole('spinbutton',{name:'CPU cores',exact:true}).fill('0.5');
    await expect(page.getByRole('button',{name:'Verify Instance setup',exact:true})).toBeDisabled();
    const saveSetup=page.waitForResponse(response=>response.request().method()==='PATCH'&&response.url().includes('/settings'));
    await page.getByRole('button',{name:'Save configuration',exact:true}).click();
    expect((await saveSetup).ok()).toBe(true);
    await expect(page.getByRole('button',{name:'Verify Instance setup',exact:true})).toBeEnabled();
    await page.getByRole('button',{name:'Verify Instance setup',exact:true}).click();
    const effectiveSandbox=page.getByRole('region',{name:'Effective sandbox configuration'});
    await expect(effectiveSandbox.getByRole('alert')).toContainText('initialization and scratch validation passed',{timeout:60000});
    await effectiveSandbox.getByRole('button',{name:'Setup verification standard output',exact:true}).click();
    await expect(effectiveSandbox).toContainText('Hello, world!');
    await effectiveSandbox.getByRole('button',{name:'Setup verification standard error',exact:true}).click();
    await expect(effectiveSandbox).toContainText('instance-stderr');
    for(const width of [1440,768,390,320]) {
      await page.setViewportSize({width,height:900});
      await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth)).toBe(width);
      await page.screenshot({path:info.outputPath(`instance-sandbox-${width}.png`),fullPage:true});
    }
    await page.reload();await expect(page.getByRole('textbox',{name:'Instance initialization script',exact:true})).toHaveValue(setupScript);
    await expect(effectiveSandbox.getByRole('alert')).toContainText('initialization and scratch validation passed');
    const session = await page.request.post('/api/v2/sessions', { headers, data: { agentInstanceId: instanceId, mode: 'text' } });
    expect(session.ok()).toBe(true); sessionId = (await session.json()).sessionId;
    await page.goto(`/c/${sessionId}`); await expect(page.getByTestId('connection')).toHaveText('Ready');
    await expect(await send(page, 'demo')).toContainText('Python failure fixed, Node test passed, Excel and Word files validated and published.');
    const artifacts = [];
    for (const name of ['report.xlsx', 'report.docx']) {
      const request = page.waitForEvent('download');
      await page.getByRole('button', { name: `Download ${name}`, exact: true }).click();
      const download = await request; expect(download.suggestedFilename()).toBe(name);
      const bytes = fs.readFileSync((await download.path())!); expect(bytes.subarray(0, 2).toString()).toBe('PK');
      artifacts.push({ name, sha256: crypto.createHash('sha256').update(bytes).digest('hex') });
    }
    await previewOfficeArtifacts(page);
    await expect(await send(page, 'init')).toContainText('"stdout":"I"');
    const lease = await environment(page, sessionId, headers);
    fs.writeFileSync(stateFile, JSON.stringify({ sessionId, generation: lease.generation, artifacts }));
    await page.getByRole('button', { name: 'Session sandbox', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Session sandbox', exact: true });
    await expect(drawer).toContainText('committed');
    await expect(drawer.getByText('Succeeded',{exact:true})).toBeVisible();
    await drawer.getByRole('button',{name:'Initialization standard output',exact:true}).click();
    await expect(drawer).toContainText('Hello, world!');
    await drawer.getByRole('button',{name:'Initialization standard error',exact:true}).click();
    await expect(drawer).toContainText('instance-stderr');
    await drawer.getByRole('region',{name:'Sandbox command result'}).getByRole('button', { name: 'Copy', exact: true }).first().click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe('cat init-count.txt');
    await page.mouse.move(0,0);
    for (const width of [1440, 1024, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await expect.poll(async () => drawer.evaluate(e => Math.round(e.getBoundingClientRect().width))).toBe(width < 768 ? width : 640);
      await expect.poll(async () => drawer.evaluate(e => Math.round(e.getBoundingClientRect().x))).toBe(width < 768 ? 0 : width-640);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
      await page.screenshot({ path: info.outputPath(`sandbox-${width}.png`), fullPage: true });
    }
    await drawer.getByRole('button', { name: 'Standard output', exact: true }).click();
    await expect(drawer.getByRole('button', { name: 'Standard output', exact: true })).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Escape'); await expect(drawer).toBeHidden();
    await expect(page.getByRole('button', { name: 'Session sandbox', exact: true })).toBeFocused();
    // Presentation fixtures isolate recovery copy from real crash injection;
    // the backend swap/receipt fault tests supply the actual crash evidence.
    const actual = await environment(page, sessionId, headers);
    const executionUrl = `**/api/v2/sessions/${sessionId}/sandbox/executions/*`;
    for (const scenario of [
      { state:'Failed', status:'Indeterminate', sync:'notCommitted', title:'Environment setup failed', message:'Environment initialization failed. Correct and verify the profile script, then reset before retrying.' },
      { state:'Terminated', status:'Completed', sync:'committed', title:'Environment ended', message:null },
      { state:'NeedsResync', status:'Indeterminate', sync:'unknown', title:'Inspect before retrying', message:null },
      { state:'NeedsResync', status:'Indeterminate', sync:'committed', title:'Inspect before retrying', message:null },
    ]) {
      await page.route(`**/api/v2/sessions/${sessionId}/sandbox`, route => route.fulfill({ json:{ ...actual, state:scenario.state, message:scenario.message, activeExecutionId:null } }));
      await page.route(executionUrl, route => route.fulfill({ json:{ executionId:actual.lastExecutionId,status:scenario.status,exitCode:null,stdout:'Retained output',stderr:'',truncated:false,durationMs:1000,workspaceSyncStatus:scenario.sync,diagnosticId:null,background:false,commandText:'python analysis.py' } }));
      for (const width of [1440,390]) {
        await page.setViewportSize({ width,height:900 });
        await page.getByRole('button', { name:'Session sandbox',exact:true }).click();
        await expect(drawer.getByText(scenario.title,{exact:true})).toBeVisible();
        if (scenario.sync==='unknown') await expect(drawer).toContainText('Files may already have been committed');
        if (scenario.status==='Indeterminate' && scenario.sync==='committed') await expect(drawer).toContainText('Canonical files were committed');
        await expect.poll(async () => drawer.evaluate(e => Math.round(e.getBoundingClientRect().width))).toBe(width < 768 ? width : 640);
        await expect.poll(async () => drawer.evaluate(e => Math.round(e.getBoundingClientRect().x))).toBe(width < 768 ? 0 : width - 640);
        await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
        await page.screenshot({path:info.outputPath(`recovery-${scenario.state}-${scenario.sync}-${width}.png`),fullPage:true});
        await page.keyboard.press('Escape'); await expect(drawer).toBeHidden();
        await expect(page.getByRole('button', { name:'Session sandbox',exact:true })).toBeFocused();
      }
      await page.unroute(`**/api/v2/sessions/${sessionId}/sandbox`);
      await page.unroute(executionUrl);
    }

  }
  expect(errors).toEqual([]);
});
