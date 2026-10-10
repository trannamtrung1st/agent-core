import { AgentConfigurationPanel, AgentSkillsResourcesSections } from "./AgentConfigurationLayout";
import { useAdminDetailLayout } from "./useAdminDetailLayout";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Alert, Button, Descriptions, Drawer, Empty, Flex, Spin, Table, Tabs, Tag, Typography, theme } from "antd";
import { getAdminDefinitionVersion, type AdminDefinitionInventoryItem, type AdminDefinitionPublicationSummary } from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";
import { AdminRetryAction } from "./adminFailure";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { DefinitionCandidateEditor, type DefinitionEditorView } from "./definitionCandidateEditor";
import { candidateToJson, type DefinitionCandidate } from "./definitionCandidate";
import { DefinitionSkillsSection } from "./DefinitionSkillsSection";
import { readDraftEnvironment } from "./draftEnvironment";

export function DefinitionVersionsTable({ rows, publications, busy, onChat, onDeprecate, renderResources }: {
  rows: AdminDefinitionInventoryItem[];
  publications: AdminDefinitionPublicationSummary[];
  busy: boolean;
  onChat: (version: number) => void;
  onDeprecate: (publication: AdminDefinitionPublicationSummary) => void;
  renderResources: (version: number) => ReactNode;
}) {
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const { token } = theme.useToken();
  const titleId = `definition-version-details-${useId()}`;
  const sectionRef = useRef<HTMLElement | null>(null);
  const triggerLabelRef = useRef<string | null>(null);
  const [selected, setSelected] = useState<AdminDefinitionInventoryItem | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);
  useEffect(() => {
    if (drawerOpen || !triggerLabelRef.current) return;
    const frame = window.requestAnimationFrame(() => {
      const trigger = Array.from(sectionRef.current?.querySelectorAll("button") ?? [])
        .find(button => button.getAttribute("aria-label") === triggerLabelRef.current);
      trigger?.focus({ preventScroll: true });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [drawerOpen]);
  const query = search.trim().toLowerCase();
  const latest = Math.max(...rows.map(row => row.version));
  const latestActive = Math.max(...rows.filter(row => row.status.toLowerCase() !== "deprecated").map(row => row.version));
  const data = rows.map(row => ({ ...row, publication: row.source === "durable"
    ? publications.find(item => item.version === row.version) : undefined }));
  return (
    <section ref={sectionRef} aria-label="Definition versions" className="admin-versions-section">
      <Typography.Title level={5}>Versions &amp; publications</Typography.Title>
      <AdminCollectionToolbar label="versions" value={search} onChange={setSearch} />
      <Table
        aria-label="Definition versions table" className="admin-collection-table" size="small"
        rowKey={row => `${row.source}:${row.version}`}
        dataSource={data.filter(row => [`v${row.version}`, row.displayName, row.source === "builtIn" ? "Built-in" : "Durable", row.status]
          .some(value => value.toLowerCase().includes(query)))}
        scroll={{ x: 1150 }} pagination={pagination}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={query || rows.length > 0 ? "No matches. Clear search or filters to see all results." : "No published versions yet."} /> }}
        columns={[
          { title: "Version", dataIndex: "version", width: 210, defaultSortOrder: "descend",
            sorter: (a, b) => a.version - b.version,
            render: (version: number, row) => <Flex className="admin-table-actions" gap={token.paddingXS} align="center">
              <Button type="link" size="small" aria-label={`View v${version} (${row.source})`}
                onClick={(event) => { triggerLabelRef.current = event.currentTarget.getAttribute("aria-label"); setSelected(row); setDrawerOpen(true); }}>v{version}</Button>
              {version === latest ? <Tag color="blue">Latest</Tag>
                : version === latestActive ? <Tag color="blue">Latest active</Tag> : null}
            </Flex> },
          { title: "Source", dataIndex: "source", width: 110,
            filters: [{ text: "Built-in", value: "builtIn" }, { text: "Durable", value: "durable" }],
            onFilter: (value, row) => row.source === value,
            render: (source: string) => source === "builtIn" ? "Built-in" : "Durable" },
          { title: "Status", dataIndex: "status", width: 110,
            filters: [...new Set(data.map(row => row.status))].map(value => ({ text: value, value })),
            onFilter: (value, row) => row.status === value,
            render: (status: string) => <Tag>{status.charAt(0).toUpperCase() + status.slice(1).toLowerCase()}</Tag> },
          { title: "Published at", key: "publishedAt", width: 210,
            sorter: (a, b) => (a.publication?.publishedAt ?? "").localeCompare(b.publication?.publishedAt ?? ""),
            render: (_, row) => row.publication
              ? new Date(row.publication.publishedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
              : <Typography.Text type="secondary" title={row.source === "durable" ? "Publication metadata unavailable" : undefined}>{row.source === "builtIn" ? "—" : "Unavailable"}</Typography.Text> },
          { title: "Metadata revision", key: "metadataRevision", width: 150, align: "right",
            sorter: (a, b) => (a.publication?.metadataRevision ?? -1) - (b.publication?.metadataRevision ?? -1),
            render: (_, row) => row.publication?.metadataRevision ?? "—" },
          { title: "Skills", key: "skills", width: 80, align: "right",
            sorter: (a, b) => (a.publication?.skills?.length ?? -1) - (b.publication?.skills?.length ?? -1),
            render: (_, row) => row.publication ? row.publication.skills?.length ?? 0 : "—" },
          { title: "Actions", key: "actions", width: 280, render: (_, row) => <Flex className="admin-table-actions" gap={token.paddingXS}>
            {row.publication ? <>
              <Button size="small" aria-label={`Start managed chat for v${row.version}`}
                disabled={busy || row.publication.status !== "Active"} onClick={() => onChat(row.version)}>Chat</Button>
              {row.publication.status === "Active" ? <Button size="small" danger disabled={busy}
                aria-label={`Deprecate publication v${row.version}`} onClick={() => onDeprecate(row.publication!)}>
                Deprecate publication
              </Button> : null}
            </> : null}
          </Flex> }
        ]}
      />
      <Drawer open={drawerOpen} onClose={() => setDrawerOpen(false)}
        afterOpenChange={open => { if (!open) setSelected(null); }} size={832}
        focusable={{ trap: drawerOpen, focusTriggerAfterClose: false }}
        styles={{ wrapper: { maxWidth: "100vw" } }}
        title={<Flex vertical gap={4}>
          <Typography.Text strong id={titleId}>Version details</Typography.Text>
          {selected ? <Typography.Text type="secondary">{selected.definitionId} · v{selected.version} · {selected.source === "builtIn" ? "Built-in" : "Durable"} · Read-only</Typography.Text> : null}
        </Flex>}
        aria-labelledby={titleId} className="admin-version-drawer" destroyOnHidden>
        {selected ? <DefinitionVersionDetails key={`${selected.source}:${selected.version}`}
          row={selected} renderResources={renderResources} /> : null}
      </Drawer>
    </section>
  );
}

function DefinitionVersionDetails({ row, renderResources }: {
  row: AdminDefinitionInventoryItem;
  renderResources: (version: number) => ReactNode;
}) {
  const { token } = theme.useToken();
  const detailLayout = useAdminDetailLayout();
  const [candidate, setCandidate] = useState<DefinitionCandidate | null>(null);
  const [error, setError] = useState<ReturnType<typeof describeAdminError> | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [view, setView] = useState<DefinitionEditorView>("form");
  useEffect(() => {
    let current = true;
    setCandidate(null);
    setError(null);
    void getAdminDefinitionVersion(row.definitionId, row.version, row.source)
      .then(value => { if (current) setCandidate(value); })
      .catch(reason => { if (current) setError(describeAdminError(reason, "Version details could not be loaded.")); });
    return () => { current = false; };
  }, [row.definitionId, row.version, row.source, attempt]);
  const environment = candidate ? readDraftEnvironment(candidate) : null;
  return (
    <section aria-label="Version details" className="admin-version-details" style={{ gap: token.paddingSM }}>
      <Typography.Paragraph type="secondary">Inspect this immutable version. Create a draft to make changes.</Typography.Paragraph>
      {error ? <Alert type="error" showIcon title={error.message}
        action={<AdminRetryAction diagnosticId={error.diagnosticId} onRetry={() => setAttempt(value => value + 1)} />} />
        : !candidate ? <Spin aria-label="Loading version details" />
        : <>
          <Tabs className="admin-draft-tabs" items={[
          { key: "definition", label: "Identity & version", children: <DefinitionCandidateEditor
            candidate={candidate} view={view} jsonText={candidateToJson(candidate)} busy={false} readOnly
            onViewChange={setView} onCandidateChange={() => {}} onJsonTextChange={() => {}} /> },
          { key: "skills", label: "Skills & resources", children: <AgentSkillsResourcesSections
            skills={<DefinitionSkillsSection
            candidate={candidate} busy={false} readOnly onChange={() => {}} />}
            resources={<AgentConfigurationPanel title="Resources" label="Published resources">{row.source === "durable"
            ? renderResources(row.version) : <Typography.Text type="secondary">Built-in resources use the shipped role environment and knowledge sources.</Typography.Text>}</AgentConfigurationPanel>}
          /> },
          { key: "capabilities", label: "Capabilities", children: <Descriptions {...detailLayout} bordered column={1} size="small">
            <Descriptions.Item label="Capability access">{environment?.capabilityMode ?? "Legacy projection"}</Descriptions.Item>
            <Descriptions.Item label="Authorized capabilities">{environment?.toolAllowlist.join(", ") || "None"}</Descriptions.Item>
            {environment?.capabilityMode ? <Descriptions.Item label="Always projected">{environment.alwaysCapabilities?.join(", ") || "None"}</Descriptions.Item> : null}
            <Descriptions.Item label="Harness references">{environment?.harness.join(", ") || "None"}</Descriptions.Item>
            <Descriptions.Item label="Workspace template">{environment?.workspaceTemplateId || "None"}</Descriptions.Item>
            <Descriptions.Item label="Attachment handling">{environment?.allowUnreadUnsupportedAttachmentTypes ? "Allow unread unsupported types" : "Reject unread unsupported types"}</Descriptions.Item>
            <Descriptions.Item label="Knowledge sources">{environment?.knowledgeSources.length ? environment.knowledgeSources.map(source =>
              <div key={source.identity}>{source.title} · {source.identity} · {source.citation} · {source.resourcePath || "Legacy knowledge fallback"}</div>
            ) : "None"}</Descriptions.Item>
          </Descriptions> },

          ]} />
        </>}
    </section>
  );
}
