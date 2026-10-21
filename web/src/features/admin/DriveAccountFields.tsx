import { Form,Input,Select } from 'antd';
import {
type SystemCredential
} from '../../services/adminApi';

export function DriveAccountFields({
  account,
}: {
  account?: SystemCredential;
}) {
  return (
    <>
      {' '}
      <Form.Item
        label="Display name"
        name="displayName"
        rules={[{ required: true, max: 120 }]}
      >
        <Input autoComplete="off" />
      </Form.Item>
      {account && (
        <Form.Item label="Status" name="status">
          <Select
            options={[
              { value: 'Active', label: 'Active' },
              { value: 'Disabled', label: 'Disabled' },
            ]}
          />
        </Form.Item>
      )}
    </>
  );
}
