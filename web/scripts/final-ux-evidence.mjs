// Reconcile source dispositions with explicitly supplied disposable browser evidence.
// This does not turn source discovery or a shared screenshot into per-state acceptance.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import ts from 'typescript';
const root = path.resolve(import.meta.dirname, '../..');
const evidence = process.env.EXPERIENCE_EVIDENCE_ROOT;
if (!evidence || !path.isAbsolute(evidence)) throw Error('Set EXPERIENCE_EVIDENCE_ROOT to the absolute capture/run directory.');
if (!fs.existsSync(evidence) || !fs.statSync(evidence).isDirectory()) throw Error('Evidence directory does not exist.');
for (const reviewer of ['a', 'b']) if (!fs.existsSync(path.join(evidence, `independent-${reviewer}.md`))) throw Error(`Missing independent review ${reviewer}.`);
const output = path.join(root, 'docs/reports/assets/final-ux-refinement');
const manifestPath = path.join(output, 'coverage.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
for (const [file, expected] of Object.entries(manifest.source_fingerprints)) {
  const current = crypto.createHash('sha256').update(fs.readFileSync(path.join(root, file))).digest('hex');
  if (current !== expected) throw Error(`Stale source inventory: ${file}. Regenerate before binding evidence.`);
}
const historical = JSON.parse(fs.readFileSync(path.join(root, 'docs/reports/assets/product-experience/coverage.json'), 'utf8'));
const old = new Map(historical.surfaces.map(row => [row.surface_id, row]));
const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const walk = folder => fs.existsSync(folder) ? fs.readdirSync(folder, { withFileTypes: true }).flatMap(item => item.isDirectory() ? walk(path.join(folder, item.name)) : [path.join(folder, item.name)]) : [];
const reachable = new Set();
function visit(file) {
  file = path.resolve(file); if (reachable.has(file) || !fs.existsSync(file)) return;
  reachable.add(file);
  if (!/\.tsx?$/.test(file)) return;
  const tree = ts.createSourceFile(file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true);
  function node(value) {
    const specifier = ts.isImportDeclaration(value) || ts.isExportDeclaration(value) ? value.moduleSpecifier
      : ts.isCallExpression(value) && value.expression.kind === ts.SyntaxKind.ImportKeyword ? value.arguments[0] : undefined;
    if (specifier && ts.isStringLiteral(specifier) && specifier.text.startsWith('.')) {
      const resolved = ts.resolveModuleName(specifier.text, file, { moduleResolution: ts.ModuleResolutionKind.Bundler }, ts.sys).resolvedModule?.resolvedFileName;
      if (resolved && resolved.startsWith(path.join(root, 'web/src'))) visit(resolved);
    }
    ts.forEachChild(value, node);
  }
  node(tree);
}
visit(path.join(root, 'web/src/main.tsx'));
const readOptional = name => ['after', 'confirmation', 'array-correction'].flatMap(folder => { const file = path.join(evidence, folder, name); return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : []; });
const routes = [...readOptional('route-visits.json'), ...readOptional('nested-route-visits.json'), ...readOptional('settings-visits.json')];
if (!routes.length) throw Error('No actual route/section visit records were supplied.');
const direct = new Map();
for (const row of routes) { const entry = direct.get(row.id) ?? { widths: [], paths: [] }; entry.widths.push(...(row.widths ?? [row.width])); if (row.route ?? row.path) entry.paths.push(row.route ?? row.path); direct.set(row.id, entry); }
const changed = new Set(['TenantAgentConfiguration', 'TenantAgentControls', 'TenantAgentPresentation', 'TenantAgents', 'TenantUserProfile', 'TenantGovernance', 'EvaluationGovernanceSettings', 'ModelDefaultEditor', 'ModelRoutingEditor', 'AccountForms', 'Organizations', 'InstanceSettingsSection', 'InstanceCooperationSettings', 'DefinitionCandidateForm', 'TenantDefinitionEditor', 'SelfManagementFields', 'StateIsolationPolicyFields', 'AutomationTriggerPermissions']);
const specialized = /(?:Workflow|Budget|Capability|Resource|Credential|Offering|ConnectionEditor|SpaceEditor|Environment|IsolationPreset|EvaluationBindings|BrowserPrivacy|AccountForms|FilePreview|Sandbox)/;
const allImages = walk(evidence).filter(file => /\.png$/.test(file) && !file.includes('results/'));
const reviewed = new Map();
for (const reviewer of ['a', 'b']) {
  const file = path.join(evidence, `independent-${reviewer}.md`); if (!fs.existsSync(file)) continue;
  const text = fs.readFileSync(file, 'utf8');
  if (![...text.matchAll(/\/tmp\/agent-core-navy\/[\w./-]+\.png/g)].some(match => match[0].startsWith(evidence + path.sep) && fs.existsSync(match[0]))) throw Error(`Review ${reviewer} has no actual linked capture.`);
  for (const match of text.matchAll(/\/tmp\/agent-core-navy\/[\w./-]+\.png/g)) {
    const current = reviewed.get(match[0]) ?? []; current.push(`independent-${reviewer}`); reviewed.set(match[0], [...new Set(current)]);
  }
}
if (!allImages.length || !reviewed.size || ![...reviewed.keys()].some(file => fs.existsSync(file) && file.startsWith(evidence + path.sep))) throw Error('No actual reviewed captures were supplied.');
const epochFile = path.join(evidence, 'capture-source-epochs.json');
const epochs = fs.existsSync(epochFile) ? JSON.parse(fs.readFileSync(epochFile, 'utf8')) : {};
const captureEpoch = file => path.relative(evidence, file).split(path.sep)[0];
const imageRecords = allImages.map(file => ({ raw_path: file, filename: path.basename(file), sha256: sha(file), viewport_width: Number(/-(1440|1024|768|390|320)\.png$/.exec(file)?.[1]) || null, inspected_by: reviewed.get(file) ?? [], capture_method: file.includes('/after/') || file.includes('/confirmation/') || file.includes('/office-confirmation/') || file.includes('/array-correction/') || file.includes('-captures/') ? 'Synthetic Playwright CLI' : 'Playwright MCP/baseline', source_digest: epochs[captureEpoch(file)] ?? null, source_epoch: epochs[captureEpoch(file)] ? `recorded runtime source: ${captureEpoch(file)}` : file.includes('/before/') ? 'baseline ab6f466a' : 'source epoch unbound; see recorded correction captures', limits: 'A capture is visual evidence only; interaction results are separate. Unlisted reviewer means captured, not certified as inspected.' }));
fs.writeFileSync(path.join(output, 'image-manifest.json'), JSON.stringify(imageRecords, null, 2) + '\n');
const routeStem = id => id.startsWith('tenant.') ? `route-tenant-${id.slice(7)}-`
  : id.startsWith('agent.') ? `route-agent-${id.slice(6)}-`
  : id.startsWith('draft.') ? `route-draft-${id.slice(6)}-`
  : id.startsWith('account.') ? `route-account-${id.slice(8)}-`
  : id.startsWith('system.') ? `route-system-${id.slice(7)}-`
  : id.startsWith('models.') ? `route-models-${id.slice(7)}-`
  : id.startsWith('space.') ? `route-space-${id.slice(6)}-`
  : id.startsWith('settings.') ? `settings-${id.slice(9)}-` : null;
const ownerStems = new Map();
for (const row of manifest.surfaces) if (direct.has(row.surface_id)) {
  const stem = routeStem(row.surface_id); if (stem) ownerStems.set(row.feature_owner, [...(ownerStems.get(row.feature_owner) ?? []), stem]);
}
const activeImages = imageRecords.filter(image => !image.raw_path.includes('/before/'));
const preferred = new Map();
const imagePriority = image => image.raw_path.includes('/array-correction/') ? 3 : image.raw_path.includes('/office-confirmation/') ? 2 : image.raw_path.includes('/confirmation/') ? 1 : 0;
for (const image of activeImages) if (!preferred.has(image.filename) || imagePriority(image) >= imagePriority(preferred.get(image.filename))) preferred.set(image.filename, image);
const representative = owner => {
  const name = path.basename(owner, '.tsx');
  const stems = [...(ownerStems.get(owner) ?? [])];
  if (/FilePreview|SpreadsheetPreview|CsvPreview|WordPreview/.test(name)) stems.push('file-preview-', 'office-');
  if (/Workflow/.test(name)) stems.push('workflow-editor-', 'workflow-sign-off-', 'workflow-completed-');
  if (/Approvals|Approval/.test(name)) stems.push('approval-review-', 'approval-exception-');
  if (/Goal|Evaluation/.test(name)) stems.push('goal-completed-', 'evaluation-evidence-');
  if (/Cooperation/.test(name)) stems.push('cooperation-');
  if (/AdminShell|AdminNavigationIcon/.test(name)) stems.push('route-tenant-agents-', 'route-agent-profile-');
  if (/AgentConfigurationLayout|InstanceSettingsSection/.test(name)) stems.push('agent-settings-', 'cooperation-');
  if (/AdminCollectionToolbar/.test(name)) stems.push('agents-', 'route-tenant-members-');
  if (owner.includes('/chat/') && !/Sandbox/.test(name)) stems.push('chat-populated-', 'chat-empty-');
  return [...preferred.values()].filter(image => stems.some(stem => image.filename.startsWith(stem)) && [1440, 390].includes(image.viewport_width)).map(image => image.raw_path);
};
for (const row of manifest.surfaces) {
  const prior = old.get(row.surface_id) ?? historical.surfaces.find(value => value.feature_owner === row.feature_owner);
  const owner = path.basename(row.feature_owner, '.tsx');
  const active = reachable.has(path.join(root, row.feature_owner));
  const executed = direct.get(row.surface_id);
  row.baseline_screenshots = prior?.baseline_screenshots ?? [];
  row.baseline_evidence_scope = 'Historical representative family captures, not an exact baseline for every panel/state.';
  row.design_decision = changed.has(owner) ? 'redesigned at shared/consumer owner; native behavior and authority retained' : active ? 'already conforming after shared navy/layout migration; retain domain composition' : 'intentionally exceptional: source-only retained composition, not imported by current app entry';
  row.implementation_status = active ? 'implemented; shared or direct presentation disposition' : 'retained source-only; no active navigation claim';
  row.settings_presentation = changed.has(owner) ? 'reuse descriptive row/panel where scalar; retain instructions, lists and matrix exceptions' : specialized.test(owner) ? 'retain specialized full-width editor/canvas/matrix; one domain owner and native validation' : 'no settings panel needed for collection, inspector, navigation or shared feedback';
  row.viewport_checks = executed ? [...new Set(executed.widths)].sort((a, b) => b - a) : [];
  row.direct_route_visit = !!executed;
  row.browser_outcome = executed ? 'direct rendered route/section; named browser test checks readiness/reflow; see route visit records' : active ? 'shared token/shell behavior sampled; dedicated panel-state coverage must be read from the owning suite and image manifest' : 'not an active route from current app entry; retained source reviewed';
  row.representative_images = representative(row.feature_owner);
  row.visual_review = executed ? 'Direct rendered visit; exact capture inspection is recorded separately in the image manifest.' : row.representative_images.length ? 'Matching owner/family composition sampled; this is not a separate image review of the exact panel/overlay state.' : 'No matching owner/state capture. Shared token/shell review only; dedicated visual inspection remains unclaimed.';
  row.interaction_tests = prior?.interaction_tests ?? ['web/e2e/product-experience.spec.ts'];
  row.test_evidence_scope = 'Suite mapping identifies the owner; it does not imply every role/state/width combination ran. Run-results ledger records actual passes and repaired attempts.';
  row.accessibility_review = 'Shared native-control label, keyboard/focus, contrast and reflow sampling in named scenarios; no assertion for every exact panel/state or physical assistive device.';
  row.docs_synced = true;
  row.evidence_path = 'docs/reports/final-ux-refinement-2026-10-31.md';
  row.blocker = /TenantSessionSandboxDrawer|BrowserRemoteDesktop/.test(owner) ? 'Synthetic host disables native execution; readiness UI is covered, real remote frame/device behavior is outside this qualification.' : null;
}
manifest.qualification = { scope: 'Controlled local Synthetic UI refinement; direct routes plus explicit shared-composition/source-only dispositions. Not every combinatorial state or native service is certified.', active_module_count: reachable.size, direct_entries: [...direct.keys()].sort(), source_only_surfaces: manifest.surfaces.filter(row => !reachable.has(path.join(root, row.feature_owner))).map(row => row.surface_id), review_manifest: 'image-manifest.json', historical_evidence: '../product-experience/coverage.json' };
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
console.log(JSON.stringify({ surfaces: manifest.surfaces.length, direct: direct.size, sourceOnly: manifest.qualification.source_only_surfaces.length, images: imageRecords.length, reviewedImages: imageRecords.filter(row => row.inspected_by.length).length }, null, 2));
