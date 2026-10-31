import { ApiOutlined, ApartmentOutlined, AppstoreOutlined, AudioOutlined, BranchesOutlined, CheckSquareOutlined, CloudUploadOutlined, CodeOutlined, ControlOutlined, DatabaseOutlined, DeploymentUnitOutlined, ExperimentOutlined, FieldTimeOutlined, FileTextOutlined, FolderOutlined, HistoryOutlined, IdcardOutlined, InfoCircleOutlined, KeyOutlined, LockOutlined, MessageOutlined, NodeIndexOutlined, ProfileOutlined, RobotOutlined, SafetyOutlined, SettingOutlined, TeamOutlined, ThunderboltOutlined, ToolOutlined, UnorderedListOutlined, UserOutlined } from '@ant-design/icons';

/** Semantic treatments shared with the canonical navigation inventory. */
export const navigationIcons: Record<string, typeof UserOutlined> = {
  chat: MessageOutlined, organizations: ApartmentOutlined, members: TeamOutlined, roles: SafetyOutlined,
  access: SafetyOutlined, audit: HistoryOutlined, governance: SafetyOutlined, 'my-profile': UserOutlined,
  instances: RobotOutlined, agents: RobotOutlined, spaces: FolderOutlined, workflows: BranchesOutlined,
  approvals: CheckSquareOutlined, evaluations: ExperimentOutlined, secrets: LockOutlined,
  definitions: FileTextOutlined, credentials: KeyOutlined, events: ThunderboltOutlined,
  'ai-models': DeploymentUnitOutlined, 'system-model-presets': DeploymentUnitOutlined,
  'system-organizations': ApartmentOutlined, 'system-accounts': TeamOutlined,
  'system-operators': SafetyOutlined, 'system-audit': HistoryOutlined, 'browser-privacy': SafetyOutlined,
  sandboxes: CodeOutlined, sandbox: CodeOutlined, overview: InfoCircleOutlined, profile: UserOutlined,
  version: BranchesOutlined, settings: ControlOutlined, connections: ApiOutlined,
  instructions: FileTextOutlined, conversation: MessageOutlined, behavior: ControlOutlined, initiative: ThunderboltOutlined,
  voice: AudioOutlined, models: DeploymentUnitOutlined, providers: ApiOutlined, execution: FieldTimeOutlined,
  'execution-budgets': FieldTimeOutlined, 'unattended-model': DeploymentUnitOutlined,
  skills: ToolOutlined, resources: FolderOutlined, workspace: FolderOutlined, capabilities: SafetyOutlined,
  'self-management': SettingOutlined, 'trigger-policy': NodeIndexOutlined, 'memory-policy': DatabaseOutlined,
  automations: FieldTimeOutlined, unattended: RobotOutlined, memory: DatabaseOutlined, experience: ExperimentOutlined,
  'state-isolation': SafetyOutlined, state: InfoCircleOutlined, sessions: MessageOutlined, runs: HistoryOutlined,
  'activity-sessions': MessageOutlined, 'activity-runs': HistoryOutlined, lifecycle: IdcardOutlined, effective: ProfileOutlined,
  versions: BranchesOutlined, drafts: FileTextOutlined, metadata: UnorderedListOutlined,
  'test-publish': CloudUploadOutlined, 'advanced-json': CodeOutlined, identity: IdcardOutlined,
  // The grid denotes the catalog only, never unrelated resource destinations.
  catalog: AppstoreOutlined,
};
export function navigationIconId(id: string) { return id.replace(/^instance-/, ''); }
const warned = new Set<string>();
export function AdminNavigationIcon({ id }: { id: string }) {
  const Icon = navigationIcons[navigationIconId(id)];
  if (!Icon) {
    if (import.meta.env.DEV && !warned.has(id)) { warned.add(id); console.warn(`Navigation icon needs review: ${id}`); }
    return null; // Explicit iconless secondary treatment; never invent a gear.
  }
  return <Icon aria-hidden className="admin-nav-icon" />;
}
