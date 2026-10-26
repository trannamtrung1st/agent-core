#!/usr/bin/env python3
"""Opt-in setup for UI/HTTP PNG qualification using existing tenant owners, not a runtime tool."""
import sys,json,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from canonical_smoke import Client
import argparse,os
parser=argparse.ArgumentParser(description="Provision a disposable Synthetic tenant for the canonical PNG approval journey; never resets an existing tenant.")
parser.add_argument('--api',required=True);parser.add_argument('--image',required=True);parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
if os.environ.get('AGENTCORE_PNG_QUALIFICATION')!='1':raise SystemExit('Set AGENTCORE_PNG_QUALIFICATION=1 for the explicit real local Sandbox proof.')
c=Client(args.api)
if c.call('/health').get('profile')!='Synthetic':raise SystemExit('Qualification requires an explicitly disposable Synthetic host.')
c.login(os.environ.get('AGENTCORE_QUALIFICATION_LOGIN','tenant-chat-owner@example.test'),os.environ.get('AGENTCORE_QUALIFICATION_PASSWORD','synthetic-tenant-chat-password'))
original_open=c.opener.open
c.opener.open=lambda request,timeout=20:original_open(request,timeout=180)
nonce=uuid.uuid4().hex[:8]
t=c.call('/api/v2/system/tenants','POST',{'name':'Clean baseline '+nonce,'slug':'clean-baseline-'+nonce},expected=201)
p='/api/v2/tenants/'+t['tenantId']
def rev():return c.revision(p)
image=args.image
profile=c.call(p+'/sandbox-profiles','POST',{'name':'PNG proof','description':'Disposable local image','enabled':True,'imageReference':image,'expectedRevision':rev(),'expectedProfileRevision':0},expected=201)['profile']
pid=profile['id']
verified=c.call(p+'/sandbox-profiles/'+pid+'/verify','POST',{'expectedRevision':rev(),'expectedProfileRevision':1})
print('Profile verification:',verified.get('attempt',{}).get('result',{}).get('passed'),flush=True)
if verified.get('attempt',{}).get('result',{}).get('passed') is not True:
 raise SystemExit('Profile verification failed; inspect the authorized profile verification view.')
draft=c.call(p+'/agent-definitions','POST',{'name':'PNG verification assistant','expectedRevision':rev()},expected=201)
v=draft['candidate'];tools=['sandbox.exec','sandbox.profiles','sandbox.status','artifacts.create_from_workspace','workspace.read','workspace.write','workspace.list']
v['agentInstructions']=json.loads((Path(__file__).resolve().parents[2]/'agents/general-assistant-v34.json').read_text())['agentInstructions']
v['executionPolicy']={'workspace':{},'knowledgeSources':[],'capabilities':{'mode':'Selected','resolvedCapabilities':tools},'projection':{'alwaysCapabilities':tools}}
v['sandboxPolicy']={'defaultProfileId':pid,'allowedProfileIds':[pid]}
v['stateIsolationPolicy']={'homeAccess':'ReadWrite'}
updated=c.call(p+'/definition-drafts/'+draft['draftId'],'PUT',{'candidate':v,'expectedRevision':rev(),'expectedDraftRevision':1})
c.call(p+'/definition-drafts/'+draft['draftId']+'/publish','POST',{'expectedRevision':rev(),'expectedDraftRevision':updated['revision']})
a=c.call(p+'/agents/from-publication','POST',{'definitionId':draft['definitionId'],'version':1,'expectedRevision':rev()},expected=201)
role=c.call(p+'/roles','POST',{'name':'PNG fixture executor','permissions':['agents.read','agents.chat','spaces.read','sandbox.read','sandbox.execute','workspaces.read','workspaces.write','workspaces.delete','sessions.read','artifacts.create','artifacts.read'],'expectedRevision':rev()},expected=201)
c.call(p+'/role-assignments','POST',{'principalId':a['principalId'],'roleId':role['roleId'],'expectedRevision':rev()},expected=201)
instance=a['instance']['instanceId']
verified=c.call(p+'/agents/'+instance+'/sandbox/verify','POST',{'expectedRevision':rev(),'expectedInstanceRevision':1,'profileId':pid,'expectedProfileRevision':1})
print('Instance verification:',verified.get('status'),flush=True)
if verified.get('attempt',{}).get('result',{}).get('passed') is not True:sys.exit(2)
policy=c.call(p+'/operation-policies/artifacts.create')
c.call(p+'/operation-policies/artifacts.create','PUT',{'mode':'always','independentReview':False,'expectedRevision':policy['securityRevision'],'expectedPolicyRevision':policy['policyRevision']})
session=c.call(p+'/sessions','POST',{'agentInstanceId':instance,'retentionKind':'saved','expectedRevision':rev(),'expectedInstanceRevision':1},expected=201)
args.output.write_text(json.dumps({'tenantId':t['tenantId'],'instanceId':instance,'sessionId':session['sessionId'],'profileId':pid,'definitionId':draft['definitionId']},indent=2))
print('Disposable fixture prepared',flush=True)
