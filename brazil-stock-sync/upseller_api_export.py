"""Direct UpSeller exports authenticated with one local us_u cookie."""

from __future__ import annotations

import json
import math
import os
import subprocess
import time
import uuid
from datetime import date, timedelta
from pathlib import Path
from urllib.parse import urlencode, urljoin


BASE_URL = "https://app.upseller.com"
ROOT = Path(__file__).resolve().parent
RUNTIME = ROOT / "runtime"
SECRET_PATH = RUNTIME / "upseller_api.local.json"
SALES_HEADS = "产品,店铺,SKU,多变种,变种ID,父SKU,产品 ID,有效订单量,销量,销售额,平均售价"
SALES_TRANSLATIONS = json.dumps({
    "Sales_by_Product": "Sales_by_Product_", "mercado": "Mercado Libre", "shopee": "Shopee",
    "amazon": "Amazon", "shein": "Shein", "tiktok": "TikTok Shop", "shopify": "Shopify",
    "nuvemshop": "Nuvemshop", "temu": "Temu", "walmart": "Walmart", "falabella": "Falabella",
    "aliexpress": "AliExpress", "magalu": "Magalu", "kwai": "Kwai", "skyhub": "Americanas",
    "physical": "手工店铺",
}, ensure_ascii=False, separators=(",", ":"))


def sales_date_range(days: int, today: str | None = None) -> tuple[str, str]:
    base = date.fromisoformat(today) if today else date.today()
    end = base - timedelta(days=1)
    start = end - timedelta(days=days - 1)
    return start.isoformat(), end.isoformat()


def extract_job_id(payload):
    value = payload.get("data", payload) if isinstance(payload, dict) else payload
    if isinstance(value, (int, float)):
        return str(int(value))
    if isinstance(value, str) and value:
        return value
    if isinstance(value, dict):
        for key in ("uuid", "taskId", "jobId", "id"):
            if value.get(key):
                return str(value[key])
    raise RuntimeError("UpSeller 导出接口未返回任务编号")


def find_first_value(payload, keys):
    if isinstance(payload, dict):
        for key in keys:
            if payload.get(key) not in (None, ""):
                return payload[key]
        for value in payload.values():
            found = find_first_value(value, keys)
            if found not in (None, ""):
                return found
    if isinstance(payload, list):
        for value in payload:
            found = find_first_value(value, keys)
            if found not in (None, ""):
                return found
    return None


def extract_download_url(payload):
    if isinstance(payload, str):
        return payload if payload.startswith(("http://", "https://", "/")) else ""
    if isinstance(payload, list):
        for item in payload:
            found = extract_download_url(item)
            if found:
                return found
    if isinstance(payload, dict):
        for key in ("downloadUrl", "download_url", "url", "fileUrl", "file_url", "path"):
            value = payload.get(key)
            if isinstance(value, str) and value.startswith(("http://", "https://", "/")):
                return value
        for value in payload.values():
            found = extract_download_url(value)
            if found:
                return found
    return ""


class UpSellerClient:
    def __init__(self, us_u: str):
        if not us_u.strip():
            raise ValueError("未配置 UpSeller us_u Cookie")
        self.us_u = us_u.strip()
        self.device_id = str(uuid.uuid5(uuid.NAMESPACE_URL, self.us_u))

    @staticmethod
    def _curl_quote(value: str) -> str:
        return json.dumps(str(value), ensure_ascii=False)

    def _curl(self, url: str, method="GET", body: str | None = None, referer="https://app.upseller.com/") -> bytes:
        headers = [
            f"Cookie: us_u={self.us_u}", "Accept: application/json, text/plain, */*", f"Referer: {referer}",
            f"Origin: {BASE_URL}", f"Deviceid: {self.device_id}",
            "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36",
        ]
        if body is not None:
            headers.append("Content-Type: application/json" if body.startswith("{") else "Content-Type: application/x-www-form-urlencoded")
        config = [f"url = {self._curl_quote(urljoin(BASE_URL, url))}", f"request = {self._curl_quote(method)}"]
        config.extend(f"header = {self._curl_quote(header)}" for header in headers)
        if body is not None:
            config.append(f"data = {self._curl_quote(body)}")
        for attempt in range(3):
            result = subprocess.run(["curl.exe", "--silent", "--show-error", "--fail-with-body", "--noproxy", "*", "--config", "-"], input="\n".join(config).encode("utf-8"), capture_output=True, timeout=180)
            if not result.returncode:
                return result.stdout
            if result.returncode != 35 or attempt == 2:
                raise RuntimeError(f"UpSeller HTTP 请求失败：{result.stderr.decode('utf-8', 'replace').strip() or result.returncode}")
            time.sleep(attempt + 1)

    def request_json(self, path: str, method="GET", data=None, referer="https://app.upseller.com/", as_form=False):
        body = None
        if data is not None:
            if isinstance(data, dict) and not as_form:
                body = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
            else:
                body = urlencode(data)
        return json.loads(self._curl(path, method, body, referer).decode("utf-8"))

    def download(self, url: str) -> bytes:
        return self._curl(url)

    def export_and_download(self, path, data, output, referer, as_form=False):
        for attempt in range(3):
            created = self.request_json(path, "POST", data, referer, as_form=as_form)
            try:
                job_id = extract_job_id(created)
                break
            except RuntimeError:
                if attempt == 2:
                    raise
                time.sleep(attempt + 1)
        for _ in range(90):
            status = self.request_json(f"/api/check-process?{urlencode({'uuid': job_id})}", referer=referer)
            url = extract_download_url(status)
            if url:
                output.write_bytes(self.download(url))
                return output
            time.sleep(2)
        raise TimeoutError(f"UpSeller 导出任务超时：{job_id}")


