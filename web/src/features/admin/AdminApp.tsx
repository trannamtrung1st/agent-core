import { updateHarness } from "../../services/adminApi";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  Alert,
  App,
  Button,
  Collapse,
  Descriptions,
  Empty,
  Flex,
  Form,
  Input,
  Layout,
  List,
  Modal,
  Radio,
  Result,
  Select,
  Spin,
  Switch,
  Tabs,
  Tag,
  Tooltip,
  Typography,
  Upload
} from "antd";
import { ArrowLeftOutlined, DeleteOutlined, InboxOutlined, MessageOutlined, RightOutlined } from "@ant-design/icons";
import {
  applyDraftEnvironmentToCandidate,
  readDraftEnvironment,
  type DraftEnvironment,
  type DraftKnowledgeSource
} from "./draftEnvironment";
import {
  applyCandidateJson,
  candidateForPersistence,
  candidateToJson,
  candidatesEqual,
  cloneCandidate,
  type DefinitionCandidate
} from "./definitionCandidate";
import { DefinitionCandidateEditor, PublishedSkillList, type DefinitionEditorView } from "./definitionCandidateEditor";
import { HarnessManagementSection, HarnessPolicyModeScopes } from "./HarnessManagementSection";
import { ApplicationConnectionSection } from "./ApplicationConnectionSection";
import { EventSourcesSection } from "./EventSourcesSection";
import { EventSubscriptionsSection } from "./EventSubscriptionsSection";
import { DefinitionDraftPublishGatePanel } from "./definitionDraftPublishGatePanel";
import { ResourceImportPanel } from "./resourceImportPanel";
import {
  type AdminDefinitionDraft,
  type AdminDefinitionDraftResource,
  type AdminDefinitionDraftSummary,
  type AdminDefinitionInventoryItem,
  type AdminDefinitionPublicationResource,
  type AdminDefinitionPublicationSummary,
  type AdminEffectiveConfiguration,
  type AdminInstanceInventoryItem,
  deprecateAdminDefinitionPublication,
  deleteAdminAgentInstance,
  deleteAdminDefinition,
  deleteAdminDefinitionDraft,
  createNewAdminDefinitionDraft,
  createAdminAgentInstance,
  type AdminCreateInstancePersona,
  forkAdminDefinitionDraft,
  getAdminDefinitionDraft,
  getAdminEffectiveConfig,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminInstances,
  listAdminPublicationResources,
  listAdminToolNames,
  publishAdminDefinitionDraft,
  removeAdminDraftResource,
  updateAdminDefinitionDraft,
  updateAdminAgentInstanceActiveVersion,
  updateAdminAgentInstanceLifecycle,
  updateAdminAgentInstancePersona,
  uploadAdminDraftResourceContent,
  upsertAdminDraftResource
} from "../../services/adminApi";
import {
  buildInstanceVersionOptions,
  managedInstanceVersionActionLabel,
  isPersonaDraftDirty,
  parsePersonaJson,
  syncPersonaOnTabChange,
  UNSAVED_PERSONA_DISCARD_MESSAGE,
  type PersonaFields
} from "./instanceManagedControls";
import {
  adminDefinitionPath,
  adminHomePath,
  adminInstancePath,
  lastChatUrl,
  navigateToAppPath,
  rememberChatUrl,
  type AdminRoute
} from "../../app/appRoute";
import { confirmAction } from "../../app/confirmAction";
import { AdminDeletionBlockedAlert } from "./adminDeletionBlocked";
import { describeAdminError, formatAdminLoadError, reportAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { startManagedPublicationChat } from "./adminManagedChat";
import { InstanceMemoryAutomationPanel } from "./instanceMemoryAutomation";

const { Header, Content } = Layout;

type LoadState<T> =
  | { kind: "loading" }
  | { kind: "error"; message: string; unauthorized?: boolean; diagnosticId?: string }
  | { kind: "ready"; data: T };

type DefinitionInventoryGroup = {
  definitionId: string;
  logicalName: string;
  defaultPersona: string;
  latestVersion: number;
  latestStatus: string;
  versions: AdminDefinitionInventoryItem[];
  draftCount: number;
  draftOnly: boolean;
};

function logicalDefinitionName(definitionId: string) {
  return definitionId
    .split("-")
    .filter(Boolean)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
    .join(" ");
}

function formatAdminTimestamp(value: string) {
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) {
    return value;
  }
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(timestamp);
}

function formatInventorySource(source: string) {
  return source === "builtIn" ? "Built-in" : "Durable";
}

function formatInventoryStatus(status: string) {
  if (!status) {
    return status;
  }
  const normalized = status.toLowerCase();
  return `${normalized.charAt(0).toUpperCase()}${normalized.slice(1)}`;
}

function formatDraftSource(sourceKind: string) {
  if (sourceKind === "ForkBuiltIn") {
    return "Built-in source";
  }
  if (sourceKind === "New") {
    return "New definition";
  }
  return "Durable source";
}

export function latestActiveVersion(versions: AdminDefinitionInventoryItem[]): number | null {
  const active = [...versions]
    .filter((row) => row.status.toLowerCase() !== "deprecated")
    .sort((left, right) => right.version - left.version)[0];
  return active?.version ?? null;
}

export function formatDefinitionVersionSummary(group: {
  latestVersion: number;
  latestStatus: string;
  versions: AdminDefinitionInventoryItem[];
  draftOnly?: boolean;
}): string {
  if (group.draftOnly) {
    return "Draft only · Never published";
  }
  const active = latestActiveVersion(group.versions);
  const latestStatus = formatInventoryStatus(group.latestStatus);
  if (active !== null && active !== group.latestVersion) {
    return `Latest v${group.latestVersion} · ${latestStatus} · Latest active v${active}`;
  }
  return `Latest v${group.latestVersion} · ${latestStatus}`;
}

export function formatDefinitionInventoryCounts(group: {
  versions: AdminDefinitionInventoryItem[];
  draftCount: number;
  draftOnly?: boolean;
}): string {
  const drafts = `${group.draftCount} ${group.draftCount === 1 ? "draft" : "drafts"}`;
  if (group.draftOnly) {
    return drafts;
  }
  const versions = `${group.versions.length} ${group.versions.length === 1 ? "version" : "versions"}`;
  return group.draftCount > 0 ? `${drafts} · ${versions}` : versions;
}

export function defaultForkSourceVersion(versions: AdminDefinitionInventoryItem[]): number | null {
  if (versions.length === 0) {
    return null;
  }
  const ordered = [...versions].sort((left, right) => right.version - left.version);
  const eligible = ordered.find((row) => row.status.toLowerCase() !== "deprecated");
  return (eligible ?? ordered[0]).version;
}

export function formatForkSourceOptionLabel(row: AdminDefinitionInventoryItem) {
  return `v${row.version} · ${formatInventorySource(row.source)} · ${formatInventoryStatus(row.status)}`;
}

export function groupDefinitionInventory(
  items: AdminDefinitionInventoryItem[]
): DefinitionInventoryGroup[] {
  const groups = new Map<string, AdminDefinitionInventoryItem[]>();
  for (const item of items) {
    const versions = groups.get(item.definitionId) ?? [];
    versions.push(item);
    groups.set(item.definitionId, versions);
  }

  return Array.from(groups, ([definitionId, versions]) => {
    const orderedVersions = [...versions].sort((left, right) => right.version - left.version);
    const published = orderedVersions.filter((row) => row.status.toLowerCase() !== "draftonly");
    const draftCount = orderedVersions.reduce((count, row) => Math.max(count, row.draftCount ?? 0), 0);
    if (published.length === 0) {
      const draftRow = orderedVersions[0];
      return {
        definitionId,
        logicalName: logicalDefinitionName(definitionId),
        defaultPersona: draftRow?.displayName ?? logicalDefinitionName(definitionId),
        latestVersion: 0,
        latestStatus: "draftOnly",
        versions: [],
        draftCount,
        draftOnly: true
      };
    }
    const latest = published[0];
    return {
      definitionId,
      logicalName: logicalDefinitionName(definitionId),
      defaultPersona: latest.displayName,
      latestVersion: latest.version,
      latestStatus: latest.status,
      versions: published,
      draftCount,
      draftOnly: false
    };
  });
}

export function AdminApp({ route }: { route: AdminRoute }) {
  const [definitions, setDefinitions] = useState<LoadState<AdminDefinitionInventoryItem[]>>({ kind: "loading" });
  const [instances, setInstances] = useState<LoadState<AdminInstanceInventoryItem[]>>({ kind: "loading" });
  const [effectiveConfig, setEffectiveConfig] = useState<LoadState<AdminEffectiveConfiguration>>({ kind: "loading" });

  const reloadDefinitions = useCallback(async () => {
    setDefinitions({ kind: "loading" });
    try {
      const definitionItems = await listAdminDefinitions();
      setDefinitions({ kind: "ready", data: definitionItems });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setDefinitions({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId
      });
    }
  }, []);

  const reloadInstances = useCallback(async () => {
    setInstances({ kind: "loading" });
    try {
      const instanceItems = await listAdminInstances();
      setInstances({ kind: "ready", data: instanceItems });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setInstances({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId
      });
    }
  }, []);

  const reloadInventory = useCallback(async () => {
    await Promise.all([reloadDefinitions(), reloadInstances()]);
  }, [reloadDefinitions, reloadInstances]);

  const reloadEffectiveConfig = useCallback(async (instanceId: string) => {
    setEffectiveConfig({ kind: "loading" });
    try {
      const data = await getAdminEffectiveConfig(instanceId);
      setEffectiveConfig({ kind: "ready", data });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setEffectiveConfig({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId
      });
    }
  }, []);

  useEffect(() => {
    void reloadInventory();
  }, [reloadInventory]);

  useEffect(() => {
    if (route.view !== "instance") {
      setEffectiveConfig({ kind: "loading" });
      return;
    }

    void reloadEffectiveConfig(route.instanceId);
  }, [route, reloadEffectiveConfig]);

  const returnToChat = () => {
    navigateToAppPath(lastChatUrl(), true);
  };

  const openChat = () => {
    rememberChatUrl(window.location.pathname);
    navigateToAppPath("/", false);
  };
  const definitionGroups = definitions.kind === "ready"
    ? groupDefinitionInventory(definitions.data)
    : [];

  return (
    <App className="antd-root" message={{ duration: 3, maxCount: 3 }}>
    <Layout className="admin-layout">
      <Header className="admin-header">
        <Flex align="center" justify="space-between" gap={12} wrap="wrap" className="admin-header-inner">
          <Flex align="center" gap={12}>
            <Typography.Title level={3} className="admin-title">
              Admin
            </Typography.Title>
            <Button type="text" icon={<MessageOutlined />} onClick={openChat}>
              Chat
            </Button>
          </Flex>
          <Button icon={<ArrowLeftOutlined />} onClick={returnToChat} aria-label="Return to last chat">
            <span className="admin-return-label-wide">Return to last chat</span>
            <span className="admin-return-label-compact">Return</span>
          </Button>
        </Flex>
      </Header>
      <Content className="admin-content">
        {route.view === "home" ? (
          <div className="admin-home">
            <Flex align="start" justify="space-between" gap={12} wrap="wrap" className="admin-home-intro">
              <div>
                <Typography.Title level={2} className="admin-home-title">Agent inventory</Typography.Title>
                <Typography.Paragraph type="secondary" className="admin-home-subtitle">
                  Inspect definitions, including drafts that have not been published, and the instances pinned to them.
                </Typography.Paragraph>
              </div>
              <Flex gap={8} wrap="wrap">
                <NewDefinitionButton groups={definitionGroups} />
                <NewInstanceButton groups={definitionGroups.filter((group) => group.versions.length > 0)} />
              </Flex>
            </Flex>
            <div className="admin-inventory-grid">
              <InventorySection
                title="Definitions"
                countLabel={definitions.kind === "ready"
                  ? `${definitionGroups.length} ${definitionGroups.length === 1 ? "definition" : "definitions"}`
                  : null}
                emptyLabel="No definitions found."
                loading={definitions.kind === "loading"}
                error={definitions.kind === "error" ? definitions.message : null}
                diagnosticId={definitions.kind === "error" ? definitions.diagnosticId : null}
                unauthorized={definitions.kind === "error" ? (definitions.unauthorized ?? false) : false}
                onRetry={() => void reloadDefinitions()}
                items={
                  definitions.kind === "ready"
                    ? definitionGroups.map((group) => ({
                        key: group.definitionId,
                        title: group.logicalName,
                        secondary: group.definitionId,
                        description: formatDefinitionVersionSummary(group),
                        detail: formatDefinitionInventoryCounts(group),
                        tag: group.draftOnly ? "Draft" : formatInventoryStatus(group.latestStatus),
                        onClick: () => navigateToAppPath(adminDefinitionPath(group.definitionId))
                      }))
                    : []
                }
              />
              <InventorySection
                title="Instances"
                countLabel={instances.kind === "ready"
                  ? `${instances.data.length} ${instances.data.length === 1 ? "instance" : "instances"}`
                  : null}
                emptyLabel="No instances yet."
                loading={instances.kind === "loading"}
                error={instances.kind === "error" ? instances.message : null}
                diagnosticId={instances.kind === "error" ? instances.diagnosticId : null}
                unauthorized={instances.kind === "error" ? (instances.unauthorized ?? false) : false}
                onRetry={() => void reloadInstances()}
                items={
                  instances.kind === "ready"
                    ? instances.data.map((item) => ({
                        key: item.instanceId,
                      title: `${item.personaName} · ${item.definitionId}`,
                      secondary: item.compatibility ? "Compatibility / legacy instance" : "Managed instance",
                      description: `Pinned to v${item.activeVersion}`,
                        tag: item.compatibility ? "Compatibility" : "Managed",
                        onClick: () => navigateToAppPath(adminInstancePath(item.instanceId))
                      }))
                    : []
                }
              />
            </div>
            <div className="admin-home-sources">
              <EventSourcesSection />
            </div>
          </div>
        ) : null}

        {route.view === "definition" ? (
          <DefinitionDetail
            definitionId={route.definitionId}
            definitions={definitions}
            onBack={() => navigateToAppPath(adminHomePath())}
            onRetryDefinitions={reloadDefinitions}
            onDeleted={() => {
              void reloadDefinitions();
              navigateToAppPath(adminHomePath());
            }}
          />
        ) : null}

        {route.view === "instance" ? (
          <InstanceDetail
            instanceId={route.instanceId}
            instances={instances}
            effective={effectiveConfig}
            onBack={() => navigateToAppPath(adminHomePath())}
            onRetryEffective={() => void reloadEffectiveConfig(route.instanceId)}
            onInstanceChanged={() => {
              void reloadEffectiveConfig(route.instanceId);
              void reloadInstances();
            }}
            onInstanceDeleted={() => {
              void reloadInstances();
              navigateToAppPath(adminHomePath());
            }}
          />
        ) : null}
      </Content>
    </Layout>
    </App>
  );
}

