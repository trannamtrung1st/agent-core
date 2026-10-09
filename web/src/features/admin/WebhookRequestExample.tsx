import { useState } from "react";
import { Alert, App, Button, Flex, Input, Tabs, Typography, theme } from "antd";

export function WebhookRequestExample({ url }: { url: string }) {
  const { token } = theme.useToken(); const { message } = App.useApp();
  const [tab, setTab] = useState("json"); const [copyError, setCopyError] = useState(false);
  const json = JSON.stringify({ eventId: "example-delivery-001", data: { reference: "EXAMPLE-42" } }, null, 2);
  const curl = `curl --request POST '${url}' \\\n  --header 'Authorization: Bearer <secret>' \\\n  --header 'Content-Type: application/json' \\\n  --data '${json}'`;
  const value = tab === "json" ? json : curl;
  async function copy() {
    try { await navigator.clipboard.writeText(value); setCopyError(false); void message.success("Copied."); }
    catch { setCopyError(true); }
  }
  return <Flex component="section" vertical gap={token.paddingXS} aria-label="Webhook request example">
    <Typography.Title level={5} style={{ margin: 0 }}>Send a signal</Typography.Title>
    <Typography.Text type="secondary">Example values only. Replace the data with your payload and use a unique eventId for each signal. Send Authorization: Bearer &lt;secret&gt; with Content-Type: application/json. Optional occurredAt must be UTC. Accepted signals run asynchronously under normal policy and approvals.</Typography.Text>
    <Tabs activeKey={tab} onChange={key => { setTab(key); setCopyError(false); }} size="small" items={[
      { key: "json", label: "JSON body" }, { key: "curl", label: "cURL request" }
    ]} />
    <Input.TextArea readOnly aria-label={tab === "json" ? "Example JSON body" : "Example cURL request"} value={value} autoSize={{ minRows: 6, maxRows: 12 }} />
    {tab === "curl" ? <Typography.Text type="secondary">Replace &lt;secret&gt; with the credential you copied when creating, rotating or reactivating this Event.</Typography.Text> : null}
    {copyError ? <Alert type="error" showIcon title="Copy failed. Select the example and copy it manually." /> : null}
    <Button style={{ alignSelf: "flex-start" }} onClick={() => void copy()}>Copy {tab === "json" ? "JSON body" : "cURL request"}</Button>
  </Flex>;
}
