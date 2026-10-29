"""Read-only SKU enrichment through the workstation's existing WeCom MCP."""
import copy
import hashlib
import io
import json
import re
import time
import tomllib
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import Request, ProxyHandler, HTTPRedirectHandler, build_opener
from PIL import Image


def document_url(value):
    value = str(value).strip()
    parsed = urlsplit(value)
    if parsed.scheme != 'https' or parsed.netloc != 'doc.weixin.qq.com' or not parsed.path.startswith('/sheet/e3_'):
        raise ValueError('请填写 doc.weixin.qq.com 的在线表格链接')
    return value


def call_tool(name, arguments):
    if name not in {'sheet_get_info', 'get_doc_content'}:
        raise ValueError('企微连接仅允许读取文档，不允许写入')
    config = Path.home() / '.codex/config.toml'
    try:
        settings = tomllib.loads(config.read_text(encoding='utf-8'))['mcp_servers']['enterprise_wechat_docs']
        endpoint = settings['url']
    except (OSError, KeyError, ValueError):
        raise ValueError('未找到本机 Codex 的企微 MCP 配置') from None
    parsed = urlsplit(endpoint)
    if not settings.get('enabled', True) or parsed.scheme != 'https' or parsed.netloc != 'qyapi.weixin.qq.com' or parsed.path != '/mcp/robot-doc':
        raise ValueError('本机企微 MCP 未启用或地址不匹配')
    payload = {'jsonrpc': '2.0', 'id': 1, 'method': 'tools/call', 'params': {'name': name, 'arguments': arguments}}
    request = Request(endpoint, data=json.dumps(payload).encode(), headers={'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
    try:
        # This official endpoint fails TLS through the workstation proxy; use direct TLS.
        with build_opener(ProxyHandler({})).open(request, timeout=30) as response:
            raw = response.read(50_000_001)
        if len(raw) > 50_000_000:
            raise ValueError('企微文档返回超过 50 MB')
        result = json.loads(raw).get('result', {})
        if result.get('isError'):
            raise ValueError('企微 MCP 读取失败，请检查授权和文档权限')
        data = json.loads(next(c['text'] for c in result['content'] if c['type'] == 'text'))
    except ValueError:
        raise
    except Exception:
        # Never leak the credential-bearing MCP URL in exception text.
        raise ValueError('企微 MCP 连接失败，请检查网络及机器人授权') from None
    if data.get('errcode') != 0:
        if data.get('errcode') == 851014:
            raise ValueError('企微机器人文档权限已过期，请在企业微信重新授权后连接')
        raise ValueError(f"企微文档读取失败（{data.get('errcode')}），请检查机器人文档权限")
    return data


def read_document(url):
    args = {'url': document_url(url), 'type': 2}
    for _ in range(60):
        data = call_tool('get_doc_content', args)
        if data.get('task_done'):
            return data['content']
        args['task_id'] = data['task_id']
        time.sleep(2)
    raise ValueError('企微文档导出超时，请稍后重新匹配')


def sheet_rows(markdown, title):
    lines = markdown.splitlines()
    start = next((i for i, line in enumerate(lines) if line.strip().lstrip('#').strip() == title), None)
    if start is None:
        raise ValueError(f'企微文档没有子表：{title}')
    rows = []
    for line in lines[start + 1:]:
        if not line.startswith('|'):
            if rows and line.strip():
                break
            continue
        cells = [v.strip().replace('\\|', '|') for v in re.split(r'(?<!\\)\|', line)[1:-1]]
        if cells and not all(re.fullmatch(r':?-+:?', c or '-') for c in cells):
            rows.append(cells)
    return rows


def indexed(rows, sku_column, columns, headers):
    header = next((i for i, r in enumerate(rows) if len(r) > max([sku_column, *columns]) and r[sku_column].upper() in {'SKU', '商品SKU', '产品SKU'}), None)
    if header is None or [rows[header][c] for c in columns] != headers:
        raise ValueError('企微表的 SKU 或指定列的表头发生变化，请核对配置')
    result = {}
    for row in rows[header + 1:]:
        if len(row) <= sku_column or not row[sku_column]:
            continue
        row = row + [''] * max(0, max(columns) + 1 - len(row))
        sku = row[sku_column].strip()
        result.setdefault(sku, []).append([row[c] for c in columns])
    return result


def match_items(items, images_markdown, sizes_markdown):
    images = indexed(sheet_rows(images_markdown, '图包作图需求'), 1, [13], ['产品图'])
    sizes = indexed(sheet_rows(sizes_markdown, '箱规汇总表'), 2, [26, 27, 28, 29], ['单套包装长', '单套包装宽', '单套包装高', '单套含包装重量（kg）'])
    matched, errors = copy.deepcopy(items), []
    for item in matched:
        sku = item['sku']
        item.update(image=None, image_url=None, length_cm=0, width_cm=0, height_cm=0, weight_g=0)
        item['data_source'] = '企微：图包作图需求 N；箱规汇总表 AA:AD（kg→g）'
        for label, index in [('产品图', images), ('尺寸重量', sizes)]:
            values = index.get(sku, [])
            unique = {tuple(v) for v in values}
            if len(unique) != 1:
                message = f'{sku} 企微{label}未匹配或重复记录冲突'
                errors.append(message)
                if label == '产品图':
                    item['image_issue'] = message
                continue
            value = next(iter(unique))
            if label == '产品图':
                urls = re.findall(r'!\[[^\]]*\]\((https://[^\s)]+)\)', value[0])
                if not urls:
                    item['image_issue'] = f'{sku} 企微 N 列没有可读取的产品图'
                    errors.append(item['image_issue'])
                else:
                    item['image_url'] = urls[0]
                    item['image_count'] = len(urls)
            else:
                try:
                    if any(not re.fullmatch(r'\d+(?:\.\d+)?', v) for v in value):
                        raise ValueError()
                    numbers = [Decimal(v) for v in value]
                    if any(not n.is_finite() or n <= 0 for n in numbers):
                        raise ValueError()
                    item.update(dict(zip(['length_cm', 'width_cm', 'height_cm', 'weight_g'], map(float, numbers[:3] + [numbers[3] * 1000]))))
                except Exception:
                    errors.append(f'{sku} 企微 AA:AD 尺寸重量无效或未填写')
    return matched, errors


def match_prices(items, markdown, title=None):
    """Bind a single batch sheet; never take a different batch's E-column price."""
    lines = markdown.splitlines()
    titles = [line.strip().lstrip('#').strip() for n, line in enumerate(lines[:-1]) if line.strip() and not line.startswith('|') and lines[n + 1].startswith('|')]
    targets = {i['sku'] for i in items}
    candidates = []
    for name in ([title] if title else titles):
        rows = sheet_rows(markdown, name)
        header = next((r for r in rows if len(r) > 4 and r[4].strip() == '单价' and any(v.strip().upper() in {'SKU', '商品SKU', '产品SKU'} for v in r)), None)
        if header is None:
            continue
        sku_column = next(n for n, v in enumerate(header) if v.strip().upper() in {'SKU', '商品SKU', '产品SKU'})
        prices = indexed(rows, sku_column, [4], ['单价'])
        if targets.issubset(prices) and (title or targets == set(prices)):
            candidates.append((name, prices))
    if len(candidates) != 1:
        raise ValueError('正清表的对应批次单价子表未匹配或存在多个候选，请选择对应批次子表（E列“单价”），不使用清单价格')
    name, prices = candidates[0]
    result = copy.deepcopy(items)
    for item in result:
        values = {v[0] for v in prices[item['sku']]}
        if len(values) != 1:
            raise ValueError(f"{item['sku']} 对应批次 E 列单价重复冲突")
        value = next(iter(values))
        if not re.fullmatch(r'\d+(?:\.\d+)?', value):
            raise ValueError(f"{item['sku']} 对应批次 E 列单价缺失或格式不明确")
        price = Decimal(value).quantize(Decimal('0.01'), rounding=ROUND_HALF_UP)
        if price <= 0:
            raise ValueError(f"{item['sku']} 对应批次 E 列单价必须大于零")
        item['unit_price'] = str(price)
        item['price_source'] = f'正清表：{name} E列“单价”'
    return result, name


def download_image(url, directory):
    def validate(target):
        p = urlsplit(target)
        if p.scheme != 'https' or p.netloc != 'wdcdn.qpic.cn':
            raise ValueError('企微产品图必须来自 wdcdn.qpic.cn，拒绝其他下载地址')
    class Redirects(HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            validate(newurl)
            return super().redirect_request(req, fp, code, msg, headers, newurl)
    validate(url)
    try:
        with build_opener(ProxyHandler({}), Redirects()).open(url, timeout=20) as response:
            raw = response.read(10_000_001)
        if len(raw) > 10_000_000:
            raise ValueError('图片超过 10 MB')
        with Image.open(io.BytesIO(raw)) as img:
            if img.format not in {'JPEG', 'PNG', 'WEBP', 'GIF'} or img.width * img.height > 30_000_000:
                raise ValueError('图片格式或像素尺寸不支持')
            ext = {'JPEG': '.jpg', 'PNG': '.png', 'WEBP': '.webp', 'GIF': '.gif'}[img.format]
            img.verify()
    except Exception:
        raise ValueError('企微产品图下载或图片校验失败，请检查图片链接') from None
    filename = 'wecom-' + hashlib.sha256(raw).hexdigest()[:24] + ext
    Path(directory).mkdir(parents=True, exist_ok=True)
    (Path(directory) / filename).write_bytes(raw)
    return filename