function NewDefinitionButton({ groups }: { groups: DefinitionInventoryGroup[] }) {
  const { message } = App.useApp();
  const [open, setOpen] = useState(false);
  const [definitionId, setDefinitionId] = useState("");
  const [busy, setBusy] = useState(false);
  const definitionIdPattern = /^[a-z0-9-]{1,64}$/;
  const definitionIdInvalid = definitionId.trim().length > 0 && !definitionIdPattern.test(definitionId.trim());

  const createNewDefinition = async () => {
    const nextId = definitionId.trim();
    if (!definitionIdPattern.test(nextId)) {
      message.error("Definition ID must be lowercase letters, digits, or hyphens.");
      return;
    }
    if (groups.some((group) => group.definitionId === nextId)) {
      message.error(`Definition '${nextId}' already exists. Open it to create or edit a draft.`);
      return;
    }
    setBusy(true);
    try {
      const draft = await createNewAdminDefinitionDraft(nextId);
      setOpen(false);
      setDefinitionId("");
      navigateToAppPath(adminDefinitionPath(draft.definitionId));
    } catch (error) {
      showAdminFailure(message, error, "New definition failed.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button type="primary" onClick={() => setOpen(true)}>
        New definition
      </Button>
      <Modal
        title="New definition"
        open={open}
        okText="Create draft"
        cancelText="Cancel"
        confirmLoading={busy}
        okButtonProps={{ disabled: !definitionIdPattern.test(definitionId.trim()) }}
        onOk={() => void createNewDefinition()}
        onCancel={() => {
          if (!busy) {
            setOpen(false);
          }
        }}
      >
        <label className="admin-draft-field">
          <Typography.Text strong>Definition ID</Typography.Text>
          <Input
            aria-label="Definition ID"
            aria-describedby="new-definition-id-hint"
            aria-invalid={definitionIdInvalid}
            status={definitionIdInvalid ? "error" : undefined}
            value={definitionId}
            onChange={(event) => setDefinitionId(event.target.value)}
            placeholder="field-guide"
            autoComplete="off"
          />
          <Typography.Text id="new-definition-id-hint" type="secondary" className="admin-draft-field-hint">
            Lowercase letters, digits, and hyphens, up to 64 characters.
          </Typography.Text>
        </label>
      </Modal>
    </>
  );
}

function NewInstanceButton({ groups }: { groups: DefinitionInventoryGroup[] }) {
  const { message } = App.useApp();
  const [open, setOpen] = useState(false);
  const [definitionId, setDefinitionId] = useState("");
  const [version, setVersion] = useState<number | null>(null);
  const [personaMode, setPersonaMode] = useState<"default" | "custom">("default");
  const [harnessMode, setHarnessMode] = useState<import("../../services/adminApi").HarnessMode>("Disabled");
  const [harnessAreas, setHarnessAreas] = useState<import("../../services/adminApi").HarnessScope[]>(["KnowledgeResources", "Skills"]);
  const [persona, setPersona] = useState<AdminCreateInstancePersona>({
    name: "",
    role: "",
    description: "",
    tone: ""
  });
  const [busy, setBusy] = useState(false);
  const selectedGroup = groups.find((group) => group.definitionId === definitionId) ?? null;
  const selectedVersion = selectedGroup?.versions.find((row) => row.version === version) ?? null;
  const customPersonaReady = personaFieldsReady(persona);
  const canCreate = Boolean(selectedGroup && selectedVersion) && (personaMode === "default" || customPersonaReady);

  const openModal = () => {
    const first = groups[0];
    if (first) {
      setDefinitionId(first.definitionId);
      setVersion(latestActiveVersion(first.versions) ?? first.latestVersion);
    } else {
      setDefinitionId("");
      setVersion(null);
    }
    setPersonaMode("default");
    setHarnessMode("Disabled");
    setHarnessAreas(["KnowledgeResources", "Skills"]);
    setPersona({ name: "", role: "", description: "", tone: "" });
    setOpen(true);
  };

  const createInstance = async () => {
    if (!selectedGroup || selectedVersion === null || version === null) {
      return;
    }
    setBusy(true);
    try {
      const created = await createAdminAgentInstance(
        selectedGroup.definitionId,
        version,
        personaMode === "custom" ? trimmedPersona(persona) : null
      );
      if (harnessMode !== "Disabled") {
        try {
          await updateHarness(created.instanceId, "policy", { expectedRevision: created.revision, mode: harnessMode,
            scopes: harnessAreas, sources: [], eligibleTools: [], frozen: false });
        } catch (error) { showAdminFailure(message, error, "Instance created; configure its authoring policy in the detail view."); }
      }
      setOpen(false);
      navigateToAppPath(adminInstancePath(created.instanceId));
    } catch (error) {
      showAdminFailure(message, error, "New instance failed.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button onClick={openModal}>New instance</Button>
      <Modal
        title="New instance"
        open={open}
        okText="Create instance"
        cancelText="Cancel"
        confirmLoading={busy}
        okButtonProps={{ disabled: !canCreate }}
        onOk={() => void createInstance()}
        onCancel={() => {
          if (!busy) {
            setOpen(false);
          }
        }}
      >
        {groups.length === 0 ? (
          <Typography.Paragraph>No published definition is available.</Typography.Paragraph>
        ) : (
          <Flex vertical gap={12}>
            <label className="admin-draft-field">
              <Typography.Text strong>Definition</Typography.Text>
              <Select
                aria-label="Definition"
                value={definitionId}
                options={groups.map((group) => ({
                  value: group.definitionId,
                  label: `${group.logicalName} · ${group.definitionId}`
                }))}
                onChange={(nextId) => {
                  setDefinitionId(nextId);
                  const next = groups.find((group) => group.definitionId === nextId);
                  if (next) {
                    setVersion(latestActiveVersion(next.versions) ?? next.latestVersion);
                  }
                }}
                disabled={busy}
              />
            </label>
            <label className="admin-draft-field">
              <Typography.Text strong>Published version</Typography.Text>
              <Select
                aria-label="Published version"
                value={version ?? undefined}
                options={(selectedGroup?.versions ?? []).map((row) => ({
                  value: row.version,
                  label: formatForkSourceOptionLabel(row)
                }))}
                onChange={(nextVersion) => setVersion(nextVersion)}
                disabled={busy}
              />
            </label>
            {selectedVersion && selectedVersion.status.toLowerCase() === "deprecated" ? (
              <Alert
                type="warning"
                showIcon
                message={`v${selectedVersion.version} is deprecated. New instances normally use the latest active publication.`}
              />
            ) : null}
            <Collapse items={[{ key: "harness", label: "Harness management (optional)", children:
              <Form layout="vertical"><HarnessPolicyModeScopes mode={harnessMode} scopes={harnessAreas} busy={busy}
                onMode={setHarnessMode} onScopes={setHarnessAreas} />
                <Typography.Text type="secondary">Configure permitted sources and prepare a candidate in the instance detail. Tool changes and publication always require your approval.</Typography.Text>
              </Form>
            }]} />
            <Radio.Group
              aria-label="Persona"
              value={personaMode}
              onChange={(event) => setPersonaMode(event.target.value)}
              disabled={busy}
            >
              <Radio value="default">Definition persona</Radio>
              <Radio value="custom">Custom persona</Radio>
            </Radio.Group>
            {personaMode === "custom" ? (
              <div className="admin-draft-field-grid">
                <PersonaField label="Persona name" value={persona.name} disabled={busy} onChange={(name) => setPersona({ ...persona, name })} />
                <PersonaField label="Persona role" value={persona.role} disabled={busy} onChange={(role) => setPersona({ ...persona, role })} />
                <PersonaField label="Persona description" value={persona.description} disabled={busy} onChange={(description) => setPersona({ ...persona, description })} />
                <PersonaField label="Persona tone" value={persona.tone} disabled={busy} onChange={(tone) => setPersona({ ...persona, tone })} />
              </div>
            ) : null}
          </Flex>
        )}
      </Modal>
    </>
  );
}

function PersonaField({
  label,
  value,
  disabled,
  onChange
}: {
  label: string;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <label className="admin-draft-field">
      <Typography.Text strong>{label}</Typography.Text>
      <Input aria-label={label} value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)} />
    </label>
  );
}

function trimmedPersona(persona: AdminCreateInstancePersona): AdminCreateInstancePersona {
  return {
    name: persona.name.trim(),
    role: persona.role.trim(),
    description: persona.description.trim(),
    tone: persona.tone.trim()
  };
}

function personaFieldsReady(persona: AdminCreateInstancePersona) {
  const fields = trimmedPersona(persona);
  return (
    fields.name.length >= 1 && fields.name.length <= 256
    && fields.role.length >= 1 && fields.role.length <= 256
    && fields.tone.length >= 1 && fields.tone.length <= 256
    && fields.description.length >= 1 && fields.description.length <= 1024
  );
}

function InventorySection({
  title,
  countLabel,
  emptyLabel,
  loading,
  error,
  diagnosticId,
  unauthorized,
  onRetry,
  items
}: {
  title: string;
  countLabel: string | null;
  emptyLabel: string;
  loading: boolean;
  error: string | null;
  diagnosticId?: string | null;
  unauthorized: boolean;
  onRetry: () => void;
  items: Array<{
    key: string;
    title: string;
    secondary?: string;
    description: string;
    detail?: string;
    tag?: string;
    onClick: () => void;
  }>;
}) {
  return (
    <section aria-label={title} className="admin-inventory-section">
      <Flex align="baseline" justify="space-between" gap={12} className="admin-inventory-heading">
        <Typography.Title level={4}>{title}</Typography.Title>
        {countLabel ? <Typography.Text type="secondary">{countLabel}</Typography.Text> : null}
      </Flex>
      {error ? (
        <Alert
          type={unauthorized ? "warning" : "error"}
          showIcon
          message={error}
          action={<AdminRetryAction onRetry={onRetry} diagnosticId={diagnosticId} />}
          className="admin-inventory-alert"
        />
      ) : null}
      {loading ? (
        <div className="admin-inventory-loading">
          <Spin aria-label={`Loading ${title}`} />
        </div>
      ) : error ? null : items.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyLabel} className="admin-inventory-empty" />
      ) : (
        <List
          className="admin-inventory-list"
          dataSource={items}
          renderItem={(item) => (
            <List.Item className="admin-inventory-item">
              <Button type="text" block className="admin-inventory-row" onClick={item.onClick}>
                <Flex align="center" justify="space-between" gap={12}>
                  <Flex vertical gap={4} className="admin-inventory-row-copy">
                    <Typography.Text strong className="admin-inventory-row-title">
                      {item.title}
                    </Typography.Text>
                    {item.secondary ? (
                      <Typography.Text type="secondary" className="admin-inventory-row-secondary">
                        {item.secondary}
                      </Typography.Text>
                    ) : null}
                    <Flex gap={8} wrap="wrap" align="center">
                      <Typography.Text type="secondary" className="admin-inventory-row-description">
                        {item.description}
                      </Typography.Text>
                      {item.detail ? (
                        <Typography.Text type="secondary" className="admin-inventory-row-description">
                          {item.detail}
                        </Typography.Text>
                      ) : null}
                      {item.tag ? <Tag>{item.tag}</Tag> : null}
                    </Flex>
                  </Flex>
                  <RightOutlined className="admin-inventory-row-arrow" aria-hidden />
                </Flex>
              </Button>
            </List.Item>
          )}
        />
      )}
    </section>
  );
}

