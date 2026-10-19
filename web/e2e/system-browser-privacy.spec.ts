import { expect, test, request as requests, type APIRequestContext, type Page } from '@playwright/test';
const login = 'tenant-chat-owner@example.test', password = 'synthetic-tenant-chat-password'; const policyApi = '/api/v2/system/browser/privacy';
type Policy = { saved: { mode: string; revision: number }; effective: { mode: string; revision: number }; durable: boolean };
let identity: Awaited<ReturnType<APIRequestContext['storageState']>>;
async function call<T>(api: APIRequestContext, path: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> {
  const csrf = await (await api.get('/api/v2/auth/csrf')).json(); const r = await api.fetch(path, { method, data: body, headers: { 'X-AgentCore-CSRF': csrf.token } });
  expect(r.ok(), `${method} ${path}: ${r.status()}`).toBeTruthy(); return r.status() === 204 ? undefined as T : r.json();
}
async function signIn(page: Page, account = login, secret = password) { await page.goto('/sign-in'); await page.getByLabel('Email', { exact: true }).fill(account); await page.getByLabel('Password', { exact: true }).fill(secret); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await expect(page.getByRole('heading', { name: 'My organizations', exact: true })).toBeVisible(); }
async function choose(page: Page, field: string, option: string) { await page.getByRole('combobox', {name:new RegExp(field)}).click(); await page.locator('.ant-select-item-option').filter({hasText:option}).click(); }
test.beforeAll(async () => { const api = await requests.newContext({baseURL:process.env.TENANT_CHAT_TEST_BASE_URL});try{expect((await(await api.get('/health')).json()).profile).toBe('Synthetic');await call(api,'/api/v2/auth/login',{login,password});identity=await api.storageState();}finally{await api.dispose();} });

test('platform policy keeps effective startup facts, recovers lost reply and retains stale Unmasked draft with a fresh acknowledgement', async ({page}) => {
  const api = await requests.newContext({baseURL:process.env.TENANT_CHAT_TEST_BASE_URL,storageState:identity}); const mutations: unknown[] = [], legacy: string[] = [], errors: string[] = [];
  page.on('request',r=>{const path=new URL(r.url()).pathname;if(path===policyApi&&r.method()==='PUT')mutations.push(r.postDataJSON());if(path.includes('/admin/browser/privacy')||path.includes('/owner-capability'))legacy.push(path);});page.on('pageerror',e=>errors.push(e.message));
  try {
    await page.setViewportSize({width:1440,height:1000}); await signIn(page); await page.getByRole('link',{name:'Browser privacy',exact:true}).click();
    await expect(page).toHaveURL(/\/system\/browser-privacy$/); await expect(page.getByRole('region',{name:'Platform Browser privacy'})).toBeVisible();
    const current=await call<Policy>(api,policyApi); const firstMode=current.saved.mode==='Disabled'?'Protected':'Disabled';
    await choose(page,'Saved screenshot privacy mode',firstMode==='Disabled'?'Disabled — no screenshots':'Protected — mask sensitive pixels');
    await page.getByRole('button',{name:/Save screenshot privacy$/}).click();
    await expect(page.getByText(`${firstMode} · revision ${current.saved.revision+1} · ephemeral, not active`,{exact:true})).toBeVisible();
    await expect(page.getByText(`${current.effective.mode} · revision ${current.effective.revision}`,{exact:true})).toBeVisible();
    const secondMode=firstMode==='Disabled'?'Protected':'Disabled'; let lost=0;
    await page.route('**'+policyApi,async route=>{if(route.request().method()!=='PUT'){await route.continue();return;}const actual=await route.fetch();expect(actual.ok()).toBeTruthy();lost++;await route.fulfill({response:actual,body:'lost policy reply'});});
    await page.setViewportSize({width:390,height:844});await choose(page,'Saved screenshot privacy mode',secondMode==='Disabled'?'Disabled — no screenshots':'Protected — mask sensitive pixels');await page.getByRole('button',{name:/Save screenshot privacy$/}).click();
    await expect(page.getByText('Read current policy before saving again',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:/Save screenshot privacy$/})).toBeDisabled();expect(lost).toBe(1);expect(mutations).toHaveLength(2);
    await page.screenshot({path:test.info().outputPath('mobile-lost-policy-reply.png'),fullPage:true});await page.getByRole('button',{name:'Reload saved policy',exact:true}).click();
    await expect(page.getByText(`${secondMode} · revision ${current.saved.revision+2} · ephemeral, not active`,{exact:true})).toBeVisible();expect(mutations).toHaveLength(2);await expect(page.getByRole('button',{name:/Save screenshot privacy$/})).toBeDisabled();
    await page.unroute('**'+policyApi);await page.setViewportSize({width:320,height:740});await choose(page,'Saved screenshot privacy mode','Unmasked — explicit confidentiality exception');await choose(page,'Trusted exact origins for Unmasked capture','https://example.test');
    await page.getByRole('button',{name:/Save screenshot privacy$/}).click();let confirm=page.getByRole('dialog',{name:'Authorize unmasked screenshots for all organizations?',exact:true});await expect(confirm).toContainText('https://example.test');
    await page.screenshot({path:test.info().outputPath('narrow-global-exposure-confirmation.png')});
    await call(api,policyApi,{expectedRevision:current.saved.revision+2,mode:'Disabled',unmaskedOrigins:[],trustedGraphicsOrigins:[],acknowledgeExposure:false},'PUT');
    await confirm.getByRole('button',{name:'Acknowledge & save',exact:true}).click();await expect(page.getByText('Read current policy before saving again',{exact:true})).toBeVisible();
    await expect(page.getByRole('combobox',{name:/Saved screenshot privacy mode/}).locator('..')).toContainText('Unmasked');expect(mutations).toHaveLength(3);
    await page.getByRole('button',{name:'Reload saved policy',exact:true}).click();await expect(page.getByRole('button',{name:/Save screenshot privacy$/})).toBeEnabled();expect(mutations).toHaveLength(3);
    await page.getByRole('button',{name:/Save screenshot privacy$/}).click();confirm=page.getByRole('dialog',{name:'Authorize unmasked screenshots for all organizations?',exact:true});await confirm.getByRole('button',{name:'Acknowledge & save',exact:true}).click();
    await expect(page.getByText(`Unmasked · revision ${current.saved.revision+4} · ephemeral, not active`,{exact:true})).toBeVisible();const saved=await call<Policy>(api,policyApi);expect(saved.effective).toEqual(current.effective);
    expect(mutations).toHaveLength(4);expect(errors).toEqual([]);expect(legacy).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  }finally{await api.dispose();}
});

