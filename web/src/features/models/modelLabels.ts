export function modelChoiceLabel(model: { displayName: string; connectionLabel?: string | null }): string {
  return model.connectionLabel ? `${model.displayName} · ${model.connectionLabel}` : model.displayName;
}

export function modelChoiceOptions(models: readonly { offeringId: string; displayName: string; connectionLabel?: string | null }[], selected?: string | null) {
  const options: { value: string; label: string; disabled?: boolean }[] = models.map(model => ({ value: model.offeringId, label: modelChoiceLabel(model) }));
  if (selected && !models.some(model => model.offeringId === selected))
    options.push({ value: selected, label: `Unavailable · ${selected}`, disabled: true });
  return options;
}
