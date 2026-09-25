import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, App, Button, Descriptions, Flex, Input, Layout, List, Result, Spin, Tag, Typography } from "antd";
import {
  type AdminDefinitionDraft,
  type AdminDefinitionDraftSummary,
  type AdminDefinitionInventoryItem,
  type AdminDefinitionPublicationSummary,
  type AdminEffectiveConfiguration,
  type AdminInstanceInventoryItem,
  forkAdminDefinitionDraft,
  getAdminDefinitionDraft,
  getAdminEffectiveConfig,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminInstances,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft
} from "../../services/adminApi";
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
            onRetryDefinitions={() => void reloadDefinitions()}
          />
        ) : null}

        {route.view === "instance" ? (
          <InstanceDetail
            instanceId={route.instanceId}
            instances={instances}
            effective={effectiveConfig}
            onBack={() => navigateToAppPath(adminHomePath())}
            onRetryEffective={() => void reloadEffectiveConfig(route.instanceId)}
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
  onRetryDefinitions: () => void;
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
  const [busy, setBusy] = useState(false);

  const savedInstructions = useMemo(() => {
    if (!activeDraft) {
      return "";
    }
    return (activeDraft.candidate as { systemInstructions?: string }).systemInstructions ?? "";
  }, [activeDraft]);

  const dirty = activeDraft !== null && instructions !== savedInstructions;

  const buildCandidate = useCallback(() => {
    if (!activeDraft) {
      return {};
    }
    return { ...activeDraft.candidate, systemInstructions: instructions };
  }, [activeDraft, instructions]);

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
          }
          const publication = await publishAdminDefinitionDraft(draft.draftId, draft.revision);
          message.success(`Published version ${publication.version}.`);
          setActiveDraft(null);
          setInstructions("");
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
            <Flex vertical gap={12} style={{ marginTop: 16 }}>
              <Typography.Text>
                Editing draft {activeDraft.draftId} (revision {activeDraft.revision})
              </Typography.Text>
              <Input.TextArea
                aria-label="System instructions"
                rows={6}
                value={instructions}
                onChange={(event) => setInstructions(event.target.value)}
                disabled={busy}
              />
              <Flex gap={8} wrap="wrap">
                <Button type="primary" onClick={() => void saveDraft()} disabled={busy}>
                  Save draft
                </Button>
                <Button onClick={() => void confirmPublish()} disabled={busy || !activeDraft}>
                  Publish…
                </Button>
              </Flex>
              {dirty ? (
                <Typography.Text type="secondary">Unsaved changes — publish will save the visible instructions first.</Typography.Text>
              ) : null}
            </Flex>
          ) : null}
          {publications.length > 0 ? (
            <Descriptions bordered size="small" column={1} style={{ marginTop: 16 }} title="Durable publications">
              {publications.map((item) => (
                <Descriptions.Item key={item.version} label={`v${item.version}`}>
                  {item.status} · metadata rev {item.metadataRevision} · {item.publishedAt}
                </Descriptions.Item>
              ))}
            </Descriptions>
          ) : null}
        </section>
      ) : null}
    </Flex>
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
  onRetryEffective
}: {
  instanceId: string;
  instances: LoadState<AdminInstanceInventoryItem[]>;
  effective: LoadState<AdminEffectiveConfiguration>;
  onBack: () => void;
  onRetryEffective: () => void;
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
      {effective.kind === "ready" ? <EffectiveConfigView config={effective.data} /> : null}
    </Flex>
  );
}

export function EffectiveConfigView({ config }: { config: AdminEffectiveConfiguration }) {
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
      <section aria-label="Persona">
        <Typography.Title level={5}>Persona</Typography.Title>
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Name">{config.persona.name}</Descriptions.Item>
          <Descriptions.Item label="Role">{config.persona.role}</Descriptions.Item>
          <Descriptions.Item label="Description">{config.persona.description}</Descriptions.Item>
          <Descriptions.Item label="Tone">{config.persona.tone}</Descriptions.Item>
        </Descriptions>
      </section>
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
