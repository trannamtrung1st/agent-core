import { Alert, App, Button, Collapse, Empty, Flex, Form, Input, Modal, Radio, Select, Table, Tag, Typography, theme } from "antd";
import { useEffect, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { adminDraftPath, adminInstancePath, navigateToAppPath } from "../../app/appRoute";
import { createAdminAgentInstance, createNewAdminDefinitionDraft, getAdminDefinitionVersion, getSelfManagement, saveSelfManagement, type AdminAgentInstance, type SelfManagementPolicy, type AdminCreateInstancePersona } from "../../services/adminApi";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction, showAdminFailure } from "./adminFailure";
import { defaultManagement, inheritedManagement, SelfManagementFields } from './SelfManagementFields';
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
  const { token } = theme.useToken();
  const [management, setManagement] = useState(inheritedManagement);
  const [managementDefaults, setManagementDefaults] = useState(defaultManagement);
  const [createdInstance, setCreatedInstance] = useState<AdminAgentInstance | null>(null);
  const [managementError, setManagementError] = useState<AdminFailureNotice | null>(null);
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
    && !personaLoading && personaSource === sourceKey && !personaError;

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
          setManagementDefaults({ ...defaultManagement, ...(definition.selfManagement as Partial<SelfManagementPolicy> | undefined) });
          setPersona(defaults);
          setPersonaSource(`${definitionId}:${source}:${version}`);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) setPersonaError(describeAdminError(error, "Definition defaults could not be loaded."));
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
    setManagement(inheritedManagement);
    setManagementDefaults(defaultManagement);
    setCreatedInstance(null);
    setManagementError(null);
    setPersona({ name: "", role: "", description: "", tone: "" });
    setOpen(true);
  };

  const createInstance = async () => {
    if (!canCreate || !selectedGroup || selectedVersion === null || version === null) {
      return;
    }
    setBusy(true);
    setManagementError(null);
    try {
      const created = createdInstance ?? await createAdminAgentInstance(
        selectedGroup.definitionId,
        version,
        personaMode === "custom" ? trimmedPersona(persona) : null
      );
      setCreatedInstance(created);
      if (Object.values(management).some(value => value !== null)) {
        try {
          const revision = createdInstance ? (await getSelfManagement(created.instanceId)).instanceRevision : created.revision;
          await saveSelfManagement(created.instanceId, { expectedRevision: revision, ...management });
        } catch (error) {
          setManagementError(describeAdminError(error, 'Instance created, but self-management could not be saved. Retry or open the Instance to configure it.'));
          return;
        }
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
      <Button type="primary" disabled={loading} onClick={openModal}>New instance</Button>
      <Modal
        title="New instance"
        open={open}
        okText={createdInstance ? "Retry self-management" : "Create instance"}
        cancelText={createdInstance ? "Open instance" : "Cancel"}
        confirmLoading={busy}
        okButtonProps={{ disabled: !canCreate }}
        onOk={() => void createInstance()}
        onCancel={() => {
          if (!busy) {
            setOpen(false);
            if (createdInstance) navigateToAppPath(adminInstancePath(createdInstance.instanceId));
          }
        }}
      >
        {groups.length === 0 ? (
          <Typography.Paragraph>No published definition is available.</Typography.Paragraph>
        ) : (
          <Flex vertical gap={token.paddingSM}>
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
                  setManagement(inheritedManagement);
                  const next = groups.find((group) => group.definitionId === nextId);
                  if (next) {
                    setVersion(latestActiveVersion(instanceVersionRows(next.versions)) ?? next.latestVersion);
                  }
                }}
                disabled={busy || !!createdInstance}
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
                onChange={(nextVersion) => { setVersion(nextVersion); setManagement(inheritedManagement); }}
                disabled={busy || !!createdInstance}
              />
            </label>
            {selectedVersion && selectedVersion.status.toLowerCase() === "deprecated" ? (
              <Alert
                type="warning"
                showIcon
                message={`v${selectedVersion.version} is deprecated. New instances normally use the latest active publication.`}
              />
            ) : null}
            {personaLoading && <Typography.Text type="secondary" role="status">Loading Definition defaults…</Typography.Text>}
            {personaError && <Alert type="error" showIcon title={personaError.message}
              description={personaError.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: personaError.diagnosticId }} /> : undefined}
              action={<Button disabled={busy} onClick={() => setPersonaRetry(value => value + 1)}>Retry</Button>} />}
            {managementError && <Alert type="error" showIcon title="Instance created; self-management was not saved"
              description={<><Typography.Paragraph>{managementError.message}</Typography.Paragraph>
                Retry saves to the same Instance. Open instance continues with its current inherited policy.
                {managementError.diagnosticId && <DiagnosticDetails fields={{ diagnosticId: managementError.diagnosticId }} />}</>} />}
            <Collapse items={[{ key: "self-management", label: "Self-management (optional)", children:
              <Flex vertical gap={token.padding}>
                <Typography.Text type="secondary">Inherit the selected Definition defaults or customize individual fields. Runtime permissions still apply independently.</Typography.Text>
                <Form layout="vertical" className="admin-config-form">
                  <SelfManagementFields value={management} defaults={managementDefaults}
                    disabled={busy || personaLoading || personaSource !== sourceKey || !!personaError} onChange={setManagement} />
                </Form>
              </Flex>
            }]} />
            <Radio.Group
              aria-label="Persona"
              value={personaMode}
              onChange={(event) => setPersonaMode(event.target.value)}
              disabled={busy || !!createdInstance}
            >
              <Radio value="default">Definition persona</Radio>
              <Radio value="custom">Custom persona</Radio>
            </Radio.Group>
            {personaMode === "custom" ? (
              <>
                <div className="admin-draft-field-grid">
                  <PersonaField label="Persona name" value={persona.name} disabled={busy || !!createdInstance || personaLoading || personaSource !== sourceKey} onChange={(name) => setPersona({ ...persona, name })} />
                  <PersonaField label="Persona role" value={persona.role} disabled={busy || !!createdInstance || personaLoading || personaSource !== sourceKey} onChange={(role) => setPersona({ ...persona, role })} />
                  <PersonaField label="Persona description" value={persona.description} disabled={busy || !!createdInstance || personaLoading || personaSource !== sourceKey} onChange={(description) => setPersona({ ...persona, description })} />
                  <PersonaField label="Persona tone" value={persona.tone} disabled={busy || !!createdInstance || personaLoading || personaSource !== sourceKey} onChange={(tone) => setPersona({ ...persona, tone })} />
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
  emptyLabel,
  loading,
  error,
  diagnosticId,
  unauthorized,
  onRetry,
  items
}: {
  title: string;
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