function DefinitionDetail({
  definitionId,
  definitions,
  onBack,
  onRetryDefinitions,
  onDeleted
}: {
  definitionId: string;
  definitions: LoadState<AdminDefinitionInventoryItem[]>;
  onBack: () => void;
  onRetryDefinitions: () => Promise<void>;
  onDeleted: () => void;
}) {
  const { message, modal } = App.useApp();
  const rows = definitions.kind === "ready"
    ? definitions.data.filter((item) => item.definitionId === definitionId)
    : [];
  const group = rows.length > 0 ? groupDefinitionInventory(rows)[0] : null;
  const [lifecycleLoading, setLifecycleLoading] = useState(false);
  const [lifecycleError, setLifecycleErrorValue] = useState<string | null>(null);
  const [lifecycleDiagnosticId, setLifecycleDiagnosticId] = useState<string | null>(null);
  const setLifecycleError = (message: string | null, diagnosticId?: string | null) => {
    setLifecycleErrorValue(message);
    setLifecycleDiagnosticId(message ? diagnosticId ?? null : null);
  };
  const reportLifecycleError = (error: unknown, fallback: string) => {
    const notice = describeAdminError(error, fallback);
    setLifecycleError(notice.message, notice.diagnosticId ?? null);
  };
  const [draftSummaries, setDraftSummaries] = useState<AdminDefinitionDraftSummary[]>([]);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [activeDraft, setActiveDraft] = useState<AdminDefinitionDraft | null>(null);
  const [forkSourceVersion, setForkSourceVersion] = useState<number | null>(null);
  const [candidate, setCandidate] = useState<DefinitionCandidate>({});
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [editorView, setEditorView] = useState<DefinitionEditorView>("form");
  const [busy, setBusy] = useState(false);
  const editorSurfaceRef = useRef<HTMLElement | null>(null);
  const openedUnpublishedDraftRef = useRef(false);

  const dirtyCandidate = activeDraft !== null && !candidatesEqual(candidate, activeDraft.candidate);
  const dirty = activeDraft !== null && (dirtyCandidate || jsonError !== null);

  const loadCandidate = useCallback((next: DefinitionCandidate) => {
    const cloned = cloneCandidate(next);
    setCandidate(cloned);
    setJsonText(candidateToJson(cloned));
    setJsonError(null);
    setEditorView("form");
  }, []);

  const editCandidate = useCallback((next: DefinitionCandidate) => {
    if (jsonError) {
      return;
    }
    setCandidate(next);
    setJsonError(null);
    if (editorView === "json") {
      setJsonText(candidateToJson(next));
    }
  }, [editorView, jsonError]);

  const deprecatePublication = async (item: AdminDefinitionPublicationSummary) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      const updated = await deprecateAdminDefinitionPublication(
        definitionId,
        item.version,
        item.metadataRevision
      );
      setPublications((current) =>
        current.map((row) => (row.version === updated.version ? updated : row))
      );
      message.success(`Publication v${item.version} deprecated.`);
      await reloadLifecycle();
      await onRetryDefinitions();
    } catch (error) {
      reportLifecycleError(error, "Failed to deprecate publication.");
    } finally {
      setBusy(false);
    }
  };

  const reloadLifecycle = useCallback(async () => {
    setLifecycleLoading(true);
    setLifecycleError(null);
    try {
      const [drafts, pubs] = await Promise.all([
        listAdminDefinitionDrafts(),
        listAdminDefinitionPublications(definitionId)
      ]);
      setDraftSummaries(drafts.filter((item) => item.definitionId === definitionId));
      setPublications(pubs);
    } catch (error) {
      reportLifecycleError(error, "Failed to load drafts.");
    } finally {
      setLifecycleLoading(false);
    }
  }, [definitionId]);

  useEffect(() => {
    if (definitions.kind === "ready") {
      void reloadLifecycle();
    }
  }, [definitions.kind, reloadLifecycle]);

  useEffect(() => {
    if (!group) {
      setForkSourceVersion(null);
      return;
    }
    setForkSourceVersion((current) =>
      current !== null && group.versions.some((item) => item.version === current)
        ? current
        : defaultForkSourceVersion(group.versions)
    );
  }, [definitionId, group?.latestVersion, group?.versions.length]);

  useEffect(() => {
    if (!activeDraft?.draftId) {
      return;
    }
    const frame = window.requestAnimationFrame(() => {
      editorSurfaceRef.current?.scrollIntoView?.({ block: "start" });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [activeDraft?.draftId]);

  const closeDraftEditor = () => {
    setActiveDraft(null);
    loadCandidate({});
  };

  const selectDraft = useCallback(async (draftId: string) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      const draft = await getAdminDefinitionDraft(draftId);
      setActiveDraft(draft);
      loadCandidate(draft.candidate);
    } catch (error) {
      reportLifecycleError(error, "Failed to load draft.");
    } finally {
      setBusy(false);
    }
  }, [loadCandidate]);

  useEffect(() => {
    if (
      definitions.kind !== "ready"
      || (group !== null && !group.draftOnly)
      || openedUnpublishedDraftRef.current
      || activeDraft
      || draftSummaries.length !== 1
    ) {
      return;
    }
    openedUnpublishedDraftRef.current = true;
    void selectDraft(draftSummaries[0].draftId);
  }, [definitions.kind, group, activeDraft, draftSummaries, selectDraft]);

  const deleteLogicalDefinition = async () => {
    setBusy(true);
    setLifecycleError(null);
    try {
      await deleteAdminDefinition(definitionId, {
        drafts: draftSummaries.map((item) => ({
          draftId: item.draftId,
          revision: activeDraft?.draftId === item.draftId ? activeDraft.revision : item.revision
        })),
        publications: publications.map((item) => ({
          version: item.version,
          metadataRevision: item.metadataRevision
        }))
      });
      message.success("Definition deleted.");
      onDeleted();
    } catch (error) {
      reportLifecycleError(error, "Definition could not be deleted.");
    } finally {
      setBusy(false);
    }
  };

  const confirmDeleteDefinition = () => {
    confirmAction(modal, {
      title: "Delete this definition?",
      content:
        "This permanently removes its drafts, draft resources, evaluation evidence, and durable publications. Admin history is kept. Instances and sessions are not deleted. If anything still references this definition, deletion is refused.",
      okText: "Delete definition",
      cancelText: "Keep definition",
      danger: true,
      onOk: () => deleteLogicalDefinition()
    });
  };

  const canDeleteDefinition = definitions.kind === "ready"
    && !lifecycleLoading
    && !(group?.versions.some((row) => row.source === "builtIn") ?? false)
    && ((group?.versions.length ?? 0) > 0 || group?.draftOnly === true || draftSummaries.length > 0 || publications.length > 0);

  const deleteDraft = async (draft: AdminDefinitionDraftSummary, expectedRevision: number) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      await deleteAdminDefinitionDraft(draft.draftId, expectedRevision);
      if (activeDraft?.draftId === draft.draftId) {
        setActiveDraft(null);
        loadCandidate({});
      }
      message.success("Draft deleted.");
      await reloadLifecycle();
      await onRetryDefinitions();
    } catch (error) {
      reportLifecycleError(error, "Draft could not be deleted.");
      showAdminFailure(message, error, "Draft could not be deleted.");
    } finally {
      setBusy(false);
    }
  };

  const confirmDeleteDraft = (
    draft: AdminDefinitionDraftSummary,
    expectedRevision: number
  ) => {
    const draftLabel = draft.sourceVersion != null
      ? `draft from v${draft.sourceVersion}`
      : "draft";
    confirmAction(modal, {
      title: `Delete ${draftLabel}?`,
      content:
        "This permanently removes the draft, its unpublished resources, and evaluation evidence. Published versions are unchanged.",
      okText: "Delete draft",
      cancelText: "Keep draft",
      danger: true,
      onOk: () => deleteDraft(draft, expectedRevision)
    });
  };

  const forkFromVersion = async (row: AdminDefinitionInventoryItem) => {
    const sourceKind = row.source === "durable" ? "ForkDurable" : "ForkBuiltIn";
    setBusy(true);
    setLifecycleError(null);
    try {
      const draft = await forkAdminDefinitionDraft(definitionId, row.version, sourceKind);
      await selectDraft(draft.draftId);
      await reloadLifecycle();
    } catch (error) {
      reportLifecycleError(error, "Fork failed.");
    } finally {
      setBusy(false);
    }
  };

  const saveDraft = async () => {
    if (!activeDraft || jsonError) {
      return;
    }
    setBusy(true);
    setLifecycleError(null);
    try {
      const updated = await updateAdminDefinitionDraft(
        activeDraft.draftId,
        activeDraft.revision,
        candidateForPersistence(candidate));
      setActiveDraft(updated);
      loadCandidate(updated.candidate);
      message.success("Draft saved.");
      await reloadLifecycle();
    } catch (error) {
      reportLifecycleError(error, "Save failed.");
      showAdminFailure(message, error, "Save failed.");
    } finally {
      setBusy(false);
    }
  };

  const changeEditorView = (next: DefinitionEditorView) => {
    if (next === editorView) {
      return;
    }
    if (editorView === "json" && next === "form" && jsonError) {
      message.error("Fix Advanced JSON before returning to the form.");
      return;
    }
    if (next === "json") {
      setJsonText(candidateToJson(candidate));
      setJsonError(null);
    }
    setEditorView(next);
  };

  const changeJsonText = (text: string) => {
    setJsonText(text);
    const applied = applyCandidateJson(text);
    if (applied.ok) {
      setCandidate(applied.candidate);
      setJsonError(null);
      return;
    }
    setJsonError(applied.error);
  };

  const confirmPublish = () => {
    if (!activeDraft) {
      return;
    }
    if (dirty) {
      message.warning("Save draft edits before publishing.");
      return;
    }
    confirmAction(modal, {
      title: "Publish this draft?",
      content: dirty
        ? "Unsaved editor changes will be saved, then published as an immutable version."
        : "Validated content becomes an immutable published version. You can continue editing the draft afterward.",
      okText: "Publish",
      onOk: async () => {
        setBusy(true);
        try {
          let draft = activeDraft;
          if (dirty) {
            if (jsonError) {
              message.error("Fix Advanced JSON before publishing.");
              return;
            }
            draft = await updateAdminDefinitionDraft(
              draft.draftId,
              draft.revision,
              candidateForPersistence(candidate)
            );
            setActiveDraft(draft);
            loadCandidate(draft.candidate);
          }
          const publication = await publishAdminDefinitionDraft(draft.draftId, draft.revision);
          message.success(`Published version ${publication.version}.`);
          setActiveDraft(null);
          loadCandidate({});
          await reloadLifecycle();
          await onRetryDefinitions();
        } catch (error) {
          reportLifecycleError(error, "Publish failed.");
          showAdminFailure(message, error, "Publish failed.");
        } finally {
          setBusy(false);
        }
      }
    });
  };

  const startManagedChat = async (publicationDefinitionId: string, version: number) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      await startManagedPublicationChat(publicationDefinitionId, version);
    } catch (error) {
      reportLifecycleError(error, "Managed chat could not be started.");
      showAdminFailure(message, error, "Managed chat could not be started.");
      setBusy(false);
    }
  };
  const selectedForkRow = group?.versions.find((row) => row.version === forkSourceVersion)
    ?? group?.versions[0]
    ?? null;

  return (
    <Flex vertical gap={16}>
      <Button type="text" icon={<ArrowLeftOutlined />} className="admin-back-button" onClick={onBack}>
        Back to inventory
      </Button>
      {group ? (
        <div className="admin-definition-heading">
          <Flex align="start" justify="space-between" gap={12} wrap="wrap">
            <Flex vertical gap={8} className="admin-inventory-row-copy">
              <Typography.Title level={2}>{group.logicalName}</Typography.Title>
              <Typography.Text type="secondary" className="admin-inventory-row-secondary">
                {group.definitionId}
              </Typography.Text>
              {group.draftOnly ? null : (
                <Typography.Text type="secondary" className="admin-inventory-row-description">
                  Latest-version persona: {group.defaultPersona}
                </Typography.Text>
              )}
              <Typography.Text type="secondary" className="admin-inventory-row-description">
                {formatDefinitionVersionSummary(group)}
              </Typography.Text>
            </Flex>
            {canDeleteDefinition ? (
              <Button danger disabled={busy} onClick={confirmDeleteDefinition}>
                Delete definition
              </Button>
            ) : null}
          </Flex>
          <Flex gap={8} wrap="wrap">
            <Tag>{formatDefinitionInventoryCounts(group)}</Tag>
            <Tag>{group.draftOnly ? "Draft" : formatInventoryStatus(group.latestStatus)}</Tag>
            {!group.draftOnly && latestActiveVersion(group.versions) !== null
              && latestActiveVersion(group.versions) !== group.latestVersion ? (
                <Tag color="blue">Latest active v{latestActiveVersion(group.versions)}</Tag>
              ) : null}
          </Flex>
        </div>
      ) : (
        <Typography.Title level={4}>{definitionId}</Typography.Title>
      )}
      {definitions.kind === "loading" ? <Spin /> : null}
      {definitions.kind === "error" ? (
        <Alert
          type="error"
          showIcon
          message={definitions.message}
          action={
            <AdminRetryAction
              onRetry={onRetryDefinitions}
              diagnosticId={definitions.diagnosticId}
            />
          }
        />
      ) : null}
      {definitions.kind === "ready" && !group && !lifecycleLoading && draftSummaries.length === 0 ? (
        <Result status="404" title="Definition not found" />
      ) : null}
      {(group || draftSummaries.length > 0) && activeDraft ? (
        <section ref={editorSurfaceRef} className="admin-draft-focused" aria-label="Draft editor">
          <Flex align="center" justify="space-between" gap={12} wrap="wrap" className="admin-draft-focused-toolbar">
            {dirty ? (
              <Button
                icon={<ArrowLeftOutlined />}
                aria-label="Back to drafts"
                onClick={() =>
                  confirmAction(modal, {
                    title: "Discard unsaved changes?",
                    content:
                      "Your saved draft remains available. Only unsaved edits in this editor will be discarded.",
                    okText: "Discard changes",
                    cancelText: "Keep editing",
                    onOk: closeDraftEditor
                  })
                }
              >
                Back to drafts
              </Button>
            ) : (
              <Button icon={<ArrowLeftOutlined />} aria-label="Back to drafts" onClick={closeDraftEditor}>
                Back to drafts
              </Button>
            )}
            <Button
              danger
              icon={<DeleteOutlined />}
              aria-label="Delete draft"
              disabled={busy}
              onClick={() => confirmDeleteDraft(activeDraft, activeDraft.revision)}
            >
              Delete draft
            </Button>
          </Flex>
          {lifecycleError ? (
            <AdminDeletionBlockedAlert
              className="admin-destructive-detail"
              message={lifecycleError}
              diagnosticId={lifecycleDiagnosticId}
              action={<Button size="small" onClick={() => void reloadLifecycle()}>Retry</Button>}
            />
          ) : null}
          <DraftEditor
            activeDraft={activeDraft}
            candidate={candidate}
            jsonText={jsonText}
            jsonError={jsonError}
            editorView={editorView}
            dirty={dirty}
            busy={busy}
            onCandidateChange={editCandidate}
            onJsonTextChange={changeJsonText}
            onEditorViewChange={changeEditorView}
            onSave={() => void saveDraft()}
            onPublish={() => void confirmPublish()}
            onDraftRevisionChange={(draft, options) => {
              setActiveDraft(draft);
              if (!options?.preserveLocalEdits) {
                loadCandidate(draft.candidate);
              }
            }}
            onError={setLifecycleError}
          />
        </section>
      ) : group || lifecycleLoading || draftSummaries.length > 0 ? (
        <div className="admin-definition-workspace">
          {group && !group.draftOnly ? (
          <section aria-label="Definition versions" className="admin-definition-panel admin-definition-versions">
            <Flex align="baseline" justify="space-between" gap={12} className="admin-definition-panel-heading">
              <Typography.Title level={4}>Versions</Typography.Title>
              <Typography.Text type="secondary">Latest v{group.latestVersion}</Typography.Text>
            </Flex>
            <div className="admin-definition-panel-body">
              <Descriptions bordered size="small" column={1}>
                {group.versions.map((row) => (
                  <Descriptions.Item key={`${row.definitionId}:${row.version}`} label={`v${row.version}`}>
                    <Flex gap={8} wrap="wrap" align="center">
                      <Typography.Text>{row.displayName} · {row.source} · {row.status}</Typography.Text>
                      {row.version === group.latestVersion ? <Tag color="blue">Latest</Tag> : null}
                      {row.version === latestActiveVersion(group.versions)
                        && row.version !== group.latestVersion ? (
                          <Tag color="blue">Latest active</Tag>
                        ) : null}
                    </Flex>
                  </Descriptions.Item>
                ))}
              </Descriptions>
            </div>
          </section>
          ) : null}
          <section aria-label="Definition drafts" className="admin-definition-panel admin-definition-drafts">
            <div className="admin-definition-panel-heading">
              <Typography.Title level={4}>Drafts &amp; publishing</Typography.Title>
              <Typography.Text type="secondary">
                Fork an immutable version to edit, validate, and publish a new one.
              </Typography.Text>
            </div>
            <div className="admin-definition-panel-body">
              {group && !group.draftOnly ? (
              <Flex gap={8} wrap="wrap" align="center" className="admin-draft-create">
                <Select
                  aria-label="Base version"
                  value={forkSourceVersion}
                  onChange={setForkSourceVersion}
                  options={group.versions.map((row) => ({
                    value: row.version,
                    label: `v${row.version} · ${formatInventorySource(row.source)}`
                  }))}
                  optionRender={(option) => {
                    const row = group.versions.find((item) => item.version === option.value);
                    return row ? formatForkSourceOptionLabel(row) : option.label;
                  }}
                  disabled={busy}
                  className="admin-draft-version-select"
                />
                <Button
                  type="primary"
                  aria-label={selectedForkRow
                    ? `Fork v${selectedForkRow.version} (${selectedForkRow.source})`
                    : "Fork version"}
                  onClick={() => selectedForkRow && void forkFromVersion(selectedForkRow)}
                  disabled={busy || !selectedForkRow}
                >
                  Create draft
                </Button>
              </Flex>
              ) : (
                <Typography.Paragraph type="secondary">
                  This definition has no published version yet. Edit the starter draft, then validate and publish it.
                </Typography.Paragraph>
              )}
          {lifecycleError ? (
            <AdminDeletionBlockedAlert
              className="admin-draft-status admin-destructive-detail"
              message={lifecycleError}
              diagnosticId={lifecycleDiagnosticId}
              action={<Button size="small" onClick={() => void reloadLifecycle()}>Retry</Button>}
            />
          ) : null}
          {lifecycleLoading ? <Spin className="admin-draft-status" /> : null}
          {!lifecycleLoading && draftSummaries.length > 0 ? (
            <div className="admin-draft-list">
              <Flex align="baseline" justify="space-between" gap={12} className="admin-draft-list-heading">
                <Typography.Text strong>Existing drafts</Typography.Text>
                <Typography.Text type="secondary">{draftSummaries.length}</Typography.Text>
              </Flex>
              <List
                dataSource={draftSummaries}
                renderItem={(item) => {
                  const selected = activeDraft?.draftId === item.draftId;
                  const deleteRevision = selected ? activeDraft.revision : item.revision;
                  return (
                    <List.Item className="admin-draft-list-item">
                      <Flex align="center" gap={8} className="admin-draft-row-shell">
                        <Button
                          type="text"
                          block
                          className="admin-draft-row"
                          aria-label={`Draft rev ${item.revision} · ${item.sourceKind}${
                            item.sourceVersion != null ? ` v${item.sourceVersion}` : ""
                          }`}
                          onClick={() => void selectDraft(item.draftId)}
                          disabled={busy}
                        >
                          <Flex align="center" justify="space-between" gap={12}>
                            <Flex vertical gap={4} className="admin-draft-row-copy">
                              <Typography.Text strong>
                                {item.sourceVersion != null ? `Draft from v${item.sourceVersion}` : "New draft"}
                              </Typography.Text>
                              <Typography.Text type="secondary" className="admin-draft-row-meta">
                                {formatDraftSource(item.sourceKind)} · Revision{" "}
                                {item.revision} · Updated {formatAdminTimestamp(item.updatedAt)}
                              </Typography.Text>
                            </Flex>
                            {selected ? <Tag color="blue">Editing</Tag> : <RightOutlined aria-hidden />}
                          </Flex>
                        </Button>
                        <Button
                          type="text"
                          danger
                          icon={<DeleteOutlined />}
                          aria-label={`Delete ${
                            item.sourceVersion != null ? `draft from v${item.sourceVersion}` : "draft"
                          }, revision ${deleteRevision}`}
                          disabled={busy}
                          className="admin-draft-delete"
                          onClick={() => confirmDeleteDraft(item, deleteRevision)}
                        />
                      </Flex>
                    </List.Item>
                  );
                }}
              />
            </div>
          ) : null}
          {!lifecycleLoading && draftSummaries.length === 0 ? (
            <Typography.Text type="secondary">No drafts yet. Fork a catalog version to start.</Typography.Text>
          ) : null}
          {publications.length > 0 ? (
            <Descriptions
              className="admin-durable-publications"
              bordered
              size="small"
              column={1}
              title="Durable publications"
            >
              {publications.map((item) => (
                <Descriptions.Item key={item.version} label={`v${item.version}`}>
                  <Flex vertical gap={8} align="start">
                    <span>
                      {item.status} · metadata rev {item.metadataRevision} · {item.publishedAt}
                    </span>
                    <PublishedSkillList skills={item.skills} />
                    <PublicationResourcesSummary definitionId={definitionId} version={item.version} />
                    <Flex gap={8} wrap="wrap" align="center">
                      <Tooltip
                        title="Creates a managed instance for this version and opens Chat."
                        trigger={["hover", "focus"]}
                      >
                        <Button
                          size="small"
                          aria-label={`Start managed chat for v${item.version}`}
                          disabled={busy || item.status !== "Active"}
                          onClick={() => void startManagedChat(definitionId, item.version)}
                        >
                          Chat
                        </Button>
                      </Tooltip>
                      {item.status === "Active" ? (
                        <Button
                          size="small"
                          danger
                          disabled={busy}
                          aria-label={`Deprecate publication v${item.version}`}
                          onClick={() =>
                            confirmAction(modal, {
                              title: `Deprecate publication v${item.version}?`,
                              content:
                                "Metadata-only change. Exact version lookup and existing sessions stay intact; avoid selecting this version for new managed work.",
                              okText: "Deprecate",
                              onOk: () => void deprecatePublication(item)
                            })
                          }
                        >
                          Deprecate publication
                        </Button>
                      ) : null}
                    </Flex>
                  </Flex>
                </Descriptions.Item>
              ))}
            </Descriptions>
          ) : null}
            </div>
        </section>
        </div>
      ) : null}
    </Flex>
  );
}