def load_us_u(secret_path: Path = SECRET_PATH) -> str:
    value = os.environ.get("UPSELLER_US_U", "").strip()
    if value:
        return value
    if not secret_path.exists():
        raise ValueError("未配置 UpSeller Cookie；请运行 配置UpSeller接口Cookie.cmd")
    return str(json.loads(secret_path.read_text(encoding="utf-8")).get("us_u", "")).strip()


def run_upseller_full_inventory_export(
    output_dir: Path, client: UpSellerClient | None = None, filename: str = "FULL库存数据.xlsx"
) -> Path:
    """Request a new FULL inventory export instead of reusing a prior file."""
    output_dir.mkdir(parents=True, exist_ok=True)
    client = client or UpSellerClient(load_us_u())
    return client.export_and_download("/api/full-inventory/export", {
        "heads": ["ML Code", "标题", "变种值", "SKU", "产品 ID", "店铺", "单价", "低库存", "可用库存", "不可用数", "总计"],
        "sortName": "1", "sortValue": "1", "pageNum": 1, "pageSize": 50,
        "firstPage": 1, "endPage": 50,
        "translationKey": "Mercado_Envios_Full_", "fullStatus": "all_full",
    }, output_dir / filename, "https://app.upseller.com/zh-CN/inventory/third-inventory/mercado-store?warehouseId=")


def run_upseller_api_exports(output_dir: Path) -> list[Path]:
    output_dir.mkdir(parents=True, exist_ok=True)
    client = UpSellerClient(load_us_u())
    sales_ref = "https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId="
    inventory_ref = "https://app.upseller.com/zh-CN/inventory/3pl-list?warehouseId="
    outputs = []
    for days, name in ((30, "近30天销售数据.xlsx"), (7, "近7天销售数据.xlsx"), (15, "近15天销售数据.xlsx"), (3, "近3天销售数据.xlsx")):
        start, end = sales_date_range(days)
        outputs.append(client.export_and_download("/api/statistics/variation-sale-all-list-export", {
            "heads": SALES_HEADS, "translationKey": SALES_TRANSLATIONS, "beginDate": start, "endDate": end,
            "sortName": 0, "sortValue": 0, "pageNum": 1, "pageSize": 50,
        }, output_dir / name, sales_ref, as_form=True))
    outputs.append(run_upseller_full_inventory_export(output_dir, client))
    warehouses = client.request_json("/api/warehouse/inventory/list-enable", "POST", {}, inventory_ref)
    # list-enable returns warehouse records keyed by idStr/puid rather than
    # warehouseId.  idStr is the identifier expected by the inventory APIs.
    warehouse_id = str(find_first_value(warehouses, ("warehouseId", "idStr", "id", "puid")) or "")
    if not warehouse_id:
        raise RuntimeError("UpSeller 未返回可用三方仓仓库编号")
    count = client.request_json("/api/tpl-warehouse-sku/count", "POST", {"searchType": "1", "warehouseId": warehouse_id, "isGroup": 0}, inventory_ref)
    # This endpoint returns one {warehouseId, skuCount} item per warehouse,
    # not a top-level total/count field.
    total = sum(int(item.get("skuCount") or 0) for item in (count.get("data") or []) if isinstance(item, dict))
    if total <= 0:
        raise RuntimeError("UpSeller 未返回三方仓 SKU 数量")
    pages = max(1, math.ceil(total / 50))
    outputs.append(client.export_and_download("/api/tpl-warehouse-sku/export", {
        "exportType": 1, "warehouseId": warehouse_id, "total": total, "sortName": "0", "sortValue": "0",
        "pageNum": 1, "pageSize": 50, "firstPage": 1, "endPage": pages, "totalPage": pages,
        # The server exports all 22 values regardless of the supplied header count.
        "heads": ["SKU", "标题", "仓库", "类目", "最低库存", "最高库存", "UpSeller占用数", "UpSeller可用库存", "在途中", "三方仓可用库存", "三方仓占用数", "三方仓不可用", "三方仓现有库存", "3天销量", "7天销量", "15天销量", "30天销量", "平均日销", "可售天数", "平均成本", "小计", "创建时间"],
        "translationKey": "3pl_Inventory_List_",
    }, output_dir / "库存数据.xlsx", inventory_ref))
    return outputs
