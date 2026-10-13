import { expect, test, type Locator, type Page } from '@playwright/test';
import { navigateAdmin } from './support/admin-navigation';
test.use({ trace: 'retain-on-failure' });
async function openInNewTab(page: Page, link: Locator, content: 'Instances table' | 'Overview' | 'Automations', gesture: 'modifier' | 'middle' = 'modifier') {
  const source = page.url(); const href = await link.getAttribute('href'); expect(href).toBeTruthy();
  const destination = new URL(href!, source).href;
  await page.bringToFront(); await link.hover();
  if (await link.getAttribute('aria-label') === 'Open Admin') await expect(page.getByRole('tooltip')).toContainText('Admin');
  const sourceSession = await page.context().newCDPSession(page);
  const { targetInfo: sourceTarget } = await sourceSession.send('Target.getTargetInfo');
  await sourceSession.detach();
  const browserSession = await page.context().browser()!.newBrowserCDPSession();
  const { targetInfos } = await browserSession.send('Target.getTargets');
  const existingTargets = new Set(targetInfos.map(target => target.targetId));
  let nativeTargetId: string | undefined;
  browserSession.on('Target.targetCreated', ({ targetInfo }) => {
    if (targetInfo.type === 'page' && targetInfo.browserContextId === sourceTarget.browserContextId && !existingTargets.has(targetInfo.targetId)) nativeTargetId ??= targetInfo.targetId;
  });
  await browserSession.send('Target.setDiscoverTargets', { discover: true });
  try {
    // Actual native gestures only: never create a scripted replacement page.
    await link.click(gesture === 'middle' ? { button: 'middle' } : { modifiers: ['ControlOrMeta'] });
    await expect.poll(() => nativeTargetId, { timeout: 15_000, message: 'Native gesture creates a new target in the source browser context' }).not.toBeUndefined();
    await browserSession.send('Target.activateTarget', { targetId: nativeTargetId! });
    // Chromium can load a native tab without delivering Playwright's Page channel.
    // Attach to that same real target and read its document, bypassing no UI checks.
    const { sessionId } = await browserSession.send('Target.attachToTarget', { targetId: nativeTargetId!, flatten: false });
    let sequence = 0;
    const pending = new Map<number, { resolve: (result: any) => void; reject: (error: Error) => void }>();
    browserSession.on('Target.receivedMessageFromTarget', event => {
      if (event.sessionId !== sessionId) return;
      const response = JSON.parse(event.message); const request = pending.get(response.id);
      if (!request) return; pending.delete(response.id);
      if (response.error) request.reject(new Error(response.error.message)); else request.resolve(response.result);
    });
    const readDocument = () => new Promise<any>((resolve, reject) => {
      const id = ++sequence; pending.set(id, { resolve, reject });
      const expression = `(({ destination, content }) => {
        const matches = content === 'Instances table'
          ? [...document.querySelectorAll('table[aria-label="Instances table"]')]
          : [...document.querySelectorAll('h2, [role="heading"][aria-level="2"]')].filter(element => element.textContent?.trim() === content);
        return location.href === destination && document.readyState === 'complete' && matches.some(element => {
          const style = getComputedStyle(element); const rect = element.getBoundingClientRect();
          return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
        });
      })(${JSON.stringify({ destination, content })})`;
      void browserSession.send('Target.sendMessageToTarget', { sessionId, message: JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }) })
        .catch(error => { pending.delete(id); reject(error); });
    });
    const bootstrapStarted = Date.now();
    await expect.poll(async () => {
      try {
        const response = await readDocument();
        if (response.exceptionDetails) throw new Error(response.exceptionDetails.text);
        return response.result?.value === true;
      } catch (error) {
        if (/Execution context was destroyed|Cannot find context with specified id|Cannot find default execution context/.test(String(error))) return false;
        throw error;
      }
    }, { timeout: 15_000, message: `Native target displays ${content} at its canonical URL` }).toBe(true);
    console.log(`Native target bootstrap: ${Date.now() - bootstrapStarted}ms`);
    await expect(page).toHaveURL(source);
    return { close: async () => {
      try { const result = await browserSession.send('Target.closeTarget', { targetId: nativeTargetId! }); expect(result.success).toBe(true); }
      finally { await browserSession.detach(); }
    } };
  } catch (error) {
    if (nativeTargetId) await browserSession.send('Target.closeTarget', { targetId: nativeTargetId }).catch(() => {});
    await browserSession.detach(); throw error;
  }
}
for (const width of [1440, 390]) test(`native canonical links preserve source drafts at ${width}px`, async ({ page }) => {
  test.setTimeout(90_000); await page.setViewportSize({ width, height: 900 });
  await page.goto('/'); await page.waitForFunction(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': (await page.evaluate(() => localStorage.getItem('agent-core.owner-capability')))! };
  const created = await page.request.post('/api/v2/admin/agent-instances', { headers, data: { definitionId: 'examiner', version: 1 } }); expect(created.ok()).toBe(true);
  const { instanceId } = await created.json(); const base = `/admin/instances/${instanceId}`;
  const admin = await openInNewTab(page, page.getByRole('link', { name: 'Open Admin', exact: true }), 'Instances table', 'middle');
  await admin.close();
  await page.getByRole('link', { name: 'Open Admin', exact: true }).click();
  await page.getByRole('textbox', { name: 'Search instances', exact: true }).fill(instanceId);
  const instance = page.locator(`a[href="${base}/overview"]`); const detail = await openInNewTab(page, instance, 'Overview');
  await detail.close(); await instance.click();
  await navigateAdmin(page, 'Profile & version'); await page.getByLabel('Persona name', { exact: true }).fill('Unsaved native navigation edit');
  const menu = page.getByRole('button', { name: 'Open Admin navigation', exact: true }); if (await menu.isVisible()) await menu.click();
  const nav = page.getByRole('navigation', { name: 'Admin navigation', exact: true });
  const source = page.url(); const automation = await openInNewTab(page, nav.getByRole('link', { name: 'Automations', exact: true }), 'Automations');
  await automation.close();
  if (width < 1024) { await page.bringToFront(); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog', { name: 'Admin navigation', exact: true })).toBeHidden(); }
  await expect(page).toHaveURL(source); await expect(page.getByLabel('Persona name', { exact: true })).toHaveValue('Unsaved native navigation edit');
  await page.getByRole('link', { name: 'Chat', exact: true }).click(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Stay', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page.getByLabel('Persona name', { exact: true })).toHaveValue('Unsaved native navigation edit');
  // Back traverses to this same Instance Overview, preserving the draft; leaving the owner asks.
  await page.goBack(); await expect(page).toHaveURL(new URL(`${base}/overview`, page.url()).href);
  await page.goBack(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Stay', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page).toHaveURL(new URL(`${base}/overview`, page.url()).href);
  await page.goBack(); await page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true }).getByRole('button', { name: 'Discard and leave', exact: true }).click(); await expect(page.getByRole('dialog', { name: 'Discard unsaved changes?', exact: true })).toBeHidden();
  await expect(page).toHaveURL(/\/admin\/instances$/);
});
