import { useCallback, useEffect, useState } from "react";
import { Alert, App, Button, Empty, Flex, Form, Input, Modal, Select, Spin, Table, Tag, Typography, theme } from "antd";
import { MinusCircleOutlined, PlusOutlined } from "@ant-design/icons";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { confirmAction } from "../../app/confirmAction";
import { adminHomePath, navigateToAppPath } from "../../app/appRoute";
import { bindCredential, createCredential, deleteCredential, listCredentialBindings, listCredentials,
  replaceCredentialValue, resetBrowserProfile, unbindCredential, updateCredential,
  type CredentialBinding, type SystemCredential } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminErrorNotice } from "./adminFailure";

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
    <Typography.Paragraph type="secondary">Metadata is non-secret and visible to bound agents. It never grants permissions.</Typography.Paragraph>
    <Form.List name="metadata">{(fields, { add, remove }) => <Flex vertical gap={token.paddingXS}>
      {fields.map(field => <Flex key={field.key} gap={token.paddingXS} align="start">
        <Form.Item name={[field.name, "key"]} rules={[{ required: true, max: 64 }]} style={{ flex: 1 }}><Input aria-label={`Metadata key ${field.name + 1}`} placeholder="Key" /></Form.Item>
        <Form.Item name={[field.name, "value"]} rules={[{ max: 2048 }]} style={{ flex: 2 }}><Input aria-label={`Metadata value ${field.name + 1}`} placeholder="Value" /></Form.Item>
        <Button type="text" icon={<MinusCircleOutlined />} aria-label={`Remove metadata ${field.name + 1}`} onClick={() => remove(field.name)} />
      </Flex>)}
      <Button type="dashed" icon={<PlusOutlined />} disabled={fields.length >= 32} onClick={() => add({ key: "", value: "" })}>Add metadata</Button>
    </Flex>}</Form.List>
    <Form.Item name="origins" label="Allowed origins" extra="One exact HTTP(S) origin per line. No paths or wildcards."><Input.TextArea rows={3} autoComplete="off" /></Form.Item>
    {create && <ProtectedValueField />}
  </>;
}
function ProtectedValueField() {
  return <Form.Item name="protectedValue" label="Protected value" rules={[{ required: true }]} extra="Protected values cannot be read back. Replace the value if it changes.">
    <Input.Password visibilityToggle={false} autoComplete="new-password" spellCheck={false} />
  </Form.Item>;
}

export function CredentialsSection() {
  const { token } = theme.useToken(); const { modal, message } = App.useApp();
  const [items, setItems] = useState<SystemCredential[]>([]); const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AdminFailureNotice>(); const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState<{ kind: "create" | "edit" | "replace"; item?: SystemCredential }>();
  const [dialogError, setDialogError] = useState<AdminFailureNotice>(); const [form] = Form.useForm<Fields>();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const load = useCallback(async () => { setLoading(true); setError(undefined);
    try { setItems(await listCredentials()); } catch (e) { setError(describeAdminError(e, "Unable to load credentials.")); }
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
  return <Flex vertical gap={token.padding}>
    <Flex justify="space-between" align="center" wrap gap={token.paddingXS}><Typography.Title level={4} style={{ margin: 0 }}>System credentials</Typography.Title>
      <Button type="primary" onClick={() => open("create")}>Create credential</Button></Flex>
    <Typography.Text type="secondary">Reusable protected values. Agents receive access through explicit bindings; bindings do not grant tools.</Typography.Text>
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={() => void load()}>Retry</Button>} />}
    {loading ? <Spin aria-label="Loading credentials" /> : <>
      <AdminCollectionToolbar label="credentials" value={search} onChange={setSearch} />
      <Table<SystemCredential> rowKey="credentialId" scroll={{ x: 850 }} pagination={pagination} locale={{ emptyText: <Empty description="No credentials yet" /> }}
        dataSource={items.filter(c => `${c.displayName} ${c.kind} ${Object.values(c.metadata).join(" ")}`.toLowerCase().includes(search.toLowerCase()))}
        columns={[
          { title: "Credential", dataIndex: "displayName", render: (name, c) => <Flex vertical><Typography.Text strong>{name}</Typography.Text><Typography.Text type="secondary">{c.kind}</Typography.Text></Flex> },
          { title: "Status", dataIndex: "status", render: s => <Tag color={s === "Active" ? "green" : undefined}>{s}</Tag> },
          { title: "Metadata & origins", render: (_, c) => <Flex vertical style={{ overflowWrap: "anywhere", maxWidth: 280 }}>{Object.entries(c.metadata).map(([k, v]) => <Typography.Text key={k}>{k}: {v}</Typography.Text>)}{c.allowedOrigins.map(o => <Typography.Text type="secondary" key={o}>{o}</Typography.Text>)}</Flex> },
          { title: "Bindings", dataIndex: "bindingCount" },
          { title: "Updated", render: (_, c) => new Date(c.updatedAtUtc).toLocaleString() },
          { title: "Actions", render: (_, c) => <Flex gap={token.paddingXS} wrap>
            <Button onClick={() => open("edit", c)}>Edit</Button><Button onClick={() => open("replace", c)}>Replace value</Button>
            <Button danger disabled={busy || c.bindingCount > 0} onClick={() => confirmAction(modal, { title: `Delete ${c.displayName}?`, content: "This permanently removes the credential. Unbind all agents first.", okText: "Delete credential", danger: true, onOk: () => remove(c) })}>Delete</Button>
          </Flex> }
        ]} />
    </>}
    <Modal open={!!dialog} title={dialog?.kind === "create" ? "Create credential" : dialog?.kind === "replace" ? "Replace protected value" : "Edit credential"}
      onCancel={close} onOk={() => void save().catch(() => {})} confirmLoading={busy} okText="Save credential" okButtonProps={{ disabled: busy, "aria-label": "Save credential" }} mask={{ closable: false }} forceRender>
      {dialogError && <Alert type="error" showIcon title={<AdminErrorNotice message={dialogError.message} diagnosticId={dialogError.diagnosticId} showDetailsLabel />} />}
      <Form form={form} layout="vertical" autoComplete="off" initialValues={{ kind: "Password", status: "Active", metadata: [], origins: "" }}>
        {dialog?.kind === "replace" ? <ProtectedValueField /> : <CredentialFields create={dialog?.kind === "create"} />}
      </Form>
    </Modal>
  </Flex>;
}