const RESOURCE_KINDS = ["Knowledge", "Reference", "Template", "StaticAsset", "EvalFixture"] as const;

function knowledgeBackingLabel(identity: string, resourcePath: string) {
  const selected = resourcePath.trim();
  if (selected.length > 0) {
    return `Backing resource: ${selected}`;
  }
  const legacyIdentity = identity.trim() || "{identity}";
  return `Legacy fallback: knowledge/${legacyIdentity}`;
}

function KnowledgeResourceBinding({
  index,
  source,
  resources,
  disabled,
  onChange
}: {
  index: number;
  source: DraftKnowledgeSource;
  resources: AdminDefinitionDraftResource[];
  disabled: boolean;
  onChange: (resourcePath: string) => void;
}) {
  const knowledgePaths = resources
    .filter((item) => item.kind === "Knowledge")
    .map((item) => item.logicalPath);
  const selected = source.resourcePath.trim();
  const options = [
    { value: "", label: knowledgeBackingLabel(source.identity, "") },
    ...knowledgePaths.map((path) => ({ value: path, label: path })),
    ...(selected.length > 0 && !knowledgePaths.includes(selected)
      ? [{ value: selected, label: `${selected} (not in this draft)` }]
      : [])
  ];

  return (
    <label className="admin-draft-field">
      <Typography.Text>Backing resource</Typography.Text>
      <Select
        aria-label={`Knowledge resource ${index + 1}`}
        value={selected}
        options={options}
        onChange={onChange}
        disabled={disabled}
      />
      <Typography.Text type="secondary">{knowledgeBackingLabel(source.identity, selected)}</Typography.Text>
    </label>
  );
}

