import assert from 'node:assert/strict';
import {HubConnectionBuilder,HttpTransportType} from '@microsoft/signalr';
import {MessagePackHubProtocol} from '@microsoft/signalr-protocol-msgpack';
const base=process.env.BASE_URL,tenant=process.env.TENANT_ID,session=process.env.SESSION_ID;
const cookies=new Map();let csrf='';const cookie=()=>[...cookies].map(([k,v])=>`${k}=${v}`).join('; ');
async function call(path,body){const r=await fetch(base+path,{method:body===undefined?'GET':'POST',headers:{Cookie:cookie(),Origin:base,'Content-Type':'application/json',...(csrf?{'X-AgentCore-CSRF':csrf}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const value of r.headers.getSetCookie()){const token=value.split(';')[0],at=token.indexOf('=');cookies.set(token.slice(0,at),token.slice(at+1));}assert.ok(r.ok,`${path}: ${r.status}`);return r.json();}
async function until(get){const end=Date.now()+30000;while(Date.now()<end){const v=await get();if(v)return v;await new Promise(r=>setTimeout(r,25));}throw new Error('Isolated Session did not settle');}
csrf=(await call('/api/v2/auth/csrf')).token;await call('/api/v2/auth/login',{login:'owner@example.test',password:'explicit-synthetic-test-password'});csrf=(await call('/api/v2/auth/csrf')).token;
const events=[];const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers:{Cookie:cookie(),Origin:base}}).withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();hub.on('SessionEvent',e=>events.push(e));
const attach=()=>hub.invoke('AttachTenant',{protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:0,timestamp:new Date().toISOString(),type:'session.attach',payload:{tenantId:tenant}});
try{
 await hub.start();assert.equal((await attach()).accepted,true);const ready=await until(()=>events.find(e=>e.type==='session.ready'));
 const eventId=crypto.randomUUID();const command={protocolVersion:1,sessionId:session,eventId,sequence:1,timestamp:new Date().toISOString(),attachmentId:ready.attachmentId,type:'user.text',payload:{text:'Start isolated background work',expectedRevision:(await call(`/api/v2/tenants/${tenant}/access/me`)).revision,expectedSessionRevision:ready.payload.sessionRevision}};
 const ack=await hub.invoke('SendText',command);assert.equal(ack.accepted,true);
 const completed=await until(()=>events.find(e=>e.type==='session.state.changed'&&e.payload.lifecycleStatus==='completed'));
 assert.equal(completed.payload.status,'ended');assert.equal(completed.payload.pendingMode,null);assert.equal(completed.payload.streamId,null);
 const before=events.filter(e=>e.attachmentId===ready.attachmentId);assert.ok(before.every((e,i)=>e.sessionId===session&&(!i||e.sequence>before[i-1].sequence)));
 assert.equal(events.filter(e=>e.type==='agent.response.completed'&&e.responseId===ack.result.responseId).length,1);
 assert.equal((await hub.invoke('SendText',command)).accepted,true); // Factual transport retry, no new execution.
 const current=await call(`/api/v2/tenants/${tenant}/sessions/${session}`);assert.equal(current.lifecycleStatus,'completed');
 const denied=await hub.invoke('SendText',{...command,eventId:crypto.randomUUID(),sequence:2,payload:{...command.payload,text:'Another independent root',expectedRevision:(await call(`/api/v2/tenants/${tenant}/access/me`)).revision,expectedSessionRevision:current.revision}});assert.equal(denied.accepted,false);assert.equal(denied.error.code,'fresh_session_required');
 await hub.stop();await hub.start();assert.equal((await attach()).accepted,true);
 const resumed=await until(()=>events.find(e=>e.type==='session.ready'&&e.attachmentId!==ready.attachmentId));assert.equal(resumed.payload.lifecycleStatus,'completed');
 console.log('Tenant request isolation wire passed');
}finally{await hub.stop();}
