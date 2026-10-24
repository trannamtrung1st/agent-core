import { canonicalFetch } from './support/tenant-auth';
import { expect, test, request as requests, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';

const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, path: string, body?: unknown): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const response = await canonicalFetch(api, path, { method: body === undefined ? 'GET' : 'POST', headers: { 'X-AgentCore-CSRF': csrf.token }, data: body });
  expect(response.ok(), `${path}: ${response.status()}`).toBeTruthy();
  return response.json();
}
for (const width of [1440, 390]) test(`tenant voice keeps capture active, credits heard separately, and requires a fresh click after reconnect at ${width}px`, async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  await page.addInitScript(() => {
    const observations: unknown[] = []; (window as unknown as { voiceObservations: unknown[] }).voiceObservations = observations;
    let prior = '';
    setInterval(() => {
      const value = { status: document.querySelector('[data-testid="connection"]')?.textContent,
        debug: window.__agentCore?.speechDebug(), prepared: window.__agentCore?.capturePrepared(),
        streaming: window.__agentCore?.captureStreaming(), authorized: window.__agentCore?.captureAuthorized() };
      const key = JSON.stringify(value); if (key !== prior && observations.length < 500) { observations.push(value); prior = key; }
    }, 20);
  });
  const wire: unknown[] = []; const protocol = new MessagePackHubProtocol();
  page.on('websocket', socket => {
    for (const direction of ['framesent', 'framereceived'] as const) socket.on(direction, frame => {
      if (typeof frame.payload === 'string') return;
      try { for (const msg of protocol.parseMessages(new Uint8Array(frame.payload).buffer, { log() {} })) {
        const message = msg as { target?: string; arguments?: { type?: string; payload?: Record<string, unknown> }[] };
        if (message.target) wire.push({ direction, target: message.target, type: message.arguments?.[0]?.type,
          mode: message.arguments?.[0]?.payload?.mode, pending: message.arguments?.[0]?.payload?.pendingMode });
      } } catch { /* JSON negotiation has a separate frame. */ }
    });
  });
  const errors: string[] = [], legacy: string[] = []; page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { const path = new URL(r.url()).pathname; if (/^\/api\/v[12]\/(owner-capability|sessions|agent-instances)(\/|$)/.test(path)) legacy.push(path); });
  try {
    await call(api, '/api/v2/auth/login', { login, password });
    const nonce = crypto.randomUUID().slice(0, 8), tenant = await call<{ tenantId: string }>(api, '/api/v2/system/tenants', { name: `Voice ${nonce}`, slug: `voice-${nonce}` });
    const prefix = `/api/v2/tenants/${tenant.tenantId}`, revision = async () => (await call<{ revision: number }>(api, prefix + '/access/me')).revision;
    await call(api, prefix + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
    await page.setViewportSize({ width, height: 900 }); await page.goto('/sign-in');
    await page.getByLabel('Email', { exact: true }).fill(login); await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); await page.goto(`/t/${tenant.tenantId}`);
    await expect(page.getByRole('button', { name: 'Voice', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Voice', exact: true }).click();
    await expect(page.getByTestId('connection')).toHaveText('Listening…');
    await expect.poll(() => page.evaluate(() => window.__agentCore?.captureStreaming())).toBe(true);
    await expect.poll(() => page.evaluate(() => window.__agentCore?.workletLoaded())).toBe(true);
    await expect.poll(() => page.evaluate(() => window.__agentCore?.audioFramesSent() ?? 0)).toBeGreaterThan(0);
    const session = page.url().split('/').pop()!;
    await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Hello in tenant voice');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText('Hello from synthetic.', { exact: true })).toBeVisible();
    await expect.poll(() => page.evaluate(() => window.__agentCore?.playbackConsumed() ?? 0)).toBeGreaterThan(0);
    await expect.poll(() => page.evaluate(() => window.__agentCore?.captureStreaming())).toBe(true);
    await page.getByRole('button', { name: 'Mute', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Unmute', exact: true })).toBeVisible();
    await expect(page.getByTestId('connection')).not.toHaveText('Listening…');
    await expect.poll(() => page.evaluate(() => window.__agentCore?.capturePrepared())).toBe(true);
    await expect.poll(async () => {
      const response = await api.get(`${prefix}/sessions/${session}/messages`);
      // A coherent history read can lose to the receipt currently being committed.
      // Retry only that read conflict; authorization and other failures still fail the scenario.
      if (response.status() === 409) return 0;
      expect(response.ok(), `history read: ${response.status()}`).toBeTruthy();
      const h = await response.json() as { items: { role: string; heardTextEndExclusive: number }[] };
      return h.items.find(e => e.role === 'assistant')?.heardTextEndExclusive ?? 0;
    }).toBeGreaterThan(0);
    await page.screenshot({ path: `.playwright-mcp/tenant-voice-${width}.png`, fullPage: true });
    await page.getByRole('button', { name: 'Unmute', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__agentCore?.captureStreaming())).toBe(true);
    await page.evaluate(() => window.__agentCore?.disconnect()); await page.evaluate(() => window.__agentCore?.reconnect?.());
    await expect(page.getByTestId('connection')).toHaveText('Ready');
    await expect.poll(() => page.evaluate(() => window.__agentCore?.capturePrepared())).toBe(false);
    await expect(page.getByRole('button', { name: 'Voice', exact: true })).toHaveAttribute('aria-pressed', 'false');
    await page.getByRole('button', { name: 'Voice', exact: true }).click();
    await expect(page.getByTestId('connection')).toHaveText('Listening…');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    expect(errors).toEqual([]); expect(legacy).toEqual([]);
  } finally {
    const states = await page.evaluate(() => (window as unknown as { voiceObservations?: unknown[] }).voiceObservations).catch(() => []);
    fs.mkdirSync('.playwright-mcp', { recursive: true }); fs.writeFileSync(`.playwright-mcp/tenant-voice-state-${width}.json`, JSON.stringify({ wire, states }, null, 2));
    await test.info().attach('voice-state', { body: JSON.stringify({ wire, states }), contentType: 'application/json' });
    await api.dispose();
  }
});
