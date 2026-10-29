"""Read shipping manifests without changing the uploaded workbook."""

import csv
import io
import json
import posixpath
import re
from collections import OrderedDict
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile, ZIP_DEFLATED
from .config import load_config, billing_config

S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
DEFAULT_COLUMNS = {"sku": "B", "name_zh": "E", "quantity": "K", "length_cm": "W", "width_cm": "X", "height_cm": "Y", "weight_kg": "Z", "unit_price": "AA", "image": "D"}
ALIASES = {
    "sku": ["sku", "商品sku", "产品sku"],
    "name_zh": ["产品", "中文品名", "中文名", "品名", "产品名称"],
    "name_pt": ["葡语名称", "葡语名", "葡语标题"],
    "quantity": ["总数量", "数量", "件数"],
    "length_cm": ["单套包装长", "单套长", "单套尺寸长", "长(cm)"],
    "width_cm": ["单套包装宽", "单套宽", "单套尺寸宽", "宽(cm)"],
    "height_cm": ["单套包装高", "单套高", "单套尺寸高", "高(cm)"],
    "weight_kg": ["单套含包装重量（kg）", "单套含包装重量(kg)", "单套重量", "重量(kg)"],
    "weight_g": ["重量(g)", "单套重量(g)"],
    "unit_price": ["开票单价", "单价", "发票单价"],
}


def number(value, label):
    text = str(value or "").strip().replace("\u00a0", "").replace("R$", "").strip()
    if "," in text:
        text = text.replace(".", "").replace(",", ".") if re.search(r",\d{1,2}$", text) else text.replace(",", "")
    try:
        result = Decimal(text)
    except InvalidOperation as exc:
        raise ValueError(f"{label} 缺失或不是有效数字") from exc
    if not result.is_finite() or result <= 0:
        raise ValueError(f"{label} 必须大于 0")
    return result


def text_key(value):
    return re.sub(r"[\s*\n]+", "", str(value or "")).lower()


def column_name(index):
    result = ""
    while index:
        index, rem = divmod(index - 1, 26)
        result = chr(65 + rem) + result
    return result


