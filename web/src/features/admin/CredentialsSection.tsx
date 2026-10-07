import { useCallback, useEffect, useState } from "react";
import { Alert, App, Button, Descriptions, Empty, Flex, Form, Input, Modal, Select, Spin, Table, Tag, Typography, theme } from "antd";
import { MinusCircleOutlined, PlusOutlined } from "@ant-design/icons";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { confirmAction } from "../../app/confirmAction";
import { adminHomePath, navigateToAppPath } from "../../app/appRoute";
import { bindCredential, createCredential, deleteCredential, listCredentialBindings, listCredentials,
  replaceCredentialValue, resetBrowserProfile, unbindCredential, updateCredential,
  type CredentialBinding, type SystemCredential } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminErrorNotice } from "./adminFailure";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

const kinds = ["Password", "ApiKey", "Token", "Certificate", "PrivateKey", "Generic"];
type Fields = { displayName: string; kind: string; status: string; metadata: Array<{ key: string; value: string }>; origins: string; protectedValue?: string };
function metadata(rows: Fields["metadata"] = []) {
  const result: Record<string, string> = Object.create(null);
  for (const { key, value } of rows) {
    if (Object.keys(result).some(k => k.toLowerCase() === key.trim().toLowerCase())) throw new Error("Metadata keys must be unique.");
    result[key.trim()] = value ?? "";
  }
  return result;
}
function safeFields(values: Fields) {
  return { displayName: values.displayName, status: values.status, metadata: metadata(values.metadata),
    allowedOrigins: values.origins.split(/\r?\n/).map(o => o.trim()).filter(Boolean) };
}
function CredentialFields({ create }: { create: boolean }) {
  const { token } = theme.useToken();
  return <>
    <Form.Item name="displayName" label="Display name" rules={[{ required: true, max: 120 }]}><Input autoComplete="off" /></Form.Item>
    {create ? <Form.Item key="kind" name="kind" label="Kind" rules={[{ required: true }]}><Select options={kinds.map(value => ({ value, label: value }))} /></Form.Item> :
      <Form.Item key="status" name="status" label="Status"><Select options={[{ value: "Active", label: "Active" }, { value: "Disabled", label: "Disabled" }]} /></Form.Item>}
    <Form.Item label="Metadata" extra="Metadata is non-secret and visible to bound agents. It never grants permissions.">
    <Form.List name="metadata">{(fields, { add, remove }) => <Flex vertical gap={token.paddingXS}>
      {fields.map(field => <Flex key={field.key} className="admin-form-row credential-metadata-row" gap={token.paddingXS} align="start">
        <Form.Item name={[field.name, "key"]} rules={[{ required: true, max: 64 }]} style={{ flex: 1, minWidth: 0 }}><Input aria-label={`Metadata key ${field.name + 1}`} placeholder="Key" /></Form.Item>
        <Form.Item name={[field.name, "value"]} rules={[{ max: 2048 }]} style={{ flex: 2, minWidth: 0 }}><Input aria-label={`Metadata value ${field.name + 1}`} placeholder="Value" /></Form.Item>
        <Button type="text" icon={<MinusCircleOutlined />} aria-label={`Remove metadata ${field.name + 1}`} onClick={() => remove(field.name)} />
      </Flex>)}
      <Button type="dashed" icon={<PlusOutlined />} disabled={fields.length >= 32} onClick={() => add({ key: "", value: "" })}>Add metadata</Button>
    </Flex>}</Form.List></Form.Item>
    <Form.Item name="origins" label="Allowed origins" extra="One exact HTTP(S) origin per line. No paths or wildcards."><Input.TextArea rows={3} autoComplete="off" /></Form.Item>
    {create && <ProtectedValueField />}
  </>;
}

function matchesCredential(credential: SystemCredential, search: string) {
  return [credential.displayName, credential.kind, credential.status,
    ...Object.entries(credential.metadata).flat(), ...credential.allowedOrigins]
    .join(" ").toLowerCase().includes(search.trim().toLowerCase());
}

