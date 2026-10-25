const EXPORT_CONFIGS = {
  upseller: {
    name: 'UpSeller',
    url: 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=',
    requiredHeaders: ['SKU', '店铺', '销量'],
  },
  oms: {
    name: 'OMS',
    url: 'https://aplichic.jfwms.net/web/inventory/index',
    requiredHeaders: ['商品SKU', '已锁', '可用库存', '不良品数'],
  },
};

function getExportConfig(source) {
  const config = EXPORT_CONFIGS[source];
  if (!config) throw new Error(`Unknown export source: ${source}`);
  return { ...config, requiredHeaders: [...config.requiredHeaders] };
}

module.exports = { getExportConfig };
