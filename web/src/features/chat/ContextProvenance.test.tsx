import { fireEvent, render, screen, within } from '@testing-library/react';
import { App } from 'antd';
import { describe, expect, it, vi } from 'vitest';
import { AgentRunDetails } from './AgentRunDetails';
import { fixtureRun } from './agentRunFixtures';

describe('Run context provenance', () => {
  it('discloses metadata and instruction source without displaying raw extra payloads or editors', async () => {
    const manifest = { contextFormatVersion: 1, coreContractVersion: 1, fingerprint: 'fingerprint',
      fieldSources: { 'instructions.agentInstructions': { kind: 'Instance', revision: '7' } },
      sections: [ { id: 'AgentInstructions', layer: 'Runtime' as const, source: 'Instance', semantics: 'Instruction',
        sourceRevision: '7', characters: 100, utf8Bytes: 100, estimatedTokens: 25 } ],
      prompt: 'NEVER_RENDER_RAW_PROMPT', userText: 'NEVER_RENDER_USER_TEXT' };
    render(<App><AgentRunDetails showContextProvenance run={{ ...fixtureRun, contextManifest: manifest }} onChange={vi.fn()} /></App>);
    const region = screen.getByRole('region', { name: 'Context provenance' });
    expect(within(region).getByText('Agent instructions: Instance · 7')).toBeVisible();
    fireEvent.click(within(region).getByRole('button', { name: /Runtime/ }));
    expect(await within(region).findByText('AgentInstructions')).toBeVisible();
    expect(region.textContent).toContain('100 characters');
    expect(region.textContent).not.toContain('NEVER_RENDER');
    expect(within(region).queryByRole('textbox')).not.toBeInTheDocument();
  });

  it('explains absent historical metadata and stays hidden on ordinary Chat detail', () => {
    const view = render(<App><AgentRunDetails showContextProvenance run={fixtureRun} onChange={vi.fn()} /></App>);
    expect(screen.getByText(/No context manifest recorded/)).toBeVisible();
    view.rerender(<App><AgentRunDetails run={fixtureRun} onChange={vi.fn()} /></App>);
    expect(screen.queryByRole('region', { name: 'Context provenance' })).not.toBeInTheDocument();
  });
});
