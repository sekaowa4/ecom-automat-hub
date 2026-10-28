import type { JobRow } from '../api/types';

export interface ExecutionActivity {
  id: string;
  name: string;
  status: string;
}

export interface ExecutionBinding {
  itemIds: string[];
  activities: ExecutionActivity[];
}

const keyFor = (store: string, sku: string) => `${store}\u001f${sku}`;

export function buildExecutionBindings(rows: JobRow[]): Map<string, ExecutionBinding> {
  const bindings = new Map<string, { itemIds: Set<string>; activities: Map<string, ExecutionActivity> }>();

  for (const row of rows) {
    const input = row.source.input;
    if (!input?.sku) continue;
    const store = input.storeName || input.store;
    const key = keyFor(store, input.sku);
    let binding = bindings.get(key);
    if (!binding) {
      binding = { itemIds: new Set(), activities: new Map() };
      bindings.set(key, binding);
    }

    const itemId = row.observation?.itemId || row.priceCut?.product?.itemId || input.itemId;
    if (itemId) binding.itemIds.add(itemId);

    const promotions = row.observation?.promotions ?? row.priceCut?.product?.promotions ?? [];
    const selectedId = row.priceCut?.promotionId || input.promotionId;
    const selected = selectedId
      ? promotions.filter(promotion => promotion.id === selectedId)
      : promotions.filter(promotion => promotion.id.startsWith('C-') && (promotion.active || /预约|scheduled|agendada|reservada/i.test(promotion.status)));
    for (const promotion of selected) {
      binding.activities.set(promotion.id, {
        id: promotion.id,
        name: promotion.name?.split('\n')[0] || promotion.id,
        status: promotion.active ? '已生效' : promotion.status || '已预约',
      });
    }
    if (selectedId && !binding.activities.has(selectedId)) {
      binding.activities.set(selectedId, { id: selectedId, name: selectedId, status: '本批执行活动' });
    }
  }

  return new Map([...bindings].map(([key, value]) => [key, {
    itemIds: [...value.itemIds].sort((a, b) => a.localeCompare(b, 'en')),
    activities: [...value.activities.values()].sort((a, b) => a.id.localeCompare(b.id, 'en')),
  }]));
}

export const executionBindingKey = keyFor;
