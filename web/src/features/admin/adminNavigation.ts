/** One registry for route parsing, headings, navigation and bounded setting discovery. */
export const globalPages = [
  { id: 'instances', path: 'instances', label: 'Instances', group: 'Agents', description: 'Manage persistent agent identities and their configuration.', aliases: [] },
  { id: 'definitions', path: 'definitions', label: 'Definitions', group: 'Agents', description: 'Inspect immutable versions and author revisioned drafts.', aliases: [] },
  { id: 'credentials', path: 'connections/credentials', label: 'Credentials', group: 'Integrations', description: 'Manage shared protected credentials and email accounts.', aliases: ['Gmail', 'OAuth', 'IMAP', 'SMTP', 'emailAccount'] },
  { id: 'events', path: 'connections/events', label: 'Events', group: 'Integrations', description: 'Inspect Built-in Events and manage shared Webhook Events.', aliases: ['Webhook', 'subscribers', 'deliveries'] },
  { id: 'browser-privacy', path: 'platform/browser-privacy', label: 'Browser privacy', group: 'Platform', description: 'Global host screenshot privacy, constrained by deployment policy.', aliases: ['screenshots', 'protected', 'unmasked', 'restart'] },
  { id: 'sandboxes', path: 'platform/sandboxes', label: 'Sandboxes', group: 'Platform', description: 'Manage prebuilt Session environments and local execution readiness.', aliases: ['Python', 'Node', 'Workbench', 'Office', 'sandbox'] },
] as const;
export const instancePages = [
  {"id": "overview", "path": "overview", "label": "Overview", "group": "", "description": "Saved identity, configuration sources and operational destinations.", "aliases": []},
  {"id": "profile", "path": "profile", "label": "Profile", "group": "Configuration", "description": "Agent persona and identity.", "aliases": ["name", "role", "tone"]},
  {"id": "version", "path": "version", "label": "Definition version", "group": "Configuration", "description": "Inspect and adopt an immutable Definition version.", "aliases": ["upgrade", "rollback"]},
  {"id": "instructions", "path": "instructions", "label": "Instructions", "group": "Configuration", "description": "Operating instructions for the agent.", "aliases": ["system instructions", "prompt"]},
  {"id": "conversation", "path": "conversation", "label": "Conversation", "group": "Configuration", "description": "Language, response length and conversational preferences.", "aliases": ["language", "max output tokens", "ask one question"]},
  {"id": "behavior", "path": "interruption", "label": "Behavior", "group": "Configuration", "description": "Interruption and response behavior.", "aliases": ["interruption", "unsupported claims"]},
  {"id": "initiative", "path": "initiative", "label": "Initiative", "group": "Configuration", "description": "Proactive responses, silence and inactivity limits.", "aliases": ["cooldown", "silence"]},
  {"id": "voice", "path": "voice", "label": "Voice", "group": "Configuration", "description": "Voice identity, speaking rate and speech behavior.", "aliases": ["voice ID", "speech"]},
  {"id": "models", "path": "models", "label": "Models", "group": "Configuration", "description": "Interactive model defaults and reasoning effort.", "aliases": ["reasoning effort", "LLM"]},
  {"id": "providers", "path": "providers", "label": "Providers", "group": "Configuration", "description": "Independently selected language, recognition and synthesis providers.", "aliases": ["STT", "TTS", "classifier"]},
  {"id": "execution", "path": "execution", "label": "Execution budgets", "group": "Configuration", "description": "Step, duration and per-tool limits.", "aliases": ["deadline", "budget"]},
  { id: 'state-isolation', path: 'state-isolation', label: 'State & Isolation', group: 'Configuration', description: 'Control continuity, resource access and per-request isolation with reviewed preset drafts.', aliases: ['Persistent', 'Session-isolated', 'Stateless', 'home access', 'state visibility'] },
  {"id": "sandbox", "path": "sandbox", "label": "Sandbox", "group": "Configuration", "description": "Inherited environment, initialization, resource and lifecycle limits.", "aliases": ["Workbench", "Python", "Node", "sandboxPolicy"]},
  {"id": "skills", "path": "skills", "label": "Skills", "group": "Configuration", "description": "Definition and Instance procedures with distinct ownership.", "aliases": ["procedures", "on demand"]},
  {"id": "resources", "path": "resources", "label": "Resources", "group": "Configuration", "description": "Definition evidence and independently owned Instance resources.", "aliases": ["import", "files", "knowledge"]},
  {"id": "workspace", "path": "workspace", "label": "Workspace", "group": "Configuration", "description": "Inspect durable /home files, download and guarded deletion.", "aliases": ["home", "folders", "download"]},
  {"id": "capabilities", "path": "capabilities", "label": "Capabilities", "group": "Governance", "description": "Capability authority and projection.", "aliases": ["selected capabilities", "always", "attachments"]},
  {"id": "credentials", "path": "credentials", "label": "Credential bindings", "group": "Governance", "description": "Protected credential grants for this Instance.", "aliases": ["email account", "bindings", "grants"]},
  {"id": "self-management", "path": "self-management", "label": "Self-management", "group": "Governance", "description": "Management authority, freeze and reviewed shared changes.", "aliases": ["Off", "Assisted", "Managed", "freeze", "promotion", "Harness"]},
  {"id": "trigger-policy", "path": "trigger-policy", "label": "Trigger restrictions", "group": "Governance", "description": "Allowed sources, scheduling permissions and registration limits.", "aliases": ["Schedule", "Event", "trigger policy"]},
  {"id": "memory-policy", "path": "memory-policy", "label": "Memory policy", "group": "Governance", "description": "Session memory, promotion and retrieval preferences.", "aliases": ["user memory", "promotion", "retrieval"]},
  {"id": "automations", "path": "automations", "label": "Automations", "group": "Operations", "description": "Scheduled and reactive registrations.", "aliases": ["Schedule", "Event", "report"]},
  {"id": "unattended", "path": "unattended", "label": "Unattended model", "group": "Operations", "description": "Model and reasoning defaults for background work.", "aliases": ["unattended execution", "reasoning"]},
  {"id": "memory", "path": "continuity/memory", "label": "Memory", "group": "Operations", "description": "Learned records and memory maintenance.", "aliases": ["continuity", "forget"]},
  {"id": "experience", "path": "continuity/experience", "label": "Experience", "group": "Operations", "description": "Experience checkpoints and their original execution sources.", "aliases": ["retrospection", "checkpoints"]},
  { id: 'state', path: 'state', label: 'State', group: 'Operations', description: 'Observe hosted and durable activity, Browser and Sandbox availability.', aliases: ['runtime state', 'live resources', 'partial', 'isolation'] },
  {"id": "sessions", "path": "activity/sessions", "label": "Sessions", "group": "Operations", "description": "Durable conversation context and exact Session inspection.", "aliases": ["Activity", "conversation", "history"]},
  {"id": "runs", "path": "activity/runs", "label": "Runs", "group": "Operations", "description": "Independent execution diagnostics and original Run results.", "aliases": ["Activity", "AgentRun", "execution"]},
  {"id": "lifecycle", "path": "lifecycle", "label": "Lifecycle", "group": "Operations", "description": "Archive, restore and explicitly delete this Instance.", "aliases": ["archive", "unarchive", "delete"]},
  {"id": "effective", "path": "effective", "label": "Effective configuration", "group": "Diagnostics", "description": "Read-only resolved configuration and Browser provider diagnostics.", "aliases": ["provenance", "Host", "readiness"]},
] as const;
export const definitionPages = [
  { id: 'versions', path: 'versions', label: 'Versions', group: 'Definition', description: 'Immutable published versions and inspection.', aliases: ['publication', 'deprecate', 'fork'] },
  { id: 'drafts', path: 'drafts', label: 'Drafts', group: 'Definition', description: 'Revisioned draft inventory and explicit authoring.', aliases: ['create', 'delete', 'edit'] },
] as const;
export const draftPages = [
  {"id": "overview", "path": "overview", "label": "Overview", "group": "", "description": "Draft identity, source, revision and unsaved status.", "aliases": []},
  {"id": "profile", "path": "profile", "label": "Identity & goals", "group": "Configuration", "description": "Definition identity and goals.", "aliases": ["name", "role", "description", "tone"]},
  {"id": "instructions", "path": "instructions", "label": "Instructions", "group": "Configuration", "description": "Operating instructions for the agent.", "aliases": ["system instructions", "prompt"]},
  {"id": "conversation", "path": "conversation", "label": "Conversation", "group": "Configuration", "description": "Language, response length and conversational preferences.", "aliases": ["language", "max output tokens", "ask one question"]},
  {"id": "behavior", "path": "interruption", "label": "Behavior", "group": "Configuration", "description": "Interruption and response behavior.", "aliases": ["interruption", "unsupported claims"]},
  {"id": "initiative", "path": "initiative", "label": "Initiative", "group": "Configuration", "description": "Proactive responses, silence and inactivity limits.", "aliases": ["cooldown", "silence"]},
  {"id": "voice", "path": "voice", "label": "Voice", "group": "Configuration", "description": "Voice identity, speaking rate and speech behavior.", "aliases": ["voice ID", "speech"]},
  {"id": "models", "path": "models", "label": "Models", "group": "Configuration", "description": "Interactive model defaults and reasoning effort.", "aliases": ["reasoning effort", "LLM"]},
  {"id": "providers", "path": "providers", "label": "Providers", "group": "Configuration", "description": "Independently selected language, recognition and synthesis providers.", "aliases": ["STT", "TTS", "classifier"]},
  {"id": "execution", "path": "execution", "label": "Execution budgets", "group": "Configuration", "description": "Step, duration and per-tool limits.", "aliases": ["deadline", "budget"]},
  { id: 'state-isolation', path: 'state-isolation', label: 'State & Isolation', group: 'Configuration', description: 'Author continuity defaults and Instance isolation ceilings.', aliases: ['home access', 'browser isolation', 'state awareness', 'Stateless'] },
  {"id": "sandbox", "path": "sandbox", "label": "Sandbox", "group": "Configuration", "description": "Authorize Global profiles and choose the default inherited by Instances.", "aliases": ["Workbench", "Python", "Node", "sandboxPolicy", "allowed profiles"]},
  {"id": "skills", "path": "skills", "label": "Skills", "group": "Configuration", "description": "Definition procedures and projection defaults.", "aliases": ["procedures", "on demand", "always"]},
  {"id": "resources", "path": "resources", "label": "Resources", "group": "Configuration", "description": "Immutable uploaded and imported Definition evidence.", "aliases": ["import", "files", "knowledge binding"]},
  {"id": "capabilities", "path": "capabilities", "label": "Capabilities", "group": "Governance", "description": "Exact capability authority, projection and knowledge references.", "aliases": ["selected", "always", "tools", "attachments", "workspace template"]},
  {"id": "memory-policy", "path": "memory-policy", "label": "Memory policy", "group": "Governance", "description": "Memory promotion and retrieval defaults.", "aliases": ["Session memory"]},
  {"id": "trigger-policy", "path": "trigger-policy", "label": "Trigger restrictions", "group": "Governance", "description": "Scheduling and Event authorization defaults.", "aliases": ["Schedule", "Event", "recurrence"]},
  {"id": "self-management", "path": "self-management", "label": "Self-management", "group": "Governance", "description": "Management authority and procedure defaults.", "aliases": ["freeze", "Harness"]},
  {"id": "metadata", "path": "metadata", "label": "Metadata", "group": "Advanced", "description": "Advanced Definition metadata.", "aliases": ["metadata"]},
  {"id": "test-publish", "path": "test-publish", "label": "Test & Publish", "group": "Release", "description": "Validation, Synthetic evaluation, publication diff and blockers.", "aliases": ["validate", "scenarios", "evaluation", "diff"]},
  {"id": "advanced-json", "path": "advanced-json", "label": "Advanced JSON", "group": "Advanced", "description": "Full JSON view of the same revisioned candidate.", "aliases": ["schema", "repair"]},
] as const;
export type AdminCollection = typeof globalPages[number]['id'];
export type InstancePage = typeof instancePages[number]['id'];
export type DefinitionPage = typeof definitionPages[number]['id'];
export type DraftPage = typeof draftPages[number]['id'];
export type NavigationPage = { id: string; path: string; label: string; group: string; description: string; aliases: readonly string[] };