test('tenant Owner has no platform policy entry or data, and retired endpoints have no owner-header fallback', async({page})=>{
  const api=await requests.newContext({baseURL:process.env.TENANT_CHAT_TEST_BASE_URL,storageState:identity});
  try {
    const tenant=(await call<{items:{tenantId:string}[]}>(api,'/api/v2/account/tenants')).items[0].tenantId;const root='/api/v2/tenants/'+tenant;
    const roles=await call<{items:{roleId:string;key:string}[]}>(api,root+'/roles');const owner=roles.items.find(r=>r.key==='owner')!;const revision=(await call<{revision:number}>(api,root+'/access/me')).revision;
    const email=`privacy-owner-${crypto.randomUUID().slice(0,8)}@example.test`,secret='synthetic-privacy-owner-password';
    const invite=await call<{invitationId:string;token:string}>(api,root+'/members/invite',{login:email,roleId:owner.roleId,expectedRevision:revision});
    const member=await requests.newContext({baseURL:process.env.TENANT_CHAT_TEST_BASE_URL});try{
      await call(member,'/api/v2/auth/register',{login:email,displayName:'Organization-only privacy owner',password:secret,invitation:{tenantId:tenant,invitationId:invite.invitationId,token:invite.token}});
      await call(member,'/api/v2/auth/login',{login:email,password:secret});expect((await member.get(policyApi)).status()).toBe(403);
    }finally{await member.dispose();}
    await signIn(page,email,secret);await expect(page.getByRole('link',{name:'Browser privacy',exact:true})).toHaveCount(0);let reads=0;page.on('request',r=>{if(new URL(r.url()).pathname===policyApi)reads++;});await page.goto('/system/browser-privacy');
    await expect(page.getByText('This page is unavailable. Choose another organization or contact its owner.',{exact:true})).toBeVisible();await expect(page.getByRole('region',{name:'Screenshot privacy'})).toHaveCount(0);expect(reads).toBe(0);
    expect((await api.get('/api/v2/admin/browser/privacy')).status()).toBe(404);await page.goto('/system/unknown-route');await expect(page.getByRole('heading',{name:'Page not found',exact:true})).toBeVisible();
  }finally{await api.dispose();}
});

for (const width of [1440, 390]) test(`policy read recovery, keyboard and explicit draft discard at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 }); await signIn(page);
  await page.route('**'+policyApi, route => route.fulfill({status:503,contentType:'application/problem+json',body:JSON.stringify({title:'temporarily_unavailable'})}));
  await page.goto('/system/browser-privacy');
  await expect(page.getByText('Read current policy before saving again',{exact:true})).toBeVisible();
  await expect(page.getByRole('combobox',{name:/Saved screenshot privacy mode/})).toHaveCount(0);
  await page.unroute('**'+policyApi); await page.getByRole('button',{name:'Reload saved policy',exact:true}).click();
  const mode=page.getByRole('combobox',{name:/Saved screenshot privacy mode/});
  await expect(mode).toBeVisible();await mode.focus();await page.keyboard.press('ArrowDown');await page.keyboard.press('Escape');await expect(mode).toBeFocused();
  const initial=await mode.locator('..').innerText();let writes=0;page.on('request',r=>{if(new URL(r.url()).pathname===policyApi&&r.method()==='PUT')writes++;});
  await choose(page,'Saved screenshot privacy mode',initial.includes('Disabled')?'Protected — mask sensitive pixels':'Disabled — no screenshots');
  const unload=()=>page.evaluate(()=>{const e=new Event('beforeunload',{cancelable:true});window.dispatchEvent(e);return e.defaultPrevented;});
  await expect.poll(unload).toBe(true);
  const navigate=async(name:string)=>{const opener=page.getByRole('button',{name:'Open Admin navigation',exact:true});if(await opener.isVisible())await opener.click();await page.getByRole('navigation',{name:'Admin navigation',exact:true}).getByRole('link',{name,exact:true}).click();};
  await navigate('My organizations');const dialog=page.getByRole('dialog',{name:'Discard unsaved changes?',exact:true});await dialog.getByRole('button',{name:'Stay',exact:true}).click();await expect.poll(unload).toBe(true);
  await navigate('My organizations');await dialog.getByRole('button',{name:'Discard and leave',exact:true}).click();await expect(page).toHaveURL(/\/account\/organizations$/);await expect.poll(unload).toBe(false);
  await navigate('Browser privacy');await expect(mode.locator('..')).toContainText(initial);await expect.poll(unload).toBe(false);expect(writes).toBe(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});
