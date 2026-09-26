import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  App,
  Button,
  Descriptions,
  Empty,
  Flex,
  Form,
  Input,
  Layout,
  List,
  Popconfirm,
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
  draftEnvironmentEquals,
  emptyDraftEnvironment,
  readDraftEnvironment,
  type DraftEnvironment,
  type DraftKnowledgeSource
} from "./draftEnvironment";
import { DefinitionDraftPublishGatePanel } from "./definitionDraftPublishGatePanel";
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
  deleteAdminDefinitionDraft,
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
import { formatAdminLoadError } from "./adminErrors";
import { startManagedPublicationChat } from "./adminManagedChat";
import { InstanceMemoryAutomationPanel } from "./instanceMemoryAutomation";

const { Header, Content } = Layout;

type LoadState<T> =
  | { kind: "loading" }
  | { kind: "error"; message: string; unauthorized?: boolean }
  | { kind: "ready"; data: T };

type DefinitionInventoryGroup = {
  definitionId: string;
  logicalName: string;
  defaultPersona: string;
  latestVersion: number;
  latestStatus: string;
  versions: AdminDefinitionInventoryItem[];
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
    const latest = orderedVersions[0];
    return {
      definitionId,
      logicalName: logicalDefinitionName(definitionId),
      defaultPersona: latest.displayName,
      latestVersion: latest.version,
      latestStatus: latest.status,
      versions: orderedVersions
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
      setDefinitions({ kind: "error", message: formatted.message, unauthorized: formatted.unauthorized });
    }
  }, []);

  const reloadInstances = useCallback(async () => {
    setInstances({ kind: "loading" });
    try {
      const instanceItems = await listAdminInstances();
      setInstances({ kind: "ready", data: instanceItems });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setInstances({ kind: "error", message: formatted.message, unauthorized: formatted.unauthorized });
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
      setEffectiveConfig({ kind: "error", message: formatted.message, unauthorized: formatted.unauthorized });
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
            <div className="admin-home-intro">
              <Typography.Title level={2} className="admin-home-title">Agent inventory</Typography.Title>
              <Typography.Paragraph type="secondary" className="admin-home-subtitle">
                Inspect published definitions and the agent instances pinned to them.
              </Typography.Paragraph>
            </div>
            <div className="admin-inventory-grid">
              <InventorySection
                title="Definitions"
                countLabel={definitions.kind === "ready"
                  ? `${definitionGroups.length} ${definitionGroups.length === 1 ? "definition" : "definitions"}`
                  : null}
                emptyLabel="No definitions found."
                loading={definitions.kind === "loading"}
                error={definitions.kind === "error" ? definitions.message : null}
                unauthorized={definitions.kind === "error" ? (definitions.unauthorized ?? false) : false}
                onRetry={() => void reloadDefinitions()}
                items={
                  definitions.kind === "ready"
                    ? definitionGroups.map((group) => ({
                        key: group.definitionId,
                        title: group.logicalName,
                        secondary: group.definitionId,
                        description: `Latest-version persona: ${group.defaultPersona} · ${group.versions.length} ${
                          group.versions.length === 1 ? "version" : "versions"
                        } · latest v${group.latestVersion}`,
                        tag: group.latestStatus,
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
                emptyLabel="No instances yet. Start a chat to create compatibility instances."
                loading={instances.kind === "loading"}
                error={instances.kind === "error" ? instances.message : null}
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
          </div>
        ) : null}

        {route.view === "definition" ? (
          <DefinitionDetail
            definitionId={route.definitionId}
            definitions={definitions}
            onBack={() => navigateToAppPath(adminHomePath())}
            onRetryDefinitions={reloadDefinitions}
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
          />
        ) : null}
      </Content>
    </Layout>
    </App>
  );
}

function InventorySection({
  title,
  countLabel,
  emptyLabel,
  loading,
  error,
  unauthorized,
  onRetry,
  items
}: {
  title: string;
  countLabel: string | null;
  emptyLabel: string;
  loading: boolean;
  error: string | null;
  unauthorized: boolean;
  onRetry: () => void;
  items: Array<{
    key: string;
    title: string;
    secondary?: string;
    description: string;
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
          action={
            <Button size="small" onClick={onRetry}>
              Retry
            </Button>
          }
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
  onRetryDefinitions
}: {
  definitionId: string;
  definitions: LoadState<AdminDefinitionInventoryItem[]>;
  onBack: () => void;
  onRetryDefinitions: () => Promise<void>;
}) {
  const { message, modal } = App.useApp();
  const rows = definitions.kind === "ready"
    ? definitions.data.filter((item) => item.definitionId === definitionId)
    : [];
  const group = rows.length > 0 ? groupDefinitionInventory(rows)[0] : null;
  const [lifecycleLoading, setLifecycleLoading] = useState(false);
  const [lifecycleError, setLifecycleError] = useState<string | null>(null);
  const [draftSummaries, setDraftSummaries] = useState<AdminDefinitionDraftSummary[]>([]);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [activeDraft, setActiveDraft] = useState<AdminDefinitionDraft | null>(null);
  const [forkSourceVersion, setForkSourceVersion] = useState<number | null>(null);
  const [instructions, setInstructions] = useState("");
  const [capabilities, setCapabilities] = useState<DraftEnvironment>(emptyDraftEnvironment());
  const [busy, setBusy] = useState(false);
  const editorSurfaceRef = useRef<HTMLElement | null>(null);

  const savedInstructions = useMemo(() => {
    if (!activeDraft) {
      return "";
    }
    return (activeDraft.candidate as { systemInstructions?: string }).systemInstructions ?? "";
  }, [activeDraft]);

  const savedCapabilities = useMemo(() => {
    if (!activeDraft) {
      return emptyDraftEnvironment();
    }
    return readDraftEnvironment(activeDraft.candidate);
  }, [activeDraft]);

  const dirtyInstructions = activeDraft !== null && instructions !== savedInstructions;
  const dirtyCapabilities =
    activeDraft !== null && !draftEnvironmentEquals(capabilities, savedCapabilities);
  const dirty = dirtyInstructions || dirtyCapabilities;

  const buildCandidate = useCallback(() => {
    if (!activeDraft) {
      return {};
    }
    const base = {
      ...(activeDraft.candidate as Record<string, unknown>),
      systemInstructions: instructions
    };
    return applyDraftEnvironmentToCandidate(base, capabilities);
  }, [activeDraft, capabilities, instructions]);

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
      setLifecycleError(error instanceof Error ? error.message : "Failed to deprecate publication.");
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
      setLifecycleError(error instanceof Error ? error.message : "Failed to load drafts.");
    } finally {
      setLifecycleLoading(false);
    }
  }, [definitionId]);

  useEffect(() => {
    if (definitions.kind === "ready" && rows.length > 0) {
      void reloadLifecycle();
    }
  }, [definitions.kind, rows.length, reloadLifecycle]);

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
    setInstructions("");
    setCapabilities(emptyDraftEnvironment());
  };

  const selectDraft = useCallback(async (draftId: string) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      const draft = await getAdminDefinitionDraft(draftId);
      setActiveDraft(draft);
      const candidate = draft.candidate as { systemInstructions?: string };
      setInstructions(candidate.systemInstructions ?? "");
      setCapabilities(readDraftEnvironment(draft.candidate));
    } catch (error) {
      setLifecycleError(error instanceof Error ? error.message : "Failed to load draft.");
    } finally {
      setBusy(false);
    }
  }, []);

  const deleteDraft = async (draft: AdminDefinitionDraftSummary, expectedRevision: number) => {
    setBusy(true);
    setLifecycleError(null);
    try {
      await deleteAdminDefinitionDraft(draft.draftId, expectedRevision);
      if (activeDraft?.draftId === draft.draftId) {
        setActiveDraft(null);
        setInstructions("");
        setCapabilities(emptyDraftEnvironment());
      }
      message.success("Draft deleted.");
      await reloadLifecycle();
    } catch (error) {
      const text = error instanceof Error ? error.message : "Draft could not be deleted.";
      setLifecycleError(text);
      message.error(text);
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
    modal.confirm({
      title: `Delete ${draftLabel}?`,
      content: "This permanently removes the draft, its unpublished resources, and evaluation evidence. Published versions are unchanged.",
      okText: "Delete draft",
      cancelText: "Keep draft",
      okButtonProps: { danger: true },
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
      setLifecycleError(error instanceof Error ? error.message : "Fork failed.");
    } finally {
      setBusy(false);
    }
  };

  const saveDraft = async () => {
    if (!activeDraft) {
      return;
    }
    setBusy(true);
    setLifecycleError(null);
    try {
      const updated = await updateAdminDefinitionDraft(
        activeDraft.draftId,
        activeDraft.revision,
        buildCandidate());
      setActiveDraft(updated);
      setInstructions((updated.candidate as { systemInstructions?: string }).systemInstructions ?? "");
      setCapabilities(readDraftEnvironment(updated.candidate));
      message.success("Draft saved.");
      await reloadLifecycle();
    } catch (error) {
      const text = error instanceof Error ? error.message : "Save failed.";
      setLifecycleError(text);
      message.error(text);
    } finally {
      setBusy(false);
    }
  };

  const confirmPublish = () => {
    if (!activeDraft) {
      return;
    }
    if (dirty) {
      message.warning("Save draft edits before publishing.");
      return;
    }
    modal.confirm({
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
            draft = await updateAdminDefinitionDraft(draft.draftId, draft.revision, buildCandidate());
            setActiveDraft(draft);
            setInstructions((draft.candidate as { systemInstructions?: string }).systemInstructions ?? "");
            setCapabilities(readDraftEnvironment(draft.candidate));
          }
          const publication = await publishAdminDefinitionDraft(draft.draftId, draft.revision);
          message.success(`Published version ${publication.version}.`);
          setActiveDraft(null);
          setInstructions("");
          setCapabilities(emptyDraftEnvironment());
          await reloadLifecycle();
          await onRetryDefinitions();
        } catch (error) {
          const text = error instanceof Error ? error.message : "Publish failed.";
          setLifecycleError(text);
          message.error(text);
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
      const text = error instanceof Error ? error.message : "Managed chat could not be started.";
      setLifecycleError(text);
      message.error(text);
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
          <Typography.Title level={2}>{group.logicalName}</Typography.Title>
          <Typography.Text type="secondary">{group.definitionId}</Typography.Text>
          <Typography.Text>Latest-version persona: {group.defaultPersona}</Typography.Text>
          <Flex gap={8} wrap="wrap">
            <Tag>{group.versions.length} {group.versions.length === 1 ? "version" : "versions"}</Tag>
            <Tag color="blue">{group.latestStatus}</Tag>
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
          action={<Button size="small" onClick={onRetryDefinitions}>Retry</Button>}
        />
      ) : null}
      {definitions.kind === "ready" && rows.length === 0 ? (
        <Result status="404" title="Definition not found" />
      ) : null}
      {group && activeDraft ? (
        <section ref={editorSurfaceRef} className="admin-draft-focused" aria-label="Draft editor">
          <Flex align="center" justify="space-between" gap={12} wrap="wrap" className="admin-draft-focused-toolbar">
            {dirty ? (
              <Popconfirm
                title="Discard unsaved changes?"
                description="Your saved draft remains available. Only unsaved edits in this editor will be discarded."
                okText="Discard changes"
                cancelText="Keep editing"
                onConfirm={closeDraftEditor}
              >
                <Button icon={<ArrowLeftOutlined />} aria-label="Back to drafts">Back to drafts</Button>
              </Popconfirm>
            ) : (
              <Button icon={<ArrowLeftOutlined />} aria-label="Back to drafts" onClick={closeDraftEditor}>Back to drafts</Button>
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
            <Alert
              type="error"
              showIcon
              title={lifecycleError}
              action={<Button size="small" onClick={() => void reloadLifecycle()}>Retry</Button>}
            />
          ) : null}
          <DraftEditor
            activeDraft={activeDraft}
            instructions={instructions}
            capabilities={capabilities}
            dirty={dirty}
            busy={busy}
            onInstructionsChange={setInstructions}
            onCapabilitiesChange={setCapabilities}
            onSave={() => void saveDraft()}
            onPublish={() => void confirmPublish()}
            onDraftRevisionChange={(draft, options) => {
              setActiveDraft(draft);
              if (!options?.preserveLocalEdits) {
                setInstructions((draft.candidate as { systemInstructions?: string }).systemInstructions ?? "");
                setCapabilities(readDraftEnvironment(draft.candidate));
              }
            }}
            onError={setLifecycleError}
          />
        </section>
      ) : group ? (
        <div className="admin-definition-workspace">
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
                    </Flex>
                  </Descriptions.Item>
                ))}
              </Descriptions>
            </div>
          </section>
          <section aria-label="Definition drafts" className="admin-definition-panel admin-definition-drafts">
            <div className="admin-definition-panel-heading">
              <Typography.Title level={4}>Drafts &amp; publishing</Typography.Title>
              <Typography.Text type="secondary">
                Fork an immutable version to edit, validate, and publish a new one.
              </Typography.Text>
            </div>
            <div className="admin-definition-panel-body">
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
          {lifecycleError ? (
            <Alert
              type="error"
              showIcon
              message={lifecycleError}
              action={<Button size="small" onClick={() => void reloadLifecycle()}>Retry</Button>}
              style={{ marginTop: 12 }}
            />
          ) : null}
          {lifecycleLoading ? <Spin style={{ marginTop: 12 }} /> : null}
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
                                {item.sourceKind === "ForkBuiltIn" ? "Built-in source" : "Durable source"} · Revision{" "}
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
            <Descriptions bordered size="small" column={1} style={{ marginTop: 16 }} title="Durable publications">
              {publications.map((item) => (
                <Descriptions.Item key={item.version} label={`v${item.version}`}>
                  <Flex vertical gap={8} align="start">
                    <span>
                      {item.status} · metadata rev {item.metadataRevision} · {item.publishedAt}
                    </span>
                    <PublicationResourcesSummary definitionId={definitionId} version={item.version} />
                    <Button
                      size="small"
                      aria-label={`Start managed chat for v${item.version}`}
                      disabled={busy || item.status !== "Active"}
                      onClick={() => void startManagedChat(definitionId, item.version)}
                    >
                      Start managed chat
                    </Button>
                    {item.status === "Active" ? (
                      <Popconfirm
                        title={`Deprecate publication v${item.version}?`}
                        description="Metadata-only change. Exact version lookup and existing sessions stay intact; avoid selecting this version for new managed work."
                        onConfirm={() => void deprecatePublication(item)}
                        okText="Deprecate"
                        cancelText="Cancel"
                      >
                        <Button
                          size="small"
                          danger
                          disabled={busy}
                          aria-label={`Deprecate publication v${item.version}`}
                        >
                          Deprecate publication
                        </Button>
                      </Popconfirm>
                    ) : null}
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

function DraftEditor({
  activeDraft,
  instructions,
  capabilities,
  dirty,
  busy,
  onInstructionsChange,
  onCapabilitiesChange,
  onSave,
  onPublish,
  onDraftRevisionChange,
  onError
}: {
  activeDraft: AdminDefinitionDraft;
  instructions: string;
  capabilities: DraftEnvironment;
  dirty: boolean;
  busy: boolean;
  onInstructionsChange: (value: string) => void;
  onCapabilitiesChange: (value: DraftEnvironment) => void;
  onSave: () => void;
  onPublish: () => void;
  onDraftRevisionChange: (
    draft: AdminDefinitionDraft,
    options?: { preserveLocalEdits?: boolean }
  ) => void;
  onError: (message: string | null) => void;
}) {
  const { message } = App.useApp();
  const [toolRegistryLoading, setToolRegistryLoading] = useState(false);
  const [toolRegistryError, setToolRegistryError] = useState<string | null>(null);
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
      onError(error instanceof Error ? error.message : "Failed to load resources.");
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
      setToolRegistryError(
        error instanceof Error ? error.message : "Failed to load tool registry."
      );
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
        { identity: "", title: "", citation: "" }
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
      const text = error instanceof Error ? error.message : "Resource upload failed.";
      onError(text);
      message.error(text);
    }
  };

  const removeResource = async (resource: AdminDefinitionDraftResource) => {
    onError(null);
    try {
      await removeAdminDraftResource(activeDraft.draftId, resource.resourceId, activeDraft.revision);
      message.success("Resource removed.");
      await refreshDraft();
    } catch (error) {
      const text = error instanceof Error ? error.message : "Remove failed.";
      onError(text);
      message.error(text);
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
            key: "instructions",
            label: "Instructions",
            children: (
              <section className="admin-draft-tab" aria-label="Draft instructions">
                <div className="admin-draft-tab-intro">
                  <Typography.Title level={5}>System instructions</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Define the durable behavior and boundaries inherited by sessions created from this definition.
                  </Typography.Paragraph>
                </div>
                <Input.TextArea
                  aria-label="System instructions"
                  rows={10}
                  value={instructions}
                  onChange={(event) => onInstructionsChange(event.target.value)}
                  disabled={busy}
                  className="admin-draft-instructions"
                />
                <DraftEditorActions
                  dirty={dirty}
                  busy={busy}
                  publishEligible={publishEligible}
                  onSave={onSave}
                  onPublish={onPublish}
                />
              </section>
            )
          },
          {
            key: "capabilities",
            label: "Capabilities",
            children: (
              <section className="admin-draft-tab" aria-label="Draft capabilities">
                <div className="admin-draft-tab-intro">
                  <Typography.Title level={5}>Capabilities</Typography.Title>
                  <Typography.Paragraph type="secondary">
                    Choose the tools, workspace behavior, and knowledge references available to this definition.
                  </Typography.Paragraph>
                </div>
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
                      disabled={busy}
                      placeholder="Add a harness label"
                    />
                  </label>
                {toolRegistryError ? (
                  <Alert
                    type="error"
                    showIcon
                    message={toolRegistryError}
                    action={
                      <Button
                        size="small"
                        aria-label="Retry tool registry"
                        onClick={() => void reloadToolRegistry()}
                        disabled={toolRegistryLoading}
                      >
                        Retry
                      </Button>
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
                      disabled={busy || toolRegistryLoading || toolRegistryError !== null}
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
                      disabled={busy}
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
                      disabled={busy}
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
                              disabled={busy}
                            />
                          </label>
                          <label className="admin-draft-field">
                            <Typography.Text>Title</Typography.Text>
                            <Input
                              aria-label={`Knowledge title ${index + 1}`}
                              placeholder="Support policy"
                              value={source.title}
                              onChange={(event) => updateKnowledgeSource(index, "title", event.target.value)}
                              disabled={busy}
                            />
                          </label>
                          <label className="admin-draft-field">
                            <Typography.Text>Citation</Typography.Text>
                            <Input
                              aria-label={`Knowledge citation ${index + 1}`}
                              placeholder="policy@demo"
                              value={source.citation}
                              onChange={(event) => updateKnowledgeSource(index, "citation", event.target.value)}
                              disabled={busy}
                            />
                          </label>
                          <Tooltip title="Remove source">
                            <Button
                              danger
                              type="text"
                              icon={<DeleteOutlined />}
                              aria-label={`Remove knowledge source ${index + 1}`}
                              className="admin-knowledge-source-remove"
                              disabled={busy}
                              onClick={() => removeKnowledgeSource(index)}
                            />
                          </Tooltip>
                        </div>
                      </div>
                    ))}
                  </Flex>
                  <Button onClick={addKnowledgeSource} disabled={busy}>
                    Add knowledge source
                  </Button>
                </section>
                <DraftEditorActions
                  dirty={dirty}
                  busy={busy}
                  publishEligible={publishEligible}
                  onSave={onSave}
                  onPublish={onPublish}
                />
              </section>
            )
          },
          {
            key: "resources",
            label: "Resources",
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
  onSave,
  onPublish
}: {
  dirty: boolean;
  busy: boolean;
  publishEligible: boolean;
  onSave: () => void;
  onPublish: () => void;
}) {
  return (
    <div className="admin-draft-actions">
      <Flex gap={8} wrap="wrap">
        <Button type={dirty ? "primary" : "default"} onClick={onSave} disabled={busy || !dirty}>
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
      {dirty ? (
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
  const [error, setError] = useState<string | null>(null);
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
        setError(loadError instanceof Error ? loadError.message : "Failed to load publication resources.");
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
        title={error}
        action={
          <Button size="small" onClick={reload}>
            Retry
          </Button>
        }
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
  onInstanceChanged
}: {
  instanceId: string;
  instances: LoadState<AdminInstanceInventoryItem[]>;
  effective: LoadState<AdminEffectiveConfiguration>;
  onBack: () => void;
  onRetryEffective: () => void;
  onInstanceChanged: () => void;
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
          action={<Button size="small" onClick={onRetryEffective}>Retry</Button>}
        />
      ) : null}
      {effective.kind === "ready" && !effective.data.compatibility ? (
        <InstanceManagedControls config={effective.data} onUpdated={onInstanceChanged} />
      ) : null}
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
  onUpdated
}: {
  config: AdminEffectiveConfiguration;
  onUpdated: () => void;
}) {
  const { message } = App.useApp();
  const [busy, setBusy] = useState(false);
  const [personaTab, setPersonaTab] = useState("form");
  const [persona, setPersona] = useState<PersonaFields>(() => ({ ...config.persona }));
  const [personaJsonDraft, setPersonaJsonDraft] = useState(() =>
    JSON.stringify(config.persona, null, 2)
  );
  const [personaJsonError, setPersonaJsonError] = useState<string | null>(null);
  const [definitionInventory, setDefinitionInventory] = useState<AdminDefinitionInventoryItem[]>([]);
  const [inventoryError, setInventoryError] = useState<string | null>(null);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [publicationsError, setPublicationsError] = useState<string | null>(null);
  const [targetVersion, setTargetVersion] = useState(config.definitionVersion);

  useEffect(() => {
    setPersona({ ...config.persona });
    setPersonaJsonDraft(JSON.stringify(config.persona, null, 2));
    setPersonaJsonError(null);
    setTargetVersion(config.definitionVersion);
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
        setInventoryError(error instanceof Error ? error.message : "Definition inventory unavailable.");
      });
  }, []);

  useEffect(() => {
    setPublicationsError(null);
    void listAdminDefinitionPublications(config.definitionId)
      .then((items) => setPublications(items))
      .catch((error) => {
        setPublications([]);
        setPublicationsError(error instanceof Error ? error.message : "Publications unavailable.");
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
    const text = error instanceof Error ? error.message : "Update failed.";
    message.error(text);
    if (text.includes("reload")) {
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
            {inventoryError ? <Alert type="warning" showIcon title={inventoryError} /> : null}
            {publicationsError ? <Alert type="warning" showIcon title={publicationsError} /> : null}
            <Flex gap={8} wrap="wrap" align="center" className="admin-instance-version-actions">
              <Select
                aria-label="Target definition version"
                value={targetVersion}
                options={versionOptions}
                onChange={setTargetVersion}
                disabled={busy}
              />
              {personaDirty ? (
                <Popconfirm
                  title="Apply version with unsaved persona?"
                  description={UNSAVED_PERSONA_DISCARD_MESSAGE}
                  onConfirm={() => void applyVersion()}
                  okText="Apply anyway"
                  cancelText="Cancel"
                >
                  <Button disabled={busy || targetVersion === config.definitionVersion}>{versionActionLabel}</Button>
                </Popconfirm>
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
              Archived instances keep history but cannot start new chats or triggered work.
            </Typography.Paragraph>
            <Flex gap={8} wrap="wrap">
              {config.instanceLifecycle === "Active" ? (
                <Popconfirm
                  title="Archive this managed instance?"
                  description={
                    personaDirty
                      ? `New chats and triggered work will stop until you unarchive. ${UNSAVED_PERSONA_DISCARD_MESSAGE}`
                      : "New chats and triggered work will stop until you unarchive."
                  }
                  onConfirm={() => void setLifecycle("Archived")}
                  okText="Archive"
                  cancelText="Cancel"
                >
                  <Button danger disabled={busy}>
                    Archive instance
                  </Button>
                </Popconfirm>
              ) : personaDirty ? (
                <Popconfirm
                  title="Unarchive with unsaved persona?"
                  description={UNSAVED_PERSONA_DISCARD_MESSAGE}
                  onConfirm={() => void setLifecycle("Active")}
                  okText="Unarchive anyway"
                  cancelText="Cancel"
                >
                  <Button disabled={busy}>Unarchive instance</Button>
                </Popconfirm>
              ) : (
                <Button disabled={busy} onClick={() => void setLifecycle("Active")}>
                  Unarchive instance
                </Button>
              )}
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
        className={`admin-effective-config-section${hidePersona ? " admin-effective-config-wide" : ""}`}
        aria-label="Instance identity"
      >
        <Typography.Title level={5}>Instance identity</Typography.Title>
        <InstanceIdentityTags
          compatibility={config.compatibility}
          lifecycle={config.instanceLifecycle}
          definitionStatus={config.definitionStatus}
        />
        <Descriptions bordered size="small" column={1} style={{ marginTop: 12 }}>
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
              ? config.knowledgeSources.map((item) => `${item.identity} (${item.title})`).join("; ")
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