export const instanceSettingGroups: Partial<Record<InstancePage, readonly string[]>> = {
  instructions: ['instructions'], conversation: ['conversationPolicy'], behavior: ['behaviorPolicy'],
  initiative: ['initiativePolicy'], voice: ['voice'], models: ['modelDefaults'], providers: ['providerPreferences'],
  capabilities: ['capabilities'], 'trigger-policy': ['triggerPolicy'], 'memory-policy': ['memoryPolicy'],
  sandbox: ['sandboxPolicy'],
};
export const draftSettingGroups: Partial<Record<DraftPage, readonly string[]>> = {
  ...instanceSettingGroups, execution: undefined, 'self-management': ['selfManagement'], metadata: ['metadata'],
};
export const legacyDraftPages: Record<string, DraftPage> = { behavior: 'instructions', policies: 'memory-policy' };

export function instancePath(id: string, page: InstancePage = 'overview') {
  return `/admin/instances/${encodeURIComponent(id.toLowerCase())}/${instancePages.find(p => p.id === page)!.path}`;
}
export function definitionPath(id: string, page: DefinitionPage = 'versions') {
  return `/admin/definitions/${encodeURIComponent(id)}/${page}`;
}
export function draftPath(id: string, draftId: string, page: DraftPage = 'overview') {
  return `${definitionPath(id, 'drafts')}/${encodeURIComponent(draftId)}/${draftPages.find(p => p.id === page)!.path}`;
}
export function globalPath(page: AdminCollection = 'instances') { return `/admin/${globalPages.find(p => p.id === page)!.path}`; }

