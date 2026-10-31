// Source inventory only. Qualification lives in the reviewed manifest, never inferred from JSX.
import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';
import crypto from 'node:crypto';
const root = path.resolve(import.meta.dirname, '../..');
const destination = path.join(root, 'docs/reports/assets/product-experience/coverage.json');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const parse = file => ts.createSourceFile(file, read(file), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const sources = ['web/src/app/AppRouter.tsx', 'web/src/app/appRoute.ts', 'web/src/app/sessionRoute.ts', 'web/src/app/antdTheme.ts', 'web/src/app/AppShell.tsx', 'web/src/app.css', 'web/src/features/access/accessRoute.ts', 'web/src/features/access/AccessApp.tsx', 'web/src/features/access/experienceNavigation.ts', 'web/src/features/admin/adminNavigation.ts'];
const rows = [];
const add = (surface_id, route_or_entry, scope, feature_owner, primary_job, journey_ids) => rows.push({surface_id, route_or_entry, scope, roles: ['authorized reader', 'permitted editor', 'denied caller'], feature_owner, primary_job, states: ['loading', 'empty', 'populated', 'error', 'read-only/denied', 'stale/unsaved where applicable'], journey_ids, baseline_screenshots: [], design_decision: 'redesign: shared experience; preserve operation and authority', implementation_status: 'pending', viewport_checks: [], visual_review: 'pending', interaction_tests: [], accessibility_review: 'pending', docs_synced: false, evidence_path: null, blocker: null});
const access = 'web/src/features/access/experienceNavigation.ts', routes = 'web/src/features/access/accessRoute.ts';
const accessOwner = name => `web/src/features/access/${name}.tsx`;
const tenantOwners = { 'my-profile': 'TenantUserProfile', agents: 'TenantAgents', workflows: '../workflows/TenantWorkflows', spaces: 'TenantSpaces', approvals: 'TenantApprovals', definitions: 'TenantDefinitions', evaluations: 'TenantEvaluations', 'ai-models': 'TenantModels', secrets: 'TenantSecrets', credentials: 'TenantCredentials', events: 'TenantEvents', sandboxes: 'TenantSandboxes', members: 'Members', roles: 'Roles', governance: 'TenantGovernance', audit: 'TenantAudit', access: 'AccessApp' };
const agentOwners = { memory: 'TenantAgentMemory', automations: 'TenantAgentAutomations', 'activity-sessions': 'TenantAgentActivity', 'activity-runs': 'TenantAgentActivity', state: 'TenantAgentState', 'state-isolation': 'TenantAgentIsolation', sandbox: 'TenantAgentSandbox', resources: 'TenantAgentResources', skills: 'TenantAgentSkills', version: 'TenantAgentVersion', 'execution-budgets': 'TenantAgentControls', 'self-management': 'TenantAgentControls', 'unattended-model': 'TenantAgentControls', profile: 'TenantAgentConfiguration', settings: 'TenantAgentConfiguration', lifecycle: 'TenantAgentLifecycle', connections: 'TenantAgentConnections', access: 'TenantAgentAccess' };
function array(name, file) { let found; const visit = node => {if(ts.isVariableDeclaration(node) && node.name.getText() === name) found = node.initializer; ts.forEachChild(node,visit);}; visit(parse(file)); if(!found) throw Error(`Missing ${name}`); return found; }
function union(name) {let result;const visit=node=>{if(ts.isTypeAliasDeclaration(node)&&node.name.text===name)result=node.type.types.map(t=>t.literal?.text).filter(Boolean);ts.forEachChild(node,visit);};visit(parse(routes));if(!result)throw Error(name);return result;}
function pageCalls(name){const out=[]; const visit=node=>{if(ts.isCallExpression(node)&&node.expression.getText()==='page')out.push({id:node.arguments[0].text,label:node.arguments[1].text,description:node.arguments[2].text});ts.forEachChild(node,visit);};visit(array(name,access));return out;}
for (const [type, registry, prefix] of [['AccessPage', 'tenantPages', ''], ['TenantInstancePage', 'instancePages', 'instance-']]) {
  const declared = union(type).sort(), registered = pageCalls(registry).map(p => p.id.slice(prefix.length)).sort();
  if (JSON.stringify(declared) !== JSON.stringify(registered)) throw Error(`${type} differs from ${registry}`);
}
const journeys = id => id.includes('model') ? ['J08'] : ['members','roles','access','governance','audit'].includes(id) ? ['J13','J15'] : id==='spaces' ? ['J07'] : id==='approvals' ? ['J12'] : id==='workflows' ? ['J10'] : id==='evaluations' ? ['J11'] : ['J06'];
add('entry.sign-in','/sign-in','public','web/src/features/access/AccountForms.tsx','Sign in / expired-session recovery',['J01','J15']);
add('entry.invitation','/invitations/:invitationId','public','web/src/features/access/InvitationAcceptance.tsx','Accept invitation',['J01']);
add('entry.not-found', 'Unrecognized or retired URL', 'public/account', accessOwner('AccessApp'), 'Recover from a missing page without inferring tenant authority', ['J01','J15']);
for(const p of pageCalls('accountPages'))add(`account.${p.id}`,`/account/${p.id}`,'account',accessOwner(p.id === 'profile' ? 'AccountForms' : 'Organizations'),p.description,['J01']);
for(const p of pageCalls('systemPages'))add(`system.${p.id}`,`/system/${p.id.replace('system-','')}`,'system',accessOwner(p.id === 'system-model-presets' ? 'SystemModelPresets' : 'SystemAdministration'),p.description,['J14']);
for(const [id,title] of [['browser-privacy','Browser privacy'],['audit','Platform audit']])add(`system.${id}`,`/system/${id}`,'system',id === 'browser-privacy' ? 'web/src/features/admin/BrowserPrivacySection.tsx' : accessOwner('TenantAudit'),title,['J14']);
for(const p of pageCalls('tenantPages')){
const route = p.id==='my-profile'?'profile':p.id==='ai-models'?'integrations/ai-models/connections':['secrets','credentials','events','sandboxes'].includes(p.id)?`integrations/${p.id}`:`admin/${p.id}`;
add(`tenant.${p.id}`,`/t/:tenantId/${route}`,'organization',path.normalize(accessOwner(tenantOwners[p.id])),p.description,journeys(p.id));}
for(const p of pageCalls('instancePages'))add(`agent.${p.id.replace('instance-','')}`,`/t/:tenantId/admin/agents/:instanceId/${p.id.replace('instance-','').replace('activity-','activity/')}`,'agent',accessOwner(agentOwners[p.id.slice('instance-'.length)]),p.description,['J06',...(p.id.includes('activity')?['J03','J09']:[]),...(p.id.includes('automation')?['J11']:[])]);
add('definition.detail','/t/:tenantId/admin/definitions/:definitionId','organization',accessOwner('TenantDefinitionDetail'),'Inspect versions / create revisioned draft',['J06']);
const draftArray=array('draftPages','web/src/features/admin/adminNavigation.ts');
const draftVisit = node => {if(ts.isObjectLiteralExpression(node)){const props=Object.fromEntries(node.properties.filter(ts.isPropertyAssignment).map(p=>[p.name.getText().replaceAll('"','').replaceAll("'",''),p.initializer.text])); if(props.id&&props.path)add(`draft.${props.id}`,`/t/:tenantId/admin/definitions/:definitionId/drafts/:draftId/${props.path}`,'definition draft','web/src/features/access/TenantDefinitionEditor.tsx',props.description,['J06']);}ts.forEachChild(node,draftVisit);};draftVisit(draftArray);
for(const id of union('TenantSpacePage'))add(`space.${id}`,`/t/:tenantId/admin/spaces/:spaceId/${id}`,'Space','web/src/features/access/TenantSpaces.tsx',`Space ${id}`,['J07','J05']);
for(const id of union('ModelAdminPage').filter(x=>x!=='connections'))add(`models.${id}`,`/t/:tenantId/integrations/ai-models/${id}`,'organization','web/src/features/access/TenantModels.tsx',`Model ${id}`,['J08']);
for(const [id,route,job] of [['new','/t/:tenantId','Start Chat'],['active','/t/:tenantId/c/:sessionId','Continue saved/temporary Chat'],['history','/t/:tenantId/c/:sessionId','Read ended Chat']])add(`chat.${id}`,route,'Session','web/src/features/chat/ChatApp.tsx',job,['J02','J03','J04','J05']);
function files(folder){return fs.readdirSync(folder,{withFileTypes:true}).flatMap(d=>d.isDirectory()?files(path.join(folder,d.name)):[path.join(folder,d.name)]);}
for(const full of files(path.join(root,'web/src/features')).filter(p=>p.endsWith('.tsx')&&!p.includes('.test.'))){const file=path.relative(root,full);const tree=parse(file);const counts={};const visit=node=>{if(ts.isJsxOpeningElement(node)||ts.isJsxSelfClosingElement(node)){const tag=node.tagName.getText(tree);if(['Drawer','Modal','Tabs','Collapse','Popover','Dropdown','SessionSandboxFrame'].includes(tag)){const count=counts[tag]=(counts[tag]??0)+1;const attributes=node.attributes.properties.filter(ts.isJsxAttribute);const a=attributes.find(a=>['title','aria-label','label'].includes(a.name.getText(tree)));const title=a?.initializer?.getText(tree)??`${tag} ${count}`;const family=file.includes('/chat/')?['J03','J09','J12']:file.includes('/workflows/')?['J10']:['J15'];add(`view.${file.replace('web/src/features/','').replace('.tsx','').replaceAll('/','.')}.${tag.toLowerCase()}.${count}`,`${file} → ${title}`,'contextual',file,`${tag}: ${title}`,family);
if (['Tabs','Collapse'].includes(tag)) {
  const items = attributes.find(a => a.name.getText(tree) === 'items');
  const seen = new Set();
  const panels = item => {
    if (ts.isObjectLiteralExpression(item)) {
      const props = Object.fromEntries(item.properties.filter(ts.isPropertyAssignment).map(p => [p.name.getText(tree).replaceAll('"','').replaceAll("'",''), p.initializer]));
      const key = props.key?.text, label = props.label?.text;
      if (key && label && !seen.has(key)) { seen.add(key); add(`panel.${file.replace('web/src/features/','').replace('.tsx','').replaceAll('/','.')}.${tag.toLowerCase()}.${count}.${key}`, `${file} → ${label}`, 'contextual panel', file, label, family); }
    }
    ts.forEachChild(item, panels);
  };
  if (items?.initializer) panels(items.initializer);
}
}}ts.forEachChild(node,visit);};visit(tree);}
for (const [id, entry, owner, job, journeys] of [
  ['workflow.new', '/t/:tenantId/admin/workflows?workflow=new', 'web/src/features/workflows/WorkflowEditor.tsx', 'Author a process', ['J10']],
  ['workflow.edit', '/t/:tenantId/admin/workflows?workflow=:workflowId', 'web/src/features/workflows/WorkflowEditor.tsx', 'Edit / validate / publish exact process', ['J10','J15']],
  ['workflow.execution', '/t/:tenantId/admin/workflows?execution=:executionId', 'web/src/features/workflows/WorkflowExecutionInspector.tsx', 'Inspect execution / sign off / inspect source proof', ['J10','J12']],
  ['evaluations.subject', '/t/:tenantId/admin/evaluations?subjectId=:subjectId', 'web/src/features/access/TenantEvaluations.tsx', 'Inspect subject evidence / manual review', ['J11']],
  ['evaluations.attempt', '/t/:tenantId/admin/evaluations?subjectId=:subjectId&attemptId=:attemptId', accessOwner('TenantEvaluations'), 'Inspect exact Evaluation attempt', ['J11']],
  ['credential.detail', '/t/:tenantId/integrations/credentials?credentialId=:credentialId', accessOwner('TenantCredentials'), 'Inspect protected Credential metadata', ['J08','J15']],
  ['sandbox.detail', '/t/:tenantId/integrations/sandboxes?profileId=:profileId', accessOwner('TenantSandboxes'), 'Inspect exact Sandbox profile', ['J06','J15']],
  ['approval.detail', '/t/:tenantId/admin/approvals/:requestId', accessOwner('TenantApprovals'), 'Review or authorize the exact pending request', ['J12']],
  ['activity.session', '/t/:tenantId/admin/agents/:instanceId/activity/sessions?session=:sessionId', accessOwner('TenantAgentActivity'), 'Inspect original Session and continue exact Chat', ['J03','J11']],
  ['activity.run', '/t/:tenantId/admin/agents/:instanceId/activity/runs?run=:runId', accessOwner('TenantAgentActivity'), 'Inspect exact Run facts and provenance', ['J11']],
]) add(id, entry, 'organization', owner, job, journeys);
const settingsSource = 'web/src/features/admin/InstanceSettingsSection.tsx';
const settingsTree = parse(settingsSource);
const discoverSettings = node => {
  if (ts.isVariableDeclaration(node) && node.name.getText() === 'titles' && ts.isObjectLiteralExpression(node.initializer)) {
    for (const property of node.initializer.properties.filter(ts.isPropertyAssignment))
      add(`settings.${property.name.getText()}`, `Agent Settings → ${property.initializer.text}`, 'agent settings', settingsSource, property.initializer.text, ['J06','J15']);
  }
  ts.forEachChild(node, discoverSettings);
}; discoverSettings(settingsTree);
// Native imperative confirmations are meaningful views too; they do not appear
// as JSX Modal nodes at the call site.
for (const full of files(path.join(root, 'web/src/features')).filter(file => file.endsWith('.tsx') && !file.includes('.test.'))) {
  const file = path.relative(root, full), tree = parse(file); let count = 0;
  const visit = node => {
    if (ts.isCallExpression(node) && ['confirmAction', 'modal.confirm', 'Modal.confirm'].includes(node.expression.getText(tree))) {
      const options = node.arguments[node.expression.getText(tree) === 'confirmAction' ? 1 : 0];
      const title = options && ts.isObjectLiteralExpression(options)
        ? options.properties.find(property => ts.isPropertyAssignment(property) && property.name.getText(tree) === 'title')?.initializer.getText(tree)
        : options?.getText(tree);
      add(`confirmation.${file.replace('web/src/features/', '').replace('.tsx', '').replaceAll('/', '.')}.${++count}`,
        `${file} → ${title ?? 'Exact action confirmation'}`, 'contextual confirmation', file, title ?? 'Confirm or cancel exact action', ['J15']);
      rows.at(-1).states = ['open', 'keyboard focus', 'confirm/cancel', 'busy', 'failure', 'stale/denied where applicable'];
    }
    ts.forEachChild(node, visit);
  };
  visit(tree);
}
const duplicates=rows.filter((r,i)=>rows.findIndex(x=>x.surface_id===r.surface_id)!==i);if(duplicates.length)throw Error(JSON.stringify(duplicates));
for (const owner of new Set(rows.map(row => row.feature_owner))) if (!sources.includes(owner)) sources.push(owner);
const fingerprints=Object.fromEntries(sources.map(f=>[f,crypto.createHash('sha256').update(read(f)).digest('hex')]));
if(process.argv.includes('--check')){const saved=JSON.parse(fs.readFileSync(destination,'utf8'));const old=new Map(saved.surfaces.map(s=>[s.surface_id,s]));const missing=rows.filter(s=>!old.has(s.surface_id));const orphaned=saved.surfaces.filter(s=>!rows.some(r=>r.surface_id===s.surface_id));const drift=Object.keys(fingerprints).filter(f=>saved.source_fingerprints[f]!==fingerprints[f]);console.log(JSON.stringify({discovered:rows.length,missing:missing.map(x=>x.surface_id),orphaned:orphaned.map(x=>x.surface_id),source_drift:drift},null,2));if(missing.length||orphaned.length||drift.length)process.exitCode=1;}else{const existing=fs.existsSync(destination)?JSON.parse(fs.readFileSync(destination,'utf8')).surfaces:[];const previous=new Map(existing.map(s=>[s.surface_id,s]));fs.writeFileSync(destination,JSON.stringify({schema_version:1,source_fingerprints:fingerprints,discovery_limits:'Routes and native overlay/tab compositions are source-derived. Dynamic feature states, operations, authority variants and browser-discovered views require explicit review and supplementation; discovery does not imply qualification.',surfaces:rows.map(r=>({...r,...previous.get(r.surface_id),route_or_entry:r.route_or_entry,feature_owner:r.feature_owner}))},null,2)+'\n');console.log(`Inventoried ${rows.length} route/contextual surfaces; qualification remains explicit.`);}
