import { act, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppRouter } from './AppRouter';
import { navigateToAppPath } from './appRoute';
import { navigateFromBrowserHistory, suspendLiveSessionForNavigation } from '../services/realtime';
vi.mock('../services/realtime', () => ({ navigateFromBrowserHistory: vi.fn(), suspendLiveSessionForNavigation: vi.fn().mockResolvedValue(undefined) }));
vi.mock('../features/access/AccessApp', () => ({ AccessApp: ({ route }: { route: { view: string } }) => <div>Access {route.view}</div> }));
vi.mock('../features/chat/TenantChatEntry', () => ({ TenantChatEntry: ({ tenantId }: { tenantId: string }) => <div>Tenant Chat {tenantId}</div> }));
const tenant = '019944af-00d1-7000-8000-000000000001';
beforeEach(() => { vi.clearAllMocks(); window.history.replaceState(null, '', '/'); });
describe('closed browser entry surface', () => {
  it('enters Account selection from root without mounting Chat', () => {
    render(<AppRouter />);
    expect(screen.getByText('Access account')).toBeVisible();
    expect(window.location.pathname).toBe('/account/organizations');
    expect(navigateFromBrowserHistory).not.toHaveBeenCalled();
  });
  it.each(['/admin', `/admin/instances/${tenant}/activity`, `/c/${tenant}?token=obsolete`, '/unknown'])('never mounts Chat for %s', path => {
    window.history.replaceState(null, '', path); render(<AppRouter />);
    expect(screen.getByText('Access not-found')).toBeVisible();
    expect(screen.queryByText(/Tenant Chat/)).not.toBeInTheDocument();
    expect(navigateFromBrowserHistory).not.toHaveBeenCalled();
    expect(suspendLiveSessionForNavigation).toHaveBeenCalled();
  });
  it('lets the tenant entry bootstrap once and delegates same-tenant history changes', () => {
    window.history.replaceState(null, '', `/t/${tenant}`); render(<AppRouter />);
    expect(screen.getByText(`Tenant Chat ${tenant}`)).toBeVisible();
    expect(navigateFromBrowserHistory).not.toHaveBeenCalled();
    act(() => navigateToAppPath(`/t/${tenant}/c/${tenant}`));
    expect(navigateFromBrowserHistory).toHaveBeenCalledTimes(1);
  });
  it('suspends and returns to the exact conversation through same-organization management', () => {
    window.history.replaceState(null, '', `/t/${tenant}/c/${tenant}`); render(<AppRouter />);
    act(() => navigateToAppPath(`/t/${tenant}/admin/agents`));
    expect(screen.getByText('Access tenant')).toBeVisible();
    expect(suspendLiveSessionForNavigation).toHaveBeenCalled();
    act(() => navigateToAppPath(`/t/${tenant}/c/${tenant}`));
    expect(screen.getByText(`Tenant Chat ${tenant}`)).toBeVisible();
    // A remount owns bootstrap. Routing must not also attach the same Session.
    expect(navigateFromBrowserHistory).not.toHaveBeenCalled();
  });
});
