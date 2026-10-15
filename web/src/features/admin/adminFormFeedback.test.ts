import type { FormInstance } from 'antd';
import { afterEach, expect, it, vi } from 'vitest';
import { showAdminFormValidation, validateAdminForm } from './adminFormFeedback';

afterEach(()=>vi.unstubAllGlobals());
it('announces the first error and defers focus until its section can be revealed',()=>{
  let reveal!: FrameRequestCallback;
  vi.stubGlobal('requestAnimationFrame',vi.fn((callback:FrameRequestCallback)=>{reveal=callback;return 1;}));
  const scrollToField=vi.fn();const warning=vi.fn();
  const failure={errorFields:[{name:['limits',0,'cpu'],errors:['CPU exceeds the Profile ceiling.']},{name:['name'],errors:['Name is required.']}]};
  showAdminFormValidation({warning}, {scrollToField} as unknown as FormInstance, failure);
  expect(warning).toHaveBeenCalledWith({key:'admin-form-validation',content:'Check the highlighted fields: CPU exceeds the Profile ceiling.',duration:6});
  expect(scrollToField).not.toHaveBeenCalled();reveal(0);
  expect(scrollToField).toHaveBeenCalledWith(['limits',0,'cpu'],{block:'center',focus:true});
});
it('retains the rejected validation result without treating stale validation or server errors as field errors',async()=>{
  vi.stubGlobal('requestAnimationFrame',vi.fn());const warning=vi.fn();
  const failure={outOfDate:true,errorFields:[{name:['name'],errors:['Name is required.']}]};
  const form={validateFields:vi.fn().mockRejectedValue(failure)} as unknown as FormInstance;
  await expect(validateAdminForm(form,{warning})).rejects.toBe(failure);
  showAdminFormValidation({warning},form,new Error('Revision changed.'));
  expect(warning).not.toHaveBeenCalled();
});
