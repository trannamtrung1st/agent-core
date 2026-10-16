import { Alert, Button, Empty, Skeleton } from 'antd';
import { IdentityError, IdentityCompletionError } from '../../services/identity';
export const accessErrorMessage = (error: unknown) => error instanceof IdentityError || error instanceof IdentityCompletionError ? error.message : 'The service could not be reached. Check your connection and try again.';
export function AccessError({ error, retry }: { error: unknown; retry?: () => void }) {
  return <Alert type="error" showIcon title={accessErrorMessage(error)} role="alert" action={error instanceof IdentityCompletionError ? <Button onClick={() => window.location.reload()}>Reload page</Button> : retry && (!(error instanceof IdentityError) || error.status >= 500 || error.status === 409 && ['stale_revision', 'stale_decision', 'resource_changed'].includes(error.reasonCode)) ? <Button onClick={retry}>Reload</Button> : undefined} />;
}
export function AccessLoading() { return <div aria-label="Loading access" role="status"><Skeleton active paragraph={{ rows: 4 }} /></div>; }
export function AccessUnavailable() { return <Empty description="This page is unavailable. Choose another organization or contact its owner."><Button href="/account/organizations">My organizations</Button></Empty>; }
