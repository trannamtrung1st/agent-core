import type { TenantContext } from '../../services/identity';
import { TenantWorkspaceFiles } from '../files/TenantWorkspaceFiles';

export function TenantAgentWorkspace({ context, id }: { context: TenantContext; id: string }) {
  return <TenantWorkspaceFiles key={`${context.tenant.tenantId}:${id}`} owner={{ kind: 'home', tenantId: context.tenant.tenantId, instanceId: id }} organization={context.tenant.name} />;
}
