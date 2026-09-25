import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  App,
  Button,
  Descriptions,
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
  Typography
} from "antd";
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

  return (
    <App className="antd-root" message={{ duration: 3, maxCount: 3 }}>
    <Layout className="admin-layout">
      <Header className="admin-header">
        <Flex align="center" justify="space-between" gap={12} wrap="wrap">
          <Flex align="center" gap={12}>
            <Typography.Title level={3} className="admin-title">
              Admin
            </Typography.Title>
            <Button type="link" onClick={openChat}>
              Chat
            </Button>
          </Flex>
          <Button onClick={returnToChat}>Return to last chat</Button>
        </Flex>
      </Header>
      <Content className="admin-content">
        {route.view === "home" ? (
          <Flex vertical gap={24}>
            <InventorySection
              title="Definitions"
              emptyLabel="No definitions found."
              loading={definitions.kind === "loading"}
              error={definitions.kind === "error" ? definitions.message : null}
              unauthorized={definitions.kind === "error" ? (definitions.unauthorized ?? false) : false}
              onRetry={() => void reloadDefinitions()}
              items={
                definitions.kind === "ready"
                  ? definitions.data.map((item) => ({
                      key: `${item.definitionId}:${item.version}`,
                      title: `${item.displayName} (${item.definitionId} v${item.version})`,
                      description: `Source: ${item.source} · Status: ${item.status}`,
                      onClick: () => navigateToAppPath(adminDefinitionPath(item.definitionId))
                    }))
                  : []
              }
            />
            <InventorySection
              title="Instances"
              emptyLabel="No instances yet. Start a chat to create compatibility instances."
              loading={instances.kind === "loading"}
              error={instances.kind === "error" ? instances.message : null}
              unauthorized={instances.kind === "error" ? (instances.unauthorized ?? false) : false}
              onRetry={() => void reloadInstances()}
              items={
                instances.kind === "ready"
                  ? instances.data.map((item) => ({
                      key: item.instanceId,
                      title: `${item.personaName} · ${item.definitionId} v${item.activeVersion}`,
                      description: item.compatibility ? "Compatibility / legacy instance" : "Managed instance",
                      tag: item.compatibility ? "Compatibility" : "Managed",
                      onClick: () => navigateToAppPath(adminInstancePath(item.instanceId))
                    }))
                  : []
              }
            />
          </Flex>
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
  emptyLabel,
  loading,
  error,
  unauthorized,
  onRetry,
  items
}: {
  title: string;
  emptyLabel: string;
  loading: boolean;
  error: string | null;
  unauthorized: boolean;
  onRetry: () => void;
  items: Array<{ key: string; title: string; description: string; tag?: string; onClick: () => void }>;
}) {
  return (
    <section aria-label={title}>
      <Typography.Title level={4}>{title}</Typography.Title>
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
          style={{ marginBottom: 16 }}
        />
      ) : null}
      {loading ? (
        <Spin aria-label={`Loading ${title}`} />
      ) : error ? null : items.length === 0 ? (
        <Typography.Text type="secondary">{emptyLabel}</Typography.Text>
      ) : (
        <List
          dataSource={items}
          renderItem={(item) => (
            <List.Item>
              <List.Item.Meta
                title={
                  <Button type="link" onClick={item.onClick} style={{ padding: 0, height: "auto" }}>
                    {item.title}
                  </Button>
                }
                description={
                  <Flex gap={8} wrap="wrap" align="center">
                    <span>{item.description}</span>
                    {item.tag ? <Tag>{item.tag}</Tag> : null}
                  </Flex>
                }
              />
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
  const [lifecycleLoading, setLifecycleLoading] = useState(false);
  const [lifecycleError, setLifecycleError] = useState<string | null>(null);
  const [draftSummaries, setDraftSummaries] = useState<AdminDefinitionDraftSummary[]>([]);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [activeDraft, setActiveDraft] = useState<AdminDefinitionDraft | null>(null);
  const [instructions, setInstructions] = useState("");
  const [capabilities, setCapabilities] = useState<DraftEnvironment>(emptyDraftEnvironment());
  const [busy, setBusy] = useState(false);

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

  return (
    <Flex vertical gap={16}>
      <Button onClick={onBack}>Back to inventory</Button>
      <Typography.Title level={4}>{definitionId}</Typography.Title>
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
      {rows.length > 0 ? (
        <Descriptions bordered size="small" column={1}>
          {rows.map((row) => (
            <Descriptions.Item key={`${row.definitionId}:${row.version}`} label={`Version ${row.version}`}>
              {row.displayName} · {row.source} · {row.status}
            </Descriptions.Item>
          ))}
        </Descriptions>
      ) : null}
      {rows.length > 0 ? (
        <section aria-label="Definition drafts">
          <Typography.Title level={5} style={{ margin: 0 }}>Drafts and publish</Typography.Title>
          <Flex gap={8} wrap="wrap" style={{ marginTop: 8 }}>
            {rows.map((row) => (
              <Button key={`${row.definitionId}:${row.version}`} onClick={() => void forkFromVersion(row)} disabled={busy}>
                Fork v{row.version} ({row.source})
              </Button>
            ))}
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
            <List
              style={{ marginTop: 12 }}
              dataSource={draftSummaries}
              renderItem={(item) => (
                <List.Item>
                  <Button type="link" onClick={() => void selectDraft(item.draftId)} disabled={busy}>
                    Draft rev {item.revision} · {item.sourceKind}
                    {item.sourceVersion != null ? ` v${item.sourceVersion}` : ""}
                  </Button>
                </List.Item>
              )}
            />
          ) : null}
          {!lifecycleLoading && draftSummaries.length === 0 ? (
            <Typography.Text type="secondary">No drafts yet. Fork a catalog version to start.</Typography.Text>
          ) : null}
          {activeDraft ? (
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
          ) : null}
          {publications.length > 0 ? (
            <Descriptions bordered size="small" column={1} style={{ marginTop: 16 }} title="Durable publications">
              {publications.map((item) => (
                <Descriptions.Item key={item.version} label={`v${item.version}`}>
                  <Flex vertical gap={8}>
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
        </section>
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
    <Flex vertical gap={12} style={{ marginTop: 16 }}>
      <Typography.Text>
        Editing draft {activeDraft.draftId} (revision {activeDraft.revision})
      </Typography.Text>
      <Tabs
        items={[
          {
            key: "instructions",
            label: "Instructions",
            children: (
              <Flex vertical gap={12}>
                <Input.TextArea
                  aria-label="System instructions"
                  rows={6}
                  value={instructions}
                  onChange={(event) => onInstructionsChange(event.target.value)}
                  disabled={busy}
                />
                <Flex gap={8} wrap="wrap">
                  <Button type="primary" onClick={onSave} disabled={busy}>
                    Save draft
                  </Button>
                  <Button onClick={onPublish} disabled={busy || !publishEligible}>
                    Publish…
                  </Button>
                </Flex>
                {dirty ? (
                  <Typography.Text type="secondary">
                    Unsaved changes — save before using Test &amp; Publish.
                  </Typography.Text>
                ) : !publishEligible ? (
                  <Typography.Text type="secondary">
                    Complete Test &amp; Publish (validate and required evaluations) to enable publish.
                  </Typography.Text>
                ) : null}
              </Flex>
            )
          },
          {
            key: "capabilities",
            label: "Capabilities",
            children: (
              <Flex vertical gap={12} aria-label="Draft capabilities">
                <Typography.Text type="secondary">
                  Typed RoleEnvironment fields — tool names come from the server registry.
                </Typography.Text>
                <label>
                  <Typography.Text>Harness labels</Typography.Text>
                  <Select
                    aria-label="Harness labels"
                    mode="tags"
                    style={{ width: "100%", marginTop: 4 }}
                    value={capabilities.harness}
                    onChange={(values) => onCapabilitiesChange({ ...capabilities, harness: values })}
                    disabled={busy}
                    placeholder="examiner-turn-taking"
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
                <label>
                  <Typography.Text>Tool allowlist</Typography.Text>
                  {toolRegistryLoading ? <Spin size="small" style={{ marginLeft: 8 }} /> : null}
                  <Select
                    aria-label="Tool allowlist"
                    mode="multiple"
                    style={{ width: "100%", marginTop: 4 }}
                    value={capabilities.toolAllowlist}
                    onChange={(values) =>
                      onCapabilitiesChange({ ...capabilities, toolAllowlist: values })
                    }
                    disabled={busy || toolRegistryLoading || toolRegistryError !== null}
                    options={toolNames.map((name) => ({ value: name, label: name }))}
                    placeholder="Select registered tools"
                  />
                </label>
                <Input
                  aria-label="Workspace template id"
                  placeholder="Workspace template id (optional)"
                  value={capabilities.workspaceTemplateId}
                  onChange={(event) =>
                    onCapabilitiesChange({
                      ...capabilities,
                      workspaceTemplateId: event.target.value
                    })
                  }
                  disabled={busy}
                />
                <Flex align="center" gap={8}>
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
                  <Typography.Text>Allow unread unsupported attachment types</Typography.Text>
                </Flex>
                <Typography.Text>Knowledge source references</Typography.Text>
                {capabilities.knowledgeSources.length === 0 ? (
                  <Typography.Text type="secondary">No knowledge sources configured.</Typography.Text>
                ) : null}
                {capabilities.knowledgeSources.map((source, index) => (
                  <Flex key={`knowledge-${index}`} gap={8} wrap="wrap" align="end">
                    <Input
                      aria-label={`Knowledge identity ${index + 1}`}
                      placeholder="identity"
                      value={source.identity}
                      onChange={(event) => updateKnowledgeSource(index, "identity", event.target.value)}
                      disabled={busy}
                      style={{ minWidth: 140, flex: 1 }}
                    />
                    <Input
                      aria-label={`Knowledge title ${index + 1}`}
                      placeholder="title"
                      value={source.title}
                      onChange={(event) => updateKnowledgeSource(index, "title", event.target.value)}
                      disabled={busy}
                      style={{ minWidth: 140, flex: 1 }}
                    />
                    <Input
                      aria-label={`Knowledge citation ${index + 1}`}
                      placeholder="citation"
                      value={source.citation}
                      onChange={(event) => updateKnowledgeSource(index, "citation", event.target.value)}
                      disabled={busy}
                      style={{ minWidth: 140, flex: 1 }}
                    />
                    <Button danger type="link" disabled={busy} onClick={() => removeKnowledgeSource(index)}>
                      Remove
                    </Button>
                  </Flex>
                ))}
                <Button onClick={addKnowledgeSource} disabled={busy}>
                  Add knowledge source
                </Button>
                <Flex gap={8} wrap="wrap">
                  <Button type="primary" onClick={onSave} disabled={busy}>
                    Save draft
                  </Button>
                  <Button onClick={onPublish} disabled={busy || !publishEligible}>
                    Publish…
                  </Button>
                </Flex>
              </Flex>
            )
          },
          {
            key: "resources",
            label: "Resources",
            children: (
              <Flex vertical gap={12} aria-label="Draft resources">
                <Flex gap={8} wrap="wrap" align="end">
                  <Input
                    aria-label="Resource logical path"
                    placeholder="knowledge/policy.md"
                    value={logicalPath}
                    onChange={(event) => setLogicalPath(event.target.value)}
                    disabled={busy}
                    style={{ minWidth: 220, flex: 1 }}
                  />
                  <Select
                    aria-label="Resource kind"
                    value={kind}
                    onChange={setKind}
                    options={RESOURCE_KINDS.map((value) => ({ value, label: value }))}
                    disabled={busy}
                    style={{ minWidth: 140 }}
                  />
                  <input
                    aria-label="Resource file"
                    type="file"
                    disabled={busy}
                    onChange={(event) => setPendingFile(event.target.files?.[0] ?? null)}
                  />
                  <Button onClick={() => void addResource()} disabled={busy}>
                    Upload and bind
                  </Button>
                </Flex>
                {resourcesLoading ? <Spin /> : null}
                {!resourcesLoading && resources.length === 0 ? (
                  <Typography.Text type="secondary">No draft resources yet.</Typography.Text>
                ) : null}
                {!resourcesLoading && resources.length > 0 ? (
                  <List
                    dataSource={resources}
                    renderItem={(item) => (
                      <List.Item
                        actions={[
                          <Button
                            key="remove"
                            type="link"
                            danger
                            disabled={busy}
                            onClick={() => void removeResource(item)}
                          >
                            Remove
                          </Button>
                        ]}
                      >
                        <List.Item.Meta
                          title={`${item.logicalPath} · ${item.kind}`}
                          description={`${item.byteLength} bytes · ${item.contentSha256.slice(0, 12)}…`}
                        />
                      </List.Item>
                    )}
                  />
                ) : null}
              </Flex>
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

  const headerIdentity =
    effective.kind === "ready" || !row
      ? null
      : { compatibility: row.compatibility, lifecycle: row.lifecycle, definitionStatus: undefined as string | undefined };

  return (
    <Flex vertical gap={16}>
      <Button onClick={onBack}>Back to inventory</Button>
      <Typography.Title level={4}>Instance {instanceId}</Typography.Title>
      {headerIdentity ? <InstanceIdentityTags {...headerIdentity} /> : null}
      {effective.kind === "loading" ? <Spin aria-label="Loading effective configuration" /> : null}
      {effective.kind === "error" ? (
        <Alert
          type={effective.unauthorized ? "warning" : "error"}
          showIcon
          message={effective.message}
          action={<Button size="small" onClick={onRetryEffective}>Retry</Button>}
        />
      ) : null}
      {effective.kind === "ready" && !effective.data.compatibility ? (
        <InstanceManagedControls config={effective.data} onUpdated={onInstanceChanged} />
      ) : null}
      {effective.kind === "ready" && !effective.data.compatibility ? (
        <InstanceMemoryAutomationPanel config={effective.data} />
      ) : null}
      {effective.kind === "ready" ? (
        <EffectiveConfigView config={effective.data} hidePersona={!effective.data.compatibility} />
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
    <Flex vertical gap={16} aria-label="Managed instance controls">
      <section aria-label="Persona editor">
        <Typography.Title level={5}>Persona</Typography.Title>
        <Typography.Text type="secondary">
          Revisions {config.instanceRevision} / persona {config.personaRevision}
        </Typography.Text>
        <Tabs
          activeKey={personaTab}
          onChange={handlePersonaTabChange}
          style={{ marginTop: 8 }}
          items={[
            {
              key: "form",
              label: "Form",
              children: (
                <Form layout="vertical" disabled={busy}>
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
        <Button type="primary" onClick={() => void savePersona()} disabled={busy} style={{ marginTop: 8 }}>
          Save persona
        </Button>
        {personaDirty ? (
          <Typography.Text type="secondary" style={{ display: "block", marginTop: 8 }}>
            Unsaved persona edits — save before other changes, or confirm discard on archive, unarchive, or version
            apply.
          </Typography.Text>
        ) : null}
      </section>
      <section aria-label="Lifecycle controls">
        <Typography.Title level={5}>Lifecycle</Typography.Title>
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
      <section aria-label="Active version">
        <Typography.Title level={5}>Active version</Typography.Title>
        {inventoryError ? (
          <Alert type="warning" showIcon message={inventoryError} style={{ marginBottom: 8 }} />
        ) : null}
        {publicationsError ? (
          <Alert type="warning" showIcon message={publicationsError} style={{ marginBottom: 8 }} />
        ) : null}
        <Flex gap={8} wrap="wrap" align="center">
          <Select
            aria-label="Target definition version"
            style={{ minWidth: 200 }}
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
    </Flex>
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
    <Flex vertical gap={20}>
      <section aria-label="Instance identity">
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
        <section aria-label="Persona">
          <Typography.Title level={5}>Persona</Typography.Title>
          <Descriptions bordered size="small" column={1}>
            <Descriptions.Item label="Name">{config.persona.name}</Descriptions.Item>
            <Descriptions.Item label="Role">{config.persona.role}</Descriptions.Item>
            <Descriptions.Item label="Description">{config.persona.description}</Descriptions.Item>
            <Descriptions.Item label="Tone">{config.persona.tone}</Descriptions.Item>
          </Descriptions>
        </section>
      )}
      <section aria-label="Runtime model">
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
      <section aria-label="Tools and resources">
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
      <section aria-label="Memory policy">
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
      <section aria-label="Automation">
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
    </Flex>
  );
}