function DraftEditor({
  activeDraft,
  candidate,
  jsonText,
  jsonError,
  editorView,
  dirty,
  busy,
  onCandidateChange,
  onJsonTextChange,
  onEditorViewChange,
  onSave,
  onPublish,
  onDraftRevisionChange,
  onError
}: {
  activeDraft: AdminDefinitionDraft;
  candidate: DefinitionCandidate;
  jsonText: string;
  jsonError: string | null;
  editorView: DefinitionEditorView;
  dirty: boolean;
  busy: boolean;
  onCandidateChange: (candidate: DefinitionCandidate) => void;
  onJsonTextChange: (text: string) => void;
  onEditorViewChange: (view: DefinitionEditorView) => void;
  onSave: () => void;
  onPublish: () => void;
  onDraftRevisionChange: (
    draft: AdminDefinitionDraft,
    options?: { preserveLocalEdits?: boolean }
  ) => void;
  onError: (message: string | null, diagnosticId?: string | null) => void;
}) {
  const capabilities = readDraftEnvironment(candidate);
  const candidateLocked = jsonError !== null;
  const onCapabilitiesChange = (next: DraftEnvironment) => {
    if (candidateLocked) {
      return;
    }
    onCandidateChange(applyDraftEnvironmentToCandidate(candidate, next));
  };
  const saveBlocked = candidateLocked;
  const { message } = App.useApp();
  const [toolRegistryLoading, setToolRegistryLoading] = useState(false);
  const [toolRegistryError, setToolRegistryError] = useState<AdminFailureNotice | null>(null);
  const [toolNames, setToolNames] = useState<string[]>([]);
  const [resources, setResources] = useState<AdminDefinitionDraftResource[]>([]);
  const [resourcesLoading, setResourcesLoading] = useState(false);
  const [logicalPath, setLogicalPath] = useState("");
  const [kind, setKind] = useState<(typeof RESOURCE_KINDS)[number]>("Reference");
  const [pendingFile, setPendingFile] = useState<File | null>(null);
  const [publishEligible, setPublishEligible] = useState(false);

  useEffect(() => {
    setPublishEligible(false);
  }, [activeDraft.draftId, activeDraft.revision]);

  const reloadResources = useCallback(async () => {
    setResourcesLoading(true);
    onError(null);
    try {
      const items = await listAdminDraftResources(activeDraft.draftId);
      setResources(items);
    } catch (error) {
      reportAdminError(onError, error, "Failed to load resources.");
    } finally {
      setResourcesLoading(false);
    }
  }, [activeDraft.draftId, onError]);

  useEffect(() => {
    void reloadResources();
  }, [reloadResources, activeDraft.revision]);

  const reloadToolRegistry = useCallback(async () => {
    setToolRegistryLoading(true);
    setToolRegistryError(null);
    try {
      const names = await listAdminToolNames();
      setToolNames(names);
    } catch (error) {
      setToolNames([]);
      setToolRegistryError(describeAdminError(error, "Failed to load tool registry."));
    } finally {
      setToolRegistryLoading(false);
    }
  }, []);

  useEffect(() => {
    void reloadToolRegistry();
  }, [reloadToolRegistry]);

  const refreshDraft = async () => {
    const draft = await getAdminDefinitionDraft(activeDraft.draftId);
    onDraftRevisionChange(draft, { preserveLocalEdits: dirty });
    await reloadResources();
  };

  const updateKnowledgeSource = (
    index: number,
    field: keyof DraftKnowledgeSource,
    value: string
  ) => {
    const next = capabilities.knowledgeSources.map((item, itemIndex) =>
      itemIndex === index ? { ...item, [field]: value } : item
    );
    onCapabilitiesChange({ ...capabilities, knowledgeSources: next });
  };

  const addKnowledgeSource = () => {
    onCapabilitiesChange({
      ...capabilities,
      knowledgeSources: [
        ...capabilities.knowledgeSources,
        { identity: "", title: "", citation: "", resourcePath: "" }
      ]
    });
  };

  const removeKnowledgeSource = (index: number) => {
    onCapabilitiesChange({
      ...capabilities,
      knowledgeSources: capabilities.knowledgeSources.filter((_, itemIndex) => itemIndex !== index)
    });
  };

  const addResource = async () => {
    if (!pendingFile || !logicalPath.trim()) {
      message.warning("Choose a file and logical path.");
      return;
    }
    onError(null);
    try {
      const stored = await uploadAdminDraftResourceContent(
        activeDraft.draftId,
        pendingFile,
        pendingFile.type || "application/octet-stream"
      );
      await upsertAdminDraftResource(
        activeDraft.draftId,
        activeDraft.revision,
        logicalPath.trim(),
        kind,
        stored
      );
      message.success("Resource bound.");
      setPendingFile(null);
      setLogicalPath("");
      await refreshDraft();
    } catch (error) {
      const notice = showAdminFailure(message, error, "Resource upload failed.");
      onError(notice.message, notice.diagnosticId ?? null);
    }
  };

  const removeResource = async (resource: AdminDefinitionDraftResource) => {
    onError(null);
    try {
      await removeAdminDraftResource(activeDraft.draftId, resource.resourceId, activeDraft.revision);
      message.success("Resource removed.");
      await refreshDraft();
    } catch (error) {
      const notice = showAdminFailure(message, error, "Remove failed.");
      onError(notice.message, notice.diagnosticId ?? null);
    }
  };

  return (
    <Flex vertical gap={12} className="admin-draft-editor">
      <Flex align="start" justify="space-between" gap={12} wrap="wrap" className="admin-draft-editor-heading">
        <div>
          <Typography.Title level={5}>
            {activeDraft.sourceVersion != null
              ? `Editing draft from v${activeDraft.sourceVersion}`
              : "Editing draft"}
          </Typography.Title>
          <Typography.Text type="secondary">
            Revision {activeDraft.revision} · Updated {formatAdminTimestamp(activeDraft.updatedAt)}
          </Typography.Text>
        </div>
        <Typography.Text
          type="secondary"
          copyable={{ text: activeDraft.draftId, tooltips: ["Copy draft ID", "Copied"] }}
          className="admin-draft-id"
        >
          ID {activeDraft.draftId.slice(0, 8)}…
        </Typography.Text>
      </Flex>
      <Tabs
        className="admin-draft-tabs"
        items={[
          {
            key: "definition",
            label: "Definition",
            destroyOnHidden: true,
            children: (
              <section className="admin-draft-tab" aria-label="Definition candidate">
                <div className="admin-draft-tab-intro">
                  <Typography.Title level={5}>Definition</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Form and Advanced JSON edit the same unsaved candidate. Saving from either view uses this draft revision.
                  </Typography.Paragraph>
                </div>
                <DefinitionCandidateEditor
                  candidate={candidate}
                  view={editorView}
                  jsonText={jsonText}
                  busy={busy}
                  onCandidateChange={onCandidateChange}
                  onJsonTextChange={onJsonTextChange}
                  onViewChange={onEditorViewChange}
                />
                {jsonError ? (
                  <Alert
                    type="error"
                    showIcon
                    title="Advanced JSON is invalid"
                    description={`${jsonError} The draft revision is unchanged until the JSON is valid.`}
                  />
                ) : null}
                <DraftEditorActions
                  dirty={dirty}
                  busy={busy}
                  publishEligible={publishEligible}
                  saveBlocked={saveBlocked}
                  onSave={onSave}
                  onPublish={onPublish}
                />
              </section>
            )
          },
          {
            key: "capabilities",
            label: "Capabilities",
            destroyOnHidden: true,
            children: (
              <section className="admin-draft-tab" aria-label="Draft capabilities">
                <div className="admin-draft-tab-intro">
                  <Typography.Title level={5}>Capabilities</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Choose the tools, workspace behavior, and knowledge references available to this definition.
                  </Typography.Paragraph>
                </div>
                {candidateLocked ? (
                  <Alert
                    type="error"
                    showIcon
                    title="Advanced JSON is invalid"
                    description="Fix Advanced JSON on the Definition tab before changing capabilities. The invalid text stays in place."
                  />
                ) : null}
                <section className="admin-draft-form-section" aria-label="Harness and tools">
                  <Typography.Title level={5}>Harness &amp; tools</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Harness labels identify behavior fixtures. Tool names come from the trusted server registry.
                  </Typography.Paragraph>
                  <label className="admin-draft-field">
                    <Typography.Text strong>Harness labels</Typography.Text>
                    <Select
                      aria-label="Harness labels"
                      mode="tags"
                      value={capabilities.harness}
                      onChange={(values) => onCapabilitiesChange({ ...capabilities, harness: values })}
                      disabled={busy || candidateLocked}
                      placeholder="Add a harness label"
                    />
                  </label>
                {toolRegistryError ? (
                  <Alert
                    type="error"
                    showIcon
                    message={toolRegistryError.message}
                    action={
                      <AdminRetryAction
                        onRetry={() => void reloadToolRegistry()}
                        diagnosticId={toolRegistryError.diagnosticId}
                        retryLabel="Retry tool registry"
                      />
                    }
                  />
                ) : null}
                  <label className="admin-draft-field">
                    <Flex align="center" gap={8}>
                      <Typography.Text strong>Tool allowlist</Typography.Text>
                      {toolRegistryLoading ? <Spin size="small" /> : null}
                    </Flex>
                    <Select
                      aria-label="Tool allowlist"
                      mode="multiple"
                      maxTagCount="responsive"
                      value={capabilities.toolAllowlist}
                      onChange={(values) =>
                        onCapabilitiesChange({ ...capabilities, toolAllowlist: values })
                      }
                      disabled={busy || candidateLocked || toolRegistryLoading || toolRegistryError !== null}
                      options={toolNames.map((name) => ({ value: name, label: name }))}
                      placeholder="Select registered tools"
                    />
                  </label>
                </section>
                <section className="admin-draft-form-section" aria-label="Workspace behavior">
                  <Typography.Title level={5}>Workspace behavior</Typography.Title>
                  <label className="admin-draft-field">
                    <Typography.Text strong>Workspace template ID</Typography.Text>
                    <Input
                      aria-label="Workspace template id"
                      placeholder="Optional template identifier"
                      value={capabilities.workspaceTemplateId}
                      onChange={(event) =>
                        onCapabilitiesChange({
                          ...capabilities,
                          workspaceTemplateId: event.target.value
                        })
                      }
                      disabled={busy || candidateLocked}
                    />
                  </label>
                  <Flex align="start" gap={12} className="admin-draft-switch-row">
                    <Switch
                      aria-label="Allow unread unsupported attachment types"
                      checked={capabilities.allowUnreadUnsupportedAttachmentTypes}
                      onChange={(checked) =>
                        onCapabilitiesChange({
                          ...capabilities,
                          allowUnreadUnsupportedAttachmentTypes: checked
                        })
                      }
                      disabled={busy || candidateLocked}
                    />
                    <div>
                      <Typography.Text strong>Allow unsupported attachments</Typography.Text>
                      <Typography.Paragraph type="secondary">
                        Keep attachments the runtime cannot read instead of rejecting the turn.
                      </Typography.Paragraph>
                    </div>
                  </Flex>
                </section>
                <section className="admin-draft-form-section" aria-label="Knowledge source references">
                  <Flex align="baseline" justify="space-between" gap={12} className="admin-draft-section-heading">
                    <Typography.Title level={5}>Knowledge sources</Typography.Title>
                    <Typography.Text type="secondary">{capabilities.knowledgeSources.length}</Typography.Text>
                  </Flex>
                  <Typography.Paragraph type="secondary">
                    Bind named references that can be cited by configured knowledge tools.
                  </Typography.Paragraph>
                  {capabilities.knowledgeSources.length === 0 ? (
                    <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No knowledge sources configured." />
                  ) : null}
                  <Flex vertical gap={8}>
                    {capabilities.knowledgeSources.map((source, index) => (
                      <div key={`knowledge-${index}`} className="admin-knowledge-source">
                        <div className="admin-knowledge-source-grid">
                          <label className="admin-draft-field">
                            <Typography.Text>Identity</Typography.Text>
                            <Input
                              aria-label={`Knowledge identity ${index + 1}`}
                              placeholder="policy"
                              value={source.identity}
                              onChange={(event) => updateKnowledgeSource(index, "identity", event.target.value)}
                              disabled={busy || candidateLocked}
                            />
                          </label>
                          <label className="admin-draft-field">
                            <Typography.Text>Title</Typography.Text>
                            <Input
                              aria-label={`Knowledge title ${index + 1}`}
                              placeholder="Support policy"
                              value={source.title}
                              onChange={(event) => updateKnowledgeSource(index, "title", event.target.value)}
                              disabled={busy || candidateLocked}
                            />
                          </label>
                          <label className="admin-draft-field">
                            <Typography.Text>Citation</Typography.Text>
                            <Input
                              aria-label={`Knowledge citation ${index + 1}`}
                              placeholder="policy@demo"
                              value={source.citation}
                              onChange={(event) => updateKnowledgeSource(index, "citation", event.target.value)}
                              disabled={busy || candidateLocked}
                            />
                          </label>
                          <Tooltip title="Remove source">
                            <Button
                              danger
                              type="text"
                              icon={<DeleteOutlined />}
                              aria-label={`Remove knowledge source ${index + 1}`}
                              className="admin-knowledge-source-remove"
                              disabled={busy || candidateLocked}
                              onClick={() => removeKnowledgeSource(index)}
                            />
                          </Tooltip>
                        </div>
                        <KnowledgeResourceBinding
                          index={index}
                          source={source}
                          resources={resources}
                          disabled={busy || candidateLocked}
                          onChange={(resourcePath) => updateKnowledgeSource(index, "resourcePath", resourcePath)}
                        />
                      </div>
                    ))}
                  </Flex>
                  <Button onClick={addKnowledgeSource} disabled={busy || candidateLocked}>
                    Add knowledge source
                  </Button>
                </section>
                <DraftEditorActions
                  dirty={dirty}
                  busy={busy}
                  publishEligible={publishEligible}
                  saveBlocked={saveBlocked}
                  onSave={onSave}
                  onPublish={onPublish}
                />
              </section>
            )
          },
          {
            key: "resources",
            label: "Resources",
            destroyOnHidden: true,
            children: (
              <section className="admin-draft-tab" aria-label="Draft resources">
                <div className="admin-draft-tab-intro">
                  <Typography.Title level={5}>Resources</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Upload immutable source material and bind it to a safe path in the published definition.
                  </Typography.Paragraph>
                </div>
                <section className="admin-draft-form-section" aria-label="Add draft resource">
                  <Typography.Title level={5}>Add resource</Typography.Title>
                  <div className="admin-resource-form-grid">
                    <label className="admin-draft-field admin-resource-path">
                      <Typography.Text strong>Logical path</Typography.Text>
                      <Input
                        aria-label="Resource logical path"
                        placeholder="knowledge/policy.md"
                        value={logicalPath}
                        onChange={(event) => setLogicalPath(event.target.value)}
                        disabled={busy}
                      />
                    </label>
                    <label className="admin-draft-field">
                      <Typography.Text strong>Kind</Typography.Text>
                      <Select
                        aria-label="Resource kind"
                        value={kind}
                        onChange={setKind}
                        options={RESOURCE_KINDS.map((value) => ({ value, label: value }))}
                        disabled={busy}
                      />
                    </label>
                    <label className="admin-draft-field admin-resource-file">
                      <Typography.Text strong>Resource file</Typography.Text>
                      <div className="admin-resource-upload-block">
                        <Upload.Dragger
                          beforeUpload={(file) => {
                            setPendingFile(file);
                            return false;
                          }}
                          showUploadList={false}
                          maxCount={1}
                          multiple={false}
                          disabled={busy}
                          className="admin-resource-upload"
                        >
                          <p className="ant-upload-drag-icon"><InboxOutlined /></p>
                          <p className="ant-upload-text">Choose a file or drag it here</p>
                          <p className="ant-upload-hint">One file will be bound to the logical path above.</p>
                        </Upload.Dragger>
                        {pendingFile ? (
                          <Tag
                            className="admin-resource-pending"
                            closable={!busy}
                            onClose={(event) => {
                              event.preventDefault();
                              setPendingFile(null);
                            }}
                          >
                            {pendingFile.name}
                          </Tag>
                        ) : null}
                      </div>
                    </label>
                  </div>
                  <Button
                    type="primary"
                    aria-label="Upload and bind"
                    onClick={() => void addResource()}
                    disabled={busy || !pendingFile || !logicalPath.trim()}
                  >
                    Upload &amp; bind
                  </Button>
                </section>
                <ResourceImportPanel
                  draftId={activeDraft.draftId}
                  expectedRevision={activeDraft.revision}
                  existingPaths={resources.map((item) => item.logicalPath)}
                  existingCount={resources.length}
                  existingBytes={resources.reduce((sum, item) => sum + item.byteLength, 0)}
                  disabled={busy}
                  onBound={refreshDraft}
                  onError={onError}
                />
                {resourcesLoading ? <Spin /> : null}
                {!resourcesLoading && resources.length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No resources bound to this draft." />
                ) : null}
                {!resourcesLoading && resources.length > 0 ? (
                  <List
                    className="admin-resource-list"
                    dataSource={resources}
                    renderItem={(item) => (
                      <List.Item
                        actions={[
                          <Button
                            key="remove"
                            type="text"
                            danger
                            aria-label="Remove"
                            disabled={busy}
                            onClick={() => void removeResource(item)}
                          >
                            Remove resource
                          </Button>
                        ]}
                      >
                        <List.Item.Meta
                          title={item.logicalPath}
                          description={`${item.kind} · ${item.byteLength.toLocaleString()} bytes · SHA-256 ${item.contentSha256.slice(0, 12)}…`}
                        />
                      </List.Item>
                    )}
                  />
                ) : null}
              </section>
            )
          },
          {
            key: "publish-gate",
            label: "Test & Publish",
            destroyOnHidden: false,
            children: (
              <DefinitionDraftPublishGatePanel
                key={activeDraft.draftId}
                activeDraft={activeDraft}
                dirty={dirty}
                busy={busy}
                toolNames={toolNames}
                onDraftRevisionChange={(draft) => onDraftRevisionChange(draft)}
                onError={onError}
                onEligibilityChange={setPublishEligible}
              />
            )
          }
        ]}
      />
    </Flex>
  );
}

