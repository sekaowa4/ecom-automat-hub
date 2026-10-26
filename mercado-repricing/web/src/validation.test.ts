import { describe,it,expect } from 'vitest';
import { invalidateValidationOnChange } from './validation';
describe('import validation',()=>{
  const selection={importId:'a',sheet:'价格',mapping:{store:'店铺',sku:'SKU'},defaultSite:'BR'};
  it('keeps validation for identical selection',()=>expect(invalidateValidationOnChange(selection,{...selection})).toBe(false));
  it.each([{sheet:'其他'},{defaultSite:'MX'},{mapping:{store:'店名',sku:'SKU'}},{importId:'b'}])('invalidates changed selection %o',change=>expect(invalidateValidationOnChange(selection,{...selection,...change})).toBe(true));
  it('invalidates preview after selecting another store',()=>expect(invalidateValidationOnChange({...selection,selectedStoreId:'1'},{...selection,selectedStoreId:'2'})).toBe(true));
  it('invalidates preview when switching from all stores to one store',()=>expect(invalidateValidationOnChange({...selection,selectedStoreId:'all'},{...selection,selectedStoreId:'2'})).toBe(true));
});
