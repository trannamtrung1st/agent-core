import { Button, Flex, Form, Input, Select, theme } from 'antd';
import { MinusCircleOutlined, PlusOutlined } from '@ant-design/icons';
const kinds = [
  'Password',
  'ApiKey',
  'Token',
  'Certificate',
  'PrivateKey',
  'Generic',
];
export type CredentialFieldsValue = {
  displayName: string;
  kind: string;
  status: string;
  metadata: Array<{ key: string; value: string }>;
  origins: string;
  protectedValue?: string;
};
function metadata(rows: CredentialFieldsValue['metadata'] = []) {
  const result: Record<string, string> = Object.create(null);
  for (const { key, value } of rows) {
    if (
      Object.keys(result).some(
        (k) => k.toLowerCase() === key.trim().toLowerCase(),
      )
    )
      throw new Error('Metadata keys must be unique.');
    result[key.trim()] = value ?? '';
  }
  return result;
}
export function safeFields(values: CredentialFieldsValue) {
  return {
    displayName: values.displayName,
    status: values.status,
    metadata: metadata(values.metadata),
    allowedOrigins: values.origins
      .split(/\r?\n/)
      .map((o) => o.trim())
      .filter(Boolean),
  };
}
export function CredentialFields({ create, modelInference = false }: { create: boolean; modelInference?: boolean }) {
  const { token } = theme.useToken();
  return (
    <>
      <Form.Item
        name="displayName"
        label="Display name"
        rules={[{ required: true, max: 120 }]}
      >
        <Input autoComplete="off" />
      </Form.Item>
      {create ? (
        <Form.Item
          key="kind"
          name="kind"
          label="Kind"
          rules={[{ required: true }]}
        >
          <Select disabled={modelInference} options={(modelInference ? ['ApiKey'] : kinds).map((value) => ({ value, label: value }))} />
        </Form.Item>
      ) : (
        <Form.Item key="status" name="status" label="Status">
          <Select
            options={[
              { value: 'Active', label: 'Active' },
              { value: 'Disabled', label: 'Disabled' },
            ]}
          />
        </Form.Item>
      )}
      <Form.Item
        label="Metadata"
        extra={modelInference ? "Metadata is non-secret. Never enter keys here." : "Metadata is non-secret and visible to bound agents. It never grants permissions."}
      >
        <Form.List name="metadata">
          {(fields, { add, remove }) => (
            <Flex vertical gap={token.paddingXS}>
              {fields.map((field) => (
                <Flex
                  key={field.key}
                  className="admin-form-row credential-metadata-row"
                  gap={token.paddingXS}
                  align="start"
                >
                  <Form.Item
                    name={[field.name, 'key']}
                    rules={[{ required: true, max: 64 }]}
                    style={{ flex: 1, minWidth: 0 }}
                  >
                    <Input
                      aria-label={`Metadata key ${field.name + 1}`}
                      placeholder="Key"
                    />
                  </Form.Item>
                  <Form.Item
                    name={[field.name, 'value']}
                    rules={[{ max: 2048 }]}
                    style={{ flex: 2, minWidth: 0 }}
                  >
                    <Input
                      aria-label={`Metadata value ${field.name + 1}`}
                      placeholder="Value"
                    />
                  </Form.Item>
                  <Button
                    type="text"
                    icon={<MinusCircleOutlined />}
                    aria-label={`Remove metadata ${field.name + 1}`}
                    onClick={() => remove(field.name)}
                  />
                </Flex>
              ))}
              <Button
                type="dashed"
                icon={<PlusOutlined />}
                disabled={fields.length >= 32}
                onClick={() => add({ key: '', value: '' })}
              >
                Add metadata
              </Button>
            </Flex>
          )}
        </Form.List>
      </Form.Item>
      {!modelInference && <Form.Item
        name="origins"
        label="Allowed origins"
        extra="One exact HTTP(S) origin per line. No paths or wildcards."
      >
        <Input.TextArea rows={3} autoComplete="off" />
      </Form.Item>}
      {create && <ProtectedValueField />}
    </>
  );
}

export function ProtectedValueField({ email = false }: { email?: boolean }) {
  return (
    <Form.Item
      name="protectedValue"
      label={email ? 'Password' : 'Protected value'}
      rules={[{ required: true }]}
      extra="Protected values cannot be read back. Replace the value if it changes."
    >
      <Input.Password
        visibilityToggle={false}
        autoComplete="new-password"
        spellCheck={false}
      />
    </Form.Item>
  );
}
