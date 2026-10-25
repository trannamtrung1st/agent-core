import { ApiOutlined, AppstoreOutlined, AudioOutlined, BranchesOutlined, CloudUploadOutlined, CodeOutlined, ControlOutlined, DatabaseOutlined, ExperimentOutlined, FieldTimeOutlined, FileTextOutlined, FolderOutlined, HistoryOutlined, IdcardOutlined, InfoCircleOutlined, KeyOutlined, MessageOutlined, NodeIndexOutlined, ProfileOutlined, RobotOutlined, SafetyOutlined, SettingOutlined, ThunderboltOutlined, ToolOutlined, UnorderedListOutlined, UserOutlined } from '@ant-design/icons';
const icons: Record<string, typeof UserOutlined> = {
  organizations: AppstoreOutlined, members: UserOutlined, roles: SafetyOutlined, access: KeyOutlined, audit: HistoryOutlined,
  instances: RobotOutlined, definitions: AppstoreOutlined, credentials: KeyOutlined, events: ThunderboltOutlined,
  'ai-models': RobotOutlined, 'system-model-presets': RobotOutlined, 'system-organizations': AppstoreOutlined, 'system-accounts': UserOutlined, 'system-operators': SafetyOutlined, 'system-audit': HistoryOutlined, 'browser-privacy': SafetyOutlined, sandboxes: CodeOutlined, overview: InfoCircleOutlined, profile: UserOutlined, version: BranchesOutlined,
  instructions: FileTextOutlined, conversation: MessageOutlined, behavior: ControlOutlined, initiative: ThunderboltOutlined,
  voice: AudioOutlined, models: RobotOutlined, providers: ApiOutlined, execution: FieldTimeOutlined,
  skills: ToolOutlined, resources: FolderOutlined, workspace: CodeOutlined, capabilities: SafetyOutlined,
  'self-management': SettingOutlined, 'trigger-policy': NodeIndexOutlined, 'memory-policy': DatabaseOutlined,
  automations: FieldTimeOutlined, unattended: RobotOutlined, memory: DatabaseOutlined, experience: ExperimentOutlined,
  'state-isolation': SafetyOutlined, state: InfoCircleOutlined, sessions: MessageOutlined, runs: HistoryOutlined, lifecycle: IdcardOutlined, effective: ProfileOutlined,
  versions: BranchesOutlined, drafts: FileTextOutlined, metadata: UnorderedListOutlined,
  'test-publish': CloudUploadOutlined, 'advanced-json': CodeOutlined,
};
export function AdminNavigationIcon({ id }: { id: string }) {
  const Icon = icons[id] ?? SettingOutlined;
  return <Icon aria-hidden className="admin-nav-icon" />;
}
