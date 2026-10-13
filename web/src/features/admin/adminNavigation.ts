/** One registry for route parsing, headings, navigation and bounded setting discovery. */
export const globalPages = [
  { id: 'instances', path: 'instances', label: 'Instances', group: 'Agents', description: 'Manage persistent agent identities and their configuration.', aliases: [] },
  { id: 'definitions', path: 'definitions', label: 'Definitions', group: 'Agents', description: 'Inspect immutable versions and author revisioned drafts.', aliases: [] },
  { id: 'credentials', path: 'connections/credentials', label: 'Credentials', group: 'Integrations', description: 'Manage shared protected credentials and email accounts.', aliases: ['Gmail', 'OAuth', 'IMAP', 'SMTP', 'emailAccount'] },
  { id: 'events', path: 'connections/events', label: 'Events', group: 'Integrations', description: 'Inspect Built-in Events and manage shared Webhook Events.', aliases: ['Webhook', 'subscribers', 'deliveries'] },
  { id: 'browser-privacy', path: 'platform/browser-privacy', label: 'Browser privacy', group: 'Platform', description: 'Global host screenshot privacy, constrained by deployment policy.', aliases: ['screenshots', 'protected', 'unmasked', 'restart'] },
] as const;
export const instancePages = [
  { id: 'overview', path: 'overview', label: 'Overview', group: '', description: 'Saved identity, configuration sources and operational destinations.', aliases: [] },
  { id: 'profile', path: 'profile', label: 'Profile & version', group: 'Configuration', description: 'Persona, lifecycle and the adopted Definition version.', aliases: ['name', 'role', 'tone', 'archive', 'upgrade', 'rollback'] },
  { id: 'behavior', path: 'behavior', label: 'Instructions & behavior', group: 'Configuration', description: 'Operating instructions, conversation, initiative and voice.', aliases: ['language', 'response length', 'max output tokens', 'interruption', 'silence', 'cooldown', 'voice'] },
  { id: 'models', path: 'models', label: 'Models & execution', group: 'Configuration', description: 'Interactive model defaults, provider preferences and execution budgets.', aliases: ['reasoning effort', 'LLM', 'STT', 'TTS', 'deadline', 'budget'] },
  { id: 'skills', path: 'skills', label: 'Skills', group: 'Configuration', description: 'Definition and Instance procedures with distinct ownership.', aliases: ['procedures', 'on demand'] },
  { id: 'resources', path: 'resources', label: 'Resources', group: 'Configuration', description: 'Definition evidence and independently owned Instance resources.', aliases: ['import', 'files', 'knowledge'] },
  { id: 'workspace', path: 'workspace', label: 'Workspace', group: 'Configuration', description: 'Inspect durable /home files, download and guarded deletion.', aliases: ['home', 'folders', 'download'] },
  { id: 'access', path: 'access', label: 'Capabilities & credentials', group: 'Governance', description: 'Capability projection and separate protected credential bindings.', aliases: ['selected capabilities', 'always', 'bindings', 'grants', 'attachments'] },
  { id: 'self-management', path: 'self-management', label: 'Self-management', group: 'Governance', description: 'Management authority, freeze and reviewed shared changes.', aliases: ['Off', 'Assisted', 'Managed', 'freeze', 'unfreeze', 'promotion', 'Harness'] },
  { id: 'automations', path: 'automations', label: 'Automations', group: 'Operations', description: 'Registrations, trigger restrictions and unattended execution defaults.', aliases: ['Schedule', 'Event', 'unattended model', 'report', 'trigger policy'] },
  { id: 'memory', path: 'continuity/memory', label: 'Memory', group: 'Operations', description: 'Memory policy, learned records and maintenance.', aliases: ['continuity', 'forget', 'promotion', 'retrieval'] },
  { id: 'experience', path: 'continuity/experience', label: 'Experience', group: 'Operations', description: 'Experience checkpoints and their original execution sources.', aliases: ['retrospection', 'checkpoints'] },
  { id: 'sessions', path: 'activity/sessions', label: 'Sessions', group: 'Operations', description: 'Durable conversation context and exact Session inspection.', aliases: ['Activity', 'conversation', 'history'] },
  { id: 'runs', path: 'activity/runs', label: 'Runs', group: 'Operations', description: 'Independent execution diagnostics and original Run results.', aliases: ['Activity', 'AgentRun', 'execution'] },
  { id: 'effective', path: 'effective', label: 'Effective configuration', group: 'Diagnostics', description: 'Read-only resolved configuration and Browser provider diagnostics.', aliases: ['provenance', 'Host', 'Definition', 'readiness'] },
] as const;
export const definitionPages = [
  { id: 'versions', path: 'versions', label: 'Versions', group: 'Definition', description: 'Immutable published versions and inspection.', aliases: ['publication', 'deprecate', 'fork'] },
  { id: 'drafts', path: 'drafts', label: 'Drafts', group: 'Definition', description: 'Revisioned draft inventory and explicit authoring.', aliases: ['create', 'delete', 'edit'] },
] as const;
export const draftPages = [
  { id: 'overview', path: 'overview', label: 'Overview', group: '', description: 'Draft identity, source, revision and unsaved status.', aliases: [] },
  { id: 'profile', path: 'profile', label: 'Identity & goals', group: 'Configuration', description: 'Definition identity and goals.', aliases: ['name', 'role', 'description', 'tone'] },
  { id: 'behavior', path: 'behavior', label: 'Instructions & behavior', group: 'Configuration', description: 'Instructions, conversation, behavior, initiative and voice.', aliases: ['language', 'response length', 'tokens', 'interruption', 'silence', 'voice'] },
  { id: 'models', path: 'models', label: 'Models & execution', group: 'Configuration', description: 'Model defaults, provider preferences and execution budgets.', aliases: ['reasoning effort', 'LLM', 'STT', 'TTS', 'deadline', 'budget'] },
  { id: 'skills', path: 'skills', label: 'Skills', group: 'Configuration', description: 'Definition procedures and projection defaults.', aliases: ['procedures', 'on demand', 'always'] },
  { id: 'resources', path: 'resources', label: 'Resources', group: 'Configuration', description: 'Immutable uploaded and imported Definition evidence.', aliases: ['import', 'files', 'knowledge binding'] },
  { id: 'capabilities', path: 'capabilities', label: 'Capabilities', group: 'Governance', description: 'Exact capability authority, projection and knowledge references.', aliases: ['selected', 'always', 'tools', 'attachments', 'workspace template'] },
  { id: 'policies', path: 'policies', label: 'Policies', group: 'Governance', description: 'Memory, trigger, self-management defaults and advanced metadata.', aliases: ['Schedule', 'Event', 'freeze', 'metadata'] },
  { id: 'test-publish', path: 'test-publish', label: 'Test & Publish', group: 'Release', description: 'Validation, Synthetic evaluation, publication diff and blockers.', aliases: ['validate', 'scenarios', 'evaluation', 'diff'] },
  { id: 'advanced-json', path: 'advanced-json', label: 'Advanced JSON', group: 'Advanced', description: 'Full JSON view of the same revisioned candidate.', aliases: ['schema', 'repair'] },
] as const;
export type AdminCollection = typeof globalPages[number]['id'];
export type InstancePage = typeof instancePages[number]['id'];
export type DefinitionPage = typeof definitionPages[number]['id'];
export type DraftPage = typeof draftPages[number]['id'];
export type NavigationPage = { id: string; path: string; label: string; group: string; description: string; aliases: readonly string[] };

