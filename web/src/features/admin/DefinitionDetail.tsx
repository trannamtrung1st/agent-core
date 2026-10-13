import { DeleteOutlined } from "@ant-design/icons";
import { Alert, App, Button, Empty, Flex, Result, Select, Spin, Table, Tag, Typography, theme } from "antd";
import { useCallback, useEffect, useRef, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { adminDefinitionPath, adminDraftPath, navigateToAppPath, type AdminDefinitionTab } from "../../app/appRoute";
import { confirmAction } from "../../app/confirmAction";
import { deleteAdminDefinition, deleteAdminDefinitionDraft, deprecateAdminDefinitionPublication, forkAdminDefinitionDraft, getAdminDefinitionDraft, listAdminDefinitionDrafts, listAdminDefinitionPublications, publishAdminDefinitionDraft, updateAdminDefinitionDraft, type AdminDefinitionDraft, type AdminDefinitionDraftSummary, type AdminDefinitionInventoryItem, type AdminDefinitionPublicationSummary } from "../../services/adminApi";
import { AdminDeletionBlockedAlert } from "./adminDeletionBlocked";
import { useAdminDraftStatus } from './AdminDraftStatus';
import { describeAdminError } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { startManagedPublicationChat } from "./adminManagedChat";
import { definitionPages, draftPages, type DraftPage } from './adminNavigation';
import { AdminOwnerHeading, AdminPageHeading } from './AdminShell';
import { applyCandidateJson, automationNumberErrors, candidateForPersistence, candidateToJson, candidatesEqual, cloneCandidate, type DefinitionCandidate } from "./definitionCandidate";
import { type DefinitionEditorView } from "./definitionCandidateEditor";
import { type DefinitionDraftEvidenceHandle } from "./definitionDraftPublishGatePanel";
import { useExecutionBudgetLimits } from "./executionBudgetLimits";
import { validExecutionBudgets } from "./ExecutionBudgetsSection";
import { RetainedAdminPage } from './RetainedAdminPage';
import { useAdminAnchor } from './useAdminAnchor';
import { useAdminUnsavedChanges } from './useAdminUnsavedChanges';

import { adminCollectionPagination } from "./AdminCollectionToolbar";
import { DefinitionVersionsTable } from "./DefinitionVersionsTable";

import { defaultForkSourceVersion, forkSourceKey, formatAdminTimestamp, formatDefinitionInventoryCounts, formatDefinitionVersionSummary, formatDraftSource, formatForkSourceOptionLabel, formatInventorySource, formatInventoryStatus, groupDefinitionInventory, latestActiveVersion, type LoadState } from './adminPresentation';
import { PublicationResourcesSummary } from './AdminRecordSummaries';
import { DefinitionDraftWorkspace } from './DefinitionDraftWorkspace';
export function DefinitionDetail({
  definitionId,
  tab,
  draftId,
  draftPage,
  definitions,
  onRetryDefinitions,
  onDeleted
}: {
  definitionId: string;
  tab?: AdminDefinitionTab;
  draftId?: string;
  draftPage?: DraftPage;
  definitions: LoadState<AdminDefinitionInventoryItem[]>;
  onBack: () => void;
  onRetryDefinitions: () => Promise<void>;
  onDeleted: () => void;
}) {
  const { message, modal } = App.useApp();
  const { token } = theme.useToken();
  const { limits: budgetLimits } = useExecutionBudgetLimits();
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
  // Catalog refreshes must not erase the exact draft's read outcome.
  const [draftReadError, setDraftReadError] = useState<string | null>(null);
  const setLifecycleError = useCallback((message: string | null, diagnosticId?: string | null) => {
    setLifecycleErrorValue(message);
    setLifecycleDiagnosticId(message ? diagnosticId ?? null : null);
  }, []);
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
  useEffect(() => () => { ++draftRead.current.sequence; }, []);
  const editorSurfaceRef = useRef<HTMLElement | null>(null);
  const draftEvidenceRef = useRef<DefinitionDraftEvidenceHandle | null>(null);

  const [transientDirtyPages, setTransientDirtyPages] = useState<readonly string[]>([]);
  const dirtyCandidate = activeDraft !== null && !candidatesEqual(candidate, activeDraft.candidate);
  const candidateAreas: Record<string, readonly string[]> = { profile: ['identity', 'goals'], behavior: ['systemInstructions', 'conversationPolicy', 'behaviorPolicy', 'initiativePolicy', 'voice'], models: ['modelDefaults', 'providerPreferences', 'executionBudgets'], skills: ['skills'], capabilities: ['environment'], policies: ['memoryPolicy', 'triggerPolicy', 'selfManagement', 'metadata'] };
  useAdminDraftStatus(activeDraft ? [...Object.entries(candidateAreas).filter(([, fields]) => fields.some(field => JSON.stringify(candidate[field]) !== JSON.stringify(activeDraft.candidate[field]))).map(([page]) => page), ...(jsonError ? ['advanced-json'] : []), ...transientDirtyPages] : []);
  const dirty = activeDraft !== null && (dirtyCandidate || jsonError !== null || transientDirtyPages.length > 0);
  const releaseConsumedDraft = useAdminUnsavedChanges(`draft:${definitionId}:${activeDraft?.draftId ?? draftId ?? ''}`, dirty);
  useAdminAnchor(draftPage ?? tab ?? 'versions', !!activeDraft);

  const loadCandidate = useCallback((next: DefinitionCandidate) => {
    const cloned = cloneCandidate(next);
    setCandidate(cloned);
    setJsonText(candidateToJson(cloned));
    setJsonError(null);
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

  const selectDraft = useCallback(async (draftId: string, updatePath = true) => {
    const sequence = ++draftRead.current.sequence;
    draftRead.current.pending = true;
    setBusy(true);
    setLifecycleError(null);
    setDraftReadError(null);
    try {
      const draft = await getAdminDefinitionDraft(draftId);
      if (sequence !== draftRead.current.sequence) return;
      // This read is complete; its own tab navigation must not cancel it.
      draftRead.current.pending = false;
      if (draft.definitionId !== definitionId) throw new Error("This draft belongs to a different Definition.");
      updateDetailTab("drafts");
      setActiveDraft(draft);
      loadCandidate(draft.candidate);
      if (updatePath) navigateToAppPath(adminDraftPath(definitionId, draft.draftId));
    } catch (error) {
      if (sequence === draftRead.current.sequence) {
        setDraftReadError(describeAdminError(error, "Failed to load draft.").message);
        reportLifecycleError(error, "Failed to load draft.");
      }
    } finally {
      if (sequence === draftRead.current.sequence) {
        draftRead.current.pending = false;
        setBusy(false);
      }
    }
  }, [loadCandidate, definitionId]);

  useEffect(() => {
    if (draftId && draftId !== activeDraft?.draftId) void selectDraft(draftId, false);
    if (!draftId) { draftEvidenceRef.current?.pause(); setActiveDraft(null); loadCandidate({}); }
  }, [draftId, selectDraft]);
  useEffect(() => {
    if (!draftPage || !activeDraft) return;
    changeEditorView(draftPage === 'advanced-json' ? 'json' : 'form');
  }, [draftPage, activeDraft?.draftId]);
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
      releaseConsumedDraft();
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
      if (activeDraft?.draftId === draft.draftId) draftEvidenceRef.current?.pause();
      await deleteAdminDefinitionDraft(draft.draftId, expectedRevision);
      if (activeDraft?.draftId === draft.draftId) {
        releaseConsumedDraft();
        setTransientDirtyPages([]);
        setActiveDraft(null);
        loadCandidate({});
      }
      message.success("Draft deleted.");
      if (draftId === draft.draftId) navigateToAppPath(adminDefinitionPath(definitionId, "drafts"), true);
      await reloadLifecycle();
      await onRetryDefinitions();
    } catch (error) {
      reportLifecycleError(error, "Draft could not be deleted.");
      showAdminFailure(message, error, "Draft could not be deleted.");
      if (activeDraft?.draftId === draft.draftId) draftEvidenceRef.current?.resume();
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
    if (!activeDraft || jsonError || automationNumberErrors(candidate).length > 0 || !validExecutionBudgets((candidate.executionBudgets ?? {}) as import("../../services/adminApi").ExecutionBudgetPolicy, budgetLimits)) {
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
    if (next === "json" && !jsonError) {
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
          draftEvidenceRef.current?.pause();
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
          draftEvidenceRef.current?.resume();
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
      {group ? (
        <AdminOwnerHeading name={group.logicalName}
          metadata={<>
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
          </>}
          extra={canDeleteDefinition ? (
              <Button danger disabled={busy} onClick={confirmDeleteDefinition}>
                Delete definition
              </Button>
            ) : null}
          status={<Flex gap={8} wrap="wrap">
            <Tag>{formatDefinitionInventoryCounts(group)}</Tag>
            <Tag>{group.draftOnly ? "Draft" : `Latest: ${formatInventoryStatus(group.latestStatus)}`}</Tag>
            {!group.draftOnly && latestActiveVersion(group.versions) !== null
              && latestActiveVersion(group.versions) !== group.latestVersion ? (
                <Tag color="blue">Latest active v{latestActiveVersion(group.versions)}</Tag>
              ) : null}
          </Flex>} />
      ) : (
        <AdminOwnerHeading name={definitionId} />
      )}
      <AdminPageHeading page={draftId ? draftPages.find(page => page.id === draftPage) : definitionPages.find(page => page.id === (tab ?? "versions"))} />
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
      {draftId && !activeDraft && <Alert type={draftReadError ? "error" : "info"} showIcon title={draftReadError ?? "Loading draft…"} action={<Button disabled={busy} onClick={() => void selectDraft(draftId, false)}>Retry draft</Button>} />}
      {(group || draftSummaries.length > 0) && activeDraft && draftId ? (
        <section ref={editorSurfaceRef} className="admin-draft-focused" aria-label="Draft editor">
          {lifecycleError ? (
            <AdminDeletionBlockedAlert
              className="admin-destructive-detail"
              message={lifecycleError}
              diagnosticId={lifecycleDiagnosticId}
              action={<Button size="small" onClick={() => void reloadLifecycle()}>Retry</Button>}
            />
          ) : null}
          <DefinitionDraftWorkspace
            onTransientDirtyChange={setTransientDirtyPages}
            page={draftPage ?? "overview"}
            evidenceRef={draftEvidenceRef}
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
      ) : !draftId && (group || lifecycleLoading || draftSummaries.length > 0) ? (
        <section aria-label="Definition drafts" className="admin-definition-drafts">
              <Typography.Paragraph type="secondary">Fork an immutable version to edit, validate, and publish a new one.</Typography.Paragraph>
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
              <RetainedAdminPage active={detailTab === "versions"}><>
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
                  </></RetainedAdminPage>
              <RetainedAdminPage active={detailTab === "drafts"}><>
                    {!lifecycleLoading && draftSummaries.length > 0 ? (
                      <div className="admin-draft-list">
                        <Flex align="baseline" wrap gap="var(--ac-space-compact)" className="admin-draft-list-heading">
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
                              href={adminDraftPath(definitionId, item.draftId)} onClick={event => followAppLink(event, () => void selectDraft(item.draftId))}>
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
                  </></RetainedAdminPage>
        </section>
      ) : null}
    </Flex>
  );
}
