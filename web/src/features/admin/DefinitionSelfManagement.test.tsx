import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { DefinitionSelfManagement } from './DefinitionSelfManagement';

describe('Definition self-management defaults', () => {
  it('does not materialize defaults on inspection and preserves unrelated candidate fields on customization', () => {
    const onChange = vi.fn();
    render(<DefinitionSelfManagement candidate={{ systemInstructions: 'Retained instructions' }} disabled={false} onChange={onChange} />);
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('combobox', { name: 'Default self-management mode' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Settings' }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ systemInstructions: 'Retained instructions', selfManagement: expect.objectContaining({ mode: 'Disabled', scopes: ['Settings'], frozen: false }) }));
  });
  it('keeps published defaults read-only', () => {
    const onChange = vi.fn();
    render(<DefinitionSelfManagement candidate={{ selfManagement: { mode: 'Managed', scopes: ['Settings'] } }} disabled onChange={onChange} />);
    expect(screen.getByRole('checkbox', { name: 'Settings' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Default configuration assistance procedure' })).toBeDisabled();
    expect(onChange).not.toHaveBeenCalled();
  });
});