def canonical_batch_name(value):
    match = re.fullmatch(r"第(\d{1,2})批", value)
    if not match:
        return value
    n = int(match[1])
    digits = "零一二三四五六七八九"
    if n < 10:
        text = digits[n]
    else:
        text = (digits[n // 10] if n >= 20 else "") + "十" + (digits[n % 10] if n % 10 else "")
    return f"第{text}批"


def relation_map(archive, member):
    relpath = posixpath.join(posixpath.dirname(member), "_rels", posixpath.basename(member) + ".rels")
    if relpath not in archive.namelist():
        return {}
    return {r.get("Id"): posixpath.normpath(posixpath.join(posixpath.dirname(member), r.get("Target", ""))) if not r.get("Target", "").startswith("/") else r.get("Target").lstrip("/") for r in ET.fromstring(archive.read(relpath)) if r.get("TargetMode") != "External"}


def read_workbook(path):
    sheets = []
    with ZipFile(path) as archive:
        if len(archive.infolist()) > 10000 or sum(i.file_size for i in archive.infolist()) > 250_000_000:
            raise ValueError("工作簿解压后过大，请上传不超过 250MB 的有效清单")
        strings = []
        if "xl/sharedStrings.xml" in archive.namelist():
            strings = ["".join(t.text or "" for t in si.iter(S + "t")) for si in ET.fromstring(archive.read("xl/sharedStrings.xml"))]
        workbook = ET.fromstring(archive.read("xl/workbook.xml"))
        rels = relation_map(archive, "xl/workbook.xml")
        for sheet in workbook.findall(S + "sheets/" + S + "sheet"):
            member = rels.get(sheet.get(R + "id"))
            if not member or member not in archive.namelist():
                continue
            root = ET.fromstring(archive.read(member))
            rows = {}
            formulas = {}
            for row in root.findall(S + "sheetData/" + S + "row"):
                n = int(row.get("r"))
                rows[n] = {}
                for cell in row.findall(S + "c"):
                    ref = cell.get("r", "")
                    col = re.sub(r"\d", "", ref)
                    value = cell.findtext(S + "v", "")
                    if cell.get("t") == "s" and value:
                        value = strings[int(value)]
                    elif cell.get("t") == "inlineStr":
                        value = "".join(t.text or "" for t in cell.iter(S + "t"))
                    rows[n][col] = value
                    if cell.find(S + "f") is not None:
                        formulas[ref] = cell.findtext(S + "f", "")
            merges = [m.get("ref") for m in root.findall(S + "mergeCells/" + S + "mergeCell")]
            sheets.append({"name": sheet.get("name"), "member": member, "rows": rows, "formulas": formulas, "merges": merges})
    if not sheets:
        raise ValueError("工作簿中没有可读取的工作表")
    return sheets


def find_header(sheet):
    for n, row in list(sheet["rows"].items())[:30]:
        candidates = {col: text_key(v) for col, v in row.items()}
        sku = next((col for col, v in candidates.items() if re.split(r'[（(]', v, 1)[0] in ALIASES['sku']), None)
        if sku:
            mapping = {"sku": sku}
            for field, aliases in ALIASES.items():
                found = next((col for col, v in candidates.items() if v in [text_key(a) for a in aliases] or (field in {'quantity', 'sku', 'name_zh', 'name_pt'} and re.split(r'[（(]', v, 1)[0] in [text_key(a) for a in aliases])), None)
                if found:
                    mapping[field] = found
            quantities = [(col, value) for col, value in candidates.items()
                          if (re.split(r'[（(]', value, 1)[0] in ALIASES['quantity'] or re.fullmatch(r'总数量(?:pcs|件|套)', value))
                          and not any(word in value for word in ['箱', 'caixa', 'perbox'])]
            totals = [(col, value) for col, value in quantities if value.startswith('总数量')]
            preferred = totals or quantities
            mapping.pop('quantity', None)
            if len(preferred) == 1:
                mapping['quantity'] = preferred[0][0]
            elif len(preferred) > 1:
                raise ValueError('数量表头存在歧义，请明确总件数列；不能使用每箱数量或箱数')
            # The verified packing-list template uses grouped headers for W:AA.
            if sku == "B" and mapping.get('quantity') == 'K' and "总数量" in str(row.get("K", "")):
                mapping = {**DEFAULT_COLUMNS, **mapping, "unit_price": "AA"}
            return n, mapping
    return None, {}


def extract_images(path, sheet, mapping, output):
    images = {}
    with ZipFile(path) as archive:
        names = set(archive.namelist())
        image_ids = {}
        if "xl/cellimages.xml" in names:
            rels = relation_map(archive, "xl/cellimages.xml")
            root = ET.fromstring(archive.read("xl/cellimages.xml"))
            for el in root:
                name = next((n.get("name") for n in el.iter() if n.tag.endswith("}cNvPr")), None)
                rid = next((n.get(R + "embed") for n in el.iter() if n.tag.endswith("}blip")), None)
                if name and rid in rels:
                    image_ids[name] = rels[rid]
        image_column = mapping.get("image", "D")
        for ref, formula in sheet["formulas"].items():
            match = re.search(r'DISPIMG\("([^"]+)"', formula)
            if match and ref.startswith(image_column) and match[1] in image_ids:
                images[int(re.sub(r"\D", "", ref))] = image_ids[match[1]]
        image_column_index = 0
        for letter in image_column.upper():
            image_column_index = image_column_index * 26 + ord(letter) - ord('A') + 1
        image_column_index -= 1
        for drawing in relation_map(archive, sheet["member"]).values():
            if "/drawings/" not in drawing or drawing not in names:
                continue
            rels = relation_map(archive, drawing)
            for anchor in ET.fromstring(archive.read(drawing)):
                origin = next((e for e in anchor if e.tag.endswith("}from")), None)
                if origin is None:
                    continue
                row = next((int(n.text) + 1 for n in origin if n.tag.endswith("}row")), None)
                col = next((int(n.text) for n in origin if n.tag.endswith("}col")), None)
                rid = next((n.get(R + "embed") for n in anchor.iter() if n.tag.endswith("}blip")), None)
                if row and col == image_column_index and rid in rels:
                    images.setdefault(row, rels[rid])
        for merged in sheet["merges"]:
            match = re.fullmatch(fr"{image_column}(\d+):{image_column}(\d+)", merged)
            if match:
                start, end = map(int, match.groups())
                if start in images:
                    for row in range(start, end + 1):
                        images[row] = images[start]
        # Explicit local exceptions complement merged-cell image relationships.
        by_sku = {str(r.get(mapping["sku"], "")): n for n, r in sheet["rows"].items()}
        for target, source in load_config().get('image_aliases', {}).items():
            if source not in by_sku or target not in by_sku:
                continue
            a, b = by_sku[source], by_sku[target]
            if a in images:
                images.setdefault(b, images[a])
        output.mkdir(parents=True, exist_ok=True)
        result = {}
        for row, member in images.items():
            if member not in names:
                continue
            ext = Path(member).suffix.lower()
            if ext not in {".png", ".jpg", ".jpeg", ".webp", ".gif"}:
                continue
            dest = output / f"row-{row}{ext}"
            dest.write_bytes(archive.read(member))
            result[row] = dest.name
        return result


def parse_manifest(path, images_dir, external_data=False, external_price=False):
    path = Path(path)
    if path.suffix.lower() == ".csv":
        raw = path.read_bytes()
        try:
            content = raw.decode("utf-8-sig")
        except UnicodeDecodeError:
            content = raw.decode("gb18030")
        csv_rows = list(csv.reader(io.StringIO(content)))
        sheets = [{"name": "CSV", "rows": {i: {column_name(j): v for j, v in enumerate(row, 1)} for i, row in enumerate(csv_rows, 1)}, "formulas": {}, "merges": []}]
    elif path.suffix.lower() == ".xlsx":
        sheets = read_workbook(path)
    else:
        raise ValueError("请上传 .xlsx 或 .csv 格式的清单")
    choices = [(s, *find_header(s)) for s in sheets]
    selected = next(((s, n, m) for s, n, m in choices if "quantity" in m), None)
    if not selected:
        raise ValueError("没有找到包含 SKU 和总数量的表头；请使用装箱清单模板")
    sheet, header, mapping = selected
    titles = {}
    for other, n, m in choices:
        if n and "name_pt" in m:
            for rn, row in other["rows"].items():
                if rn > n and row.get(m["sku"]) and row.get(m["name_pt"]):
                    titles[str(row[m["sku"]]).strip()] = str(row[m["name_pt"]]).strip()
    # WBL's auxiliary product sheet uses C=SKU and D=Portuguese title.
    for other in sheets:
        if other is not sheet:
            for row in other["rows"].values():
                sku = str(row.get("C", "")).strip()
                title = str(row.get("D", "")).strip()
                if re.fullmatch(r"NW[A-Za-z0-9._-]+", sku) and title:
                    titles.setdefault(sku, title)
    images = extract_images(path, sheet, mapping, Path(images_dir)) if path.suffix.lower() == ".xlsx" else {}
    errors, warnings, items = [], [], OrderedDict()
    source_rows = 0
    declared_total = None
    for rn, row in sheet["rows"].items():
        if rn <= header:
            continue
        sku = str(row.get(mapping["sku"], "")).strip()
        if sku.upper() in {"TOTAL", "TOTALS", "合计", "总计"} or (not sku and any("合计" in str(v) or "总计" in str(v) for v in row.values())):
            total = row.get(mapping["quantity"], "")
            if total:
                try:
                    declared_total = number(total, "清单合计")
                except ValueError:
                    pass
            continue
        if not sku:
            continue
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]{1,199}", sku):
            if row.get(mapping["quantity"]):
                errors.append(f"第 {rn} 行 SKU 无效：{sku}")
            continue
        source_rows += 1
        item = {"sku": sku, "name_pt": titles.get(sku, str(row.get(mapping.get("name_pt", ""), "")).strip()), "name_zh": str(row.get(mapping.get("name_zh", ""), "")).strip(), "source_rows": [rn], "image": images.get(rn), "quantity": 0, "unit_price": "0.00"}
        for field in ["quantity", "length_cm", "width_cm", "height_cm", "weight_kg", "unit_price"]:
            if external_price and field == 'unit_price':
                continue
            if external_data and field not in {'quantity', 'unit_price'}:
                item['weight_g' if field == 'weight_kg' else field] = 0
                continue
            use_field = "weight_g" if field == "weight_kg" and "weight_g" in mapping else field
            try:
                value = number(row.get(mapping.get(use_field, "")), f"{sku} 第 {rn} 行 {mapping.get(use_field, use_field)}")
                if field == "quantity":
                    if value != int(value):
                        raise ValueError(f"{sku} 件数必须是整数")
                    if value > 9007199254740991:
                        raise ValueError(f"{sku} 件数超过精确计算范围")
                    item[field] = int(value)
                elif field == "unit_price":
                    item[field] = str(value.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP))
                elif field == "weight_kg":
                    item["weight_g"] = float(value if use_field == "weight_g" else value * 1000)
                else:
                    item[field] = float(value)
            except ValueError as exc:
                errors.append(str(exc))
                item["weight_g" if field == "weight_kg" else field] = 0
        if sku in items:
            previous = items[sku]
            for field in ["unit_price", "length_cm", "width_cm", "height_cm", "weight_g"]:
                if previous[field] != item[field]:
                    errors.append(f"{sku} 第 {previous['source_rows'][0]}、{rn} 行 {field} 不一致，请修正清单")
            previous["quantity"] += item["quantity"]
            previous["source_rows"].append(rn)
            previous["image"] = previous["image"] or item["image"]
        else:
            items[sku] = item
    if not items:
        errors.append("清单中未找到有效商品")
    quantity = sum(i["quantity"] for i in items.values())
    amount = sum((Decimal(str(i["unit_price"])) * i["quantity"] for i in items.values()), Decimal(0)).quantize(Decimal("0.01"))
    if quantity > 9007199254740991 or amount * 100 > 9007199254740991:
        errors.append("本批件数或金额超过网页允许的精确计算范围")
    if declared_total is not None and declared_total != quantity:
        errors.append(f"逐 SKU 件数合计 {quantity} 与清单总计 {declared_total} 不符")
    missing_images = [i["sku"] for i in items.values() if not i["image"]]
    missing_titles = [i["sku"] for i in items.values() if not i["name_pt"]]
    if missing_images and not external_data:
        warnings.append(f"{len(missing_images)} 个 SKU 没有提取到图片；核对为新品后须补齐")
    if missing_titles:
        warnings.append(f"{len(missing_titles)} 个 SKU 缺少葡语名称；核对为新品后须补齐")
    batch = re.search(r"第[一二三四五六七八九十百零〇\d]+批", path.stem)
    return {"batch_name": canonical_batch_name(batch[0]) if batch else path.stem, "sheet": sheet["name"], "columns": mapping, "items": list(items.values()), "errors": errors, "warnings": warnings, "totals": {"skus": len(items), "source_rows": source_rows, "duplicates": source_rows - len(items), "quantity": quantity, "amount": str(amount)}}


