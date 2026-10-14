import { useEffect, useState } from 'react';
import { Alert, Button, Descriptions, Flex, Select, Spin, Tag, Typography, theme } from 'antd';
import { getSandboxPlatform, type SandboxPlatform } from '../../services/sandboxes';
import { followAppLink } from '../../app/appLink';
import type { DefinitionCandidate } from './definitionCandidate';
import { SandboxProfileDetails } from './SandboxProfileDetails';
import { useAdminDetailLayout } from './useAdminDetailLayout';

type Policy = { defaultProfileId: string; allowedProfileIds: string[] };
export function DefinitionSandboxPolicy({ candidate, disabled, readOnly, onChange }: {
  candidate: DefinitionCandidate; disabled?: boolean; readOnly?: boolean; onChange?: (value: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const layout = useAdminDetailLayout();
  const [platform, setPlatform] = useState<SandboxPlatform | null>(null);
  const [error, setError] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const policy = candidate.sandboxPolicy as Policy | null | undefined;
  useEffect(() => {
    const controller = new AbortController();
    setPlatform(null); setError(false);
    getSandboxPlatform(controller.signal).then(value => {
      if (!controller.signal.aborted) setPlatform(value);
    }).catch(() => { if (!controller.signal.aborted) setError(true); });
    return () => controller.abort();
  }, [attempt]);
  const profiles = platform?.profiles;
  const options = [...new Map([
    ...(profiles ?? []).map(profile => [profile.id, { value: profile.id, label: `${profile.name}${profile.enabled ? '' : ' · Disabled'}` }] as const),
    ...(policy?.allowedProfileIds ?? []).filter(id => !profiles?.some(profile => profile.id === id))
      .map(id => [id, { value: id, label: `${id} · Unavailable` }] as const)
  ]).values()];
  const profile = profiles?.find(value => value.id === policy?.defaultProfileId);
  const label = (id: string) => options.find(option => option.value === id)?.label ?? id;
  return <Flex vertical gap={token.padding}>
    <section className="admin-draft-form-section admin-definition-settings" aria-label="Definition sandbox profiles">
      <Typography.Text type="secondary">Profile choices authorize environments. Sandbox capabilities separately authorize commands. Instances may apply bounded overrides and a supplement script; new Runs pin the resolved setup.</Typography.Text>
      {error && <Alert type="warning" showIcon title="Profile inventory unavailable"
        description="Existing selections are preserved. Retry to inspect current Global profiles."
        action={<Button onClick={() => setAttempt(value => value + 1)}>Retry profiles</Button>} />}
      {!platform && !error && <Spin aria-label="Loading sandbox profiles" />}
      {readOnly ? <Descriptions {...layout} bordered size="small" column={1}>
        <Descriptions.Item label="Allowed sandbox profiles">{policy?.allowedProfileIds.map(label).join(', ') || 'No explicit profile policy'}</Descriptions.Item>
        <Descriptions.Item label="Default sandbox profile">{policy?.defaultProfileId ? label(policy.defaultProfileId) : 'No explicit default'}</Descriptions.Item>
      </Descriptions> : <>
        <label className="admin-draft-field"><Typography.Text strong>Allowed sandbox profiles</Typography.Text>
          <Select aria-label="Allowed sandbox profiles" mode="multiple" value={policy?.allowedProfileIds ?? []} options={options} disabled={disabled || !profiles}
            onChange={allowedProfileIds => onChange?.({ ...candidate, sandboxPolicy: allowedProfileIds.length ? {
              allowedProfileIds, defaultProfileId: policy && allowedProfileIds.includes(policy.defaultProfileId) ? policy.defaultProfileId : allowedProfileIds[0]
            } : null })} />
        </label>
        <label className="admin-draft-field"><Typography.Text strong>Default sandbox profile</Typography.Text>
          <Select aria-label="Default sandbox profile" value={policy?.defaultProfileId} options={options.filter(option => policy?.allowedProfileIds.includes(option.value))}
            disabled={disabled || !profiles || !policy?.allowedProfileIds.length}
            onChange={defaultProfileId => onChange?.({ ...candidate, sandboxPolicy: { ...policy, defaultProfileId } })} />
        </label>
      </>}
      {!policy && <Typography.Text type="secondary">No explicit profile policy. Historical bounded sandbox.run compatibility may still apply where that capability is granted.</Typography.Text>}
      <a href="/admin/platform/sandboxes" onClick={followAppLink}>Manage Global Sandbox Profiles</a>
    </section>
    {platform && policy && <section className="admin-draft-form-section" aria-label="Default Global sandbox profile">
      <Typography.Title level={5}>Default Global profile</Typography.Title>
      {profile && <Typography.Text strong>{profile.name} · revision {profile.revision}</Typography.Text>}
      <Typography.Text type="secondary">Current Global configuration for reference. This Definition stores profile IDs; Runs resolve and pin a profile revision when admitted.</Typography.Text>
      <Flex wrap gap={token.paddingXS} align="center">
        <Tag color={platform.state === 'Ready' && profile?.enabled ? 'success' : 'default'}>{profile ? profile.enabled ? platform.state : 'Disabled' : 'Unavailable'}</Tag>
        <Typography.Text>{profile ? platform.message : 'The default profile is unavailable. Choose an available profile before publishing.'}</Typography.Text>
      </Flex>
      {profile && <SandboxProfileDetails profile={profile} />}
    </section>}
  </Flex>;
}
