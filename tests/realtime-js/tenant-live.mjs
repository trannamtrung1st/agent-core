import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
const base = process.env.BASE_URL;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(get) { const end = Date.now()+25000; while(Date.now()<end) { const value=await get(); if(value) return value; await sleep(25); } throw new Error('Expected tenant state did not arrive'); }
class Human {
  cookies = new Map(); csrf = '';
  get cookie() { return [...this.cookies].map(([k,v])=>`${k}=${v}`).join('; '); }
  async request(path, body) {
    const response = await fetch(base+path, { method: body===undefined?'GET':'POST', headers: {Cookie:this.cookie, Origin:base,
      'Content-Type':'application/json', ...(this.csrf?{'X-AgentCore-CSRF':this.csrf}:{})}, body: body===undefined?undefined:JSON.stringify(body) });
    for(const value of response.headers.getSetCookie()) { const token=value.split(';')[0], at=token.indexOf('='); this.cookies.set(token.slice(0,at),token.slice(at+1)); }
    return response;
  }
  async json(path, body) { const response=await this.request(path,body); assert.ok(response.ok, `${path}: ${response.status} ${await response.clone().text()}`); return response.json(); }
  async login() { this.csrf=(await this.json('/api/v2/auth/csrf')).token; await this.json('/api/v2/auth/login',{login:'owner@example.test',password:'explicit-synthetic-test-password'}); this.csrf=(await this.json('/api/v2/auth/csrf')).token; }
  async revision(tenant) {return (await this.json(`/api/v2/tenants/${tenant}/access/me`)).revision;}
  async session(tenant) {
    const prefix=`/api/v2/tenants/${tenant}`;
    const agent=await this.json(prefix+'/agents',{templateId:'examiner',version:1,expectedRevision:await this.revision(tenant)});
    return (await this.json(prefix+'/sessions',{agentInstanceId:agent.instance.instanceId,expectedRevision:await this.revision(tenant),expectedInstanceRevision:1})).sessionId;
  }
  async send(tenant,session,text) {
    const prefix=`/api/v2/tenants/${tenant}`, metadata=await this.json(prefix+`/sessions/${session}`);
    return this.json(prefix+`/sessions/${session}/messages`,{messageId:crypto.randomUUID(),text,expectedRevision:await this.revision(tenant),expectedSessionRevision:metadata.revision});
  }
  async finished(tenant,run) { return until(async()=>{const item=await this.json(`/api/v2/tenants/${tenant}/agent-runs/${run}`); return ['completed','failed','cancelled'].includes(item.status)?item:null;}); }
}
const connections=[];
async function connect(human,origin=base) {
  const events=[]; const headers={...(human?{Cookie:human.cookie}:{}),...(origin?{Origin:origin}:{})};
  const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers})
    .withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();
  hub.on('SessionEvent',event=>events.push(event)); await hub.start(); connections.push(hub); return {hub,events};
}
const attach=(session,tenant)=>({protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:0,timestamp:new Date().toISOString(),type:'session.attach',payload:{tenantId:tenant}});
try {
  const human=new Human(); await human.login();
  const tenant=(await human.json('/api/v2/account/tenants')).items[0].tenantId;
  const session=await human.session(tenant);
  const anonymous=await connect(null); await assert.rejects(anonymous.hub.invoke('AttachTenant',attach(session,tenant))); assert.equal(anonymous.events.length,0);
  for(const origin of ['https://foreign.example.test',null]) {
    const denied=await connect(human,origin); const ack=await denied.hub.invoke('AttachTenant',attach(session,tenant)); assert.equal(ack.accepted,false);assert.equal(ack.error.code,'origin_not_allowed');assert.equal(denied.events.length,0);
  }
  const live=await connect(human); assert.equal((await live.hub.invoke('AttachTenant',attach(session,tenant))).accepted,true);
  const firstReady=await until(()=>live.events.find(e=>e.type==='session.ready'));
  assert.equal(firstReady.payload.tenantId,tenant);assert.equal(firstReady.payload.pendingMode,null);assert.deepEqual(firstReady.payload.history,[]);
  assert.equal(firstReady.sequence,1);assert.ok(firstReady.attachmentId);
  const receipt=await human.send(tenant,session,'Hello tenant MessagePack');
  const terminal=await until(()=>live.events.find(e=>e.type==='agent.response.completed'&&e.responseId===receipt.responseId));
  assert.equal(terminal.payload.status,'completed');
  const deltas=live.events.filter(e=>e.type==='agent.text.delta'&&e.responseId===receipt.responseId); assert.ok(deltas.length>0);
  let text='';for(const delta of deltas){assert.equal(delta.payload.textStart,text.length);text+=delta.payload.text;}
  assert.equal(text,'Hello from synthetic.');
  for(let i=1;i<live.events.length;i++)assert.equal(live.events[i].sequence,live.events[i-1].sequence+1);
  assert.ok(live.events.every(e=>e.attachmentId===firstReady.attachmentId));
  assert.equal(live.events.find(e=>e.type==='agent.response.started').payload.agentRunId,receipt.runId);
  assert.equal((await human.finished(tenant,receipt.runId)).status,'completed');
  const page=await until(async()=>{const r=await human.request(`/api/v2/tenants/${tenant}/sessions/${session}/messages`);return r.ok?r.json():null;});
  assert.equal(page.items.find(e=>e.responseId===receipt.responseId).receivedTextEndExclusive,0);
  const before=live.events.length;
  assert.equal((await live.hub.invoke('AttachTenant',attach(session,tenant))).accepted,true);
  const secondReady=await until(()=>live.events.slice(before).find(e=>e.type==='session.ready'));
  assert.notEqual(secondReady.attachmentId,firstReady.attachmentId);assert.equal(secondReady.sequence,1);assert.equal(secondReady.payload.history.length,2);
  const beta=(await human.json('/api/v2/system/tenants',{name:'Live beta',slug:'live-beta'})).tenantId;
  const second=await human.session(beta);
  assert.equal((await live.hub.invoke('AttachTenant',attach(second,beta))).accepted,true);
  const switchAt=live.events.length;
  const old=await human.send(tenant,session,'A later Alpha turn');assert.equal((await human.finished(tenant,old.runId)).status,'completed');
  const next=await human.send(beta,second,'A Beta turn');await until(()=>live.events.slice(switchAt).some(e=>e.type==='agent.response.completed'&&e.responseId===next.responseId));
  assert.ok(live.events.slice(switchAt).every(e=>e.sessionId===second));
  const foreign=await live.hub.invoke('AttachTenant',attach(session,beta));assert.equal(foreign.accepted,false);
  assert.equal((await live.hub.invoke('AttachTenant',attach(second,beta))).accepted,true);
  const logoutAt=live.events.length;assert.equal((await human.request('/api/v2/auth/logout',{})).status,204);
  const fresh=new Human();await fresh.login();const after=await fresh.send(beta,second,'After old browser logout');await fresh.finished(beta,after.runId);
  assert.equal(live.events.length,logoutAt);
  assert.ok(!JSON.stringify(firstReady.payload).match(/ownerCapability|summary|definition|protectedPayload|permissionSnapshot/i));
  console.log('Tenant live wire passed');
} finally {await Promise.allSettled(connections.map(connection=>connection.stop()));}