function DraftEditorActions({
  dirty,
  busy,
  publishEligible,
  saveBlocked = false,
  onSave,
  onPublish
}: {
  dirty: boolean;
  busy: boolean;
  publishEligible: boolean;
  saveBlocked?: boolean;
  onSave: () => void;
  onPublish: () => void;
}) {
  return (
    <div className="admin-draft-actions">
      <Flex gap={8} wrap="wrap">
        <Button type={dirty ? "primary" : "default"} onClick={onSave} disabled={busy || !dirty || saveBlocked}>
          Save draft
        </Button>
        <Button
          type={!dirty && publishEligible ? "primary" : "default"}
          onClick={onPublish}
          disabled={busy || dirty || !publishEligible}
        >
          Publish…
        </Button>
      </Flex>
      {saveBlocked ? (
        <Typography.Text type="danger">
          Advanced JSON is invalid — fix it before saving. This does not change the draft revision.
        </Typography.Text>
      ) : dirty ? (
        <Typography.Text type="warning">
          Unsaved changes — save before using Test &amp; Publish.
        </Typography.Text>
      ) : !publishEligible ? (
        <Typography.Text type="secondary">
          Complete Test &amp; Publish to enable publishing.
        </Typography.Text>
      ) : (
        <Typography.Text type="success">
          Validation and required evaluations are current. This draft can be published.
        </Typography.Text>
      )}
    </div>
  );
}