function CredentialPolicySummary({ credential }: { credential: SystemCredential }) {
  const entries = Object.entries(credential.metadata);
  const more = [entries.length > 2 ? `${entries.length - 2} more metadata ${entries.length === 3 ? "field" : "fields"}` : "",
    credential.allowedOrigins.length > 1 ? `${credential.allowedOrigins.length - 1} more ${credential.allowedOrigins.length === 2 ? "origin" : "origins"}` : ""].filter(Boolean);
  return <Flex vertical style={{ minWidth: 0 }}>
    {entries.slice(0, 2).map(([key, value]) => <Typography.Text key={key} ellipsis title={`${key}: ${value}`}>{key}: {value}</Typography.Text>)}
    {credential.allowedOrigins.slice(0, 1).map(origin => <Typography.Text key={origin} type="secondary" ellipsis title={origin}>{origin}</Typography.Text>)}
    {more.length > 0 && <Typography.Text type="secondary">{more.join(" · ")}</Typography.Text>}
    {entries.length === 0 && credential.allowedOrigins.length === 0 && <Typography.Text type="secondary">No metadata or origins</Typography.Text>}
  </Flex>;
}

function CredentialDetails({ credential }: { credential: SystemCredential }) {
  const detailLayout = useAdminDetailLayout();
  return <div role="region" aria-label={`Details for ${credential.displayName}`} style={{ whiteSpace: "normal" }}>
    <Descriptions bordered column={1} size="small" {...detailLayout} items={[
      { key: "kind", label: "Kind", children: credential.kind },
      { key: "metadata", label: "Metadata", children: Object.keys(credential.metadata).length
        ? <Flex vertical>{Object.entries(credential.metadata).map(([key, value]) => <Typography.Text key={key}>{key}: {value}</Typography.Text>)}</Flex> : "None" },
      { key: "origins", label: "Allowed origins", children: credential.allowedOrigins.length
        ? <Flex vertical>{credential.allowedOrigins.map(origin => <Typography.Text key={origin}>{origin}</Typography.Text>)}</Flex> : "None configured" }
    ]} />
  </div>;
}
function ProtectedValueField() {
  return <Form.Item name="protectedValue" label="Protected value" rules={[{ required: true }]} extra="Protected values cannot be read back. Replace the value if it changes.">
    <Input.Password visibilityToggle={false} autoComplete="new-password" spellCheck={false} />
  </Form.Item>;
}

