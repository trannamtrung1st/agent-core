import { ConfigProvider } from 'antd';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SkillDrawer, type SkillDrawerValue } from './SkillDrawer';

const value: SkillDrawerValue = { name: 'Review', description: 'Review evidence', procedure: 'Keep this procedure visible.', projection: 'OnDemand', enabled: true, capabilities: '' };

describe('Skill drawer closing presentation', () => {
  it.each([false, true])('retains title, content and mode while closing (details: %s)', async readOnly => {
    const title = readOnly ? 'Definition Skill details' : 'New Instance Skill';
    const props = { title, value, readOnly, context: 'Skill context', contentLabel: 'Skill content', onClose: vi.fn() };
    const { rerender } = render(<ConfigProvider><SkillDrawer {...props} open /></ConfigProvider>);
    await waitFor(() => expect(screen.getByRole('dialog', { name: title })).toBeVisible());
    rerender(<ConfigProvider><SkillDrawer {...props} open={false} title="Edit Instance Skill" value={null} readOnly={false} context="Cleared" /></ConfigProvider>);
    expect(screen.getByText(title)).toBeInTheDocument();
    expect(screen.queryByText('Edit Instance Skill')).not.toBeInTheDocument();
    expect(screen.getByText('Skill context')).toBeInTheDocument();
    if (readOnly) expect(screen.getByText(value.procedure)).toBeInTheDocument();
    else expect(screen.getByLabelText('Procedure')).toHaveValue(value.procedure);
  });
  it('keeps a fixed ID read-only during closing and loads fresh values on reopen', async () => {
    const props = { title: 'Edit Instance Skill', value: { ...value, id: 'review' }, idReadOnly: true,
      context: 'Context', contentLabel: 'Skill content', onClose: vi.fn() };
    const { rerender } = render(<ConfigProvider><SkillDrawer {...props} open /></ConfigProvider>);
    await waitFor(() => expect(screen.getByLabelText('Skill ID')).toHaveValue('review'));
    fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Unsaved change' } });
    rerender(<ConfigProvider><SkillDrawer {...props} open={false} value={null} idReadOnly={false} /></ConfigProvider>);
    expect(screen.getByLabelText('Skill ID')).toHaveAttribute('readonly');
    expect(screen.getByLabelText('Procedure')).toHaveValue('Unsaved change');
    rerender(<ConfigProvider><SkillDrawer {...props} open title="New Instance Skill" idReadOnly={false}
      value={{ ...value, id: '', name: 'New skill', procedure: 'Fresh procedure' }} /></ConfigProvider>);
    await waitFor(() => expect(screen.getByLabelText('Procedure')).toHaveValue('Fresh procedure'));
    expect(screen.getByLabelText('Skill ID')).not.toHaveAttribute('readonly');
  });

  it('gives separate mounted drawers unique labeled control IDs', async () => {
    const props = { value, context: 'Context', contentLabel: 'Skill content', onClose: vi.fn() };
    render(<ConfigProvider><SkillDrawer {...props} open title="First Skill" /><SkillDrawer {...props} open title="Second Skill" /></ConfigProvider>);
    await waitFor(() => expect(screen.getAllByLabelText('Skill name')).toHaveLength(2));
    const fields = screen.getAllByLabelText('Skill name');
    expect(fields[0].id).not.toBe(fields[1].id);
    for (const field of fields) expect(document.querySelector(`label[for="${field.id}"]`)).toHaveTextContent('Skill name');
  });

});