def read_catalog(path):
    sheet = read_workbook(path)[0]
    rows = list(sheet["rows"].values())
    header_index = next((i for i, row in enumerate(rows[:20]) if any(text_key(v) in ALIASES["sku"] for v in row.values())), None)
    if header_index is None:
        raise ValueError("商品导出中未找到 SKU 表头")
    sku_column = next(c for c, v in rows[header_index].items() if text_key(v) in ALIASES["sku"])
    result = {}
    for row in rows[header_index + 1:]:
        sku = str(row.get(sku_column, "")).strip()
        if sku:
            if sku in result:
                raise ValueError(f"商品导出中 SKU 重复：{sku}")
            raw_ncm = str(row.get("AG", "")).strip()
            ncm = re.sub(r"[.\s-]", "", raw_ncm)
            if ncm.isdigit() and len(ncm) <= 8:
                ncm = ncm.zfill(8)
            result[sku] = {"sku": sku, "ncm": ncm, "name_pt": row.get("D", ""), "image_url": row.get("AB", "")}
    return result


def update_oms_export(source, destination, items):
    lookup = {i["sku"]: i for i in items}
    sheets = read_workbook(source)
    sheet = sheets[0]
    actual = {str(row.get("A", "")).strip(): n for n, row in sheet["rows"].items() if str(row.get("A", "")).strip() in lookup}
    matched_count = sum(str(row.get("A", "")).strip() in lookup for row in sheet["rows"].values())
    if set(actual) != set(lookup) or matched_count != len(lookup):
        raise ValueError("OMS 导出的 SKU 集合与待更新新品不一致")
    with ZipFile(source) as archive:
        xml = archive.read(sheet["member"]).decode("utf-8-sig")
        for sku, n in actual.items():
            pattern = re.compile(fr'<(?P<tag>(?:[\w.-]+:)?row)\b(?P<attrs>[^>]*\br="{n}"[^>]*)>(?P<body>.*?)</(?P=tag)>', re.S)
            row = pattern.search(xml)
            if row is None:
                raise ValueError(f"找不到工作表第 {n} 行")
            body = row["body"]
            prefix = row["tag"].removesuffix("row")
            for col, key in zip("DEFG", ["length_cm", "width_cm", "height_cm", "weight_g"]):
                cell_pattern = re.compile(fr'<(?P<tag>(?:[\w.-]+:)?c)\b(?P<attrs>[^>]*?\br="{col}{n}"[^>]*?)(?:\s*/>|>.*?</(?P=tag)>)', re.S)
                cell = cell_pattern.search(body)
                attrs = re.sub(r'\s+t="[^"]*"', '', cell["attrs"]) if cell else f' r="{col}{n}"'
                tag = cell["tag"] if cell else prefix + "c"
                replacement = f'<{tag}{attrs}><{prefix}v>{lookup[sku][key]}</{prefix}v></{tag}>'
                if cell:
                    body = body[:cell.start()] + replacement + body[cell.end():]
                else:
                    later = next((m for m in re.finditer(r'<(?:[\w.-]+:)?c\b[^>]*\br="([A-Z]+)\d+"', body) if (len(m[1]), m[1]) > (len(col), col)), None)
                    at = later.start() if later else len(body)
                    body = body[:at] + replacement + body[at:]
            xml = xml[:row.start()] + f'<{row["tag"]}{row["attrs"]}>' + body + f'</{row["tag"]}>' + xml[row.end():]
        with ZipFile(destination, "w", ZIP_DEFLATED) as out:
            for member in archive.infolist():
                out.writestr(member, xml.encode("utf-8") if member.filename == sheet["member"] else archive.read(member.filename))
    revised = read_workbook(destination)[0]["rows"]
    for n, row in sheet["rows"].items():
        for col in set(row) | set(revised[n]):
            if col not in "DEFG" and row.get(col, "") != revised[n].get(col, ""):
                raise ValueError(f"检测到授权范围以外的变化：{col}{n}")
    return str(destination)


