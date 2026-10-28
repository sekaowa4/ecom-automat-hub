import { describe, expect, it } from 'vitest';
import type { JobRow } from '../api/types';
import { buildExecutionBindings, executionBindingKey } from './executionBindings';

function row(id: string, store: string, itemId: string, promotionId: string, name: string, status: string): JobRow {
  return {
    id,
    source: { sourceRow: 1, sheet: '价格表', original: {}, input: { store, storeName: store, site: 'MLB', currency: 'BRL', sku: 'SKU-DEMO-MULTILINK', itemId, promotionId, targetPrice: 99.9, basePrice: null, allowExit: false, allowBaseChange: false }, errors: [] },
    status: 'Succeeded', route: '普通活动调价', message: '', checkpoint: 'cut-verified',
    observation: { storeId: store, itemId, title: '样例商品', basePriceText: 'R$ 199,00', currentPrice: 99.9, difference: 0, checkedAt: '2026-10-28T00:00:00Z', identityNote: '', promotions: [{ id: promotionId, name, status, priceText: 'R$ 99,90', active: status === '激活' }] },
    priceCut: { promotionId, product: { itemId, promotions: [] } },
  };
}

describe('execution detail links and activities', () => {
  it('groups every link ID and exact submitted activity by store and SKU', () => {
    const bindings = buildExecutionBindings([
      row('1', '样例店铺A', '10000001', 'C-DEMO-ACTIVE', 'DEMO-ACTIVE', '激活'),
      row('2', '样例店铺A', '10000002', 'C-DEMO-SCHEDULED', 'DEMO-SCHEDULED', '已预约'),
      row('3', '样例店铺B', '20000001', 'C-DEMO-OTHER', 'DEMO-OTHER', '已预约'),
    ]);

    expect(bindings.get(executionBindingKey('样例店铺A', 'SKU-DEMO-MULTILINK'))).toEqual({
      itemIds: ['10000001', '10000002'],
      activities: [
        { id: 'C-DEMO-ACTIVE', name: 'DEMO-ACTIVE', status: '已生效' },
        { id: 'C-DEMO-SCHEDULED', name: 'DEMO-SCHEDULED', status: '已预约' },
      ],
    });
    expect(bindings.get(executionBindingKey('样例店铺B', 'SKU-DEMO-MULTILINK'))?.itemIds).toEqual(['20000001']);
  });
});
