import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
const base=process.env.BASE_URL, connections=[];
async function until(get) { const end=Date.now()+25000; while(Date.now()<end) { const v=await get();if(v)return v;await new Promise(r=>setTimeout(r,25)); }throw new Error('Expected Attachment wire state did not arrive'); }
class Human {
  cookies=new Map();csrf='';
  get cookie(){return [...this.cookies].map(([k,v])=>`${k}=${v}`).join('; ');}
  async request(path,body,method) {
    const form=body instanceof FormData;
    const response=await fetch(base+path,{method:method??(body===undefined?'GET':'POST'),headers:{Cookie:this.cookie,Origin:base,
      ...(!form?{'Content-Type':'application/json'}:{}),...(this.csrf?{'X-AgentCore-CSRF':this.csrf}:{})},body:body===undefined?undefined:form?body:JSON.stringify(body)});
    for(const value of response.headers.getSetCookie()){const token=value.split(';')[0],at=token.indexOf('=');this.cookies.set(token.slice(0,at),token.slice(at+1));}return response;
  }
  async json(path,body,method){const r=await this.request(path,body,method);assert.ok(r.ok,`${path}: ${r.status} ${await r.clone().text()}`);return r.json();}
  async login(login='owner@example.test',password='explicit-synthetic-test-password'){this.csrf=(await this.json('/api/v2/auth/csrf')).token;await this.json('/api/v2/auth/login',{login,password});this.csrf=(await this.json('/api/v2/auth/csrf')).token;}
  async revision(t){return (await this.json(`/api/v2/tenants/${t}/access/me`)).revision;}
  async witness(t,s){return {expectedRevision:await this.revision(t),expectedSessionRevision:(await this.json(`/api/v2/tenants/${t}/sessions/${s}`)).revision};}
  async upload(t,s,name='Wire source.txt'){const form=new FormData();form.append('file',new Blob(['Private wire source'],{type:'text/plain'}),name);const w=await this.witness(t,s);return (await this.json(`/api/v2/tenants/${t}/sessions/${s}/attachments?uploadId=${crypto.randomUUID()}&expectedRevision=${w.expectedRevision}&expectedSessionRevision=${w.expectedSessionRevision}`,form)).attachmentId;}
}
const attach=(t,s)=>({protocolVersion:1,sessionId:s,eventId:crypto.randomUUID(),sequence:0,timestamp:new Date().toISOString(),type:'session.attach',payload:{tenantId:t}});
async function connect(h,t,s){const events=[];const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers:{Cookie:h.cookie,Origin:base}}).withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();hub.on('SessionEvent',e=>events.push(e));await hub.start();connections.push(hub);assert.equal((await hub.invoke('AttachTenant',attach(t,s))).accepted,true);return {hub,events,ready:await until(()=>events.find(e=>e.type==='session.ready'))};}
try {
  const owner=new Human();await owner.login();const tenant=(await owner.json('/api/v2/account/tenants')).items[0].tenantId,api=`/api/v2/tenants/${tenant}`;
  const agent=await owner.json(api+'/agents',{templateId:'examiner',version:1,expectedRevision:await owner.revision(tenant)});
  async function session(t=tenant,a=agent){return (await owner.json(`/api/v2/tenants/${t}/sessions`,{agentInstanceId:a.instance.instanceId,expectedRevision:await owner.revision(t),expectedInstanceRevision:1})).sessionId;}
  const s=await session(),other=await session();
  async function role(name,permissions){return (await owner.json(api+'/roles',{name,permissions,expectedRevision:await owner.revision(tenant)})).roleId;}
  const baseRole=await role('Wire file sender',['sessions.read','agents.chat','agent-runs.read','attachments.create','attachments.delete']);
  const invite=await owner.json(api+'/members/invite',{login:'wire-sender@example.test',roleId:baseRole,expectedRevision:await owner.revision(tenant)});
  const sender=new Human();sender.csrf=(await sender.json('/api/v2/auth/csrf')).token;
  await sender.json('/api/v2/auth/register',{login:'wire-sender@example.test',displayName:'Wire sender',password:'explicit-wire-sender-password',invitation:{tenantId:tenant,invitationId:invite.invitationId,token:invite.token}});
  await sender.login('wire-sender@example.test','explicit-wire-sender-password');const principal=(await sender.json(api+'/access/me')).principalId;
  const readRole=await role('Wire file reads',['attachments.read']);const readGrant=await owner.json(api+'/role-assignments',{principalId:principal,roleId:readRole,expectedRevision:await owner.revision(tenant)});
  const agentRole=await role('Wire independent Agent file reads',['sessions.read','attachments.read']);await owner.json(api+'/role-assignments',{principalId:agent.principalId,roleId:agentRole,expectedRevision:await owner.revision(tenant)});
  const id=await sender.upload(tenant,s),foreignSession=await owner.upload(tenant,other);
  const beta=(await owner.json('/api/v2/system/tenants',{name:'Wire beta',slug:'wire-beta-files'})).tenantId;
  const betaAgent=await owner.json(`/api/v2/tenants/${beta}/agents`,{templateId:'examiner',version:1,expectedRevision:await owner.revision(beta)});
  const betaSession=await session(beta,betaAgent),foreignTenant=await owner.upload(beta,betaSession);
  const live=await connect(sender,tenant,s);let sequence=0;
  async function command(ids,text=''){return {protocolVersion:1,sessionId:s,eventId:crypto.randomUUID(),sequence:++sequence,timestamp:new Date().toISOString(),attachmentId:live.ready.attachmentId,type:'user.text',payload:{text,attachmentIds:ids,...await sender.witness(tenant,s)}};}
  for(const ids of [[id,id],['bad'],['00000000-0000-0000-0000-000000000000'],Array.from({length:11},()=>crypto.randomUUID())]){const ack=await live.hub.invoke('SendText',await command(ids));assert.equal(ack.accepted,false);assert.equal(ack.error.code,'ValidationError');}
  for(const foreign of [foreignSession,foreignTenant]){const ack=await live.hub.invoke('SendText',await command([foreign]));assert.equal(ack.accepted,false);assert.equal(ack.error.code,'attachment_unavailable');}
  assert.equal((await owner.json(api+`/sessions/${s}/messages`)).items.length,0);
  const sent=await command([id]);const ack=await live.hub.invoke('SendText',sent);assert.equal(ack.accepted,true);assert.equal(ack.result.created,true);
  await until(()=>live.events.find(e=>e.type==='agent.response.completed'&&e.responseId===ack.result.responseId));
  assert.equal((await live.hub.invoke('SendText',sent)).result.runId,ack.result.runId);
  const changed=await live.hub.invoke('SendText',{...sent,payload:{...sent.payload,attachmentIds:[foreignSession]}});assert.equal(changed.error.code,'ProtocolError');
  const rebound=await live.hub.invoke('SendText',await command([id]));assert.equal(rebound.accepted,false);assert.equal(rebound.error.code,'attachment_unavailable');
  const history=await owner.json(api+`/sessions/${s}/messages`);const entry=history.items.find(e=>e.entryId===ack.result.entryId);assert.equal(entry.attachments[0].attachmentId,id);
  const fresh=await connect(sender,tenant,s);const replay={...sent,sequence:1,attachmentId:fresh.ready.attachmentId,payload:{text:'',attachmentIds:[id],...await sender.witness(tenant,s)}};
  const factual=await fresh.hub.invoke('SendText',replay);assert.equal(factual.accepted,true);assert.equal(factual.result.created,false);assert.equal(factual.result.runId,ack.result.runId);
  const pending=await sender.upload(tenant,s,'Still pending.txt');
  const removed=await owner.request(api+`/role-assignments/${readGrant.assignmentId}?expectedRevision=${await owner.revision(tenant)}`,undefined,'DELETE');assert.equal(removed.status,204);
  const denied=await fresh.hub.invoke('SendText',replay);assert.equal(denied.accepted,false);assert.equal(denied.error.code,'attachment_unavailable');
  const freshFile={...replay,eventId:crypto.randomUUID(),sequence:2,payload:{text:'Keep draft',attachmentIds:[pending],...await sender.witness(tenant,s)}};
  const refused=await fresh.hub.invoke('SendText',freshFile);assert.equal(refused.accepted,false);assert.equal(refused.error.code,'attachment_unavailable');
  const plain={...replay,eventId:crypto.randomUUID(),sequence:3,payload:{text:'Plain text remains usable',...await sender.witness(tenant,s)}};
  const usable=await fresh.hub.invoke('SendText',plain);assert.equal(usable.accepted,true);await until(()=>fresh.events.find(e=>e.type==='agent.response.completed'&&e.responseId===usable.result.responseId));
  const content=await sender.request(api+`/sessions/${s}/attachments/${id}/content`);assert.equal(content.status,403);
  assert.equal((await owner.json(api+`/sessions/${s}/messages`)).items.filter(e=>e.role==='user'&&e.attachments?.some(a=>a.attachmentId===id)).length,1);
  console.log('Tenant Attachment MessagePack passed');
} finally {await Promise.allSettled(connections.map(c=>c.stop()));}
