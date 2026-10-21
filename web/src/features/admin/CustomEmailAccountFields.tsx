import { Alert,Flex,Form,Input,InputNumber,Select,Typography,theme } from 'antd';
import type { EmailAccountProfile,SystemCredential } from '../../services/adminApi';
export type CustomEmailAccountFieldsValue = {
  displayName: string;
  status: string;
  profile: EmailAccountProfile;
  protectedValue?: string;
};
export const custom: EmailAccountProfile = {
  preset: 'Custom',
  emailAddress: '',
  imapUsername: '',
  smtpUsername: '',
  imapEndpoint: { host: '', port: 993, tlsMode: 'ImplicitTls' },
  smtpEndpoint: { host: '', port: 465, tlsMode: 'ImplicitTls' },
};
export function CustomEmailAccountFields({ account }: { account?: SystemCredential }) {
 const { token } = theme.useToken();
 return <>
      <Form.Item
        name="displayName"
        label="Display name"
        rules={[{ required: true, max: 120 }]}
      >
        <Input />
      </Form.Item>
      {account && (
        <Form.Item name="status" label="Credential status">
          <Select
            options={[
              { value: 'Active', label: 'Active' },
              { value: 'Disabled', label: 'Disabled' },
            ]}
          />
        </Form.Item>
      )}
          <Form.Item
            name={['profile', 'emailAddress']}
            label="Sender email address"
            rules={[{ required: true, type: 'email', max: 254 }]}
            extra="This account supplies From. The agent cannot choose another sender."
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Alert
            type="info"
            showIcon
            title="Custom endpoints require the operator’s host/port allowlist"
            description="Only public DNS destinations and verified TLS are allowed. Local/private mail servers are blocked."
          />
          {(['imap', 'smtp'] as const).map((protocol) => (
            <Flex vertical gap={token.paddingXS} key={protocol}>
              <Typography.Text strong>
                {protocol === 'imap' ? 'Incoming (IMAP)' : 'Outgoing (SMTP)'}
              </Typography.Text>
              <Form.Item
                name={['profile', `${protocol}Username`]}
                label={`${protocol.toUpperCase()} username`}
                rules={[{ required: true, max: 254 }]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'host']}
                label={`${protocol.toUpperCase()} host`}
                rules={[{ required: true, max: 253 }]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'port']}
                label={`${protocol.toUpperCase()} port`}
                rules={[{ required: true }]}
              >
                <InputNumber min={1} max={65535} />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'tlsMode']}
                label={`${protocol.toUpperCase()} TLS`}
                rules={[{ required: true }]}
              >
                <Select
                  options={[
                    { value: 'ImplicitTls', label: 'Required implicit TLS' },
                    { value: 'StartTls', label: 'Required STARTTLS' },
                  ]}
                />
              </Form.Item>
            </Flex>
          ))}
          {!account && (
            <Form.Item
              name="protectedValue"
              label="Password"
              rules={[{ required: true }]}
              extra="Encrypted on the server. Never read back or shared with the agent."
            >
              <Input.Password
                visibilityToggle={false}
                autoComplete="new-password"
                spellCheck={false}
              />
            </Form.Item>
          )}

 </>;
}
