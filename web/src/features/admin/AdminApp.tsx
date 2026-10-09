import { groupedCapabilityOptions, reconcileAlwaysCapabilities } from "./capabilityOptions";
import { InstanceSkillsSection } from "./InstanceSkillsSection";
import { useAdminDetailLayout } from "./useAdminDetailLayout";
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
  Table,
  Tag,
  Tooltip,
  Typography,
  Upload,
  theme
} from "antd";
import { ArrowLeftOutlined, DeleteOutlined, InboxOutlined, MessageOutlined } from "@ant-design/icons";
import {
  applyDraftEnvironmentToCandidate,
  readDraftEnvironment,
  type DraftEnvironment,
  type DraftKnowledgeSource
} from "./draftEnvironment";
import {
  applyCandidateJson,
  automationNumberErrors,
  candidateForPersistence,
  candidateToJson,
  candidatesEqual,
  cloneCandidate,
  type DefinitionCandidate
} from "./definitionCandidate";
import { DefinitionCandidateEditor, type DefinitionEditorView } from "./definitionCandidateEditor";
import { HarnessManagementSection, HarnessPolicyModeScopes } from "./HarnessManagementSection";
import { CredentialsSection, InstanceCredentialsSection } from "./CredentialsSection";
import { EventsSection } from "./EventsSection";
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
  getAdminDefinitionVersion,
  getAdminEffectiveConfig,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminInstances,
  listAdminPublicationResources,
  getAdminToolRegistry,
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
  type PersonaFields
} from "./instanceManagedControls";
import {
  adminDefinitionPath,
  adminHomePath,
  adminInstancePath,
  lastChatUrl,
  navigateToAppPath,
  rememberChatUrl,
  type AdminRoute,
  type AdminCollection,
  type AdminDefinitionTab,
  type AdminInstanceTab,
  type AdminInstanceSection
} from "../../app/appRoute";
import { confirmAction } from "../../app/confirmAction";
import { AdminDeletionBlockedAlert } from "./adminDeletionBlocked";
import { describeAdminError, formatAdminLoadError, reportAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { startManagedPublicationChat } from "./adminManagedChat";
import { InstanceWorkspaceSection } from "./InstanceWorkspaceSection";
import { IdentityMaintenanceSection, ExperienceSection, InstanceRunsSection, type ExperienceSelection } from "./InstanceContinuitySection";
import { InstanceAutomationsSection } from "./InstanceAutomationsSection";
import type { AutomationSelection, RunSource } from "../chat/runPresentation";
import { InstanceMemoryAutomationPanel } from "./instanceMemoryAutomation";

import { DefinitionVersionsTable } from "./DefinitionVersionsTable";
import { AdminCollectionToolbar, adminCollectionPagination, useAdminCollectionSearch } from "./AdminCollectionToolbar";

const { Header, Content } = Layout;

type LoadState<T> =
  | { kind: "loading" }
  | { kind: "error"; message: string; unauthorized?: boolean; diagnosticId?: string }
  | { kind: "ready"; data: T };

type EffectiveConfigLoadState = LoadState<AdminEffectiveConfiguration> & {
  data?: AdminEffectiveConfiguration;
};

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

function forkSourceKey(row: AdminDefinitionInventoryItem) {
  return `${row.source}:${row.version}`;
}

function instanceVersionRows(versions: AdminDefinitionInventoryItem[]) {
  // Exact-version resolution prefers a built-in definition over a durable publication.
  return [...new Map([...versions]
    .sort((a, b) => Number(a.source === "builtIn") - Number(b.source === "builtIn"))
    .map(row => [row.version, row])).values()];
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
  const [collection, setCollection] = useState<AdminCollection>(route.view === "home" ? route.collection ?? "definitions" : "definitions");
  const [connectionCollection, setConnectionCollection] = useState<"events" | "credentials">(route.view === "home" && route.collection === "events" ? "events" : "credentials");
  useEffect(() => {
    if (route.view === "home") {
      setCollection(route.collection ?? "definitions");
      if (route.collection === "events" || route.collection === "credentials") setConnectionCollection(route.collection);
    }
  }, [route]);
  const [definitions, setDefinitions] = useState<LoadState<AdminDefinitionInventoryItem[]>>({ kind: "loading" });
  const [instances, setInstances] = useState<LoadState<AdminInstanceInventoryItem[]>>({ kind: "loading" });
  const [effectiveConfig, setEffectiveConfig] = useState<EffectiveConfigLoadState>({ kind: "loading" });
  const effectiveRequest = useRef(0);

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
    const request = ++effectiveRequest.current;
    setEffectiveConfig(current => ({ kind: "loading",
      data: current.data?.instanceId === instanceId ? current.data : undefined }));
    try {
      const data = await getAdminEffectiveConfig(instanceId);
      if (request === effectiveRequest.current) setEffectiveConfig({ kind: "ready", data });
    } catch (error) {
      if (request !== effectiveRequest.current) return;
      const formatted = formatAdminLoadError(error);
      setEffectiveConfig(current => ({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId,
        data: formatted.unauthorized ? undefined : current.data
      }));
    }
  }, []);

  useEffect(() => {
    void reloadInventory();
  }, [reloadInventory]);

  const routedInstanceId = route.view === "instance" ? route.instanceId : undefined;
  useEffect(() => {
    if (!routedInstanceId) {
      ++effectiveRequest.current;
      setEffectiveConfig({ kind: "loading" });
      return;
    }

    void reloadEffectiveConfig(routedInstanceId);
    return () => { ++effectiveRequest.current; };
  }, [routedInstanceId, reloadEffectiveConfig]);

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
                <Typography.Title level={2} className="admin-home-title">{collection === "events" || collection === "credentials" ? "Connections" : "Agent inventory"}</Typography.Title>
                <Typography.Paragraph type="secondary" className="admin-home-subtitle">
                  {collection === "definitions" ? "Inspect published definitions and drafts, then open a version or continue editing."
                    : collection === "instances" ? "Manage agent identities, continuity, automation, and their pinned definition versions."
                    : "Manage shared credentials and Events. Grant credential access and configure reactions on each Instance."}
                </Typography.Paragraph>
              </div>
              <Flex gap={8} wrap="wrap">
                {collection === "definitions" ? <NewDefinitionButton groups={definitionGroups} /> : null}
                {collection === "instances" ? <NewInstanceButton groups={definitionGroups.filter((group) => group.versions.length > 0)} /> : null}
              </Flex>
            </Flex>
            <Tabs className="admin-collection-tabs" activeKey={collection === "events" || collection === "credentials" ? "connections" : collection}
              onChange={(key) => {
                const next = key === "connections" ? connectionCollection : key as AdminCollection;
                setCollection(next);
                navigateToAppPath(adminHomePath(next));
              }}
              items={[
              { key: "definitions", label: "Definitions", children: <InventorySection
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
                        latestActive: latestActiveVersion(group.versions),
                        draftCount: group.draftCount,
                        versionCount: group.versions.length,
                        description: formatDefinitionVersionSummary(group),
                        detail: formatDefinitionInventoryCounts(group),
                        tag: group.draftOnly ? "Draft" : formatInventoryStatus(group.latestStatus),
                        version: group.latestVersion,
                        onClick: () => navigateToAppPath(adminDefinitionPath(group.definitionId))
                      }))
                    : []
                }
              /> },
              { key: "instances", label: "Instances", children: <InventorySection
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
                        name: item.personaName,
                        definitionId: item.definitionId,
                        secondary: item.instanceId,
                        description: `Pinned to v${item.activeVersion}`,
                        version: item.activeVersion,
                        tag: "Agent Instance",
                        status: item.lifecycle,
                        detail: `Updated ${formatAdminTimestamp(item.updatedAt)}`,
                        updatedAt: item.updatedAt,
                        onClick: () => navigateToAppPath(adminInstancePath(item.instanceId))
                      }))
                    : []
                }
              /> },
              { key: "connections", label: "Connections", children: <Tabs aria-label="Global connection resources" activeKey={connectionCollection}
                onChange={key => { setConnectionCollection(key as "events" | "credentials"); setCollection(key as AdminCollection); navigateToAppPath(adminHomePath(key as AdminCollection)); }} items={[
                  { key: "credentials", label: "Credentials", children: <CredentialsSection /> },
                  { key: "events", label: "Events", children: <EventsSection /> }
                ]} /> }
              ]}
            />
          </div>
        ) : null}

        {route.view === "definition" ? (
          <DefinitionDetail
            key={route.definitionId}
            definitionId={route.definitionId}
            tab={route.tab}
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
            key={route.instanceId}
            instanceId={route.instanceId}
            tab={route.tab}
            section={route.section}
            instances={instances}
            effective={effectiveConfig}
            onBack={() => navigateToAppPath(adminHomePath("instances"))}
            onRetryEffective={() => void reloadEffectiveConfig(route.instanceId)}
            onInstanceChanged={() => {
              void reloadEffectiveConfig(route.instanceId);
              void reloadInstances();
            }}
            onInstanceDeleted={() => {
              void reloadInstances();
              navigateToAppPath(adminHomePath("instances"));
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
  const [harnessAreas, setHarnessAreas] = useState<import("../../services/adminApi").HarnessScope[]>(["KnowledgeResources"]);
  const [persona, setPersona] = useState<AdminCreateInstancePersona>({
    name: "",
    role: "",
    description: "",
    tone: ""
  });
  const [personaSource, setPersonaSource] = useState<string | null>(null);
  const [personaLoading, setPersonaLoading] = useState(false);
  const [personaError, setPersonaError] = useState<AdminFailureNotice | null>(null);
  const [personaRetry, setPersonaRetry] = useState(0);
  const [busy, setBusy] = useState(false);
  const selectedGroup = groups.find((group) => group.definitionId === definitionId) ?? null;
  const instanceVersions = instanceVersionRows(selectedGroup?.versions ?? []);
  const selectedVersion = instanceVersions.find((row) => row.version === version) ?? null;
  const source = selectedVersion?.source;
  const sourceKey = selectedVersion ? `${definitionId}:${source}:${version}` : null;
  const customPersonaReady = personaFieldsReady(persona);
  const canCreate = Boolean(selectedGroup && selectedVersion) && (personaMode === "default" ||
    (customPersonaReady && !personaLoading && personaSource === sourceKey))
    && (harnessMode === "Disabled" || harnessAreas.length > 0);

  useEffect(() => {
    if (!open || !definitionId || version === null || !source) return;
    let cancelled = false;
    setPersonaSource(null);
    setPersonaLoading(true);
    setPersonaError(null);
    setPersona({ name: "", role: "", description: "", tone: "" });
    void getAdminDefinitionVersion(definitionId, version, source)
      .then(definition => {
        const defaults = parsePersonaJson(JSON.stringify(definition.identity));
        if (!cancelled) {
          setPersona(defaults);
          setPersonaSource(`${definitionId}:${source}:${version}`);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) setPersonaError(describeAdminError(error, "Definition persona could not be loaded."));
      })
      .finally(() => { if (!cancelled) setPersonaLoading(false); });
    return () => { cancelled = true; };
  }, [open, definitionId, version, source, personaRetry]);

  const openModal = () => {
    const first = groups[0];
    if (first) {
      setDefinitionId(first.definitionId);
      setVersion(latestActiveVersion(instanceVersionRows(first.versions)) ?? first.latestVersion);
    } else {
      setDefinitionId("");
      setVersion(null);
    }
    setPersonaMode("default");
    setHarnessMode("Disabled");
    setHarnessAreas(["KnowledgeResources"]);
    setPersona({ name: "", role: "", description: "", tone: "" });
    setOpen(true);
  };

  const createInstance = async () => {
    if (!canCreate || !selectedGroup || selectedVersion === null || version === null) {
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
                showSearch={{ optionFilterProp: "label" }}
                value={definitionId}
                options={groups.map((group) => ({
                  value: group.definitionId,
                  label: `${group.logicalName} · ${group.definitionId}`
                }))}
                onChange={(nextId) => {
                  setDefinitionId(nextId);
                  const next = groups.find((group) => group.definitionId === nextId);
                  if (next) {
                    setVersion(latestActiveVersion(instanceVersionRows(next.versions)) ?? next.latestVersion);
                  }
                }}
                disabled={busy}
              />
            </label>
            <label className="admin-draft-field">
              <Typography.Text strong>Published version</Typography.Text>
              <Select
                aria-label="Published version"
                showSearch
                optionFilterProp="label"
                value={version ?? undefined}
                options={instanceVersions.map((row) => ({
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
              <>
                {personaLoading ? <Typography.Text type="secondary" role="status">Loading Definition persona…</Typography.Text> : null}
                {personaError ? <Alert type="error" showIcon title={personaError.message}
                  description={personaError.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: personaError.diagnosticId }} /> : undefined}
                  action={<Button disabled={busy} onClick={() => setPersonaRetry(value => value + 1)}>Retry</Button>} /> : null}
                <div className="admin-draft-field-grid">
                  <PersonaField label="Persona name" value={persona.name} disabled={busy || personaLoading || personaSource !== sourceKey} onChange={(name) => setPersona({ ...persona, name })} />
                  <PersonaField label="Persona role" value={persona.role} disabled={busy || personaLoading || personaSource !== sourceKey} onChange={(role) => setPersona({ ...persona, role })} />
                  <PersonaField label="Persona description" value={persona.description} disabled={busy || personaLoading || personaSource !== sourceKey} onChange={(description) => setPersona({ ...persona, description })} />
                  <PersonaField label="Persona tone" value={persona.tone} disabled={busy || personaLoading || personaSource !== sourceKey} onChange={(tone) => setPersona({ ...persona, tone })} />
                </div>
              </>
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
    name?: string;
    definitionId?: string;
    secondary?: string;
    description: string;
    version: number;
    latestActive?: number | null;
    draftCount?: number;
    versionCount?: number;
    updatedAt?: string;
    detail?: string;
    tag?: string;
    status?: string;
    onClick: () => void;
  }>;
}) {
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const query = search.trim().toLowerCase();
  const filteredItems = items.filter(item =>
    [item.title, item.secondary, item.description, item.detail, item.tag, item.status, item.key]
      .some(value => value?.toLowerCase().includes(query))
  );
  return (
    <section aria-label={title} className="admin-inventory-section">
      <Flex align="baseline" justify="space-between" gap={12} className="admin-inventory-heading">
        <Typography.Title level={4}>{title}</Typography.Title>
        {countLabel ? <Typography.Text type="secondary">{countLabel}</Typography.Text> : null}
      </Flex>
      {error ? <Alert type={unauthorized ? "warning" : "error"} showIcon title={error}
        action={<AdminRetryAction onRetry={onRetry} diagnosticId={diagnosticId} />}
        className="admin-inventory-alert" /> : null}
      {!error ? <>
        <AdminCollectionToolbar label={title.toLowerCase()} value={search} onChange={setSearch} />
        <Table
          aria-label={`${title} table`}
          className="admin-collection-table"
          rowKey="key"
          loading={loading}
          dataSource={filteredItems}
          size="small"
          scroll={{ x: title === "Definitions" ? 1050 : 1320 }}
          pagination={pagination}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={query || items.length > 0 ? "No matches. Clear search or filters to see all results." : emptyLabel} /> }}
          columns={[
            { title: title === "Definitions" ? "Definition" : "Instance", key: "name", width: 240, ellipsis: true,
              sorter: (a, b) => a.title.localeCompare(b.title),
              render: (_, item) => <Button type="link" size="small" className="admin-collection-name"
                title={item.name ?? item.title}
                aria-label={title === "Definitions" ? `${item.title} · ${item.secondary}` : item.title} onClick={item.onClick}>
                <span>{item.name ?? item.title}</span>
              </Button> },
            { title: title === "Definitions" ? "Definition ID" : "Instance ID", dataIndex: "secondary", width: title === "Definitions" ? 230 : 340, ellipsis: true },
            ...(title === "Instances" ? [{ title: "Definition", dataIndex: "definitionId", width: 190, ellipsis: true }] : []),
            { title: title === "Definitions" ? "Latest version" : "Version", key: "version", width: 110,
              sorter: (a, b) => a.version - b.version,
              render: (_, item) => item.version > 0 ? `v${item.version}` : "—" },
            ...(title === "Definitions" ? [{ title: "Latest active", dataIndex: "latestActive", width: 130,
              render: (value: number | null) => value == null ? "—" : `v${value}` }] : []),
            { title: title === "Definitions" ? "Status" : "Type", dataIndex: "tag", width: 130,
              filters: [...new Set(items.map(item => item.tag).filter(Boolean))].map(value => ({ text: value!, value: value! })),
              onFilter: (value, item) => item.tag === value,
              render: (value: string) => <Tag>{value}</Tag> },
            ...(title === "Instances" ? [{ title: "Lifecycle", dataIndex: "status", width: 110,
              filters: [...new Set(items.map(item => item.status).filter(Boolean))].map(value => ({ text: value!, value: value! })),
              onFilter: (value: React.Key | boolean, item: typeof items[number]) => item.status === value,
              render: (value: string) => <Tag>{value}</Tag> },
              { title: "Updated at", dataIndex: "updatedAt", width: 200,
                sorter: (a: typeof items[number], b: typeof items[number]) => (a.updatedAt ?? "").localeCompare(b.updatedAt ?? ""),
                render: (value: string) => formatAdminTimestamp(value) }] : [
              { title: "Drafts", dataIndex: "draftCount", width: 90, align: "right" as const,
                sorter: (a: typeof items[number], b: typeof items[number]) => (a.draftCount ?? 0) - (b.draftCount ?? 0) },
              { title: "Versions", dataIndex: "versionCount", width: 90, align: "right" as const,
                sorter: (a: typeof items[number], b: typeof items[number]) => (a.versionCount ?? 0) - (b.versionCount ?? 0) }
            ])
          ]}
        />
      </> : null}
    </section>
  );
}

function DefinitionDetail({
  definitionId,
  tab,
  definitions,
  onBack,
  onRetryDefinitions,
  onDeleted
}: {
  definitionId: string;
  tab?: AdminDefinitionTab;
  definitions: LoadState<AdminDefinitionInventoryItem[]>;
  onBack: () => void;
  onRetryDefinitions: () => Promise<void>;
  onDeleted: () => void;
}) {
  const { message, modal } = App.useApp();
  const { token } = theme.useToken();
  const [detailTab, updateDetailTab] = useState<AdminDefinitionTab>(tab ?? "versions");
  const setDetailTab = useCallback((next: AdminDefinitionTab) => {
    updateDetailTab(next);
    const path = adminDefinitionPath(definitionId, next);
    if (window.location.pathname !== path) navigateToAppPath(path);
  }, [definitionId]);
  const rows = definitions.kind === "ready"
    ? definitions.data.filter((item) => item.definitionId === definitionId)
    : [];
  const group = rows.length > 0 ? groupDefinitionInventory(rows)[0] : null;
  useEffect(() => { updateDetailTab(tab ?? (group?.draftOnly ? "drafts" : "versions")); }, [tab, group?.draftOnly]);
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
  const [forkSource, setForkSource] = useState<string | null>(null);
  const [candidate, setCandidate] = useState<DefinitionCandidate>({});
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [editorView, setEditorView] = useState<DefinitionEditorView>("form");
  const [busy, setBusy] = useState(false);
  const draftRead = useRef({ sequence: 0, pending: false });
  useEffect(() => {
    const cancelPendingDraftRead = () => {
      if (!draftRead.current.pending) return;
      ++draftRead.current.sequence;
      draftRead.current.pending = false;
      setBusy(false);
    };
    window.addEventListener("popstate", cancelPendingDraftRead);
    return () => {
      ++draftRead.current.sequence;
      window.removeEventListener("popstate", cancelPendingDraftRead);
    };
  }, []);
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

  const editCandidate = useCallback((next: DefinitionCandidate | ((current: DefinitionCandidate) => DefinitionCandidate)) => {
    if (jsonError) {
      return;
    }
    setCandidate(next);
    setJsonError(null);
    if (editorView === "json" && typeof next !== "function") {
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
      setForkSource(null);
      return;
    }
    const defaultRow = group.versions.find(item => item.version === defaultForkSourceVersion(group.versions));
    setForkSource((current) =>
      current !== null && group.versions.some((item) => forkSourceKey(item) === current)
        ? current
        : defaultRow ? forkSourceKey(defaultRow) : null
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
    setDetailTab("drafts");
    setActiveDraft(null);
    loadCandidate({});
  };

  const selectDraft = useCallback(async (draftId: string, updatePath = true) => {
    const sequence = ++draftRead.current.sequence;
    draftRead.current.pending = true;
    setBusy(true);
    setLifecycleError(null);
    try {
      const draft = await getAdminDefinitionDraft(draftId);
      if (sequence !== draftRead.current.sequence) return;
      // This read is complete; its own tab navigation must not cancel it.
      draftRead.current.pending = false;
      if (updatePath) setDetailTab("drafts");
      else updateDetailTab("drafts");
      setActiveDraft(draft);
      loadCandidate(draft.candidate);
    } catch (error) {
      if (sequence === draftRead.current.sequence) reportLifecycleError(error, "Failed to load draft.");
    } finally {
      if (sequence === draftRead.current.sequence) {
        draftRead.current.pending = false;
        setBusy(false);
      }
    }
  }, [loadCandidate, setDetailTab]);

  useEffect(() => {
    if (
      definitions.kind !== "ready"
      || tab !== undefined
      || (group !== null && !group.draftOnly)
      || openedUnpublishedDraftRef.current
      || activeDraft
      || draftSummaries.length !== 1
    ) {
      return;
    }
    openedUnpublishedDraftRef.current = true;
    void selectDraft(draftSummaries[0].draftId, false);
  }, [definitions.kind, group, activeDraft, draftSummaries, selectDraft, tab]);

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
    if (!activeDraft || jsonError || automationNumberErrors(candidate).length > 0) {
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
        : "Validated content becomes an immutable published version and this draft is removed. To make further changes, create a draft from the published version.",
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
          setDetailTab("versions");
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
  const selectedForkRow = group?.versions.find((row) => forkSourceKey(row) === forkSource)
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
      {(group || draftSummaries.length > 0) && activeDraft && detailTab === "drafts" ? (
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
            onDelete={() => confirmDeleteDraft(activeDraft, activeDraft.revision)}
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
          <section aria-label="Definition drafts" className="admin-definition-panel admin-definition-drafts">
            <div className="admin-definition-panel-heading">
              <Typography.Title level={4}>Versions &amp; drafts</Typography.Title>
              <Typography.Text type="secondary">
                Fork an immutable version to edit, validate, and publish a new one.
              </Typography.Text>
            </div>
            <div className="admin-definition-panel-body">
              {group && !group.draftOnly ? (
                <Flex wrap gap={token.paddingXS} align="center" className="admin-draft-create">
                  <Select
                    aria-label="Base version"
                    value={forkSource}
                    onChange={setForkSource}
                    options={group.versions.map((row) => ({
                      value: forkSourceKey(row),
                      label: `v${row.version} · ${formatInventorySource(row.source)}`
                    }))}
                    optionRender={(option) => {
                      const row = group.versions.find((item) => forkSourceKey(item) === option.value);
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
              <Tabs className="admin-draft-tabs" activeKey={detailTab}
                onChange={key => setDetailTab(key as AdminDefinitionTab)} items={[
                  { key: "versions", label: "Versions", children: <>
                    {group && !group.draftOnly ? (
                      <DefinitionVersionsTable key={definitionId}
                        rows={group.versions} publications={publications} busy={busy}
                        renderResources={(version) => <PublicationResourcesSummary definitionId={definitionId} version={version} />}
                        onChat={(version) => void startManagedChat(definitionId, version)}
                        onDeprecate={(item) => confirmAction(modal, {
                          title: `Deprecate publication v${item.version}?`,
                          content: "Metadata-only change. Exact version lookup and existing sessions stay intact; avoid selecting this version for new managed work.",
                          okText: "Deprecate", onOk: () => void deprecatePublication(item)
                        })}
                      />
                    ) : null}
                    {group?.draftOnly ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No published versions yet." /> : null}
                  </> },
                  { key: "drafts", label: "Drafts", children: <>
                    {!lifecycleLoading && draftSummaries.length > 0 ? (
                      <div className="admin-draft-list">
                        <Flex align="baseline" justify="space-between" gap={12} className="admin-draft-list-heading">
                          <Typography.Text strong>Existing drafts</Typography.Text>
                          <Typography.Text type="secondary">{draftSummaries.length}</Typography.Text>
                        </Flex>
                        <Table
                          aria-label="Definition drafts table" className="admin-collection-table" rowKey="draftId"
                          size="small" scroll={{ x: 560 }} dataSource={draftSummaries} pagination={adminCollectionPagination}
                          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="No matches. Clear filters to see all drafts." /> }}
                          columns={[
                            { title: "Draft", key: "draft", render: (_, item) => <Button type="link" size="small" disabled={busy}
                              aria-label={`Draft rev ${item.revision} · ${item.sourceKind}${item.sourceVersion != null ? ` v${item.sourceVersion}` : ""}`}
                              onClick={() => void selectDraft(item.draftId)}>
                              {item.sourceVersion != null ? `Draft from v${item.sourceVersion}` : "New draft"}
                            </Button> },
                            { title: "Revision", dataIndex: "revision", width: 100, sorter: (a, b) => a.revision - b.revision },
                            { title: "Source", dataIndex: "sourceKind", render: formatDraftSource,
                              filters: [...new Set(draftSummaries.map(item => item.sourceKind))].map(value => ({ text: formatDraftSource(value), value })),
                              onFilter: (value, item) => item.sourceKind === value },
                            { title: "Updated", dataIndex: "updatedAt", defaultSortOrder: "descend",
                              sorter: (a, b) => a.updatedAt.localeCompare(b.updatedAt), render: formatAdminTimestamp },
                            { title: "Actions", key: "actions", width: 80, render: (_, item) => <Button type="text" size="small" danger
                              icon={<DeleteOutlined />} disabled={busy}
                              aria-label={`Delete ${item.sourceVersion != null ? `draft from v${item.sourceVersion}` : "draft"}, revision ${item.revision}`}
                              onClick={() => confirmDeleteDraft(item, item.revision)} /> }
                          ]}
                        />
                      </div>
                    ) : null}
                    {!lifecycleLoading && draftSummaries.length === 0 ? (
                      <Typography.Text type="secondary">No drafts yet. Fork a catalog version to start.</Typography.Text>
                    ) : null}
                  </> }
                ]} />
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
  onDelete,
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
  onCandidateChange: (candidate: DefinitionCandidate | ((current: DefinitionCandidate) => DefinitionCandidate)) => void;
  onJsonTextChange: (text: string) => void;
  onEditorViewChange: (view: DefinitionEditorView) => void;
  onSave: () => void;
  onPublish: () => void;
  onDelete: () => void;
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
    onCandidateChange(applyDraftEnvironmentToCandidate(candidate, { ...next, capabilityMode: next.capabilityMode ?? "Selected",
      alwaysCapabilities: reconcileAlwaysCapabilities(next.alwaysCapabilities ?? authorizedNames, next.toolAllowlist, capabilityCatalog) }));
  };

  const { message } = App.useApp();
  const [toolRegistryLoading, setToolRegistryLoading] = useState(false);
  const [toolRegistryError, setToolRegistryError] = useState<AdminFailureNotice | null>(null);
  const [toolNames, setToolNames] = useState<string[]>([]);
  const [capabilityCatalog, setCapabilityCatalog] = useState<import("../../services/adminApi").AdminCapabilityDescriptor[]>([]);
  const [catalogReady, setCatalogReady] = useState(false);
  const selectableCatalog = capabilityCatalog;
  const allCapabilityNames = capabilityCatalog.map(c => c.name);
  const authorizedNames = capabilities.capabilityMode === "All"
    ? allCapabilityNames
    : capabilities.toolAllowlist;
  const alwaysNames = reconcileAlwaysCapabilities(capabilities.alwaysCapabilities ?? authorizedNames, authorizedNames, capabilityCatalog);
  const otherAuthorizedCapabilities = capabilityCatalog.filter(c => authorizedNames.includes(c.name) && c.discoverable && !alwaysNames.includes(c.name));
  const { token } = theme.useToken();
  // Migrate an editable legacy candidate using its exact grants and fixed discoverable projection.
  // Published versions remain immutable; Core still owns context-only projection.
  useEffect(() => {
    if (catalogReady && !candidateLocked && editorView === "form" && !capabilities.capabilityMode) {
      onCandidateChange(current => {
        const environment = readDraftEnvironment(current);
        return environment.capabilityMode ? current : applyDraftEnvironmentToCandidate(current, { ...environment, capabilityMode: "Selected",
          alwaysCapabilities: reconcileAlwaysCapabilities(environment.toolAllowlist, environment.toolAllowlist, capabilityCatalog) });
      });
    }
  }, [catalogReady, candidateLocked, editorView, capabilities.capabilityMode, capabilityCatalog, onCandidateChange]);
  const numberErrors = automationNumberErrors(candidate);
  const saveBlocked = candidateLocked || numberErrors.length > 0 || (editorView === "form" && !catalogReady);
  const saveBlockedMessage = candidateLocked
    ? "Advanced JSON is invalid — fix it before saving. This does not change the draft revision."
    : numberErrors[0]?.message ?? "Authoring options are loading. Wait before saving or use Advanced JSON.";
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
      const registry = await getAdminToolRegistry();
      setToolNames(registry.toolNames);
      setCapabilityCatalog(registry.capabilities ?? registry.toolNames.map(name => ({ name, category: name.split(".")[0], summary: name, tags: [], discoverable: true, defaultProjectionClass: "onDemand", configured: true })));
      setCatalogReady(true);
    } catch (error) {
      setToolNames([]);
      setCatalogReady(false);
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
      <DraftEditorActions
        dirty={dirty} busy={busy} publishEligible={publishEligible} saveBlocked={saveBlocked} saveBlockedMessage={saveBlockedMessage}
        onSave={onSave} onPublish={onPublish} onDelete={onDelete}
      />
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
                    <Typography.Text strong>Capability access</Typography.Text>
                    <Select aria-label="Capability access" value={capabilities.capabilityMode ?? "Selected"}
                      disabled={busy || candidateLocked || !catalogReady || toolRegistryLoading}
                      options={[{ value: "Selected", label: "Selected capabilities" }, { value: "All", label: "All current capabilities" }]}
                      onChange={mode => onCapabilitiesChange({ ...capabilities, capabilityMode: mode as "Selected" | "All",
                        toolAllowlist: mode === "All" ? allCapabilityNames : capabilities.toolAllowlist,
                        alwaysCapabilities: alwaysNames })} />
                  </label>
                  {capabilities.capabilityMode === "All" ? <Typography.Text type="secondary">All capabilities currently known to Core are included in the next publication as an immutable snapshot. Later capabilities are not automatically granted.</Typography.Text> : null}
                  <label className="admin-draft-field">
                    <Typography.Text strong>Authorized capabilities</Typography.Text>
                    <Typography.Text type="secondary">What the agent is permitted to use.</Typography.Text>
                    <Select aria-label="Authorized capabilities" mode="multiple" maxTagCount="responsive" value={authorizedNames}
                      disabled={busy || candidateLocked || !catalogReady || capabilities.capabilityMode === "All"}
                      onChange={values => onCapabilitiesChange({ ...capabilities, toolAllowlist: values,
                        alwaysCapabilities: reconcileAlwaysCapabilities(alwaysNames, values, capabilityCatalog) })}
                      options={groupedCapabilityOptions(selectableCatalog)} optionFilterProp="label" showSearch
                      placeholder="Select registered capabilities" />
                  </label>
                  {<>
                    <label className="admin-draft-field">
                      <Typography.Text strong>Always available to the model</Typography.Text>
                      <Typography.Text type="secondary">Select capability details to include in the initial model context. Other authorized capabilities can be loaded on demand.</Typography.Text>
                      <Select aria-label="Always projected capabilities" mode="multiple" maxTagCount="responsive"
                        value={alwaysNames} disabled={busy || candidateLocked || !catalogReady || toolRegistryLoading}
                        onChange={values => onCapabilitiesChange({ ...capabilities, alwaysCapabilities: values })}
                        options={groupedCapabilityOptions(capabilityCatalog, authorizedNames)} optionFilterProp="label" showSearch />
                    </label>
                    <Typography.Text type="secondary">Core also includes authorized, eligible Browser v2 bootstrap tools and active Skill requirements. These can appear without an Always selection; permission still comes from Authorized capabilities.</Typography.Text>
                    <Typography.Text aria-live="polite">Authorized: {authorizedNames.length} · Always selected: {alwaysNames.length}</Typography.Text>
                    <Typography.Text type="secondary">Not selected as Always available: {otherAuthorizedCapabilities.length}. Context-only capabilities remain controlled by Core.</Typography.Text>
                    {otherAuthorizedCapabilities.length ? <Collapse ghost items={[{ key: "on-demand", label: "Other authorized capabilities", children: <Flex wrap gap={token.paddingXS}>
                      {otherAuthorizedCapabilities.map(capability => <Tag key={capability.name} title={capability.summary}>{capability.name}</Tag>)}
                    </Flex> }]} /> : null}
                  </>}

                </section>
                <section className="admin-draft-form-section" aria-label="Workspace behavior">
                  <Typography.Title level={5}>Workspace behavior</Typography.Title>
                  <Typography.Text type="secondary">Every Session starts in durable /home. Use explicit /working paths for temporary files and workspace.copy to transfer files or trees.</Typography.Text>
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
                toolNames={capabilities.capabilityMode ? allCapabilityNames : toolNames}
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
  saveBlockedMessage,
  onSave,
  onPublish,
  onDelete
}: {
  dirty: boolean;
  busy: boolean;
  publishEligible: boolean;
  saveBlocked?: boolean;
  saveBlockedMessage?: string;
  onSave: () => void;
  onPublish: () => void;
  onDelete: () => void;
}) {
  return (
    <div className="admin-draft-actions">
      <Flex gap={16} wrap="wrap" align="center">
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
        <Button danger aria-label="Delete draft" icon={<DeleteOutlined />} onClick={onDelete} disabled={busy}
          >Delete draft</Button>
      </Flex>
      {saveBlocked ? (
        <Typography.Text type="danger">
          {saveBlockedMessage}
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
  lifecycle,
  definitionStatus
}: {
  lifecycle: string;
  definitionStatus?: string;
}) {
  return (
    <Flex gap={8} wrap="wrap">
      <Tag color="blue">Agent Instance</Tag>
      <Tag>{lifecycle}</Tag>
      {definitionStatus ? <Tag>{definitionStatus}</Tag> : null}
    </Flex>
  );
}

export function InstanceDetail({
  instanceId,
  tab,
  section,
  instances,
  effective,
  onBack,
  onRetryEffective,
  onInstanceChanged,
  onInstanceDeleted
}: {
  instanceId: string;
  tab?: AdminInstanceTab;
  section?: AdminInstanceSection;
  instances: LoadState<AdminInstanceInventoryItem[]>;
  effective: EffectiveConfigLoadState;
  onBack: () => void;
  onRetryEffective: () => void;
  onInstanceChanged: () => void;
  onInstanceDeleted: () => void;
}) {
  const { token } = theme.useToken();
  const [activeTab, updateActiveTab] = useState<AdminInstanceTab>(tab ?? "identity");
  const [continuityTab, setContinuityTab] = useState("memory");
  const [automationTab, setAutomationTab] = useState("automations");
  const [identityTab, setIdentityTab] = useState("profile");
  const [sourceSelection, setSourceSelection] = useState<AutomationSelection>();
  const [experienceSelection, setExperienceSelection] = useState<ExperienceSelection>();
  const [selectedAgentRunId, setSelectedAgentRunId] = useState<string>();
  const [runDetailsOpen, setRunDetailsOpen] = useState(false);
  const runOpener = useRef<HTMLElement | null>(null);
  const restoreRunFocus = useRef(false);
  useEffect(() => {
    updateActiveTab(tab ?? "identity");
    if (tab === "continuity") setContinuityTab(section ?? "memory");
    if (tab === "automation") setAutomationTab(section ?? "automations");
    if (tab === "identity") setIdentityTab(section ?? "profile");
  }, [instanceId, tab, section]);
  useEffect(() => {
    const source = new URLSearchParams(window.location.search).get("automation");
    setSourceSelection(source && /^[0-9a-f-]{36}$/i.test(source) ? { kind: "automation", automationId: source, request: Date.now() } : undefined);
    setExperienceSelection(undefined); setSelectedAgentRunId(undefined); setRunDetailsOpen(false);
  }, [instanceId]);
  const setActiveTab = (next: AdminInstanceTab, selectedSection?: AdminInstanceSection) => {
    const nextSection = selectedSection ?? (next === "continuity" ? continuityTab : next === "automation" ? automationTab : next === "identity" ? identityTab === "profile" ? undefined : identityTab : undefined);
    updateActiveTab(next);
    if (next === "continuity" && nextSection) setContinuityTab(nextSection);
    if (next === "automation" && nextSection) setAutomationTab(nextSection);
    if (next === "identity") setIdentityTab(nextSection ?? "profile");
    navigateToAppPath(adminInstancePath(instanceId, next, nextSection as AdminInstanceSection | undefined));
  };
  const viewRun = (workId?: string) => {
    if (!workId) { setActiveTab("runs"); return; }
    runOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    restoreRunFocus.current = false;
    setSelectedAgentRunId(workId);
    setRunDetailsOpen(true);
  };
  const viewSource = (source: RunSource) => {
    restoreRunFocus.current = false;
    setRunDetailsOpen(false);
    if (source.kind === "experience") {
      setExperienceSelection({ agentRunId: source.agentRunId, request: Date.now() }); setActiveTab("continuity", "experience"); return;
    }
    setSourceSelection({ ...source, request: Date.now() });
    setActiveTab("automation", "automations");
  };
  const row = instances.kind === "ready"
    ? instances.data.find((item) => item.instanceId.toLowerCase() === instanceId.toLowerCase())
    : undefined;

  const resolved = effective.data?.instanceId === instanceId ? effective.data : null;
  useEffect(() => {
    if (!resolved || !tab) return;
    const availableTab = resolved.instanceLifecycle !== "Active" && tab === "automation" ? "identity" : tab;
    if (availableTab !== tab) navigateToAppPath(adminInstancePath(instanceId, availableTab), true);
  }, [resolved, instanceId, tab]);
  const headerIdentity = resolved
    ? {
        lifecycle: resolved.instanceLifecycle,
        definitionStatus: resolved.definitionStatus
      }
    : row
      ? {
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
      {resolved ? (
        <div inert={effective.kind !== "ready"}>
          <Tabs
            className="admin-draft-tabs admin-instance-tabs"
            activeKey={resolved.instanceLifecycle !== "Active" && activeTab === "automation" ? "identity" : activeTab}
            onChange={key => setActiveTab(key as AdminInstanceTab)}
            items={[
              ...([{
                key: "identity",
                label: "Identity & version",
                children: <Tabs activeKey={identityTab} onChange={key => setActiveTab("identity", key as AdminInstanceSection)} aria-label="Identity sections" items={[
                  { key: "profile", label: "Profile", children: <InstanceManagedControls config={resolved} onUpdated={onInstanceChanged} onDeleted={onInstanceDeleted} /> },
                  { key: "workspace", label: "Workspace", children: <InstanceWorkspaceSection key={instanceId} instanceId={instanceId} archived={resolved.instanceLifecycle !== "Active"} /> }
                ]} />
              }]),
              ...([{
                key: "skills", label: "Skills", children: <InstanceSkillsSection key={instanceId} instanceId={instanceId} archived={resolved.instanceLifecycle !== "Active"} active={activeTab === "skills"} onUpdated={onInstanceChanged} />
              },
              {
                key: "continuity", label: "Continuity",
                children: <Flex vertical gap={token.padding}>{resolved.instanceLifecycle === "Active" ? <IdentityMaintenanceSection instanceId={instanceId} /> : null}<Tabs activeKey={continuityTab} onChange={key => setActiveTab("continuity", key as AdminInstanceSection)} aria-label="Continuity sections" items={[
                  { key: "memory", label: "Memory", children: <InstanceMemoryAutomationPanel config={resolved} section="memory" /> },
                  { key: "experience", label: "Experience", children: resolved.instanceLifecycle === "Active" ? <ExperienceSection instanceId={instanceId} active={activeTab === "continuity" && continuityTab === "experience"} onWork={viewRun} selection={activeTab === "continuity" && continuityTab === "experience" ? experienceSelection : undefined} /> : <Alert type="info" showIcon title="Experience is available when this instance is active" description="Unarchive the instance from Identity & version to inspect its experience." /> }
                ]} /></Flex>
              }]),
              ...(resolved.instanceLifecycle === "Active" ? [{
                key: "automation", label: "Automation",
                children: <Flex vertical gap={16}>
                  <Typography.Text type="secondary">An Automation produces a Run when its trigger fires or you choose Run now.</Typography.Text>
                  <Tabs activeKey={automationTab} onChange={key => setActiveTab("automation", key as AdminInstanceSection)} aria-label="Automation sections" items={[
                    { key: "automations", label: "Triggers", children: <InstanceAutomationsSection instanceId={instanceId} active={activeTab === "automation" && automationTab === "automations"} onWork={viewRun} selection={activeTab === "automation" ? sourceSelection : undefined} /> },
                    { key: "controls", label: "Policies & models", children: <Flex vertical gap={16}>
                      <HarnessManagementSection instanceId={instanceId} eligibleTools={resolved.effectiveToolAllowlist} onUpdated={onInstanceChanged} />
                      <InstanceMemoryAutomationPanel config={resolved} section="automation" />
                    </Flex> }
                  ]} />
                </Flex>
              }] : []),
              ...([{ key: "runs", label: "Runs", children:
                resolved.instanceLifecycle === "Active" ? <InstanceRunsSection instanceId={instanceId} open={activeTab === "runs"} inline onRun={viewRun} onClose={() => setActiveTab("automation")} /> : <Alert type="info" showIcon title="Runs are available when this instance is active" description="Unarchive the instance from Identity & version to inspect execution history." />
              }]),
              {
                key: "credentials", label: "Credentials",
                children: <InstanceCredentialsSection instanceId={instanceId} revision={resolved.instanceRevision} archived={resolved.instanceLifecycle !== "Active"} />
              },
            {
              key: "effective",
              label: "Effective configuration",
              children: (
                <section className="admin-definition-panel" aria-label="Effective configuration">
                  <div className="admin-definition-panel-heading">
                    <Typography.Title level={4}>Effective configuration</Typography.Title>
                    <Typography.Text type="secondary">
                      Read-only values resolved from the active definition and instance overrides.
                    </Typography.Text>
                  </div>
                  <div className="admin-definition-panel-body">
                    <EffectiveConfigView config={resolved} hidePersona />
                  </div>
                </section>
              )
            }
          ]}
        />
        </div>
      ) : null}
      {resolved?.instanceLifecycle === "Active" ? <InstanceRunsSection instanceId={instanceId}
        open={runDetailsOpen} detailsOnly selectedAgentRunId={selectedAgentRunId} onSource={viewSource}
        onClose={() => { restoreRunFocus.current = true; setRunDetailsOpen(false); }}
        afterClose={() => { if (restoreRunFocus.current && runOpener.current?.isConnected) runOpener.current.focus({ preventScroll: true }); }} /> : null}
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
  }, [config.instanceId, config.personaRevision]);

  useEffect(() => {
    setTargetVersion(config.definitionVersion);
  }, [config.instanceId, config.definitionVersion]);

  useEffect(() => {
    setDeleteError(null);
  }, [config.instanceId, config.instanceLifecycle]);

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
                Unsaved persona edits stay in this editor across lifecycle and version changes until you save or
                leave this instance.
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
              <Button onClick={() => void applyVersion()}
                disabled={busy || targetVersion === config.definitionVersion}>
                {versionActionLabel}
              </Button>
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
                      content: "New chats and triggered work will stop until you unarchive. Unsaved persona edits stay in this editor.",
                      okText: "Archive",
                      danger: true,
                      onOk: () => void setLifecycle("Archived")
                    })
                  }
                >
                  Archive instance
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
  const detailLayout = useAdminDetailLayout();
  return (
    <div className="admin-effective-config-grid">
      <section
        className={`admin-effective-config-section admin-instance-identity${hidePersona ? " admin-effective-config-wide" : ""}`}
        aria-label="Instance identity"
      >
        <Typography.Title level={5}>Instance identity</Typography.Title>
        <InstanceIdentityTags
                    lifecycle={config.instanceLifecycle}
          definitionStatus={config.definitionStatus}
        />
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Instance id">{config.instanceId}</Descriptions.Item>
          <Descriptions.Item label="Definition status">{config.definitionStatus}</Descriptions.Item>
          <Descriptions.Item label="Lifecycle">{config.instanceLifecycle}</Descriptions.Item>
        </Descriptions>
      </section>
      {hidePersona ? null : (
        <section className="admin-effective-config-section" aria-label="Persona">
          <Typography.Title level={5}>Persona</Typography.Title>
          <Descriptions {...detailLayout} bordered size="small" column={1}>
            <Descriptions.Item label="Name">{config.persona.name}</Descriptions.Item>
            <Descriptions.Item label="Role">{config.persona.role}</Descriptions.Item>
            <Descriptions.Item label="Description">{config.persona.description}</Descriptions.Item>
            <Descriptions.Item label="Tone">{config.persona.tone}</Descriptions.Item>
          </Descriptions>
        </section>
      )}
      <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Runtime model">
        <Typography.Title level={5}>Runtime model</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
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
        <Descriptions {...detailLayout} bordered size="small" column={1}>
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
      {config.browser ? (
        <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Browser provider">
          <Typography.Title level={5}>Browser provider</Typography.Title>
          <Descriptions {...detailLayout} bordered size="small" column={1}>
            <Descriptions.Item label="Provider">{config.browser.displayName} ({config.browser.providerId})</Descriptions.Item>
            <Descriptions.Item label="Engine">{config.browser.engine || "Unknown"}</Descriptions.Item>
            <Descriptions.Item label="Readiness">{!config.browser.enabled ? "Disabled" : config.browser.ready ? "Ready" : "Unavailable"}</Descriptions.Item>
            <Descriptions.Item label="Profile mode">{config.browser.profileMode}</Descriptions.Item>
            <Descriptions.Item label="Policy mode">{config.browser.policyMode}</Descriptions.Item>
            <Descriptions.Item label="Supported features">{config.browser.supportedFeatures.join(", ") || "None"}</Descriptions.Item>
            <Descriptions.Item label="Output limits">Snapshot {config.browser.maxSnapshotChars} characters; screenshot {config.browser.maxCaptureBytes} bytes; download {config.browser.maxDownloadBytes} bytes</Descriptions.Item>
          </Descriptions>
        </section>
      ) : null}
      <section className="admin-effective-config-section" aria-label="Memory policy">
        <Typography.Title level={5}>Memory policy</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
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
        <Descriptions {...detailLayout} bordered size="small" column={1}>
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
