import { useId } from 'react';
import { Flex, Input, Typography } from 'antd';

// Shared literal content in exact-action consent; never interpreted as markup.
export function SavedApprovalText({ label, text }: { label: string; text: string }) {
  const id = useId();
  return <Flex vertical gap="var(--ac-space-compact)"><label htmlFor={id}>{label}</label><Input.TextArea id={id} readOnly value={text} autoSize={{ minRows: 3, maxRows: 10 }} />{text.length === 0 && <Typography.Text type="secondary">Empty text</Typography.Text>}</Flex>;
}