export function CredentialsSection() {
  const { token } = theme.useToken(); const { modal, message } = App.useApp();
  const [items, setItems] = useState<SystemCredential[]>([]); const [loading, setLoading] = useState(true); const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<AdminFailureNotice>(); const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState<{ kind: "create" | "edit" | "replace"; item?: SystemCredential }>();
  const [dialogError, setDialogError] = useState<AdminFailureNotice>(); const [form] = Form.useForm<Fields>();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [expanded, setExpanded] = useState<React.Key[]>([]);
  const load = useCallback(async () => { setLoading(true); setError(undefined);
    try { setItems(await listCredentials()); setLoaded(true); } catch (e) { setError(describeAdminError(e, "Unable to load credentials.")); }
    finally { setLoading(false); } }, []);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (!dialog) return;
    const item = dialog.item;
    form.setFieldsValue(item ? { displayName: item.displayName, kind: item.kind, status: item.status,
      metadata: Object.entries(item.metadata).map(([key, value]) => ({ key, value })), origins: item.allowedOrigins.join("\n") }
      : { kind: "Password", status: "Active", metadata: [], origins: "" });
  }, [dialog, form]);
  function open(kind: "create" | "edit" | "replace", item?: SystemCredential) {
    setDialogError(undefined); setDialog({ kind, item });
  }
  function close() { form.resetFields(); setDialog(undefined); setDialogError(undefined); }
  async function save() {
    const values = await form.validateFields(); if (!dialog) return; setBusy(true); setDialogError(undefined);
    try {
      if (dialog.kind === "create") await createCredential({ ...safeFields(values), kind: values.kind, protectedValue: values.protectedValue! });
      else if (dialog.kind === "replace") await replaceCredentialValue(dialog.item!, values.protectedValue!);
      else await updateCredential(dialog.item!, safeFields(values));
      close(); await load(); message.success("Credential saved");
    } catch (e) { setDialogError(describeAdminError(e, "Credential could not be saved. Enter the protected value again to retry.")); }
    finally { form.setFieldValue("protectedValue", undefined); setBusy(false); }
  }
  async function remove(item: SystemCredential) {
    setBusy(true); try { await deleteCredential(item); await load(); }
    catch (e) { setError(describeAdminError(e, "Credential could not be deleted.")); } finally { setBusy(false); }
  }
  return <section className="admin-definition-panel" aria-label="System credentials">
    <div className="admin-definition-panel-heading">
    <Flex justify="space-between" align="center" wrap gap={token.paddingXS}><Typography.Title level={4}>System credentials</Typography.Title>
      <Button type="primary" disabled={busy || loading || !loaded} onClick={() => open("create")}>Create credential</Button></Flex>
    <Typography.Text type="secondary">Reusable protected values. Agents receive access through explicit bindings; bindings do not grant tools.</Typography.Text>
    </div><div className="admin-definition-panel-body"><Flex vertical gap={token.paddingSM}>
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={() => void load()}>Retry</Button>} />}
    {loading ? <Spin aria-label="Loading credentials" /> : loaded && <>
      <AdminCollectionToolbar label="credentials" value={search} onChange={setSearch} />
      <Table<SystemCredential> aria-label="System credentials table" className="admin-collection-table" size="small" rowKey="credentialId" scroll={{ x: 1200 }} pagination={pagination}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search.trim() || items.length > 0 ? "No matches. Clear search or filters to see all results." : "No credentials yet"} /> }}
        dataSource={items.filter(c => matchesCredential(c, search))}
        expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, c) => setExpanded(open ? [c.credentialId] : []), expandedRowRender: c => <CredentialDetails credential={c} /> }}
        columns={[
          { title: "Credential", dataIndex: "displayName", width: 220, ellipsis: true, sorter: (a, b) => a.displayName.localeCompare(b.displayName), render: (name, c) => <Flex vertical style={{ minWidth: 0 }}><Typography.Text strong ellipsis title={name}>{name}</Typography.Text><Typography.Text type="secondary">{c.kind}</Typography.Text></Flex> },
          { title: "Status", dataIndex: "status", width: 100, filters: [{ text: "Active", value: "Active" }, { text: "Disabled", value: "Disabled" }], onFilter: (status, c) => c.status === status, render: s => <Tag>{s}</Tag> },
          { title: "Metadata & origins", width: 280, render: (_, c) => <CredentialPolicySummary credential={c} /> },
          { title: "Bindings", dataIndex: "bindingCount", width: 80, align: "right" },
          { title: "Updated", width: 180, render: (_, c) => <time dateTime={c.updatedAtUtc}>{new Date(c.updatedAtUtc).toLocaleString()}</time> },
          { title: "Actions", width: 300, render: (_, c) => <Flex className="admin-table-actions" gap={token.paddingXS}>
            <Button disabled={busy} onClick={() => open("edit", c)}>Edit</Button><Button disabled={busy} onClick={() => open("replace", c)}>Replace value</Button>
            <Button danger disabled={busy || c.bindingCount > 0} onClick={() => confirmAction(modal, { title: `Delete ${c.displayName}?`, content: "This permanently removes the credential. Unbind all agents first.", okText: "Delete credential", danger: true, onOk: () => remove(c) })}>Delete</Button>
          </Flex> }
        ]} />
    </>}
    </Flex></div>
    <Modal className="admin-credential-dialog" centered width={dialog?.kind === "replace" ? 520 : 640} open={!!dialog} title={dialog?.kind === "create" ? "Create credential" : dialog?.kind === "replace" ? "Replace protected value" : "Edit credential"}
      onCancel={close} onOk={() => void save().catch(() => {})} confirmLoading={busy} closable={!busy} keyboard={!busy} cancelButtonProps={{ disabled: busy }} okText="Save credential" okButtonProps={{ disabled: busy, "aria-label": "Save credential" }} mask={{ closable: false }} forceRender>
      <Flex vertical gap={token.padding}>
      {dialog?.kind === "replace" && <Flex align="center" gap={token.paddingXS} wrap><Typography.Text strong style={{ overflowWrap: "anywhere" }}>{dialog.item?.displayName}</Typography.Text><Tag>{dialog.item?.kind}</Tag></Flex>}
      {dialogError && <Alert type="error" showIcon title={<AdminErrorNotice message={dialogError.message} diagnosticId={dialogError.diagnosticId} showDetailsLabel />} />}
      <Form form={form} className="admin-config-form" layout="vertical" disabled={busy} autoComplete="off" initialValues={{ kind: "Password", status: "Active", metadata: [], origins: "" }}>
        {dialog?.kind === "replace" ? <ProtectedValueField /> : <CredentialFields create={dialog?.kind === "create"} />}
      </Form></Flex>
    </Modal>
  </section>;
}

