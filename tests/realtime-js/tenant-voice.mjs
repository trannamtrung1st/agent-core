import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
const base=process.env.BASE_URL;
const browser=process.env.VOICE_TRANSPORT==='client';
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(read){const end=Date.now()+25000;while(Date.now()<end){const value=await read();if(value)return value;await pause(20);}throw new Error('Expected tenant voice output did not arrive');}
const cookies=new Map();let csrf='';
const cookie=()=>[...cookies].map(([k,v])=>`${k}=${v}`).join('; ');
async function request(path,body){const res=await fetch(base+path,{method:body===undefined?'GET':'POST',headers:{Cookie:cookie(),Origin:base,'Content-Type':'application/json',...(csrf?{'X-AgentCore-CSRF':csrf}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const value of res.headers.getSetCookie()){const raw=value.split(';')[0],at=raw.indexOf('=');cookies.set(raw.slice(0,at),raw.slice(at+1));}return res;}
async function json(path,body){const res=await request(path,body);assert.ok(res.ok,`${path}: ${res.status}: ${await res.clone().text()}`);return res.json();}
const connections=[];
async function connect(){const events=[],audio=[];const hub=new HubConnectionBuilder().withUrl(base+'/hubs/session',{skipNegotiation:true,transport:HttpTransportType.WebSockets,headers:{Cookie:cookie(),Origin:base}}).withHubProtocol(new MessagePackHubProtocol()).configureLogging('None').build();hub.on('SessionEvent',e=>events.push(e));hub.on('AudioOutput',a=>audio.push(a));await hub.start();connections.push(hub);return{hub,events,audio};}
try{
 csrf=(await json('/api/v2/auth/csrf')).token;await json('/api/v2/auth/login',{login:'owner@example.test',password:'explicit-synthetic-test-password'});csrf=(await json('/api/v2/auth/csrf')).token;
 const tenant=(await json('/api/v2/account/tenants')).items[0].tenantId,api=`/api/v2/tenants/${tenant}`;
 const revision=async()=>(await json(api+'/access/me')).revision;
 const createdAgent=await json(api+'/agents',{templateId:'examiner',version:1,expectedRevision:await revision()});
 const instance=createdAgent.instance.instanceId;
 const space=await json(api+`/agents/${instance}/private-space`);
 const role=await json(api+'/roles',{name:'Exact voice wire context',permissions:['spaces.read'],expectedRevision:await revision()});
 await json(api+'/role-assignments',{principalId:createdAgent.principalId,roleId:role.roleId,resourceType:'space',resourceId:space.spaceId,expectedRevision:await revision()});
 const session=(await json(api+'/sessions',{retentionKind:'saved',agentInstanceId:instance,expectedRevision:await revision(),expectedInstanceRevision:1})).sessionId;
 const live=await connect();const attach=()=>({protocolVersion:1,type:'session.attach',sessionId:session,eventId:crypto.randomUUID(),sequence:0,payload:{tenantId:tenant}});
 assert.equal((await live.hub.invoke('AttachTenant',attach())).accepted,true);
 const ready=await until(()=>live.events.find(e=>e.type==='session.ready'));let sequence=0;
 function command(type,payload,responseId){return{protocolVersion:1,type,sessionId:session,eventId:crypto.randomUUID(),sequence:++sequence,attachmentId:ready.attachmentId,payload,...(responseId?{responseId}:{})};}
 async function invoke(method,type,payload,responseId,accepted=true){const ack=await live.hub.invoke(method,command(type,payload,responseId));assert.equal(ack.accepted,accepted,`${method}: ${JSON.stringify(ack)}`);return ack;}
 await invoke('SetMode','session.mode.set',{mode:'voice'});
 const active=await until(()=>live.events.find(e=>e.type==='session.state.changed'&&e.payload.mode==='voice'&&e.payload.streamId));let stream=active.payload.streamId;
 if(!browser){
  const oldStream=stream;
  await live.hub.invoke('SendAudio',{protocolVersion:1,sessionId:session,attachmentId:ready.attachmentId,streamId:stream,frameSequence:2,sampleOffset:0,data:new Uint8Array(960)});
  const restarted=await until(()=>live.events.find(e=>e.type==='session.state.changed'&&e.payload.streamId&&e.payload.streamId!==oldStream));
  stream=restarted.payload.streamId;assert.equal(restarted.payload.mode,'voice');
  await invoke('SpeechStarted','user.speech.started',{streamId:oldStream,utteranceId:crypto.randomUUID(),sampleOffset:0,activityScore:.9},undefined,false);
  assert.ok(!live.events.some(e=>e.type==='transcript.final'));
 }
 const utterance=crypto.randomUUID();
 if(browser){
  await invoke('SpeechEvidence','client.speech.evidence',{streamId:stream,kind:'started',utteranceId:utterance});
  await invoke('SpeechEvidence','client.speech.evidence',{streamId:crypto.randomUUID(),kind:'final',utteranceId:utterance,text:'Forged stale stream'},undefined,false);
  await invoke('SpeechEvidence','client.speech.evidence',{streamId:stream,kind:'partial',utteranceId:utterance,revision:1,text:'Hello client speech',confidence:.9});
  await invoke('SpeechEvidence','client.speech.evidence',{streamId:stream,kind:'final',utteranceId:utterance,text:'Hello client speech',confidence:.9});
  await invoke('SpeechEvidence','client.speech.evidence',{streamId:stream,kind:'ended',utteranceId:utterance});
 }else{
  await invoke('SpeechStarted','user.speech.started',{streamId:stream,utteranceId:utterance,sampleOffset:0,activityScore:.9});
  await live.hub.invoke('SendAudio',{protocolVersion:1,sessionId:session,attachmentId:ready.attachmentId,streamId:stream,frameSequence:1,sampleOffset:0,data:new Uint8Array(960)});
  await invoke('SpeechEnded','user.speech.ended',{streamId:stream,utteranceId:utterance,sampleOffset:480,activityScore:.9,durationMs:300});
 }
 const final=await until(()=>live.events.find(e=>e.type==='transcript.final'&&e.payload.utteranceId===utterance));assert.equal(final.payload.text,browser?'Hello client speech':'Hello there');
 const started=await until(()=>live.events.find(e=>e.type==='agent.response.started')),response=started.responseId;
 const projection=await until(()=>live.events.find(e=>e.type==='agent.speech.projection'&&e.responseId===response));assert.ok(projection.payload.text.length>0);
 let samples=0;
 if(browser){await until(()=>live.events.find(e=>e.type==='speech.output.completed'&&e.responseId===response));assert.equal(live.audio.length,0);}
 else{const last=await until(()=>live.audio.find(a=>a.responseId===response&&a.isFinal));assert.ok(last.data instanceof Uint8Array);samples=last.sampleOffset+last.data.length/2;let offset=0,frame=1;for(const a of live.audio){assert.equal(a.attachmentId,ready.attachmentId);assert.equal(a.frameSequence,frame++);assert.equal(a.sampleOffset,offset);offset+=a.data.length/2;}assert.equal(offset,samples);}
 await invoke('PlaybackCompleted','playback.completed',{consumedSamples:samples,textEndExclusive:projection.payload.text.length},response);
 await until(()=>live.events.find(e=>e.type==='agent.response.completed'&&e.responseId===response));
 const history=await until(async()=>{const r=await request(api+`/sessions/${session}/messages`);return r.ok?r.json():null;});
 assert.equal(history.items.find(e=>e.responseId===response).heardTextEndExclusive,projection.payload.text.length);assert.equal(history.items.find(e=>e.entryId===final.payload.entryId).deliveryMode,'voice');
 await invoke('SetMuted','session.mute',{muted:true});
 await invoke('SetMuted','session.mute',{muted:false});
 const fresh=await until(()=>live.events.find(e=>e.type==='session.state.changed'&&e.payload.streamId&&e.payload.streamId!==stream));assert.notEqual(fresh.payload.streamId,stream);
 await invoke('SetMode','session.mode.set',{mode:'text'});
 const at=live.events.length;assert.equal((await live.hub.invoke('AttachTenant',attach())).accepted,true);
 const resumed=await until(()=>live.events.slice(at).find(e=>e.type==='session.ready'));assert.equal(resumed.payload.mode,'text');assert.equal(resumed.payload.pendingMode,null);assert.equal(resumed.payload.streamId,null);
 assert.equal(live.audio.filter(a=>a.responseId===response).length,live.audio.length);
 console.log('Tenant voice wire passed: '+(browser?'clientTranscript/clientSpeech':'serverAudio'));
}finally{await Promise.all(connections.map(h=>h.stop()));}