export function PublicationResourcesSummary({
  definitionId,
  version
}: {
  definitionId: string;
  version: number;
}) {
  const [items, setItems] = useState<AdminDefinitionPublicationResource[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<{ message: string; diagnosticId?: string } | null>(null);
  const requestGenerationRef = useRef(0);

  const load = useCallback(
    async (generation: number) => {
      setLoading(true);
      setError(null);
      try {
        const resources = await listAdminPublicationResources(definitionId, version);
        if (generation !== requestGenerationRef.current) {
          return;
        }

        setItems(resources);
      } catch (loadError) {
        if (generation !== requestGenerationRef.current) {
          return;
        }

        setItems([]);
        setError(formatAdminLoadError(loadError));
      } finally {
        if (generation === requestGenerationRef.current) {
          setLoading(false);
        }
      }
    },
    [definitionId, version]
  );

  const reload = useCallback(() => {
    const generation = ++requestGenerationRef.current;
    void load(generation);
  }, [load]);

  useEffect(() => {
    reload();
  }, [reload]);

  if (loading) {
    return <Typography.Text type="secondary">Loading resources…</Typography.Text>;
  }
  if (error) {
    return (
      <Alert
        type="error"
        showIcon
        title={error.message}
        action={<AdminRetryAction onRetry={reload} diagnosticId={error.diagnosticId} />}
      />
    );
  }
  if (items.length === 0) {
    return <Typography.Text type="secondary">No bound resources.</Typography.Text>;
  }
  return (
    <List
      size="small"
      dataSource={items}
      renderItem={(item) => (
        <List.Item>
          {item.logicalPath} · {item.kind} · {item.contentSha256.slice(0, 12)}…
        </List.Item>
      )}
    />
  );
}

function InstanceIdentityTags({
  compatibility,
  lifecycle,
  definitionStatus
}: {
  compatibility: boolean;
  lifecycle: string;
  definitionStatus?: string;
}) {
  return (
    <Flex gap={8} wrap="wrap">
      {compatibility ? <Tag color="gold">Compatibility / legacy</Tag> : <Tag color="blue">Managed</Tag>}
      <Tag>{lifecycle}</Tag>
      {definitionStatus ? <Tag>{definitionStatus}</Tag> : null}
    </Flex>
  );
}

function InstanceDetail({
  instanceId,
  instances,
  effective,
  onBack,
  onRetryEffective,
  onInstanceChanged,
  onInstanceDeleted
}: {
  instanceId: string;
  instances: LoadState<AdminInstanceInventoryItem[]>;
  effective: LoadState<AdminEffectiveConfiguration>;
  onBack: () => void;
  onRetryEffective: () => void;
  onInstanceChanged: () => void;
  onInstanceDeleted: () => void;
}) {
  const row = instances.kind === "ready"
    ? instances.data.find((item) => item.instanceId.toLowerCase() === instanceId.toLowerCase())
    : undefined;

  const resolved = effective.kind === "ready" ? effective.data : null;
  const headerIdentity = resolved
    ? {
        compatibility: resolved.compatibility,
        lifecycle: resolved.instanceLifecycle,
        definitionStatus: resolved.definitionStatus
      }
    : row
      ? {
          compatibility: row.compatibility,
          lifecycle: row.lifecycle,
          definitionStatus: undefined as string | undefined
        }
      : null;
  const instanceName = resolved?.persona.name ?? row?.personaName ?? "Agent instance";
  const definitionSummary = resolved
    ? `${resolved.definitionId} · v${resolved.definitionVersion}`
    : row
      ? `${row.definitionId} · v${row.activeVersion}`
      : null;

  return (
    <Flex vertical gap={16}>
      <Button
        type="text"
        icon={<ArrowLeftOutlined />}
        className="admin-back-button"
        onClick={onBack}
      >
        Back to inventory
      </Button>
      <div className="admin-definition-heading admin-instance-heading">
        <Typography.Title level={2}>{instanceName}</Typography.Title>
        {definitionSummary ? <Typography.Text>{definitionSummary}</Typography.Text> : null}
        <Typography.Text type="secondary" className="admin-instance-id">
          Instance {instanceId}
        </Typography.Text>
        {headerIdentity ? <InstanceIdentityTags {...headerIdentity} /> : null}
      </div>
      {effective.kind === "loading" ? <Spin aria-label="Loading effective configuration" /> : null}
      {effective.kind === "error" ? (
        <Alert
          type={effective.unauthorized ? "warning" : "error"}
          showIcon
          title={effective.message}
          action={<AdminRetryAction onRetry={onRetryEffective} diagnosticId={effective.diagnosticId} />}
        />
      ) : null}
      {effective.kind === "ready" && !effective.data.compatibility ? (
        <InstanceManagedControls
          config={effective.data}
          onUpdated={onInstanceChanged}
          onDeleted={onInstanceDeleted}
        />
      ) : null}
      {effective.kind === "ready" && !effective.data.compatibility && effective.data.instanceLifecycle === "Active" ? (
        <HarnessManagementSection instanceId={instanceId} eligibleTools={effective.data.effectiveToolAllowlist} onUpdated={onInstanceChanged} />
      ) : null}
      <ApplicationConnectionSection instanceId={instanceId} />
      <EventSubscriptionsSection instanceId={instanceId} />
      {effective.kind === "ready" && !effective.data.compatibility ? (
        <section className="admin-definition-panel" aria-label="Memory and automation">
          <div className="admin-definition-panel-heading">
            <Typography.Title level={4}>Memory &amp; automation</Typography.Title>
            <Typography.Text type="secondary">
              Inspect learned memory and manage durable registrations for this instance.
            </Typography.Text>
          </div>
          <div className="admin-definition-panel-body admin-instance-admin-body">
            <InstanceMemoryAutomationPanel config={effective.data} />
          </div>
        </section>
      ) : null}
      {effective.kind === "ready" ? (
        <section className="admin-definition-panel" aria-label="Effective configuration">
          <div className="admin-definition-panel-heading">
            <Typography.Title level={4}>Effective configuration</Typography.Title>
            <Typography.Text type="secondary">
              Read-only values resolved from the active definition and instance overrides.
            </Typography.Text>
          </div>
          <div className="admin-definition-panel-body">
            <EffectiveConfigView config={effective.data} hidePersona={!effective.data.compatibility} />
          </div>
        </section>
      ) : null}
    </Flex>
  );
}

export function InstanceManagedControls({
  config,
  onUpdated,
  onDeleted
}: {
  config: AdminEffectiveConfiguration;
  onUpdated: () => void;
  onDeleted: () => void;
}) {
  const { message, modal } = App.useApp();
  const [busy, setBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<AdminFailureNotice | null>(null);
  const [personaTab, setPersonaTab] = useState("form");
  const [persona, setPersona] = useState<PersonaFields>(() => ({ ...config.persona }));
  const [personaJsonDraft, setPersonaJsonDraft] = useState(() =>
    JSON.stringify(config.persona, null, 2)
  );
  const [personaJsonError, setPersonaJsonError] = useState<string | null>(null);
  const [definitionInventory, setDefinitionInventory] = useState<AdminDefinitionInventoryItem[]>([]);
  const [inventoryError, setInventoryError] = useState<AdminFailureNotice | null>(null);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [publicationsError, setPublicationsError] = useState<AdminFailureNotice | null>(null);
  const [targetVersion, setTargetVersion] = useState(config.definitionVersion);

  useEffect(() => {
    setPersona({ ...config.persona });
    setPersonaJsonDraft(JSON.stringify(config.persona, null, 2));
    setPersonaJsonError(null);
    setTargetVersion(config.definitionVersion);
    setDeleteError(null);
  }, [
    config.instanceRevision,
    config.personaRevision,
    config.definitionVersion,
    config.persona.name,
    config.persona.role,
    config.persona.description,
    config.persona.tone
  ]);

  useEffect(() => {
    setInventoryError(null);
    void listAdminDefinitions()
      .then((items) => setDefinitionInventory(items))
      .catch((error) => {
        setDefinitionInventory([]);
        setInventoryError(describeAdminError(error, "Definition inventory unavailable."));
      });
  }, []);

  useEffect(() => {
    setPublicationsError(null);
    void listAdminDefinitionPublications(config.definitionId)
      .then((items) => setPublications(items))
      .catch((error) => {
        setPublications([]);
        setPublicationsError(describeAdminError(error, "Publications unavailable."));
      });
  }, [config.definitionId]);

  const handlePersonaTabChange = (nextTab: string) => {
    const synced = syncPersonaOnTabChange(personaTab, nextTab, persona, personaJsonDraft);
    if (!synced.ok) {
      setPersonaJsonError(synced.error);
      message.error(synced.error);
      return;
    }
    setPersona(synced.persona);
    setPersonaJsonDraft(synced.personaJsonDraft);
    setPersonaJsonError(null);
    setPersonaTab(nextTab);
  };

  const resolvePersonaForSave = (): PersonaFields | null => {
    if (personaTab === "form") {
      return persona;
    }
    try {
      const parsed = parsePersonaJson(personaJsonDraft);
      setPersona(parsed);
      setPersonaJsonError(null);
      return parsed;
    } catch (error) {
      const text = error instanceof Error ? error.message : "Invalid persona JSON.";
      setPersonaJsonError(text);
      message.error(text);
      return null;
    }
  };

  const handleMutationError = (error: unknown) => {
    const notice = showAdminFailure(message, error, "Update failed.");
    if (notice.message.includes("reload")) {
      onUpdated();
    }
  };

  const savePersona = async () => {
    const nextPersona = resolvePersonaForSave();
    if (!nextPersona) {
      return;
    }
    setBusy(true);
    try {
      await updateAdminAgentInstancePersona(config.instanceId, {
        expectedRevision: config.instanceRevision,
        expectedPersonaRevision: config.personaRevision,
        ...nextPersona
      });
      message.success("Persona updated.");
      onUpdated();
    } catch (error) {
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const setLifecycle = async (lifecycle: "Active" | "Archived") => {
    setBusy(true);
    try {
      await updateAdminAgentInstanceLifecycle(config.instanceId, config.instanceRevision, lifecycle);
      message.success(lifecycle === "Archived" ? "Instance archived." : "Instance unarchived.");
      onUpdated();
    } catch (error) {
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const deleteInstance = async () => {
    setBusy(true);
    setDeleteError(null);
    try {
      await deleteAdminAgentInstance(config.instanceId, config.instanceRevision);
      message.success("Instance deleted.");
      onDeleted();
    } catch (error) {
      setDeleteError(describeAdminError(error, "Instance could not be deleted."));
    } finally {
      setBusy(false);
    }
  };

  const applyVersion = async () => {
    if (targetVersion === config.definitionVersion) {
      return;
    }
    setBusy(true);
    try {
      await updateAdminAgentInstanceActiveVersion(
        config.instanceId,
        config.instanceRevision,
        targetVersion
      );
      message.success(`Active version set to v${targetVersion}.`);
      onUpdated();
    } catch (error) {
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const versionOptions = buildInstanceVersionOptions(
    config.definitionId,
    config.definitionVersion,
    definitionInventory,
    publications
  );
  const versionActionLabel = managedInstanceVersionActionLabel(targetVersion, config.definitionVersion);

  const personaDirty = isPersonaDraftDirty(
    config.persona,
    personaTab,
    persona,
    personaJsonDraft
  );

  return (
    <section
      className="admin-definition-panel admin-instance-management"
      aria-label="Managed instance controls"
    >
      <div className="admin-definition-panel-heading">
        <Typography.Title level={4}>Instance management</Typography.Title>
        <Typography.Text type="secondary">
          Update the persona, active definition version, and lifecycle.
        </Typography.Text>
      </div>
      <div className="admin-definition-panel-body admin-instance-control-grid">
        <section className="admin-instance-persona" aria-label="Persona editor">
          <Flex align="baseline" justify="space-between" gap={12} wrap="wrap">
            <Typography.Title level={5}>Persona</Typography.Title>
            <Typography.Text type="secondary">
              Rev {config.personaRevision}
            </Typography.Text>
          </Flex>
          <Tabs
            className="admin-instance-persona-tabs"
            activeKey={personaTab}
            onChange={handlePersonaTabChange}
            items={[
              {
                key: "form",
                label: "Form",
                children: (
                  <Form className="admin-instance-persona-form" layout="vertical" disabled={busy}>
                    <Form.Item label="Name">
                      <Input
                        aria-label="Persona name"
                        value={persona.name}
                        onChange={(event) => setPersona({ ...persona, name: event.target.value })}
                      />
                    </Form.Item>
                    <Form.Item label="Role">
                      <Input
                        aria-label="Persona role"
                        value={persona.role}
                        onChange={(event) => setPersona({ ...persona, role: event.target.value })}
                      />
                    </Form.Item>
                    <Form.Item label="Description">
                      <Input.TextArea
                        aria-label="Persona description"
                        rows={3}
                        value={persona.description}
                        onChange={(event) => setPersona({ ...persona, description: event.target.value })}
                      />
                    </Form.Item>
                    <Form.Item label="Tone">
                      <Input
                        aria-label="Persona tone"
                        value={persona.tone}
                        onChange={(event) => setPersona({ ...persona, tone: event.target.value })}
                      />
                    </Form.Item>
                  </Form>
                )
              },
              {
                key: "json",
                label: "JSON",
                children: (
                  <Flex vertical gap={8}>
                    <Input.TextArea
                      aria-label="Persona JSON"
                      rows={8}
                      value={personaJsonDraft}
                      onChange={(event) => {
                        setPersonaJsonDraft(event.target.value);
                        setPersonaJsonError(null);
                      }}
                    />
                    {personaJsonError ? <Typography.Text type="danger">{personaJsonError}</Typography.Text> : null}
                  </Flex>
                )
              }
            ]}
          />
          <Flex vertical gap={8} align="start" className="admin-instance-persona-actions">
            <Button type="primary" onClick={() => void savePersona()} disabled={busy}>
              Save persona
            </Button>
            {personaDirty ? (
              <Typography.Text type="secondary">
                Unsaved persona edits — save before other changes, or confirm discard on archive, unarchive, or
                version apply.
              </Typography.Text>
            ) : null}
          </Flex>
        </section>
        <div className="admin-instance-assignment">
          <section aria-label="Active version">
            <Typography.Title level={5}>Active version</Typography.Title>
            <Typography.Paragraph type="secondary">
              Choose the immutable definition version used by new sessions.
            </Typography.Paragraph>
            {inventoryError ? (
              <Alert
                type="warning"
                showIcon
                title={inventoryError.message}
                action={inventoryError.diagnosticId
                  ? <DiagnosticDetails fields={{ diagnosticId: inventoryError.diagnosticId }} />
                  : undefined}
              />
            ) : null}
            {publicationsError ? (
              <Alert
                type="warning"
                showIcon
                title={publicationsError.message}
                action={publicationsError.diagnosticId
                  ? <DiagnosticDetails fields={{ diagnosticId: publicationsError.diagnosticId }} />
                  : undefined}
              />
            ) : null}
            <Flex gap={8} wrap="wrap" align="center" className="admin-instance-version-actions">
              <Select
                aria-label="Target definition version"
                value={targetVersion}
                options={versionOptions}
                onChange={setTargetVersion}
                disabled={busy}
              />
              {personaDirty ? (
                <Button
                  disabled={busy || targetVersion === config.definitionVersion}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Apply version with unsaved persona?",
                      content: UNSAVED_PERSONA_DISCARD_MESSAGE,
                      okText: "Apply anyway",
                      onOk: () => void applyVersion()
                    })
                  }
                >
                  {versionActionLabel}
                </Button>
              ) : (
                <Button
                  onClick={() => void applyVersion()}
                  disabled={busy || targetVersion === config.definitionVersion}
                >
                  {versionActionLabel}
                </Button>
              )}
            </Flex>
          </section>
          <section className="admin-instance-lifecycle" aria-label="Lifecycle controls">
            <Flex align="baseline" justify="space-between" gap={12} wrap="wrap">
              <Typography.Title level={5}>Lifecycle</Typography.Title>
              <Tag color={config.instanceLifecycle === "Active" ? "green" : "default"}>
                {config.instanceLifecycle}
              </Tag>
            </Flex>
            <Typography.Paragraph type="secondary">
              Archived instances keep history but cannot start new chats or triggered work. Delete is available after archive, and only when nothing still references this instance.
            </Typography.Paragraph>
            {deleteError ? (
              <AdminDeletionBlockedAlert
                className="admin-destructive-detail"
                message={deleteError.message}
                diagnosticId={deleteError.diagnosticId}
              />
            ) : null}
            <Flex gap={8} wrap="wrap">
              {config.instanceLifecycle === "Active" ? (
                <Button
                  danger
                  disabled={busy}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Archive this managed instance?",
                      content: personaDirty
                        ? `New chats and triggered work will stop until you unarchive. ${UNSAVED_PERSONA_DISCARD_MESSAGE}`
                        : "New chats and triggered work will stop until you unarchive.",
                      okText: "Archive",
                      danger: true,
                      onOk: () => void setLifecycle("Archived")
                    })
                  }
                >
                  Archive instance
                </Button>
              ) : personaDirty ? (
                <Button
                  disabled={busy}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Unarchive with unsaved persona?",
                      content: UNSAVED_PERSONA_DISCARD_MESSAGE,
                      okText: "Unarchive anyway",
                      onOk: () => void setLifecycle("Active")
                    })
                  }
                >
                  Unarchive instance
                </Button>
              ) : (
                <Button disabled={busy} onClick={() => void setLifecycle("Active")}>
                  Unarchive instance
                </Button>
              )}
              {config.instanceLifecycle === "Archived" ? (
                <Button
                  danger
                  disabled={busy}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Delete this archived instance?",
                      content:
                        "This permanently removes the managed instance. Sessions, learned memory, triggers, and background work stay in place. If any of those still reference this instance, deletion is refused.",
                      okText: "Delete instance",
                      cancelText: "Keep instance",
                      danger: true,
                      onOk: () => void deleteInstance()
                    })
                  }
                >
                  Delete instance
                </Button>
              ) : null}
            </Flex>
          </section>
          <Typography.Text type="secondary" className="admin-instance-revision">
            Instance revision {config.instanceRevision}
          </Typography.Text>
        </div>
      </div>
    </section>
  );
}