def build_single_import(template, destination, catalog_path, skus):
    catalog = read_catalog(catalog_path)
    sheet = read_workbook(template)[0]
    header = sheet["rows"].get(1, {})
    if not str(header.get("A", "")).startswith("SKU") or not str(header.get("B", "")).startswith("标题") or not str(header.get("M", "")).startswith("图片"):
        raise ValueError("UpSeller 单品模板结构已变化，需要核对字段")
    from xml.sax.saxutils import escape
    with ZipFile(template) as archive:
        xml = archive.read(sheet["member"]).decode("utf-8-sig")
        data = re.search(r'<(?P<tag>(?:[\w.-]+:)?sheetData)\b[^>]*>(?P<body>.*?)</(?P=tag)>', xml, re.S)
        header_row = re.search(r'<(?P<tag>(?:[\w.-]+:)?row)\b[^>]*\br="1"[^>]*>.*?</(?P=tag)>', data["body"], re.S) if data else None
        if not header_row:
            raise ValueError("单品模板缺少表头行")
        prefix = data["tag"].removesuffix("sheetData")
        new_rows = [header_row[0]]
        for rn, sku in enumerate(skus, 2):
            record = catalog.get(sku)
            if not record or not record["name_pt"] or not record["image_url"]:
                raise ValueError(f"{sku} 的常用账号导出缺少葡语名称或图片直链")
            cells = []
            for col, text in [("A", sku), ("B", record["name_pt"]), ("M", record["image_url"])]:
                cells.append(f'<{prefix}c r="{col}{rn}" t="inlineStr"><{prefix}is><{prefix}t>{escape(str(text))}</{prefix}t></{prefix}is></{prefix}c>')
            new_rows.append(f'<{prefix}row r="{rn}">' + ''.join(cells) + f'</{prefix}row>')
        xml = xml[:data.start("body")] + ''.join(new_rows) + xml[data.end("body"):]
        xml = re.sub(r'(<(?:[\w.-]+:)?dimension\b[^>]*\bref=")[^"]*(")', fr'\g<1>A1:V{len(skus) + 1}\2', xml, count=1)
        with ZipFile(destination, "w", ZIP_DEFLATED) as out:
            for member in archive.infolist():
                out.writestr(member, xml.encode("utf-8") if member.filename == sheet["member"] else archive.read(member.filename))
    return str(destination)