export function InstanceCredentialsSection({ instanceId, revision, archived }: { instanceId: string; revision: number; archived: boolean }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [bindings, setBindings] = useState<CredentialBinding[]>([]); const [choices, setChoices] = useState<SystemCredential[]>([]);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice>();
  const [open, setOpen] = useState(false); const [form] = Form.useForm<{ credentialId: string; reference: string }>();
  const load = useCallback(async () => { setLoading(true); setError(undefined); try { const [grants, credentials] = await Promise.all([listCredentialBindings(instanceId), listCredentials()]); setBindings(grants); setChoices(credentials); }
    catch (e) { setError(describeAdminError(e, "Unable to load bindings.")); } finally { setLoading(false); } }, [instanceId]);
  useEffect(() => { void load(); }, [load, revision]);
  async function mutate(action: () => Promise<unknown>) { setBusy(true); setError(undefined); try { await action(); setOpen(false); form.resetFields(); await load(); }
    catch (e) { setError(describeAdminError(e, "Binding operation failed. Reload the instance if its revision changed.")); } finally { setBusy(false); } }
  return <Flex vertical gap={token.padding}>
    <Flex justify="space-between" align="center" gap={token.paddingXS} wrap><Typography.Title level={4} style={{ margin: 0 }}>Credential bindings</Typography.Title>
      <Flex gap={token.paddingXS} wrap><Button onClick={() => navigateToAppPath(adminHomePath("credentials"))}>Manage system credentials</Button><Button type="primary" disabled={archived || busy} onClick={() => setOpen(true)}>Bind credential</Button></Flex></Flex>
    {archived && <Alert type="info" title="Archived instance bindings are read-only and cannot be used." />}
    {error && <Alert type="error" title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} showIcon action={<Button onClick={() => void load()}>Retry</Button>} />}
    {loading ? <Spin aria-label="Loading credential bindings" /> : <Table<CredentialBinding> rowKey="bindingId" pagination={false} scroll={{ x: 620 }} locale={{ emptyText: <Empty description="No credentials bound" /> }} dataSource={bindings} columns={[
      { title: "Reference", dataIndex: "reference" }, { title: "Credential", render: (_, b) => <Flex vertical><Typography.Text>{b.credential.displayName}</Typography.Text><Typography.Text type="secondary">{b.credential.kind} · {b.credential.status}</Typography.Text></Flex> },
      { title: "Metadata & origins", render: (_, b) => <Flex vertical style={{ maxWidth: 280, overflowWrap: "anywhere" }}>{Object.entries(b.credential.metadata).map(([k, v]) => <Typography.Text key={k}>{k}: {v}</Typography.Text>)}{b.credential.allowedOrigins.map(o => <Typography.Text key={o} type="secondary">{o}</Typography.Text>)}</Flex> },
      { title: "Action", render: (_, b) => <Button danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: `Unbind ${b.reference}?`, content: "Removes only this agent's grant. Other bindings and browser sign-in remain.", okText: "Unbind", danger: true, onOk: () => mutate(() => unbindCredential(instanceId, b, revision)) })}>Unbind</Button> }
    ]} />}
    <Typography.Title level={4} style={{ margin: 0 }}>Browser state</Typography.Title>
    <Typography.Text type="secondary">Persistent browser state may contain cookies and signed-in sessions. Reset closes the browser and removes only this agent's profile. Credentials and bindings remain.</Typography.Text>
    <Flex><Button danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: "Reset browser profile?", content: "This agent will need to sign in again.", okText: "Reset browser profile", danger: true, onOk: () => mutate(() => resetBrowserProfile(instanceId, revision)) })}>Reset browser profile</Button></Flex>
    <Modal open={open} title="Bind credential" onCancel={() => { setOpen(false); form.resetFields(); }} confirmLoading={busy} onOk={() => void form.validateFields().then(v => mutate(() => bindCredential(instanceId, v.credentialId, v.reference, revision))).catch(() => {})} forceRender>
      <Form form={form} layout="vertical" preserve={false}><Form.Item name="credentialId" label="System credential" rules={[{ required: true }]}><Select showSearch optionFilterProp="label" options={choices.filter(c => !bindings.some(b => b.credentialId === c.credentialId)).map(c => ({ value: c.credentialId, label: `${c.displayName} · ${c.kind} · ${c.status}` }))} /></Form.Item>
        <Form.Item name="reference" label="Reference" rules={[{ required: true, pattern: /^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$/, message: "Use 1–64 letters, digits or hyphens." }]} extra="Stable agent alias. Changing it requires unbinding and binding again."><Input autoComplete="off" /></Form.Item></Form>
    </Modal>
  </Flex>;
}