export function EffectiveConfigView({
  config,
  hidePersona = false
}: {
  config: AdminEffectiveConfiguration;
  hidePersona?: boolean;
}) {
  const trigger = config.triggerPolicy;
  return (
    <div className="admin-effective-config-grid">
      <section
        className={`admin-effective-config-section admin-instance-identity${hidePersona ? " admin-effective-config-wide" : ""}`}
        aria-label="Instance identity"
      >
        <Typography.Title level={5}>Instance identity</Typography.Title>
        <InstanceIdentityTags
          compatibility={config.compatibility}
          lifecycle={config.instanceLifecycle}
          definitionStatus={config.definitionStatus}
        />
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Instance id">{config.instanceId}</Descriptions.Item>
          <Descriptions.Item label="Definition status">{config.definitionStatus}</Descriptions.Item>
          <Descriptions.Item label="Lifecycle">{config.instanceLifecycle}</Descriptions.Item>
          <Descriptions.Item label="Compatibility mode">{config.compatibility ? "Yes" : "No"}</Descriptions.Item>
        </Descriptions>
      </section>
      {hidePersona ? null : (
        <section className="admin-effective-config-section" aria-label="Persona">
          <Typography.Title level={5}>Persona</Typography.Title>
          <Descriptions bordered size="small" column={1}>
            <Descriptions.Item label="Name">{config.persona.name}</Descriptions.Item>
            <Descriptions.Item label="Role">{config.persona.role}</Descriptions.Item>
            <Descriptions.Item label="Description">{config.persona.description}</Descriptions.Item>
            <Descriptions.Item label="Tone">{config.persona.tone}</Descriptions.Item>
          </Descriptions>
        </section>
      )}
      <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Runtime model">
        <Typography.Title level={5}>Runtime model</Typography.Title>
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Definition">
            {config.definitionId} v{config.definitionVersion} ({config.definitionSource})
          </Descriptions.Item>
          <Descriptions.Item label="Display name">{config.effectiveModel.displayName}</Descriptions.Item>
          <Descriptions.Item label="Catalog key">{config.effectiveModel.catalogKey}</Descriptions.Item>
          <Descriptions.Item label="Model id">{config.effectiveModel.modelId ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Selection source">{config.effectiveModel.selectionSource}</Descriptions.Item>
          <Descriptions.Item label="Reasoning effort">{config.effectiveModel.reasoningEffort ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Language model alias">{config.providerPreferences.languageModel}</Descriptions.Item>
          <Descriptions.Item label="Speech recognizer">
            {config.providerPreferences.speechRecognizer ?? "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Speech synthesizer">
            {config.providerPreferences.speechSynthesizer ?? "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Interruption classifier">
            {config.providerPreferences.interruptionClassifier}
          </Descriptions.Item>
        </Descriptions>
      </section>
      <section
        className="admin-effective-config-section admin-effective-config-wide"
        aria-label="Tools and resources"
      >
        <Typography.Title level={5}>Tools and resources</Typography.Title>
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Offered tools">
            {config.effectiveToolAllowlist.length > 0 ? config.effectiveToolAllowlist.join(", ") : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Harness references">
            {config.harnessReferences.length > 0 ? config.harnessReferences.join(", ") : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Workspace template">{config.workspaceTemplateId ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Knowledge">
            {config.knowledgeSources.length > 0
              ? config.knowledgeSources
                  .map((item) =>
                    item.resolvedResourcePath
                      ? `${item.identity} (${item.title}) · ${item.resolvedResourcePath}`
                      : `${item.identity} (${item.title})`
                  )
                  .join("; ")
              : "None"}
          </Descriptions.Item>
        </Descriptions>
      </section>
      <section className="admin-effective-config-section" aria-label="Memory policy">
        <Typography.Title level={5}>Memory policy</Typography.Title>
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Session memory">{config.memoryPolicy.sessionMemory ? "On" : "Off"}</Descriptions.Item>
          <Descriptions.Item label="Identity promotion">
            {config.memoryPolicy.identityUserPromotion ? "On" : "Off"}
          </Descriptions.Item>
          <Descriptions.Item label="Identity retrieval">
            {config.memoryPolicy.identityUserRetrieval ? "On" : "Off"}
          </Descriptions.Item>
          <Descriptions.Item label="User promotion">{config.memoryPolicy.userPromotion ? "On" : "Off"}</Descriptions.Item>
          <Descriptions.Item label="User retrieval">{config.memoryPolicy.userRetrieval ? "On" : "Off"}</Descriptions.Item>
        </Descriptions>
      </section>
      <section className="admin-effective-config-section" aria-label="Automation">
        <Typography.Title level={5}>Automation</Typography.Title>
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Trigger enabled">{trigger?.enabled ? "Yes" : "No"}</Descriptions.Item>
          <Descriptions.Item label="User scheduling">{trigger?.allowUserScheduling ? "Yes" : "No"}</Descriptions.Item>
          <Descriptions.Item label="Allowed source kinds">
            {trigger?.allowedSourceKinds.join(", ") || "None"}
          </Descriptions.Item>
          <Descriptions.Item label="One-shot / daily / weekly">
            {trigger
              ? `${trigger.allowOneShot ? "one-shot" : "—"} / ${trigger.allowDaily ? "daily" : "—"} / ${trigger.allowWeekly ? "weekly" : "—"}`
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Recurrence limits">
            {trigger
              ? `max registrations ${trigger.maxActiveRegistrations}, horizon ${trigger.oneShotHorizonDays}d, min recurrence ${trigger.minRecurrenceDays}d`
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Fixed interval">
            {trigger?.allowFixedInterval
              ? `allowed (min ${trigger.minFixedIntervalSeconds}s)`
              : trigger
                ? "disabled"
                : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Durable work eligibility">
            {config.durableExecutionEligibility.canAcceptNewTriggeredWork ? "Eligible" : "Not eligible"} · schedule{" "}
            {config.durableExecutionEligibility.allowsScheduleSource ? "allowed" : "blocked"} · application events{" "}
            {config.durableExecutionEligibility.allowsApplicationEventSource ? "allowed" : "blocked"}
          </Descriptions.Item>
        </Descriptions>
      </section>
    </div>
  );
}