def verify_oms(source, exported, items):
    before = read_workbook(source)[0]["rows"]
    after = read_workbook(exported)[0]["rows"]
    original = {r.get("A"): r for r in before.values()}
    actual = {r.get("A"): r for r in after.values()}
    for item in items:
        row = actual.get(item["sku"])
        if not row or row.get("B") != original[item["sku"]].get("B"):
            raise ValueError(f"{item['sku']} 葡语名称或导出结果不匹配")
        for col, key in zip("DEFG", ["length_cm", "width_cm", "height_cm", "weight_g"]):
            if Decimal(str(row.get(col) or 0)) != Decimal(str(item[key])):
                raise ValueError(f"{item['sku']} {key} 复核失败")
    return {"verified": len(items)}


def prepare_attachments(source, pdf, xml, output, batch_name):
    from PIL import Image
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    for path in [pdf, xml, source]:
        if not Path(path).is_file() or not Path(path).stat().st_size:
            raise ValueError(f"附件不存在或为空：{Path(path).name}")
    if not Path(pdf).read_bytes().startswith(b"%PDF-"):
        raise ValueError("下载内容不是 PDF 文件")
    xml_bytes = Path(xml).read_bytes()
    ET.fromstring(xml_bytes)
    pdf_dest = output / f"{batch_name}入库发票.pdf"
    pdf_dest.write_bytes(Path(pdf).read_bytes())
    xml_dest = output / f"{batch_name}入库发票.xml"
    xml_dest.write_bytes(xml_bytes)
    zip_dest = output / f"{batch_name}入库发票_XML.zip"
    with ZipFile(zip_dest, "w", ZIP_DEFLATED) as archive:
        archive.writestr(xml_dest.name, xml_bytes)
    with ZipFile(zip_dest) as archive:
        assert archive.read(xml_dest.name) == xml_bytes
    manifest_dest = output / Path(source).name
    if Path(source).stat().st_size <= 10_000_000:
        manifest_dest.write_bytes(Path(source).read_bytes())
    elif Path(source).suffix.lower() == ".xlsx":
        with ZipFile(source) as archive, ZipFile(manifest_dest, "w", ZIP_DEFLATED) as out:
            for member in archive.infolist():
                data = archive.read(member.filename)
                if member.filename.startswith("xl/media/") and Path(member.filename).suffix.lower() in {".png", ".jpg", ".jpeg"}:
                    with Image.open(io.BytesIO(data)) as image:
                        image.thumbnail((1400, 1400))
                        buffer = io.BytesIO()
                        if Path(member.filename).suffix.lower() == ".png":
                            image.convert("RGB").quantize(colors=128).save(buffer, "PNG", optimize=True)
                        else:
                            image.convert("RGB").save(buffer, "JPEG", quality=70, optimize=True)
                        if len(buffer.getvalue()) < len(data):
                            data = buffer.getvalue()
                out.writestr(member, data)
    else:
        raise ValueError("清单超过 10MB，请调整后重新上传")
    if manifest_dest.stat().st_size > 10_000_000:
        raise ValueError("图片压缩后清单仍超过 10MB，需要进一步处理")
    return [str(pdf_dest), str(manifest_dest), str(zip_dest)]


