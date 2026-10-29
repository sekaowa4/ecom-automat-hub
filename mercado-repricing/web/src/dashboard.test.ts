import {describe,it,expect} from 'vitest';
import {dashboardDataUrl,defaultReportDate} from './api/dashboard';

describe('dashboard reporting date',()=>{
 it('defaults to the latest complete Shanghai calendar day',()=>{
  expect(defaultReportDate(new Date('2026-10-28T16:00:00.000Z'))).toBe('2026-10-28');
 });
 it('includes the selected date with the store in the data request',()=>{
  expect(dashboardDataUrl('Meli 店铺2','2026-10-25')).toBe('/dashboard/data?shop=Meli%20%E5%BA%97%E9%93%BA2&reportDate=2026-10-25&refresh=false');
 });
});