/** Approved presentation labels only, with stable section anchors; no private content. */
export const settingSearch = [
  { label: 'Operating instructions', page: 'instructions', anchor: 'instructions', aliases: ['system instructions', 'prompt'] },
  { label: 'Conversation language & response length', page: 'conversation', anchor: 'conversationPolicy', aliases: ['maximum output tokens', 'ask one question'] },
  { label: 'Interruption behavior', page: 'behavior', anchor: 'behaviorPolicy', aliases: ['unsupported claims', 'acknowledge'] },
  { label: 'Initiative & silence', page: 'initiative', anchor: 'initiativePolicy', aliases: ['proactive', 'cooldown', 'inactivity'] },
  { label: 'Voice', page: 'voice', anchor: 'voice', aliases: ['voice ID', 'speaking rate'] },
  { label: 'Interactive model & reasoning', page: 'models', anchor: 'modelDefaults', aliases: ['default model', 'reasoning effort'] },
  { label: 'Provider preferences', page: 'providers', anchor: 'providerPreferences', aliases: ['language model', 'STT', 'TTS', 'classifier'] },
  { label: 'Execution budgets', page: 'execution', anchor: 'execution-budgets', aliases: ['deadline', 'steps', 'duration', 'per tool'] },
  { label: 'State & Isolation', page: 'state-isolation', draftPage: 'state-isolation', anchor: 'state-isolation', aliases: ['Persistent', 'Session-isolated', 'Stateless', 'home access', 'browser isolation', 'continuity'] },
  { label: 'Sandbox environment', page: 'sandbox', draftPage: 'sandbox', anchor: 'sandboxPolicy', aliases: ['Workbench', 'Python', 'Node', 'allowed profiles', 'initialization'] },
  { label: 'Memory policy', page: 'memory-policy', draftPage: 'memory-policy', anchor: 'memoryPolicy', aliases: ['promotion', 'retrieval', 'Session memory'] },
  { label: 'Trigger restrictions', page: 'trigger-policy', draftPage: 'trigger-policy', anchor: 'restrictions', draftAnchor: 'triggerPolicy', aliases: ['Schedule', 'Event', 'source kinds', 'recurrence'] },
  { label: 'Unattended execution defaults', page: 'unattended', anchor: 'execution-defaults', instanceOnly: true, aliases: ['unattended model', 'reasoning'] },
  { label: 'Credential bindings', page: 'credentials', anchor: 'credentials', instanceOnly: true, aliases: ['email account', 'grant'] },
  { label: 'Capabilities', page: 'capabilities', draftPage: 'capabilities', anchor: 'capabilities', aliases: ['selected capabilities', 'always projected', 'attachments'] },
  { label: 'Advanced metadata', page: 'effective', draftPage: 'metadata', anchor: 'metadata', draftOnly: true, aliases: ['metadata'] },
] satisfies readonly { label: string; page: InstancePage; draftPage?: DraftPage; anchor: string; draftAnchor?: string; aliases: readonly string[]; instanceOnly?: boolean; draftOnly?: boolean }[];