def verify_invoice_files(pdf, xml, expected_amount):
    from pypdf import PdfReader
    root = ET.fromstring(Path(xml).read_bytes())
    def value(name):
        return next((n.text for n in root.iter() if n.tag.rsplit("}", 1)[-1] == name), "")
    invoice_no = value("nNF")
    key = value("chNFe") or next((n.get("Id", "").removeprefix("NFe") for n in root.iter() if n.tag.rsplit("}", 1)[-1] == "infNFe"), "")
    if not invoice_no or not re.fullmatch(r"\d{44}", key) or Decimal(value("vNF") or "0") != Decimal(expected_amount):
        raise ValueError("XML 发票号、访问密钥或总额与本批不符")
    recipient = next((n for n in root.iter() if n.tag.rsplit("}", 1)[-1] == "dest"), None)
    recipient_cnpj = next((n.text for n in recipient.iter() if n.tag.rsplit("}", 1)[-1] == "CNPJ"), "") if recipient is not None else ""
    if recipient_cnpj != re.sub(r'\D', '', billing_config()['cnpj']):
        raise ValueError("XML 收件人 CNPJ 与本机配置不符")
    text = "\n".join(page.extract_text() or "" for page in PdfReader(pdf).pages)
    if key not in re.sub(r"\D", "", text):
        raise ValueError("无法在 PDF 中核对到 XML 的 44 位发票访问密钥，请人工核实下载对象")
    return {"invoice_no": invoice_no.zfill(9), "access_key_verified": True}
