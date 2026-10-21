import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password';
async function call<T>(api: APIRequestContext, url: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json();
  const r = await api.fetch(url, { method, data: body, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(r.ok(), `${method} ${url}: ${r.status()} ${await r.text()}`).toBeTruthy();
  return r.status() === 204 ? undefined as T : r.json();
}
async function signIn(page: Page, account = login, secret = password) {
  await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(account); await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible();
}
test('audit queries page exact facts, retire details on access loss and isolate platform facts', async ({ page }) => {
  const api = await requests.newContext({ baseURL: process.env.TENANT_CHAT_TEST_BASE_URL });
  const errors: string[] = [], legacy: string[] = [], reads: URL[] = [];
  page.on('pageerror', e => errors.push(e.message)); page.on('request', r => { const u = new URL(r.url()); if (u.pathname.endsWith('/audit')) reads.push(u); if (u.pathname.startsWith('/api/v1/') || u.pathname.includes('owner-capability')) legacy.push(u.pathname); });
  const captureRoot = path.resolve('..', '.impeccable/review/authorization-audit'); fs.mkdirSync(captureRoot, { recursive: true });
  try {
    expect((await (await api.get('/health')).json()).profile).toBe('Synthetic'); await call(api, '/api/v2/auth/login', { login, password });
    const tenant = (await call<{items:{tenantId:string}[]}>(api, '/api/v2/account/tenants')).items[0].tenantId;
    const root = '/api/v2/tenants/' + tenant; const access = await call<{principalId:string}>(api, root + '/access/me');
    const revision = async () => (await call<{revision:number}>(api, root + '/access/me')).revision;
    const role = async (name: string, permissions = ['agents.read']) => call<{roleId:string}>(api, root + '/roles', { name, permissions, expectedRevision: await revision() });
    for (let i = 0; i < 3; i++) await role('Private audit fixture ' + i);
    await signIn(page); await page.goto('/t/' + tenant + '/admin/audit');
    await expect(page.getByRole('heading', {name:'Audit',exact:true})).toBeVisible(); await expect(page.locator('a[aria-current=page]')).toHaveCount(1);
    await page.getByLabel('Activity code', {exact:true}).fill('iam.roles.manage'); await page.getByRole('button',{name:'Apply filters',exact:true}).click();
    await expect(page.getByText('3 entries',{exact:true})).toBeVisible();
    await page.getByLabel('Actor principal',{exact:true}).fill(access.principalId); await page.getByLabel('From (local time)',{exact:true}).fill('2000-01-01T00:00'); await page.getByLabel('Through (local time)',{exact:true}).fill('2099-01-01T00:00');
    await page.getByRole('button',{name:'Apply filters',exact:true}).click(); await expect(page.getByText('3 entries',{exact:true})).toBeVisible();
    await expect.poll(() => reads.at(-1)?.searchParams.get('actorPrincipalId')).toBe(access.principalId);
    for (const width of [1440,1024,768,390,320]) {
      await page.setViewportSize({width,height:1000}); await expect(page.getByRole('button',{name:'View details: Role saved'})).toHaveCount(3);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({animations:'disabled',path:path.join(captureRoot,`tenant-${width}.png`),fullPage:true});
      if (width===390) { await page.getByRole('button',{name:'View details: Role saved'}).first().click(); await expect(page.getByRole('dialog',{name:'Audit details',exact:true})).toBeVisible(); await expect.poll(()=>page.getByRole('dialog',{name:'Audit details',exact:true}).evaluate(el=>Math.round(el.getBoundingClientRect().x))).toBe(0); await page.screenshot({animations:'disabled',path:path.join(captureRoot,'tenant-390-details.png'),fullPage:true}); await page.getByRole('button',{name:'Close',exact:true}).click(); }
    }
    const before=reads.length; await page.getByLabel('Actor principal',{exact:true}).fill('invalid'); await page.getByRole('button',{name:'Apply filters',exact:true}).click(); await expect(page.getByText('Enter a complete principal ID.',{exact:true})).toBeVisible(); expect(reads.length).toBe(before);
    await page.getByRole('button',{name:'Clear filters',exact:true}).click(); await expect(page.getByLabel('Actor principal',{exact:true})).toHaveValue('');
    for(let i=0;i<31;i++)await role('Paging fixture '+i);
    await page.setViewportSize({width:1440,height:1000}); await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await page.getByLabel('Activity code',{exact:true}).fill('iam.roles.manage'); await page.getByRole('button',{name:'Apply filters',exact:true}).click(); await expect(page.getByText('34 entries',{exact:true})).toBeVisible();
    await page.getByRole('button',{name:'View details: Role saved'}).first().click(); await expect(page.getByRole('region',{name:'Audit entry details'})).toBeVisible();
    await page.getByTitle('2',{exact:true}).click(); await expect(page.getByRole('button',{name:'View details: Role saved'})).toHaveCount(4); await expect(page.getByRole('region',{name:'Audit entry details'})).toHaveCount(0); expect(reads.at(-1)?.searchParams.get('page')).toBe('1');
    const policy=await call<{saved:{revision:number}}>(api,'/api/v2/system/browser/privacy');
    await call(api,'/api/v2/system/browser/privacy',{expectedRevision:policy.saved.revision,mode:'Disabled',unmaskedOrigins:[],trustedGraphicsOrigins:[],acknowledgeExposure:false},'PUT');
    await page.goto('/system/audit'); await expect(page.getByRole('heading',{name:'Platform audit',exact:true})).toBeVisible(); await expect(page.getByRole('button',{name:'View details: Browser privacy saved'})).toBeVisible();
    await expect(page.getByRole('button',{name:'View details: Role saved'})).toHaveCount(0);
    for(const width of [1440,320]){await page.setViewportSize({width,height:1000});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);await page.screenshot({animations:'disabled',path:path.join(captureRoot,`system-${width}.png`),fullPage:true});}
    const auditRole=await role('Only audit access',['security.audit.read']); const email='audit-reader-'+crypto.randomUUID().slice(0,8)+'@example.test',secret='synthetic-audit-reader-password';
    const invite=await call<{invitationId:string;token:string}>(api,root+'/members/invite',{login:email,roleId:auditRole.roleId,expectedRevision:await revision()});
    const member=await requests.newContext({baseURL:process.env.TENANT_CHAT_TEST_BASE_URL});
    try{await call(member,'/api/v2/auth/register',{login:email,displayName:'Audit reader',password:secret,invitation:{tenantId:tenant,invitationId:invite.invitationId,token:invite.token}});}finally{await member.dispose();}
    await page.context().clearCookies();await signIn(page,email,secret);const systemBefore=reads.filter(u=>u.pathname==='/api/v2/system/audit').length;
    await page.goto('/system/audit');await expect(page.getByText('This page is unavailable. Choose another organization or contact its owner.',{exact:true})).toBeVisible();expect(reads.filter(u=>u.pathname==='/api/v2/system/audit').length).toBe(systemBefore);
    await page.goto('/t/'+tenant+'/admin/audit');await expect(page.getByRole('button',{name:'View details: Role saved'}).first()).toBeVisible();await page.getByRole('button',{name:'View details: Role saved'}).first().click();await expect(page.getByRole('dialog',{name:'Audit details',exact:true})).toBeVisible();await page.getByRole('button',{name:'Close',exact:true}).click();
    const members=await call<{items:{login:string;roles:{assignmentId:string}[]}[]}>(api,root+'/members');const assignment=members.items.find(m=>m.login.toLowerCase()===email)!.roles[0].assignmentId;
    await call(api,root+'/role-assignments/'+assignment+'?expectedRevision='+await revision(),undefined,'DELETE');await page.getByRole('button',{name:'Refresh',exact:true}).click();await expect(page.getByRole('alert')).toBeVisible();await expect(page.getByRole('button',{name:/details: Role saved/})).toHaveCount(0);await expect(page.getByText(/\d+ entries$/)).toHaveCount(0);
    expect(errors).toEqual([]);expect(legacy).toEqual([]);
  } finally { await api.dispose(); }
});
