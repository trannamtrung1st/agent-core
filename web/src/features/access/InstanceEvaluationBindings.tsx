import { Empty, Flex, Tag, Typography } from 'antd';
import { hookLabels, type EvaluationBinding } from '../../services/tenantEvaluations';
import { tenantApi } from '../../services/identity';
import { policyLabels } from './EvaluationBindingsForm';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
export function InstanceEvaluationBindings({ tenant, instance }: { tenant: string; instance: string }) {
  const state = useIdentityQuery<{ items: { binding: EvaluationBinding; suiteName: string; source: string }[] }>(tenantApi(tenant, `/agents/${instance}/evaluation-bindings`));
  return <section aria-label="Inherited evaluation bindings"><Flex vertical gap="var(--ac-space-compact)"><Typography.Title level={3}>Evaluation bindings</Typography.Title>
    <Typography.Paragraph type="secondary">Effective exact versions apply to future admissions. Use Evaluation hooks above to select permitted Instance bindings. Required Definition policies and organization defaults remain enforced; selections grant no execution authority.</Typography.Paragraph>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : !state.data?.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No evaluation hooks configured." /> : state.data.items.map((item, index) => <Flex key={index} vertical gap="var(--ac-space-compact)"><Typography.Text strong>{item.suiteName} · v{item.binding.version}</Typography.Text><Typography.Text type="secondary">{item.source}</Typography.Text><Flex wrap gap="var(--ac-space-compact)"><Tag>{hookLabels[item.binding.hook]}</Tag><Tag>{policyLabels[item.binding.policy]}</Tag></Flex></Flex>)}
  </Flex></section>;
}
