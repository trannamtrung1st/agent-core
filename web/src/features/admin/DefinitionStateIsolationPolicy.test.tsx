import { useState } from 'react';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { expect, test, vi } from 'vitest';
import { DefinitionStateIsolationPolicy } from './DefinitionStateIsolationPolicy';
import { applyDefinitionIsolationPreset, definitionIsolationChanges, definitionIsolationPreset } from './definitionIsolationPresets';
import type { DefinitionCandidate } from './definitionCandidate';

const saved: DefinitionCandidate = { identity: { name: 'Keep identity' }, agentInstructions: 'Keep instructions', executionPolicy: { toolAllowlist: ['workspace.read'] },
  memoryPolicy: { sessionMemory: true, identityUserPromotion: true, identityUserRetrieval: true, userPromotion: false, userRetrieval: false },
  voice: { enabled: true, voiceId: 'saved-voice', speakingRate: 1.2 }, providerPreferences: { speechRecognizer: 'saved-stt', speechSynthesizer: 'saved-tts' } };

test('Definition recipes coordinate all owned settings and preserve unrelated authority', () => {
  const stateless = applyDefinitionIsolationPreset(saved, saved, 'Stateless');
  expect(definitionIsolationPreset(stateless, saved)).toBe('Stateless');
  expect(stateless.memoryPolicy).toEqual({ sessionMemory: false, identityUserPromotion: false, identityUserRetrieval: false, userPromotion: false, userRetrieval: false });
  expect(stateless.voice).toEqual({ enabled: false, voiceId: 'saved-voice', speakingRate: 1.2 });
  expect(stateless.providerPreferences).toEqual({ speechRecognizer: null, speechSynthesizer: null });
  expect(stateless.identity).toEqual(saved.identity); expect(stateless.agentInstructions).toBe(saved.agentInstructions); expect(stateless.executionPolicy).toEqual(saved.executionPolicy);
  expect(definitionIsolationChanges(stateless, saved).map(change => change.owner)).toEqual(expect.arrayContaining(['stateIsolationPolicy', 'memoryPolicy', 'voice', 'providerPreferences']));
  const session = applyDefinitionIsolationPreset(stateless, saved, 'SessionIsolated');
  expect(definitionIsolationPreset(session, saved)).toBe('SessionIsolated');
  expect(session.memoryPolicy).toEqual({ sessionMemory: true, identityUserPromotion: false, identityUserRetrieval: false, userPromotion: false, userRetrieval: false });
  expect(session.voice).toEqual(saved.voice); expect(session.providerPreferences).toEqual(saved.providerPreferences);
  const persistent = applyDefinitionIsolationPreset(session, saved, 'Persistent');
  expect(definitionIsolationPreset(persistent, saved)).toBe('Persistent'); expect(persistent.memoryPolicy).toEqual(saved.memoryPolicy);
  expect(persistent.stateIsolationPolicy).toMatchObject({ agentStateVisibility: 'InstanceSummary', maxAgentStateVisibility: 'InstanceSummary' });
  expect(definitionIsolationPreset({ ...session, voice: { ...session.voice as object, enabled: false } }, saved)).toBe('Custom');
  expect(definitionIsolationPreset({ ...session, memoryPolicy: { ...session.memoryPolicy as object, userRetrieval: true } }, saved)).toBe('Custom');
});

function Fixture() {
  const [candidate, setCandidate] = useState(saved);
  return <ConfigProvider theme={{ token: { motion: false } }}><DefinitionStateIsolationPolicy candidate={candidate} savedCandidate={saved} onChange={setCandidate} /></ConfigProvider>;
}
test('Definition presets remain editable drafts and replacement requires confirmation', async () => {
  render(<Fixture />);
  const select = screen.getByRole('combobox', { name: 'Isolation preset' });
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Stateless · new Session for each request'));
  expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).not.toBeChecked();
  const preview = screen.getByRole('region', { name: 'Isolation change preview' });
  expect(preview).toHaveTextContent('Voice'); expect(preview).toHaveTextContent('Memory policy'); expect(preview).toHaveTextContent('Definition draft');
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Persistent assistant'));
  const dialog = (await screen.findByText('Replace unsaved isolation changes?', { selector: '.ant-modal-title' })).closest('[role="dialog"]')!;
  expect(dialog).toHaveTextContent('Memory, Voice');
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).not.toBeChecked();
  fireEvent.mouseDown(select); fireEvent.click(await screen.findByText('Persistent assistant'));
  fireEvent.click(screen.getByRole('button', { name: 'Replace draft' }));
  await waitFor(() => expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).toBeChecked());
  expect(within(preview).getByText('State & Isolation · Maximum agent state visibility')).toBeVisible();
  expect(preview).toHaveTextContent('Off → Aggregate Instance summary');
});

test('published Definition preset and all policy controls stay read-only', () => {
  render(<DefinitionStateIsolationPolicy candidate={applyDefinitionIsolationPreset(saved, saved, 'Stateless')} readOnly />);
  expect(screen.getByText(/These Definition permissions do not activate Experience or autonomous identity maintenance/)).toBeVisible();
  expect(screen.getByRole('combobox', { name: 'Isolation preset' })).toBeDisabled();
  expect(screen.getByRole('combobox', { name: 'Default agent state visibility' })).toBeDisabled();
  expect(screen.getByRole('combobox', { name: 'Maximum agent state visibility' })).toBeDisabled();
  screen.getAllByRole('switch').forEach(control => expect(control).toBeDisabled());
  expect(screen.queryByRole('button', { name: 'Use Definition defaults' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Use compatibility defaults' })).not.toBeInTheDocument();
});

test('lowering a legacy default preserves its original Instance ceiling', async () => {
  const onChange = vi.fn();
  render(<DefinitionStateIsolationPolicy candidate={{ ...saved, stateIsolationPolicy: { agentStateVisibility: 'InstanceSummary' } }} onChange={onChange} />);
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Default agent state visibility' }));
  fireEvent.click(await screen.findByText('Off'));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ stateIsolationPolicy: expect.objectContaining({
    agentStateVisibility: 'Off', maxAgentStateVisibility: 'InstanceSummary'
  }) }));
});
