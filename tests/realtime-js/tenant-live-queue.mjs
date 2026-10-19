import assert from 'node:assert/strict';
import {HubConnectionBuilder,HttpTransportType} from '@microsoft/signalr';
import {MessagePackHubProtocol} from '@microsoft/signalr-protocol-msgpack';
const base=process.env.BASE_URL,tenant=process.env.TENANT_ID,session=process.env.SESSION_ID;
const cookies=new Map();let csrf='';const cookie=()=>[...cookies].map(([k,v])=>`${k}=${v}`).join('; ');
async function call(path,body){const response=await fetch(base+path,{method:body===undefined?'GET':'POST',headers:{Cookie:cookie(),Origin:base,'Content-Type':'application/json',...(csrf?{'X-AgentCore-CSRF':csrf}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const value of response.headers.getSetCookie()){const token=value.split(';')[0],at=token.indexOf('=');cookies.set(token.slice(0,at),token.slice(at+1));}assert.ok(response.ok,`${path}: ${response.status}`);return response.json();}
async function until(get){const end=Date.now()+30000;while(Date.now()<end){const value=await get();if(value)return value;await new Promise(resolve=>setTimeout(resolve,25));}throw new Error('Expected queued response did not arrive');}
csrf=(await call('/api/v2/auth/csrf')).token;await call('/api/v2/auth/login',{login:'owner@example.test',password:'explicit-synthetic-test-password'});csrf=(await call('/api/v2/auth/csrf')).token;
const events=[];const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers:{Cookie:cookie(),Origin:base}}).withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();hub.on('SessionEvent',event=>events.push(event));
try{
 await hub.start();assert.equal((await hub.invoke('AttachTenant',{protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:0,timestamp:new Date().toISOString(),type:'session.attach',payload:{tenantId:tenant}})).accepted,true);
 const ready=await until(()=>events.find(e=>e.type==='session.ready'));
 const command={protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:1,timestamp:new Date().toISOString(),attachmentId:ready.attachmentId,type:'user.text',payload:{text:'Keep the queued file available: wire',behavior:'queue',expectedRevision:(await call(`/api/v2/tenants/${tenant}/access/me`)).revision,expectedSessionRevision:ready.payload.sessionRevision}};
 const accepted=await hub.invoke('SendText',command);assert.equal(accepted.accepted,true,JSON.stringify(accepted));
 assert.deepEqual(await hub.invoke('SendText',command),accepted);
 const interrupt=await hub.invoke('SendText',{...command,eventId:crypto.randomUUID(),sequence:2,payload:{...command.payload,behavior:'interrupt'}});assert.equal(interrupt.accepted,false);
 console.log('Queue accepted');
 await until(()=>events.find(e=>e.type==='agent.response.completed'&&e.responseId===accepted.result.responseId));
 const history=await call(`/api/v2/tenants/${tenant}/sessions/${session}/messages?limit=100`);assert.equal(history.items.filter(e=>e.responseId===accepted.result.responseId&&e.text==='Queued file noted.').length,1);
 assert.equal(history.items.filter(e=>e.text===command.payload.text).length,1);
 const metadata=await call(`/api/v2/tenants/${tenant}/agent-runs/${accepted.result.runId}`);assert.equal(metadata.context.rootActivationId,accepted.result.activationId);assert.equal(metadata.context.origin,'userMessage');
 assert.equal(events.filter(e=>e.type==='agent.response.completed'&&e.responseId===accepted.result.responseId).length,1);
 console.log('Tenant live queue wire passed');
}finally{await hub.stop();}
