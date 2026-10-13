import { Alert, App, Button, Collapse, Empty, Flex, Form, Input, Modal, Radio, Select, Table, Tag, Typography } from "antd";
import { useEffect, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { adminDraftPath, adminInstancePath, navigateToAppPath } from "../../app/appRoute";
import { createAdminAgentInstance, createNewAdminDefinitionDraft, getAdminDefinitionVersion, updateHarness, type AdminCreateInstancePersona } from "../../services/adminApi";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { HarnessPolicyModeScopes } from "./HarnessManagementSection";
import { parsePersonaJson } from "./instanceManagedControls";

import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";

import { formatAdminTimestamp, formatForkSourceOptionLabel, instanceVersionRows, latestActiveVersion, type DefinitionInventoryGroup } from './adminPresentation';
export function NewDefinitionButton({ groups }: { groups: DefinitionInventoryGroup[] }) {
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
      navigateToAppPath(adminDraftPath(draft.definitionId, draft.draftId));
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

export function NewInstanceButton({ groups, loading = false }: { groups: DefinitionInventoryGroup[]; loading?: boolean }) {
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
      <Button disabled={loading} onClick={openModal}>New instance</Button>
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

export function InventorySection({
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
    href: string;
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
      <Flex align="baseline" wrap gap="var(--ac-space-compact)" className="admin-inventory-heading">
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
                aria-label={title === "Definitions" ? `${item.title} · ${item.secondary}` : item.title} href={item.href} onClick={followAppLink}>
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