export const instanceSettingGroups: Partial<Record<InstancePage, readonly string[]>> = {
  behavior: ['instructions', 'conversationPolicy', 'behaviorPolicy', 'initiativePolicy', 'voice'],
  models: ['modelDefaults', 'providerPreferences'], access: ['capabilities'],
  automations: ['triggerPolicy'], memory: ['memoryPolicy'],
};
export const draftSettingGroups: Partial<Record<DraftPage, readonly string[]>> = {
  behavior: ['instructions', 'conversationPolicy', 'behaviorPolicy', 'initiativePolicy', 'voice'],
  models: ['modelDefaults', 'providerPreferences'], policies: ['memoryPolicy', 'triggerPolicy', 'selfManagement', 'metadata'],
};

export function instancePath(id: string, page: InstancePage = 'overview') {
  return `/admin/instances/${encodeURIComponent(id.toLowerCase())}/${instancePages.find(p => p.id === page)!.path}`;
}
export function definitionPath(id: string, page: DefinitionPage = 'versions') {
  return `/admin/definitions/${encodeURIComponent(id)}/${page}`;
}
export function draftPath(id: string, draftId: string, page: DraftPage = 'overview') {
  return `${definitionPath(id, 'drafts')}/${encodeURIComponent(draftId)}/${page}`;
}
export function globalPath(page: AdminCollection = 'instances') { return `/admin/${globalPages.find(p => p.id === page)!.path}`; }

/** Approved presentation labels only, with stable section anchors; no private content. */
export const settingSearch = [
  { label: 'Operating instructions', page: 'behavior', anchor: 'instructions', aliases: ['system instructions', 'prompt'] },
  { label: 'Conversation language & response length', page: 'behavior', anchor: 'conversationPolicy', aliases: ['maximum output tokens', 'ask one question'] },
  { label: 'Interruption behavior', page: 'behavior', anchor: 'behaviorPolicy', aliases: ['unsupported claims', 'acknowledge'] },
  { label: 'Initiative & silence', page: 'behavior', anchor: 'initiativePolicy', aliases: ['proactive', 'cooldown', 'inactivity'] },
  { label: 'Voice', page: 'behavior', anchor: 'voice', aliases: ['voice ID', 'speaking rate'] },
  { label: 'Interactive model & reasoning', page: 'models', anchor: 'modelDefaults', aliases: ['default model', 'reasoning effort'] },
  { label: 'Provider preferences', page: 'models', anchor: 'providerPreferences', aliases: ['language model', 'STT', 'TTS', 'classifier'] },
  { label: 'Execution budgets', page: 'models', anchor: 'execution-budgets', aliases: ['deadline', 'steps', 'duration', 'per tool'] },
  { label: 'Memory policy', page: 'memory', draftPage: 'policies', anchor: 'memoryPolicy', aliases: ['promotion', 'retrieval', 'Session memory'] },
  { label: 'Trigger restrictions', page: 'automations', draftPage: 'policies', anchor: 'restrictions', draftAnchor: 'triggerPolicy', aliases: ['Schedule', 'Event', 'source kinds', 'recurrence'] },
  { label: 'Unattended execution defaults', page: 'automations', anchor: 'execution-defaults', instanceOnly: true, aliases: ['unattended model', 'reasoning'] },
  { label: 'Credential bindings', page: 'access', anchor: 'credentials', instanceOnly: true, aliases: ['email account', 'grant'] },
  { label: 'Capabilities', page: 'access', draftPage: 'capabilities', anchor: 'capabilities', aliases: ['selected capabilities', 'always projected', 'attachments'] },
  { label: 'Advanced metadata', page: 'effective', draftPage: 'policies', anchor: 'metadata', draftOnly: true, aliases: ['metadata'] },
] satisfies readonly { label: string; page: InstancePage; draftPage?: DraftPage; anchor: string; draftAnchor?: string; aliases: readonly string[]; instanceOnly?: boolean; draftOnly?: boolean }[];
