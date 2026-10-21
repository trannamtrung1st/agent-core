import { vi } from 'vitest';
import type { ActivitySource } from '../../services/activitySource';
export const fixtureTenant = '11111111-1111-1111-1111-111111111111';
export function activityFixture(): ActivitySource {
  return { scope: `${fixtureTenant}:person:instance`, tenantId: fixtureTenant, readScope: `${fixtureTenant}:person`,
    listSessions: vi.fn(), getSession: vi.fn(), listBackground: vi.fn(), getBackground: vi.fn(), continue: vi.fn(),
    listRuns: vi.fn(), listInstanceRuns: vi.fn(), getInstanceRun: vi.fn(), actions: { cancel: vi.fn(), read: vi.fn() } };
}
