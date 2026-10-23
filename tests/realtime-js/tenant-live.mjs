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
    const directory=await this.json(prefix+'/chat/agents?page=0&pageSize=100');
    const selected=directory.items.find(item=>item.instanceId===agent.instance.instanceId);
    assert.ok(selected);assert.equal(typeof selected.voiceAvailable,'boolean');assert.equal(directory.canCreateSessions,true);
    assert.ok(!JSON.stringify(selected).match(/ownerCapability|systemIdentityId|principalId|settingsOverrides|executionBudgets|harnessManagement|tone/i));
    return (await this.json(prefix+'/sessions',{agentInstanceId:selected.instanceId,expectedRevision:directory.securityRevision,expectedInstanceRevision:selected.revision})).sessionId;
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
  await assert.rejects(connect(null), /WebSocket|401|Unauthorized|Unexpected server response/i);
  for(const origin of ['https://foreign.example.test',null]) {
    const denied=await connect(human,origin); const ack=await denied.hub.invoke('AttachTenant',attach(session,tenant)); assert.equal(ack.accepted,false);assert.equal(ack.error.code,'origin_not_allowed');assert.equal(denied.events.length,0);
  }
  const live=await connect(human); assert.equal((await live.hub.invoke('AttachTenant',attach(session,tenant))).accepted,true);
  const firstReady=await until(()=>live.events.find(e=>e.type==='session.ready'));
  assert.equal(firstReady.payload.tenantId,tenant);assert.equal(firstReady.payload.pendingMode,null);assert.deepEqual(firstReady.payload.history,[]);
  assert.equal(firstReady.sequence,1);assert.ok(firstReady.attachmentId);
  const sendCommand={protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:1,timestamp:new Date().toISOString(),
    attachmentId:firstReady.attachmentId,type:'user.text',payload:{text:'Hello tenant MessagePack',expectedRevision:await human.revision(tenant),expectedSessionRevision:firstReady.payload.sessionRevision,
      principalId:crypto.randomUUID(),tenantId:crypto.randomUUID(),rootActivationId:crypto.randomUUID()}};
  const sent=await live.hub.invoke('SendText',sendCommand);assert.equal(sent.accepted,true);assert.equal(sent.result.created,true);const receipt=sent.result;
  const terminal=await until(()=>live.events.find(e=>e.type==='agent.response.completed'&&e.responseId===receipt.responseId));
  assert.equal(terminal.payload.status,'completed');
  const deltas=live.events.filter(e=>e.type==='agent.text.delta'&&e.responseId===receipt.responseId); assert.ok(deltas.length>0);
  let text='';for(const delta of deltas){assert.equal(delta.payload.textStart,text.length);text+=delta.payload.text;}
  assert.equal(text,'Hello from synthetic.');
  for(let i=1;i<live.events.length;i++)assert.equal(live.events[i].sequence,live.events[i-1].sequence+1);
  assert.ok(live.events.every(e=>e.attachmentId===firstReady.attachmentId));
  assert.equal(live.events.find(e=>e.type==='agent.response.started').payload.agentRunId,receipt.runId);
  assert.equal((await human.finished(tenant,receipt.runId)).status,'completed');
  const repeat=await live.hub.invoke('SendText',{...sendCommand,timestamp:new Date().toISOString()});assert.equal(repeat.accepted,true);assert.equal(repeat.result.runId,receipt.runId);
  const changed=await live.hub.invoke('SendText',{...sendCommand,payload:{...sendCommand.payload,text:'Different command'}});assert.equal(changed.accepted,false);assert.equal(changed.error.code,'ProtocolError');assert.equal(changed.error.category,'Protocol');
  const gap=await live.hub.invoke('SendText',{...sendCommand,eventId:crypto.randomUUID(),sequence:3});assert.equal(gap.accepted,false);assert.equal(gap.error.code,'StaleCommand');
  const staleLease=await live.hub.invoke('SendText',{...sendCommand,eventId:crypto.randomUUID(),sequence:2,attachmentId:crypto.randomUUID()});assert.equal(staleLease.accepted,false);assert.equal(staleLease.error.code,'StaleAttachment');
  const stale=await live.hub.invoke('SendText',{...sendCommand,eventId:crypto.randomUUID(),sequence:2});assert.equal(stale.accepted,false);assert.equal(stale.error.code,'stale_revision');
  const context=await human.json(`/api/v2/tenants/${tenant}/agent-runs/${receipt.runId}`);
  assert.equal(context.context.originalInitiatorPrincipalId,(await human.json(`/api/v2/tenants/${tenant}/access/me`)).principalId);
  const page=await until(async()=>{const r=await human.request(`/api/v2/tenants/${tenant}/sessions/${session}/messages`);return r.ok?r.json():null;});
  assert.equal(page.items.find(e=>e.responseId===receipt.responseId).receivedTextEndExclusive,0);
  const before=live.events.length;
  assert.equal((await live.hub.invoke('AttachTenant',attach(session,tenant))).accepted,true);
  const secondReady=await until(()=>live.events.slice(before).find(e=>e.type==='session.ready'));
  assert.notEqual(secondReady.attachmentId,firstReady.attachmentId);assert.equal(secondReady.sequence,1);assert.equal(secondReady.payload.history.length,2);
  const replay=await live.hub.invoke('SendText',{...sendCommand,attachmentId:secondReady.attachmentId,payload:{text:sendCommand.payload.text,expectedRevision:await human.revision(tenant),expectedSessionRevision:secondReady.payload.sessionRevision}});
  assert.equal(replay.accepted,true);assert.equal(replay.result.created,false);assert.equal(replay.result.runId,receipt.runId);
  const delivery={protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:2,attachmentId:secondReady.attachmentId,
    responseId:receipt.responseId,type:'response.received',payload:{textEndExclusive:text.length+1}};
  assert.equal((await live.hub.invoke('ResponseReceived',delivery)).accepted,false);
  const delivered={...delivery,eventId:crypto.randomUUID(),sequence:3,payload:{textEndExclusive:text.length}};
  assert.equal((await live.hub.invoke('ResponseReceived',delivered)).accepted,true);
  assert.equal((await live.hub.invoke('ResponseReceived',delivered)).accepted,true);
  const credited=await human.json(`/api/v2/tenants/${tenant}/sessions/${session}/messages`);
  const creditedEntry=credited.items.find(e=>e.responseId===receipt.responseId);
  assert.equal(creditedEntry.receivedTextEndExclusive,text.length);assert.equal(creditedEntry.heardTextEndExclusive,0);
  // Structured reference identity survives actual MessagePack admission and replay. A selection
  // of this conversation uses its existing context rather than copying its growing history.
  const alphaPrefix=`/api/v2/tenants/${tenant}`;
  const readRole=await human.json(alphaPrefix+'/roles',{name:'Composer exact source reader',permissions:['agents.read','sessions.read'],expectedRevision:await human.revision(tenant)});
  await human.json(alphaPrefix+'/role-assignments',{principalId:context.context.agentPrincipalId,roleId:readRole.roleId,expectedRevision:await human.revision(tenant)});
  const refMetadata=await human.json(alphaPrefix+`/sessions/${session}`);
  const referenceCommand={protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:4,attachmentId:secondReady.attachmentId,type:'user.text',
    payload:{text:`@session:${session}`,parts:[{kind:'reference',reference:{kind:'session',sessionId:session,selectedRevision:refMetadata.revision}}],expectedRevision:await human.revision(tenant),expectedSessionRevision:refMetadata.revision}};
  const refAck=await live.hub.invoke('SendText',referenceCommand);assert.equal(refAck.accepted,true,JSON.stringify(refAck));
  await human.finished(tenant,refAck.result.runId);
  const refReplay=await live.hub.invoke('SendText',referenceCommand);assert.equal(refReplay.accepted,true,JSON.stringify(refReplay));assert.equal(refReplay.result.runId,refAck.result.runId);
  const alteredRef=await live.hub.invoke('SendText',{...referenceCommand,payload:{...referenceCommand.payload,parts:[{kind:'reference',reference:{kind:'session',sessionId:session,selectedRevision:refMetadata.revision+1}}]}});
  assert.equal(alteredRef.accepted,false);assert.equal(alteredRef.error.code,'ProtocolError');
  const typedHistory=await human.json(alphaPrefix+`/sessions/${session}/messages`);
  assert.equal(typedHistory.items.find(e=>e.entryId===refAck.result.entryId).parts[0].reference.sessionId,session);
  const invalidRef=await live.hub.invoke('SendText',{...referenceCommand,eventId:crypto.randomUUID(),sequence:5,payload:{...referenceCommand.payload,expectedRevision:await human.revision(tenant),expectedSessionRevision:(await human.json(alphaPrefix+`/sessions/${session}`)).revision,
    parts:[{kind:'reference',reference:{kind:'session',sessionId:session,unexpected:'not-authority'}}]}});
  assert.equal(invalidRef.accepted,false);assert.equal(invalidRef.error.category,'Validation');
  const beta=(await human.json('/api/v2/system/tenants',{name:'Live beta',slug:'live-beta'})).tenantId;
  const second=await human.session(beta);
  assert.equal((await live.hub.invoke('AttachTenant',attach(second,beta))).accepted,true);
  const switchAt=live.events.length;
  const old=await human.send(tenant,session,'A later Alpha turn');assert.equal((await human.finished(tenant,old.runId)).status,'completed');
  const betaReady=[...live.events].reverse().find(e=>e.type==='session.ready');
  const nextCommand={protocolVersion:1,sessionId:second,eventId:crypto.randomUUID(),sequence:1,attachmentId:betaReady.attachmentId,type:'user.text',payload:{text:'A Beta turn',expectedRevision:await human.revision(beta),expectedSessionRevision:betaReady.payload.sessionRevision}};
  const betaAck=await live.hub.invoke('SendText',nextCommand);assert.equal(betaAck.accepted,true);const next=betaAck.result;await until(()=>live.events.slice(switchAt).some(e=>e.type==='agent.response.completed'&&e.responseId===next.responseId));
  assert.ok(live.events.slice(switchAt).every(e=>e.sessionId===second));
  const betaContext=await human.json(`/api/v2/tenants/${beta}/agent-runs/${next.runId}`);assert.notEqual(betaContext.context.originalInitiatorPrincipalId,context.context.originalInitiatorPrincipalId);
  const foreignCommand=await live.hub.invoke('SendText',{...sendCommand,eventId:crypto.randomUUID(),sequence:2});assert.equal(foreignCommand.accepted,false);assert.equal(foreignCommand.error.code,'StaleAttachment');
  const foreign=await live.hub.invoke('AttachTenant',attach(session,beta));assert.equal(foreign.accepted,false);
  assert.equal((await live.hub.invoke('AttachTenant',attach(second,beta))).accepted,true);
  const resumedBeta=[...live.events].reverse().find(e=>e.type==='session.ready');
  const cacheCommand={...nextCommand,eventId:crypto.randomUUID(),attachmentId:resumedBeta.attachmentId,payload:{text:'A cached Beta admission',expectedRevision:await human.revision(beta),expectedSessionRevision:resumedBeta.payload.sessionRevision}};
  const cached=await live.hub.invoke('SendText',cacheCommand);assert.equal(cached.accepted,true);
  await until(()=>live.events.some(e=>e.type==='agent.response.completed'&&e.responseId===cached.result.responseId));await human.finished(beta,cached.result.runId);
  const betaReceipt={protocolVersion:1,sessionId:second,eventId:crypto.randomUUID(),sequence:2,attachmentId:resumedBeta.attachmentId,
    responseId:cached.result.responseId,type:'response.received',payload:{textEndExclusive:5}};
  assert.equal((await live.hub.invoke('ResponseReceived',betaReceipt)).accepted,true);
  const logoutAt=live.events.length;assert.equal((await human.request('/api/v2/auth/logout',{})).status,204);
  const revokedReplay=await live.hub.invoke('SendText',cacheCommand);assert.equal(revokedReplay.accepted,false);assert.equal(revokedReplay.error.category,'Authorization');
  assert.equal((await live.hub.invoke('ResponseReceived',betaReceipt)).accepted,false);
  const fresh=new Human();await fresh.login();const after=await fresh.send(beta,second,'After old browser logout');await fresh.finished(beta,after.runId);
  assert.equal(live.events.length,logoutAt);
  const deniedCommand=await live.hub.invoke('SendText',{...nextCommand,eventId:crypto.randomUUID(),sequence:1});assert.equal(deniedCommand.accepted,false);
  assert.ok(!JSON.stringify(firstReady.payload).match(/ownerCapability|summary|definition|protectedPayload|permissionSnapshot/i));
  console.log('Tenant live wire passed');
} finally {await Promise.allSettled(connections.map(connection=>connection.stop()));}
