import type { FormInstance } from 'antd';
import type { MessageInstance } from 'antd/es/message/interface';

type ValidationFailure = { errorFields: { name: (string | number)[]; errors: string[] }[]; outOfDate?: boolean };

export function isAdminFormValidationFailure(reason: unknown): reason is ValidationFailure {
  return !!reason && typeof reason === 'object' && 'errorFields' in reason && Array.isArray(reason.errorFields);
}

/** Keep field errors persistent, announce failed submission, and reveal the first correction. */
export function showAdminFormValidation(message: Pick<MessageInstance, 'warning'>, form: FormInstance, reason: unknown) {
  if (!isAdminFormValidationFailure(reason) || reason.outOfDate) return;
  const first = reason.errorFields.find(field => field.errors.length > 0);
  if (!first) return;
  message.warning({ key: 'admin-form-validation', content: `Check the highlighted fields: ${first.errors[0]}`, duration: 6 });
  // A caller may first need to expand the section containing this field.
  requestAnimationFrame(() => form.scrollToField(first.name, { block: 'center', focus: true }));
}

/** Modal buttons use the same feedback as native Form submission. */
export async function validateAdminForm<T>(form: FormInstance<T>, message: Pick<MessageInstance, 'warning'>, fields?: Parameters<FormInstance<T>['validateFields']>[0]) {
  try { return await form.validateFields(fields); }
  catch (reason) { showAdminFormValidation(message, form, reason); throw reason; }
}
