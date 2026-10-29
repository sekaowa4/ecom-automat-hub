export async function api(path, data, raw = false) {
  const response = await fetch(
    `/api${path}`,
    data === undefined
      ? {}
      : {
          method: "POST",
          headers: raw ? {} : { "Content-Type": "application/json" },
          body: raw ? data : JSON.stringify(data),
        },
  );
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || "操作未完成，请重试");
  return result;
}
export const count = (value) =>
  new Intl.NumberFormat("zh-CN").format(value || 0);
export const money = (value) =>
  new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(
    Number(value || 0),
  );
export const date = (value) =>
  value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—";
export const statusNames = {
  matching: "企微匹配中",
  ready: "待开始",
  invalid: "清单需修正",
  running: "处理中",
  pausing: "正在暂停",
  waiting_confirmation: "等待确认",
  paused: "已暂停",
  blocked: "需要处理",
  completed: "已完成",
};
