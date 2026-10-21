import { tenantApi, type TenantContext } from '../../services/identity';
import { AuthorizationAuditCollection } from './AuthorizationAuditCollection';

export function TenantAudit({ context }: { context: TenantContext }) {
  return <AuthorizationAuditCollection key={context.tenant.tenantId} endpoint={tenantApi(context.tenant.tenantId, '/audit')} scope="organization" />;
}

export function SystemAudit() {
  return <AuthorizationAuditCollection endpoint="/api/v2/system/audit" scope="platform" />;
}