export function InstanceCredentialsSection({ instanceId, revision, archived }: { instanceId: string; revision: number; archived: boolean }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [bindings, setBindings] = useState<CredentialBinding[]>([]); const [choices, setChoices] = useState<SystemCredential[]>([]);
  const [loading, setLoading] = useState(true); const [loaded, setLoaded] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice>();
  const [bindingError, setBindingError] = useState<AdminFailureNotice>();
  const [open, setOpen] = useState(false); const [form] = Form.useForm<{ credentialId: string; reference: string }>();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [expanded, setExpanded] = useState<React.Key[]>([]);
  const available = choices.filter(c => !bindings.some(b => b.credentialId === c.credentialId));
  const load = useCallback(async () => { setLoading(true); setError(undefined); try { const [grants, credentials] = await Promise.all([listCredentialBindings(instanceId), listCredentials()]); setBindings(grants); setChoices(credentials); setLoaded(true); }
    catch (e) { setError(describeAdminError(e, "Unable to load bindings.")); } finally { setLoading(false); } }, [instanceId]);
  useEffect(() => { void load(); }, [load, revision]);
  async function mutate(action: () => Promise<unknown>, inDialog = false) {
    setBusy(true); if (inDialog) setBindingError(undefined); else setError(undefined);
    try { await action(); setOpen(false); form.resetFields(); await load(); }
    catch (e) { const notice = describeAdminError(e, "Binding operation failed. Reload the instance if its revision changed.");
      if (inDialog) setBindingError(notice); else setError(notice); }
    finally { setBusy(false); }
  }
  return <Flex vertical gap={token.padding}>
    <section className="admin-definition-panel" aria-label="Credential bindings">
    <div className="admin-definition-panel-heading">
    <Flex justify="space-between" align="center" gap={token.paddingXS} wrap><Typography.Title level={4} style={{ margin: 0 }}>Credential bindings</Typography.Title>
      <Flex gap={token.paddingXS} wrap><Button onClick={() => navigateToAppPath(adminHomePath("credentials"))}>Manage system credentials</Button><Button type="primary" disabled={archived || busy || loading || !loaded} onClick={() => { setBindingError(undefined); setOpen(true); }}>Bind credential</Button></Flex></Flex>
    </div><div className="admin-definition-panel-body"><Flex vertical gap={token.paddingSM}>
    {archived && <Alert type="info" title="Archived instance bindings are read-only and cannot be used." />}
    {error && <Alert type="error" title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} showIcon action={<Button onClick={() => void load()}>Retry</Button>} />}
    {loading ? <Spin aria-label="Loading credential bindings" /> : loaded && <>
    <AdminCollectionToolbar label="credential bindings" value={search} onChange={setSearch} />
    <Table<CredentialBinding> aria-label="Credential bindings table" className="admin-collection-table" size="small" rowKey="bindingId" pagination={pagination} scroll={{ x: 920 }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search.trim() || bindings.length > 0 ? "No matches. Clear search or filters to see all results." : "No credentials bound"} /> }}
      dataSource={bindings.filter(b => b.reference.toLowerCase().includes(search.trim().toLowerCase()) || matchesCredential(b.credential, search))}
      expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, b) => setExpanded(open ? [b.bindingId] : []), expandedRowRender: b => <CredentialDetails credential={b.credential} /> }} columns={[
      { title: "Reference", dataIndex: "reference", width: 180, ellipsis: true, sorter: (a, b) => a.reference.localeCompare(b.reference) },
      { title: "Credential", width: 240, ellipsis: true, render: (_, b) => <Flex vertical style={{ minWidth: 0 }}><Typography.Text strong ellipsis title={b.credential.displayName}>{b.credential.displayName}</Typography.Text><Typography.Text type="secondary">{b.credential.kind} · {b.credential.status}</Typography.Text></Flex> },
      { title: "Metadata & origins", width: 320, render: (_, b) => <CredentialPolicySummary credential={b.credential} /> },
      { title: "Action", width: 130, render: (_, b) => <Button danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: `Unbind ${b.reference}?`, content: "Removes only this agent's grant. Other bindings and browser sign-in remain.", okText: "Unbind", danger: true, onOk: () => mutate(() => unbindCredential(instanceId, b, revision)) })}>Unbind</Button> }
    ]} /></>}
    </Flex></div></section>
    <section className="admin-definition-panel" aria-label="Browser state">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Browser state</Typography.Title>
    <Typography.Text type="secondary">Persistent browser state may contain cookies and signed-in sessions. Reset closes the browser and removes only this agent's profile. Credentials and bindings remain.</Typography.Text>
    </div><div className="admin-definition-panel-body">
    <Flex><Button danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: "Reset browser profile?", content: "This agent will need to sign in again.", okText: "Reset browser profile", danger: true, onOk: () => mutate(() => resetBrowserProfile(instanceId, revision)) })}>Reset browser profile</Button></Flex>
    </div></section>
    <Modal className="admin-credential-dialog" centered open={open} title="Bind credential" onCancel={() => { setOpen(false); setBindingError(undefined); form.resetFields(); }} confirmLoading={busy}
      closable={!busy} keyboard={!busy} mask={{ closable: false }} cancelButtonProps={{ disabled: busy }} okText="Bind credential" okButtonProps={{ disabled: busy || available.length === 0, "aria-label": "Bind credential" }}
      onOk={() => void form.validateFields().then(v => mutate(() => bindCredential(instanceId, v.credentialId, v.reference, revision), true)).catch(() => {})} forceRender>
      <Flex vertical gap={token.padding}>
      {bindingError && <Alert type="error" showIcon title={<AdminErrorNotice message={bindingError.message} diagnosticId={bindingError.diagnosticId} showDetailsLabel />} />}
      {available.length === 0 && <Alert type="info" showIcon title="No unbound credentials available" description="Manage system credentials to create another credential or review existing bindings." />}
      <Form form={form} className="admin-config-form" layout="vertical" disabled={busy || available.length === 0} preserve={false}><Form.Item name="credentialId" label="System credential" rules={[{ required: true }]}><Select showSearch optionFilterProp="label" options={available.map(c => ({ value: c.credentialId, label: `${c.displayName} · ${c.kind} · ${c.status}` }))} /></Form.Item>
        <Form.Item name="reference" label="Reference" rules={[{ required: true, pattern: /^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$/, message: "Use 1–64 letters, digits or hyphens." }]} extra="Stable agent alias. Changing it requires unbinding and binding again."><Input autoComplete="off" /></Form.Item></Form>
      </Flex></Modal>
  </Flex>;
}
