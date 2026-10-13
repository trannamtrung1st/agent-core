import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { InstanceConfigurationActions, InstanceConfigurationProvider, useInstanceConfigurationEditor } from './InstanceConfigurationActions';
afterEach(cleanup);
function Editor({ name, persist }: { name: string; persist: (revision: number) => Promise<number> }) {
  const [value, setValue] = useState('inherited'); const [saved, setSaved] = useState('inherited'); const [revision, setRevision] = useState(1);
  useInstanceConfigurationEditor(name, { dirty: value !== saved, busy: false, revision, save: async current => { const next = await persist(current); setSaved(value); setRevision(next); return next; }, discard: () => setValue(saved) });
  return <input aria-label={name} value={value} onChange={event => setValue(event.target.value)} />;
}
function fixture(persist: Array<(revision: number) => Promise<number>>, key = 'owner') {
  return <InstanceConfigurationProvider key={key} revision={1} disabled={false} onUpdated={() => {}}><InstanceConfigurationActions />{persist.map((save, i) => <Editor key={i} name={`Page ${i + 1}`} persist={save} />)}</InstanceConfigurationProvider>;
}
function edit(count: number) { for (let i = 1; i <= count; i++) fireEvent.change(screen.getByLabelText(`Page ${i}`), { target: { value: `draft ${i}` } }); }
it('saves retained edits across pages in order with the preceding write receipt', async () => {
  const first = vi.fn(async () => 2); const second = vi.fn(async () => 3); render(fixture([first, second])); edit(2);
  fireEvent.click(screen.getByRole('button', { name: 'Save configuration' }));
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Configuration saved.'));
  expect(first).toHaveBeenCalledWith(1); expect(second).toHaveBeenCalledWith(2); expect(screen.getByRole('button', { name: 'Save configuration' })).toBeDisabled();
});
it('stops after a failed write, reports partial success and retains uncommitted fields', async () => {
  const first = vi.fn(async () => 2); const second = vi.fn(async () => { throw new Error('Revision conflict.'); }); const third = vi.fn(async () => 4); render(fixture([first, second, third])); edit(3);
  fireEvent.click(screen.getByRole('button', { name: 'Save configuration' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('1 configuration group saved before the failure. Revision conflict.');
  expect(second).toHaveBeenCalledWith(2); expect(third).not.toHaveBeenCalled(); expect(screen.getByLabelText('Page 2')).toHaveValue('draft 2'); expect(screen.getByLabelText('Page 3')).toHaveValue('draft 3');
  fireEvent.click(screen.getByRole('button', { name: 'Discard configuration changes' })); expect(screen.getByLabelText('Page 1')).toHaveValue('draft 1'); expect(screen.getByLabelText('Page 2')).toHaveValue('inherited');
});
it('does not issue a following write after the exact Instance owner is replaced', async () => {
  let release!: (revision: number) => void; const first = vi.fn(() => new Promise<number>(resolve => { release = resolve; })); const second = vi.fn(async () => 3); const { rerender } = render(fixture([first, second])); edit(2);
  fireEvent.click(screen.getByRole('button', { name: 'Save configuration' })); await waitFor(() => expect(first).toHaveBeenCalled()); rerender(fixture([first, second], 'another-owner'));
  await act(async () => release(2)); expect(second).not.toHaveBeenCalled(); expect(screen.getByLabelText('Page 2')).toHaveValue('inherited');
});
