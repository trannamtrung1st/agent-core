import assert from 'node:assert/strict';
import {HubConnectionBuilder,HttpTransportType} from '@microsoft/signalr';
import {MessagePackHubProtocol} from '@microsoft/signalr-protocol-msgpack';
const base=process.env.BASE_URL,tenant=process.env.TENANT_ID,session=process.env.SESSION_ID;
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(get){const end=Date.now()+30000;while(Date.now()<end){const value=await get();if(value)return value;await pause(25);}throw new Error('Expected background state did not arrive');}
const cookies=new Map();let csrf='';
const cookie=()=>[...cookies].map(([k,v])=>`${k}=${v}`).join('; ');
async function call(path,body){const response=await fetch(base+path,{method:body===undefined?'GET':'POST',headers:{Cookie:cookie(),Origin:base,'Content-Type':'application/json',...(csrf?{'X-AgentCore-CSRF':csrf}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const value of response.headers.getSetCookie()){const token=value.split(';')[0],at=token.indexOf('=');cookies.set(token.slice(0,at),token.slice(at+1));}assert.ok(response.ok,`${path}: ${response.status}`);return response.json();}
csrf=(await call('/api/v2/auth/csrf')).token;await call('/api/v2/auth/login',{login:'owner@example.test',password:'explicit-synthetic-test-password'});csrf=(await call('/api/v2/auth/csrf')).token;
const events=[];const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers:{Cookie:cookie(),Origin:base}}).withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();
hub.on('SessionEvent',event=>events.push(event));
try{
 await hub.start();const attachment=await hub.invoke('AttachTenant',{protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:0,timestamp:new Date().toISOString(),type:'session.attach',payload:{tenantId:tenant}});assert.equal(attachment.accepted,true);
 const ready=await until(()=>events.find(e=>e.type==='session.ready'));
 const receipt=await hub.invoke('SendText',{protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:1,timestamp:new Date().toISOString(),attachmentId:ready.attachmentId,type:'user.text',payload:{text:'Run the exact owned background check',expectedRevision:(await call(`/api/v2/tenants/${tenant}/access/me`)).revision,expectedSessionRevision:ready.payload.sessionRevision}});assert.equal(receipt.accepted,true);
 const report=await until(()=>events.find(e=>e.type==='agent.response.completed'&&e.responseId!==receipt.result.responseId));
 const started=events.find(e=>e.type==='agent.response.started'&&e.responseId===report.responseId);assert.ok(started?.payload.agentRunId);
 assert.ok(events.every(e=>e.sessionId===session&&e.attachmentId===ready.attachmentId));
 const source=await call(`/api/v2/tenants/${tenant}/agent-runs/${receipt.result.runId}`);const metadata=await call(`/api/v2/tenants/${tenant}/agent-runs/${started.payload.agentRunId}`);
 assert.equal(metadata.context.rootActivationId,source.context.rootActivationId);assert.equal(metadata.context.originalInitiatorPrincipalId,source.context.originalInitiatorPrincipalId);assert.notEqual(metadata.context.parentRunId,source.context.runId);
 const entry=await until(async()=>{const page=await call(`/api/v2/tenants/${tenant}/sessions/${session}/messages`);return page.items.find(e=>e.responseId===report.responseId);});
 assert.equal(entry.text,'Background work: Owned background result.');assert.equal(entry.receivedTextEndExclusive,0);
 const ack=await hub.invoke('ResponseReceived',{protocolVersion:1,sessionId:session,eventId:crypto.randomUUID(),sequence:2,attachmentId:ready.attachmentId,responseId:report.responseId,type:'response.received',payload:{textEndExclusive:entry.text.length}});assert.equal(ack.accepted,true);
 await until(async()=>{const page=await call(`/api/v2/tenants/${tenant}/sessions/${session}/messages`);return page.items.find(e=>e.responseId===report.responseId)?.receivedTextEndExclusive===entry.text.length;});
 assert.equal(events.filter(e=>e.type==='agent.response.completed'&&e.responseId===report.responseId).length,1);
 console.log('Tenant background wire passed');
}finally{await hub.stop();}
