import { DeleteOutlined, InboxOutlined } from "@ant-design/icons";
import { Alert, App, Button, Empty, Flex, Input, Select, Spin, Switch, Table, Tag, Tooltip, Typography, Upload, theme } from "antd";
import { useCallback, useEffect, useRef, useState, type Ref } from "react";
import { followAppLink } from "../../app/appLink";
import { adminDraftPath } from "../../app/appRoute";
import { confirmAction } from "../../app/confirmAction";
import { getAdminDefinitionDraft, getAdminToolRegistry, listAdminDraftResources, removeAdminDraftResource, uploadAdminDraftResourceContent, upsertAdminDraftResource, type AdminDefinitionDraft, type AdminDefinitionDraftResource } from "../../services/adminApi";
import { describeAdminError, reportAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { type DraftPage } from './adminNavigation';
import { AgentConfigurationPanel, AgentSkillsResourcesSections } from "./AgentConfigurationLayout";
import { groupedCapabilityOptions, reconcileAlwaysCapabilities } from "./capabilityOptions";
import { automationNumberErrors, type DefinitionCandidate } from "./definitionCandidate";
import { DefinitionCandidateEditor, type DefinitionEditorView } from "./definitionCandidateEditor";
import { DefinitionDraftPublishGatePanel, type DefinitionDraftEvidenceHandle } from "./definitionDraftPublishGatePanel";
import { DefinitionSkillsSection } from "./DefinitionSkillsSection";
import { applyDraftEnvironmentToCandidate, readDraftEnvironment, type DraftEnvironment, type DraftKnowledgeSource } from "./draftEnvironment";
import { useExecutionBudgetLimits } from "./executionBudgetLimits";
import { validExecutionBudgets } from "./ExecutionBudgetsSection";
import { ResourceImportPanel } from "./resourceImportPanel";
import { RESOURCE_FILE_HELP, resourceBatchLimitProblem, resourceMediaType, resourcePreviewProblem } from "./resourcePreview";
import { ResourceDeletionContent, ResourceSelectionToolbar } from "./ResourceSelectionToolbar";
import { RetainedAdminPage } from './RetainedAdminPage';

import { adminCollectionPagination } from "./AdminCollectionToolbar";

import { formatAdminTimestamp, formatDraftSource } from './adminPresentation';
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

export function DefinitionDraftWorkspace({
  activeDraft,
  page,
  evidenceRef,
  candidate,
  jsonText,
  jsonError,
  editorView,
  dirty,
  busy: ownerBusy,
  onCandidateChange,
  onJsonTextChange,
  onEditorViewChange,
  onSave,
  onPublish,
  onDelete,
  onDraftRevisionChange,
  onError,
  onTransientDirtyChange
}: {
  onTransientDirtyChange?: (pages: readonly string[]) => void;
  evidenceRef: Ref<DefinitionDraftEvidenceHandle>;
  activeDraft: AdminDefinitionDraft;
  page: DraftPage;
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
  const { limits: budgetLimits } = useExecutionBudgetLimits();
  const capabilities = readDraftEnvironment(candidate);
  const candidateLocked = jsonError !== null;
  const onCapabilitiesChange = (next: DraftEnvironment) => {
    if (candidateLocked) {
      return;
    }
    onCandidateChange(applyDraftEnvironmentToCandidate(candidate, { ...next, capabilityMode: next.capabilityMode ?? "Selected",
      alwaysCapabilities: reconcileAlwaysCapabilities(next.alwaysCapabilities ?? authorizedNames, next.toolAllowlist, capabilityCatalog) }));
  };

  const { message, modal } = App.useApp();
  const [resourceBusy, setResourceBusy] = useState(false);
  const busy = ownerBusy || resourceBusy;
  const [selectedResources, setSelectedResources] = useState<React.Key[]>([]);
  const resourceOwner = useRef(activeDraft.draftId);
  const resourceReadEpoch = useRef(0);
  resourceOwner.current = activeDraft.draftId;
  useEffect(() => { setSelectedResources([]); }, [activeDraft.draftId, activeDraft.revision]);
  useEffect(() => { setResourceBusy(false); setResourceDeletionError(null); setResourceRevisionKnown(true); }, [activeDraft.draftId]);
  useEffect(() => { resourceOwner.current = activeDraft.draftId; return () => { resourceOwner.current = ""; resourceReadEpoch.current++; }; }, [activeDraft.draftId]);
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
  const { token } = theme.useToken();
  // Migrate an editable legacy candidate using its exact grants and fixed discoverable projection.
  // Published versions remain immutable; Core still owns context-only projection.
  useEffect(() => {
    if (catalogReady && !candidateLocked && editorView === "form" && ["profile", "behavior", "models", "policies", "capabilities"].includes(page) && !capabilities.capabilityMode) {
      onCandidateChange(current => {
        const environment = readDraftEnvironment(current);
        return environment.capabilityMode ? current : applyDraftEnvironmentToCandidate(current, { ...environment, capabilityMode: "Selected",
          alwaysCapabilities: reconcileAlwaysCapabilities(environment.toolAllowlist, environment.toolAllowlist, capabilityCatalog) });
      });
    }
  }, [catalogReady, candidateLocked, editorView, page, capabilities.capabilityMode, capabilityCatalog, onCandidateChange]);
  const numberErrors = automationNumberErrors(candidate);
  const budgetsValid = validExecutionBudgets((candidate.executionBudgets ?? {}) as import("../../services/adminApi").ExecutionBudgetPolicy, budgetLimits);
  const saveBlocked = candidateLocked || !budgetsValid || numberErrors.length > 0 || (editorView === "form" && !catalogReady);
  const saveBlockedMessage = candidateLocked
    ? "Advanced JSON is invalid — fix it before saving. This does not change the draft revision."
    : !budgetsValid ? budgetLimits
      ? "Review Execution budgets in Models & execution. Values must be whole numbers within the host limits."
      : "Execution limits are unavailable. Retry Execution budgets in Models & execution before saving budget values."
    : numberErrors.length > 0
      ? `Review Trigger restrictions in Policies. ${numberErrors[0].message}`
      : toolRegistryError
        ? "The capability catalog is unavailable. Open Capabilities and choose Retry tool registry, or use Advanced JSON."
        : "The capability catalog is loading. Wait before saving or use Advanced JSON.";
  const [resources, setResources] = useState<AdminDefinitionDraftResource[]>([]);
  const [resourcesLoading, setResourcesLoading] = useState(false);
  const [resourcesAvailable, setResourcesAvailable] = useState(false);
  const [resourceRevisionKnown, setResourceRevisionKnown] = useState(true);
  const [resourceDeletionError, setResourceDeletionError] = useState<string | null>(null);
  const [skillDirty, setSkillDirty] = useState(false);
  const [importDirty, setImportDirty] = useState(false);
  const [logicalPath, setLogicalPath] = useState("");
  const [kind, setKind] = useState<(typeof RESOURCE_KINDS)[number]>("Reference");
  const [pendingFile, setPendingFile] = useState<File | null>(null);
  const resourcesReady = resourcesAvailable && resourceRevisionKnown;
  useEffect(() => { onTransientDirtyChange?.([...(skillDirty ? ['skills'] : []), ...(importDirty || !!logicalPath.trim() || !!pendingFile ? ['resources'] : [])]); }, [skillDirty, importDirty, logicalPath, pendingFile, onTransientDirtyChange]);
  const pendingEditorPage = skillDirty ? 'skills' : importDirty || !!logicalPath.trim() || !!pendingFile ? 'resources' : undefined;
  const pendingResource = pendingFile ? { logicalPath, kind, byteLength: pendingFile.size, mediaType: resourceMediaType(pendingFile, logicalPath) } : null;
  const pendingResourceProblem = pendingResource
    ? resourcePreviewProblem(pendingResource, [logicalPath], resources.map(item => item.logicalPath))
      ?? resourceBatchLimitProblem([pendingResource], resources.length, resources.reduce((sum, item) => sum + item.byteLength, 0))
    : null;
  const [publishEligible, setPublishEligible] = useState(false);

  useEffect(() => {
    setPublishEligible(false);
  }, [activeDraft.draftId, activeDraft.revision]);

  const reloadResources = useCallback(async (signal?: AbortSignal) => {
    const request = ++resourceReadEpoch.current;
    setResourcesLoading(true);
    setResourcesAvailable(false);
    setSelectedResources([]);
    onError(null);
    try {
      const items = await listAdminDraftResources(activeDraft.draftId, signal);
      if (signal?.aborted || request !== resourceReadEpoch.current || resourceOwner.current !== activeDraft.draftId) return;
      setResources(items);
      setResourcesAvailable(true);
      return true;
    } catch (error) {
      if (!signal?.aborted && request === resourceReadEpoch.current && resourceOwner.current === activeDraft.draftId) {
        reportAdminError(onError, error, "Failed to load resources.");
        setResourceDeletionError(describeAdminError(error, "Failed to load resources. Reload before changing them.").message);
      }
      return false;
    } finally {
      if (request === resourceReadEpoch.current && resourceOwner.current === activeDraft.draftId) setResourcesLoading(false);
    }
  }, [activeDraft.draftId, onError]);

  useEffect(() => {
    if (busy) return;
    const controller = new AbortController();
    void reloadResources(controller.signal);
    return () => controller.abort();
  }, [reloadResources, activeDraft.revision, busy]);

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
    if (resourceOwner.current !== activeDraft.draftId) return;
    setResourceRevisionKnown(false);
    setResourcesAvailable(false);
    setSelectedResources([]);
    const draft = await getAdminDefinitionDraft(activeDraft.draftId);
    if (resourceOwner.current !== activeDraft.draftId) return;
    onDraftRevisionChange(draft, { preserveLocalEdits: dirty });
    setResourceRevisionKnown(true);
    if (await reloadResources() === false && resourceOwner.current === activeDraft.draftId) {
      throw new Error("Unable to reload resources. Retry before changing them.");
    }
  };

  const retryResourceRead = async () => {
    if (busy || resourcesLoading) return;
    setResourceBusy(true);
    try { await refreshDraft(); if (resourceOwner.current === activeDraft.draftId) setResourceDeletionError(null); }
    catch (error) {
      if (resourceOwner.current !== activeDraft.draftId) return;
      const notice = describeAdminError(error, "Unable to reload resources. Retry before changing them.");
      setResourceDeletionError(notice.message);
      onError(notice.message, notice.diagnosticId ?? null);
    } finally { if (resourceOwner.current === activeDraft.draftId) setResourceBusy(false); }
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
    if (busy || resourcesLoading || !resourcesReady || !pendingFile || !pendingResource?.mediaType || pendingResourceProblem) return;
    const draftId = activeDraft.draftId;
    setResourceBusy(true);
    setResourceDeletionError(null);
    onError(null);
    try {
      const stored = await uploadAdminDraftResourceContent(
        draftId,
        pendingFile,
        pendingResource.mediaType
      );
      if (resourceOwner.current !== draftId) return;
      await upsertAdminDraftResource(
        draftId,
        activeDraft.revision,
        logicalPath.trim(),
        kind,
        stored
      );
      if (resourceOwner.current !== draftId) return;
      message.success("Resource bound.");
      setPendingFile(null);
      setLogicalPath("");
      await refreshDraft();
    } catch (error) {
      if (resourceOwner.current !== draftId) return;
      const notice = showAdminFailure(message, error, "Resource upload failed.");
      onError(notice.message, notice.diagnosticId ?? null);
      setResourceDeletionError(notice.message);
    } finally { if (resourceOwner.current === draftId) setResourceBusy(false); }
  };

  const removeResources = async (items: AdminDefinitionDraftResource[]) => {
    if (busy || resourcesLoading || !resourcesReady || resourceOwner.current !== activeDraft.draftId) return;
    const draftId = activeDraft.draftId;
    let revision = activeDraft.revision;
    let deleted = 0;
    setResourceBusy(true);
    setResourceDeletionError(null);
    onError(null);
    let failure: unknown;
    try {
      for (const item of items) {
        if (resourceOwner.current !== draftId) break;
        await removeAdminDraftResource(draftId, item.resourceId, revision);
        revision += 1;
        deleted += 1;
      }
    } catch (error) { failure = error; }
    finally {
      if (resourceOwner.current === draftId) {
        setSelectedResources([]);
        try { await refreshDraft(); }
        catch (error) { failure ??= error; }
        setResourceBusy(false);
        if (failure) {
          const notice = showAdminFailure(message, failure, "Resource deletion stopped. Reload before retrying.");
          setResourceDeletionError(`${deleted} of ${items.length} resources deleted. ${notice.message}`);
          onError(`${deleted} of ${items.length} resources deleted. ${notice.message}`, notice.diagnosticId ?? null);
        } else message.success(`${deleted} resource${deleted === 1 ? '' : 's'} deleted.`);
      }
    }
  };
  const confirmResourceDeletion = (items: AdminDefinitionDraftResource[]) => confirmAction(modal, {
    title: `Delete ${items.length} draft resource${items.length === 1 ? '' : 's'}?`,
    content: <ResourceDeletionContent notice="Removes these bindings from this draft. Published versions keep their content. Deletion stops if the draft changes." paths={items.map(item => item.logicalPath)} />,
    okText: 'Delete resources', danger: true, onOk: () => removeResources(items)
  });

  return (
    <Flex vertical gap={12} className="admin-draft-editor">
      <Flex vertical gap={token.paddingXS} className="admin-draft-editor-heading">
        <Flex align="baseline" wrap gap={token.paddingXS}>
          <Typography.Title level={5}>
            {activeDraft.sourceVersion != null
              ? `Editing draft from v${activeDraft.sourceVersion}`
              : "Editing draft"}
          </Typography.Title>
          <Typography.Text
            type="secondary"
            copyable={{ text: activeDraft.draftId, tooltips: ["Copy draft ID", "Copied"] }}
            className="admin-draft-id"
          >
            ID {activeDraft.draftId.slice(0, 8)}…
          </Typography.Text>
        </Flex>
        <Typography.Text type="secondary">
          Revision {activeDraft.revision} · Updated {formatAdminTimestamp(activeDraft.updatedAt)}
        </Typography.Text>
      </Flex>
      <DraftEditorActions
        dirty={dirty} busy={busy} publishEligible={publishEligible} saveBlocked={saveBlocked} saveBlockedMessage={saveBlockedMessage}
        saveBlockedHref={adminDraftPath(activeDraft.definitionId, activeDraft.draftId, candidateLocked ? 'advanced-json' : !budgetsValid ? 'models' : numberErrors.length ? 'policies' : 'capabilities')}
        onSave={onSave} onPublish={onPublish} onDelete={onDelete}
      />
      {page === 'overview' && <AgentConfigurationPanel title="Draft status" label="Draft overview" bodyGap="section"><Typography.Text>{activeDraft.definitionId} · revision {activeDraft.revision} · {formatDraftSource(activeDraft.sourceKind)}</Typography.Text><Typography.Text>{dirty ? 'Unsaved candidate changes retained across draft pages' : 'Saved candidate'}</Typography.Text><Typography.Text type="secondary">Published versions remain immutable. Save, validate and evaluate this exact revision before publishing.</Typography.Text><a href={adminDraftPath(activeDraft.definitionId, activeDraft.draftId, 'test-publish')} onClick={followAppLink}>Open Test & Publish</a></AgentConfigurationPanel>}
      <RetainedAdminPage active={['profile', 'behavior', 'models', 'policies', 'advanced-json'].includes(page)}>

              <section className="admin-draft-tab" aria-label="Definition candidate">
                <div className="admin-draft-tab-intro">
                  <Typography.Paragraph type="secondary">
                    Form and Advanced JSON edit the same unsaved candidate. Saving from either view uses this draft revision.
                  </Typography.Paragraph>
                </div>
                <DefinitionCandidateEditor
                  page={page}
                  candidate={candidate}
                  view={editorView}
                  jsonText={jsonText}
                  busy={busy || candidateLocked && page !== 'advanced-json'}
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
      </RetainedAdminPage>
      <RetainedAdminPage active={page === 'skills' || page === 'resources'}>
              <AgentSkillsResourcesSections section={page}
                skills={<Flex vertical gap={token.padding}>
                  {candidateLocked && <Alert type="error" showIcon title="Advanced JSON is invalid" description="Fix Advanced JSON in Advanced JSON before changing Skills. Your invalid text is retained." />}
                  <DefinitionSkillsSection active={page === "skills"} onDirtyChange={setSkillDirty} candidate={candidate} busy={busy || candidateLocked} readOnly={false} onChange={onCandidateChange} />
                </Flex>}
                resources={(
              <AgentConfigurationPanel title="Resources" label="Draft resources" description="Upload immutable source material and bind it to a safe path in the published definition." bodyClassName="admin-draft-tab">
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
                  <Typography.Paragraph type="secondary" style={{ margin: 0 }}>{RESOURCE_FILE_HELP}</Typography.Paragraph>
                  {pendingResourceProblem && <Alert type="error" showIcon title={pendingResourceProblem} />}
                  <Button
                    type="primary"
                    aria-label="Upload and bind"
                    onClick={() => void addResource()}
                    disabled={busy || resourcesLoading || !resourcesReady || !pendingFile || !!pendingResourceProblem}
                  >
                    Upload &amp; bind
                  </Button>
                </section>
                <ResourceImportPanel
                  key={activeDraft.draftId}
                  onDirtyChange={setImportDirty}
                  draftId={activeDraft.draftId}
                  expectedRevision={activeDraft.revision}
                  existingPaths={resources.map((item) => item.logicalPath)}
                  existingCount={resources.length}
                  existingBytes={resources.reduce((sum, item) => sum + item.byteLength, 0)}
                  disabled={busy || resourcesLoading || !resourcesReady}
                  onBusyChange={value => { if (resourceOwner.current === activeDraft.draftId) setResourceBusy(value); }}
                  onBound={refreshDraft}
                  onError={onError}
                />
                {resourceDeletionError && <Alert type="error" showIcon title={resourceDeletionError} action={<Button disabled={busy || resourcesLoading} onClick={() => void retryResourceRead()}>Reload resources</Button>} />}
                <ResourceSelectionToolbar count={selectedResources.length} disabled={busy || resourcesLoading || !resourcesReady} busy={resourceBusy}
                  onClear={() => setSelectedResources([])} onDelete={() => confirmResourceDeletion(resources.filter(item => selectedResources.includes(item.resourceId)))} />
                {resourcesLoading ? <Spin /> : null}
                {!resourcesLoading && resources.length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No resources bound to this draft." />
                ) : null}
                {!resourcesLoading && resources.length > 0 ? (
                  <Table<AdminDefinitionDraftResource>
                    className="admin-collection-table" size="small" rowKey="resourceId" tableLayout="fixed"
                    scroll={{ x: 1200 }} pagination={adminCollectionPagination} dataSource={resources}
                    rowSelection={{ selectedRowKeys: selectedResources, onChange: setSelectedResources,
                      getCheckboxProps: item => ({ disabled: busy || resourcesLoading || !resourcesReady, 'aria-label': `Select resource ${item.logicalPath}` }) }}
                    columns={[
                      { title: 'Resource path', dataIndex: 'logicalPath', width: 300, ellipsis: true, render: (path: string) => <Typography.Text strong title={path}>{path}</Typography.Text> },
                      { title: 'Kind', dataIndex: 'kind', width: 130 },
                      { title: 'Media type', dataIndex: 'mediaType', width: 240 },
                      { title: 'Size (bytes)', dataIndex: 'byteLength', width: 130, align: 'right', render: (bytes: number) => bytes.toLocaleString() },
                      { title: 'Content hash', dataIndex: 'contentSha256', width: 200, ellipsis: true, render: (hash: string) => <Typography.Text type="secondary" title={hash}>{hash.slice(0, 12)}…</Typography.Text> },
                      { title: 'Actions', width: 160, render: (_, item) => <Button type="text" danger aria-label="Remove" disabled={busy || resourcesLoading || !resourcesReady} onClick={() => confirmResourceDeletion([item])}>Remove resource</Button> }
                    ]}
                  />
                ) : null}
              </AgentConfigurationPanel>
                )}
              />
      </RetainedAdminPage>
      <RetainedAdminPage active={page === 'capabilities'}>
              <AgentConfigurationPanel id="capabilities" title="Capabilities" label="Draft capabilities" description="Choose the tools, workspace behavior, and knowledge references available to this definition." bodyClassName="admin-draft-tab">
                {candidateLocked ? (
                  <Alert
                    type="error"
                    showIcon
                    title="Advanced JSON is invalid"
                    description="Fix Advanced JSON before changing capabilities. The invalid text stays in place."
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
                  <Flex align="baseline" className="admin-draft-section-heading">
                    <Typography.Title level={5}>Knowledge sources</Typography.Title>
                    <Typography.Text type="secondary" aria-label={`${capabilities.knowledgeSources.length} knowledge sources`}>({capabilities.knowledgeSources.length})</Typography.Text>
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
              </AgentConfigurationPanel>
      </RetainedAdminPage>
      <RetainedAdminPage active={page === 'test-publish'}>
              <DefinitionDraftPublishGatePanel
                evidenceRef={evidenceRef}
                key={activeDraft.draftId}
                activeDraft={activeDraft}
                dirty={dirty}
                dirtyReason={pendingEditorPage ? `Finish or discard unsaved ${pendingEditorPage === "skills" ? "Skill" : "resource"} input and save any draft edits before validating or publishing.` : undefined}
                dirtyHref={pendingEditorPage ? adminDraftPath(activeDraft.definitionId, activeDraft.draftId, pendingEditorPage) : undefined}
                busy={busy}
                toolNames={capabilities.capabilityMode ? allCapabilityNames : toolNames}
                onDraftRevisionChange={(draft) => onDraftRevisionChange(draft)}
                onError={onError}
                onEligibilityChange={setPublishEligible}
              />
      </RetainedAdminPage>
    </Flex>
  );
}

function DraftEditorActions({
  dirty,
  busy,
  publishEligible,
  saveBlocked = false,
  saveBlockedMessage,
  saveBlockedHref,
  onSave,
  onPublish,
  onDelete
}: {
  dirty: boolean;
  busy: boolean;
  publishEligible: boolean;
  saveBlocked?: boolean;
  saveBlockedMessage?: string;
  saveBlockedHref?: string;
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
          {saveBlockedMessage} {saveBlockedHref && <a href={saveBlockedHref} onClick={followAppLink}>Review blocking settings</a>}
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
