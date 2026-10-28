import { afterEach, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useRef, useState } from 'react';
import { App, Button } from 'antd';
import { SessionSandboxFrame } from './SessionSandboxFrame';
afterEach(cleanup);
it('closes only the nested inspector and restores its opener inside the remaining dialog', async () => {
  function Fixture() {
    const [parent, setParent] = useState(true); const [child, setChild] = useState(false); const opener = useRef<HTMLButtonElement>(null);
    return <SessionSandboxFrame open={parent} narrow={false} title="Process" onClose={() => setParent(false)}>
      <Button ref={opener} onClick={() => setChild(true)}>Inspect worker</Button>
      {child && <SessionSandboxFrame open narrow={false} title="Worker" openerRef={opener} onClose={() => setChild(false)}><Button>Worker evidence</Button></SessionSandboxFrame>}
    </SessionSandboxFrame>;
  }
  render(<App><Fixture /></App>); fireEvent.click(await screen.findByRole('button', { name: 'Inspect worker' }));
  // Ant Design intentionally reuses test-id for drawer titles in test mode; real browser names are checked separately.
  const child = (await screen.findByText('Worker', { exact: true })).closest('[role="dialog"]') as HTMLElement; const control = within(child).getByRole('button', { name: 'Worker evidence' }); control.focus(); fireEvent.keyDown(control, { key: 'Escape' });
  await waitFor(() => expect(screen.queryByText('Worker', { exact: true })).not.toBeInTheDocument());
  expect(screen.getByText('Process', { exact: true })).toBeInTheDocument(); await waitFor(() => expect(screen.getByRole('button', { name: 'Inspect worker' })).toHaveFocus());
});
