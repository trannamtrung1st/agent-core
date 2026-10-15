import { fireEvent, render, screen } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { expect, test, vi } from 'vitest';
import { IsolationPresetControl } from './IsolationPresetControl';

test.each(['Definition', 'Instance'] as const)('%s: Custom is selectable without changing data and survives a cancelled replacement', async scope => {
  const onSelect = vi.fn();
  render(<ConfigProvider theme={{ token: { motion: false } }}><IsolationPresetControl value="Persistent" disabled={false} dirty scope={scope} onSelect={onSelect} /></ConfigProvider>);
  const select = screen.getByRole('combobox', { name: 'Isolation preset' });
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Custom'));
  expect(onSelect).not.toHaveBeenCalled();
  expect(screen.getByText(/Custom keeps the current values/)).toBeVisible();
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Stateless · new Session for each request'));
  fireEvent.click(await screen.findByRole('button', { name: 'Keep editing' }));
  expect(screen.getByText(/Custom keeps the current values/)).toBeVisible();
  expect(onSelect).not.toHaveBeenCalled();
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Stateless · new Session for each request'));
  fireEvent.click(await screen.findByRole('button', { name: 'Replace draft' }));
  expect(onSelect).toHaveBeenCalledWith('Stateless');
  expect(screen.queryByText(/Custom keeps the current values/)).not.toBeInTheDocument();
});
