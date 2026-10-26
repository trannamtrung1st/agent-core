import { Alert, Button } from 'antd';
import { AccessError } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

export function DecisionServiceReadiness({ path }: { path: string | null }) {
  const readiness = useIdentityQuery<{ status: string; engineId: string; engineVersion: string; shadowMode: boolean; preAdmissionSupported: boolean }>(path);
  if (!path) return null;
  if (readiness.error) return <AccessError error={readiness.error} retry={readiness.reload} />;
  return <Alert className="decision-readiness" type={readiness.data?.status === 'ready' && !readiness.data.shadowMode ? 'success' : 'info'}
    title={readiness.loading ? 'Checking decision service…' : `Decision service: ${readiness.data?.status ?? 'unknown'}`}
    description={readiness.data ? `${readiness.data.engineId} · ${readiness.data.shadowMode ? 'Shadow mode; recommendations do not change routing' : 'Effective mode'} · ${readiness.data.preAdmissionSupported ? 'Supports admission routing' : 'Admitted Run decisions only'}` : undefined}
    action={<Button disabled={readiness.loading} onClick={readiness.reload}>Check readiness</Button>} />;
}
