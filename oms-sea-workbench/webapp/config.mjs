import { readFileSync, existsSync } from 'node:fs';

const filename = new URL('../local-config.json', import.meta.url);
export const localConfig = existsSync(filename) ? JSON.parse(readFileSync(filename, 'utf8')) : {};
export const omsOrigin = localConfig.oms_origin || 'https://oms.example.invalid';
export function billingConfig(config = localConfig) {
  const billing = config.billing || {};
  if (!['shop', 'name', 'cnpj', 'company'].every(key => typeof billing[key] === 'string' && billing[key].trim()) || billing.cnpj.replace(/\D/g, '').length !== 14)
    throw new Error('请先在本机 local-config.json 填写并核对开票收件资料');
  return billing;
}
