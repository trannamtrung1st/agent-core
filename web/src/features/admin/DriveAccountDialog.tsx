import { useEffect, useState } from 'react';
import { Alert, Form, Input, Modal, Select } from 'antd';
import { createDriveAccount, getDriveOptions } from '../../services/drive';
import {
  updateCredential,
  type SystemCredential,
} from '../../services/adminApi';
export function DriveAccountDialog({
  open,
  account,
  onClose,
  onSaved,
}: {
  open: boolean;
  account?: SystemCredential;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const [form] = Form.useForm<{ displayName: string; status: string }>();
  const [busy, setBusy] = useState(false);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [disabled, setDisabled] = useState(false);
  useEffect(() => {
    setReady(false);
    setError(null);
    if (!open) return;
    let cancelled = false;
    void getDriveOptions()
      .then((options) => {
        if (!cancelled) setDisabled(!options.enabled);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [open]);
  async function save() {
    if (busy || !ready) return;
    try {
      const fields = await form.validateFields();
      setBusy(true);
      setError(null);
      if (account)
        await updateCredential(account, {
          ...fields,
          metadata: account.metadata,
          allowedOrigins: [],
        });
      else await createDriveAccount(fields.displayName);
      await onSaved();
      onClose();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : 'Drive account could not be saved. Retry.',
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      aria-label={
        account ? 'Edit Google Drive Account' : 'Add Google Drive Account'
      }
      open={open}
      centered
      destroyOnHidden
      title={account ? 'Edit Google Drive Account' : 'Add Google Drive Account'}
      afterOpenChange={(shown) => {
        if (shown) {
          form.resetFields();
          form.setFieldsValue({
            displayName: account?.displayName ?? '',
            status: account?.status ?? 'Active',
          });
          setReady(true);
        }
      }}
      onCancel={onClose}
      confirmLoading={busy}
      closable={!busy}
      keyboard={!busy}
      cancelButtonProps={{ disabled: busy }}
      okButtonProps={{ disabled: busy || !ready }}
      okText={account ? 'Save account' : 'Create account'}
      onOk={() => void save()}
    >
      {disabled && (
        <Alert
          type="info"
          showIcon
          title="Google Drive is disabled on this host"
          description="The account can be saved; the operator must enable Drive before connecting."
        />
      )}
      {error && <Alert type="error" showIcon title={error} />}
      <Form
        name="google-drive-account"
        form={form}
        layout="vertical"
        disabled={busy || !ready}
        preserve={false}
      >
        <DriveAccountFields account={account} />
      </Form>
    </Modal>
  );
}

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
