import { followAppLink } from "../../app/appLink";
import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Flex, Form, Input, Modal, Radio, Typography, theme } from 'antd';
import { decideEmailAccount, getEmailAccountRequest, type EmailAccountRequest } from '../../services/emailAccounts';
import { adminHomePath } from '../../app/appRoute';

export function EmailAccountPicker({ sessionId, connected }: { sessionId: string | null; connected: boolean }) {
  const { token } = theme.useToken(); const [request, setRequest] = useState<EmailAccountRequest | null>(null);
  const [selected, setSelected] = useState<string>(); const [reference, setReference] = useState('email');
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string>(); const [receipt, setReceipt] = useState<string>();
  const previousFocus = useRef<HTMLElement | null>(null); const receiptFocus = useRef<HTMLDivElement>(null);
  const current = useRef(request); current.current = request;
  const version = useRef(0); const requestSession = useRef(sessionId); requestSession.current = sessionId;
  const decided = useRef(new Set<string>());
  const deciding = useRef(false);
  const shown = request !== null && connected;
  function acceptRequest(result: EmailAccountRequest | null) {
    if (result?.requestId !== current.current?.requestId) {
      if (result) { previousFocus.current = document.activeElement as HTMLElement; setSelected(undefined); setReference('email'); setError(undefined); }
      else if (current.current) setReceipt('Account request is no longer current. Send a new user turn to retry.');
    } else setSelected(choice => result?.choices.some(c => c.accountId === choice) ? choice : undefined);
    current.current = result; setRequest(result);
  }
  useEffect(() => {
    const generation = ++version.current; const controller = new AbortController();
    deciding.current = false; decided.current.clear(); current.current = null; setBusy(false);
    setRequest(null); setSelected(undefined); setReceipt(undefined); setError(undefined);
    if (!sessionId || !connected) return () => controller.abort();
    let timer: ReturnType<typeof setTimeout>; let cancelled = false;
    async function poll() {
      try {
        const result = await getEmailAccountRequest(sessionId!, controller.signal);
        if (cancelled || generation !== version.current) return;
        if (deciding.current || (result && decided.current.has(result.requestId))) return;
        acceptRequest(result);
      } catch { if (!cancelled && current.current) setError('Account request could not be refreshed. Reconnect or retry.'); }
      finally { if (!cancelled) timer = setTimeout(() => void poll(), 2000); }
    }
    void poll(); return () => { cancelled = true; controller.abort(); clearTimeout(timer); };
  }, [sessionId, connected]);
  async function decide(accountId: string | null) {
    if (!request || !sessionId) return; const sourceSession = sessionId; const generation = version.current;
    if (accountId !== null && !request.choices.some(c => c.accountId === accountId)) return;
    deciding.current = true; setBusy(true); setError(undefined);
    const choice = request.choices.find(c => c.accountId === accountId);
    try {
      const result = await decideEmailAccount(sessionId, request, accountId, choice?.accountRef ?? reference);
      if (requestSession.current !== sourceSession || generation !== version.current) return;
      decided.current.add(request.requestId); current.current = null;
      setRequest(null); setSelected(undefined); setReceipt(result.message);
    } catch (e) { if (requestSession.current === sourceSession && generation === version.current) setError(e instanceof Error ? e.message : 'Account selection failed. Retry.'); }
    finally { if (generation === version.current) { deciding.current = false; setBusy(false); } }
  }
  const aliasValid = /^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$/.test(reference);
  async function refresh() {
    if (!sessionId) return;
    const generation = version.current;
    try {
      const result = await getEmailAccountRequest(sessionId);
      if (generation !== version.current || deciding.current || (result && decided.current.has(result.requestId))) return;
      acceptRequest(result); setError(undefined);
    } catch { if (generation === version.current) setError('Refresh failed. Reconnect and retry.'); }
  }
  return <>
    {receipt && <div className="conversation-column conversation-banners" ref={receiptFocus} tabIndex={-1}><Alert type="info" showIcon title={receipt} closable onClose={() => setReceipt(undefined)} /></div>}
    <Modal className="admin-credential-dialog" centered open={shown} title="Choose an email account" okText="Allow account" cancelText="Cancel"
      onOk={() => void decide(selected!)} onCancel={() => void decide(null)} confirmLoading={busy}
      closable={!busy} keyboard={!busy} cancelButtonProps={{ disabled: busy }} mask={{ closable: false }}
      okButtonProps={{ disabled: busy || !request?.choices.some(c => c.accountId === selected) || (!request?.choices.find(c => c.accountId === selected)?.accountRef && !aliasValid) }}
      afterOpenChange={open => { if (!open) { if (receipt) receiptFocus.current?.focus(); else previousFocus.current?.focus(); } }}>
      <Flex vertical gap={token.paddingSM}>
        <Typography.Paragraph>Allow {request?.agentName ?? 'this agent'} to use one account for authorized email tools. Account access does not enable additional tools.</Typography.Paragraph>
        {error && <Alert type="error" showIcon title={error} action={<Button disabled={busy} onClick={() => void refresh()}>Refresh</Button>} />}
        {request?.choices.length === 0 ? <Alert type="info" showIcon title="No active email accounts" description={<Flex vertical gap={token.paddingXS}><span>Add or enable an account in Connections → Credentials, then choose it here.</span><Button href={adminHomePath('credentials')} onClick={followAppLink}>Manage credentials</Button></Flex>} /> :
          <Radio.Group value={selected} disabled={busy} onChange={e => setSelected(e.target.value as string)} aria-label="Email account">
            <Flex vertical gap={token.paddingSM}>{request?.choices.map(c => <Radio key={c.accountId} value={c.accountId}><Flex vertical style={{ overflowWrap: 'anywhere' }}>
              <Typography.Text strong>{c.displayName}</Typography.Text><Typography.Text>{c.emailAddress}</Typography.Text>
              <Typography.Text type="secondary">{c.preset}{c.accountRef ? ` · Bound as ${c.accountRef}` : ' · New account grant'}</Typography.Text>
            </Flex></Radio>)}</Flex>
          </Radio.Group>}
        {selected && !request?.choices.find(c => c.accountId === selected)?.accountRef && <Form layout="vertical"><Form.Item label="Account reference" extra="Stable alias for this agent. Use 1–64 letters, digits or hyphens."><Input aria-label="Account reference" value={reference} disabled={busy} onChange={e => setReference(e.target.value)} /></Form.Item></Form>}
      </Flex>
    </Modal>
  </>;
}
