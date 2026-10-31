import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { DrawerProps } from 'antd';
import { SessionSandboxFrame } from './SessionSandboxFrame';

const lifecycle = vi.hoisted(() => ({ finishOpening: undefined as (() => void) | undefined }));
vi.mock('antd', async original => ({
  ...await original<object>(),
  // Drive the native motion-completion boundary after the visitor interacts.
  Drawer: ({ children, afterOpenChange }: DrawerProps) => {
    lifecycle.finishOpening = () => afterOpenChange?.(true);
    return <div role="dialog" aria-modal="true">{children}</div>;
  },
}));
afterEach(cleanup);

it('sets initial keyboard focus when opening finishes before interaction', () => {
  render(<SessionSandboxFrame open narrow={false} onClose={() => {}}><button>Reload tasks</button></SessionSandboxFrame>);
  act(() => lifecycle.finishOpening?.());
  expect(screen.getByRole('button', { name: 'Reload tasks' })).toHaveFocus();
});

it('preserves a field focused before opening motion finishes', () => {
  render(<SessionSandboxFrame open narrow={false} onClose={() => {}}><button>Reload tasks</button><input aria-label="Assigned Agent" /></SessionSandboxFrame>);
  const field = screen.getByRole('textbox', { name: 'Assigned Agent' });
  field.focus();
  act(() => lifecycle.finishOpening?.());
  expect(field).toHaveFocus();
});

it('leaves a newer protected dialog in control of focus', () => {
  render(<><SessionSandboxFrame open narrow={false} onClose={() => {}}><button>Reload tasks</button></SessionSandboxFrame><div role="dialog" aria-modal="true"><input aria-label="Newer inspector" /></div></>);
  const field = screen.getByRole('textbox', { name: 'Newer inspector' });
  field.focus();
  act(() => lifecycle.finishOpening?.());
  expect(field).toHaveFocus();
});
