import { expect, test } from "@playwright/test";
import { selectInstanceIdentity } from "./support/instance-identity";

test("email harness approval modal approves synthetic send", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  const owner = await selectInstanceIdentity(page, { id: 'general-assistant', version: 35 });
  const token = await page.evaluate(() => localStorage.getItem('agent-core.owner-capability'));
  const headers = { 'X-AgentCore-Owner-Capability': token! };
  const account = await page.request.post('/api/v2/admin/email-accounts', { headers, data: {
    displayName: 'Synthetic harness account', profile: { preset: 'Gmail', emailAddress: 'assistant@example.test',
      imapUsername: '', smtpUsername: '', imapEndpoint: null, smtpEndpoint: null }
  } });
  expect(account.ok(), await account.text()).toBe(true);
  const created = await account.json();
  const started = await page.request.post(`/api/v2/admin/email-accounts/${created.credentialId}/oauth/start`, { headers, data: { expectedRevision: 1 } });
  expect(started.ok()).toBe(true);
  const auth = await started.json(); const state = new URL(auth.authorizationUrl, 'http://localhost').searchParams.get('state');
  const callback = await page.request.get(`/api/v2/integrations/google-email/oauth/callback?state=${encodeURIComponent(state!)}&code=synthetic-consent`);
  expect(callback.ok()).toBe(true);
  const grant = await page.request.post(`/api/v2/admin/agent-instances/${owner}/credential-bindings`, { headers, data: { credentialId: created.credentialId, reference: 'harness-mail', expectedInstanceRevision: 1 } });
  expect(grant.ok(), await grant.text()).toBe(true);

  await page.getByLabel("Message").fill("Please run the email harness end to end.");
  await page.getByRole("button", { name: "Send" }).click();

  const modal = page.getByRole("dialog", { name: "Approve sensitive action" });
  await expect(modal).toBeVisible({ timeout: 30_000 });
  await expect(modal).toContainText("Send email");
  await expect(modal).toContainText("recipient@example.test");
  await expect(modal).toContainText("bcc@example.test");
  await expect(modal).toContainText("assistant@example.test");
  await expect(modal).toContainText("harness-mail");
  await expect(modal).toContainText("Harness draft");
  await expect(modal).toContainText("Synthetic email harness send path.");

  await modal.getByRole("button", { name: "Approve" }).click();
  await expect(modal).toBeHidden({ timeout: 20_000 });
  await expect(page.getByText("Email harness completed after approval.")).toBeVisible({ timeout: 30_000 });
});
