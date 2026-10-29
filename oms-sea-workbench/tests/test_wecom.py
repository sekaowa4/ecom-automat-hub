import copy
import io
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile
from xml.etree import ElementTree as ET
from PIL import Image
from webapp.manifest import S, R, parse_manifest


def table(title, header, rows):
    return title + '\n' + '\n'.join('|' + '|'.join(row) + '|' for row in [header, ['---'] * len(header), *rows])


def xlsx_with_floating_image(path, anchor_column=3, wps_cell_image=False):
    sheet = ET.Element(S + 'worksheet')
    data = ET.SubElement(sheet, S + 'sheetData')
    rows = [(1, {'B': 'SKU', 'K': '总数量'}), (2, {'B': 'DEMO001', 'K': '7'})]
    if wps_cell_image:
        rows.extend([(3, {'B': 'DEMO002', 'K': '8'}), (4, {'B': 'DEMO003', 'K': '9'})])
    for number, values in rows:
        row = ET.SubElement(data, S + 'row', {'r': str(number)})
        for column, value in values.items():
            cell = ET.SubElement(row, S + 'c', {'r': f'{column}{number}', 't': 'inlineStr'})
            ET.SubElement(ET.SubElement(cell, S + 'is'), S + 't').text = value
    if wps_cell_image:
        formula_cell = ET.SubElement(data.find(f"{S}row[@r='2']"), S + 'c', {'r': 'C2'})
        ET.SubElement(formula_cell, S + 'f').text = '_xlfn.DISPIMG("ID_DEMO",1)'
        merges = ET.SubElement(sheet, S + 'mergeCells')
        ET.SubElement(merges, S + 'mergeCell', {'ref': 'C3:C4'})
    ET.SubElement(sheet, S + 'drawing', {R + 'id': 'rIdDraw'})

    drawing = ET.Element('{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}wsDr')
    anchor = ET.SubElement(drawing, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}oneCellAnchor')
    origin = ET.SubElement(anchor, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}from')
    ET.SubElement(origin, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}col').text = str(anchor_column)
    ET.SubElement(origin, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}row').text = '2' if wps_cell_image else '1'
    picture = ET.SubElement(anchor, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}pic')
    fill = ET.SubElement(picture, '{http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing}blipFill')
    ET.SubElement(fill, '{http://schemas.openxmlformats.org/drawingml/2006/main}blip', {R + 'embed': 'rIdImage'})

    png = io.BytesIO()
    Image.new('RGB', (1, 1), 'red').save(png, format='PNG')
    with ZipFile(path, 'w') as archive:
        archive.writestr('xl/workbook.xml', '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="清单" sheetId="1" r:id="rId1"/></sheets></workbook>')
        archive.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>')
        archive.writestr('xl/worksheets/sheet1.xml', ET.tostring(sheet))
        archive.writestr('xl/worksheets/_rels/sheet1.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdDraw" Target="../drawings/drawing1.xml"/></Relationships>')
        archive.writestr('xl/drawings/drawing1.xml', ET.tostring(drawing))
        archive.writestr('xl/drawings/_rels/drawing1.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdImage" Target="../media/image1.png"/></Relationships>')
        archive.writestr('xl/media/image1.png', png.getvalue())
        if wps_cell_image:
            archive.writestr('xl/cellimages.xml', '<cellImages><cellImage><cNvPr xmlns="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" name="ID_DEMO"/><blip xmlns="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="rIdCellImage"/></cellImage></cellImages>')
            archive.writestr('xl/_rels/cellimages.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdCellImage" Target="media/image1.png"/></Relationships>')


class WecomTests(unittest.TestCase):
    def test_bilingual_manifest_uses_total_pieces_and_does_not_read_rmb_price(self):
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory) / 'demo.csv'
            p.write_text('商品SKU,单箱数量（Quantidade por Caixa）,总数量（Quantidade Total）,单价（人民币）\nDEMO001,10,400,99\nDEMO001,1,540,99\n', encoding='utf-8')
            m = parse_manifest(p, Path(directory), external_data=True, external_price=True)
            self.assertEqual(m['errors'], [])
            self.assertEqual(m['columns']['quantity'], 'C')
            self.assertEqual(m['items'][0]['quantity'], 940)
            self.assertEqual(m['items'][0]['unit_price'], '0.00')
            p.write_text('商品SKU（SKU）,数量（每箱）,总数量（Quantidade Total）\nDEMO001,5,100\n', encoding='utf-8')
            m = parse_manifest(p, Path(directory), external_data=True, external_price=True)
            self.assertEqual(m['items'][0]['quantity'], 100)
            self.assertEqual(m['columns']['quantity'], 'C')

    def test_online_batch_e_price_is_selected_by_sku_and_conflicts_block(self):
        from webapp.wecom import match_prices
        md = table('批次甲', ['商品', 'SKU', '名称', '备注', '单价'], [['示例', 'DEMO001', '', '', '12.345']])
        items, title = match_prices([{'sku': 'DEMO001', 'quantity': 7, 'unit_price': '99'}], md)
        self.assertEqual(title, '批次甲')
        self.assertEqual(items[0]['unit_price'], '12.35')
        with self.assertRaises(ValueError):
            match_prices([{'sku': 'DEMO001'}], md + '\n' + table('批次乙', ['商品', 'SKU', '名称', '备注', '单价'], [['示例', 'DEMO001', '', '', '8']]))
        self.assertEqual(match_prices([{'sku': 'DEMO001'}], md, '批次甲')[1], '批次甲')
        with self.assertRaises(ValueError):
            match_prices([{'sku': 'DEMO001'}], md + '\n|示例|DEMO001|||13|', '批次甲')
        with self.assertRaises(ValueError):
            match_prices([{'sku': 'ABSENT'}], md, '批次甲')
        with self.assertRaises(ValueError):
            match_prices([{'sku': 'DEMO001'}], md + '\n|示例|OTHER|||8|')

    def fixtures(self):
        images = [''] * 14
        images[1], images[13] = 'SKU', '产品图'
        row = [''] * 14
        row[1], row[13] = 'DEMO001', '![](https://wdcdn.qpic.cn/example?w=800&h=800)'
        sizes = [''] * 30
        sizes[2] = 'SKU'
        sizes[26:30] = ['单套包装长', '单套包装宽', '单套包装高', '单套含包装重量（kg）']
        size = [''] * 30
        size[2], size[26:30] = 'DEMO001', ['10', '20', '30', '0.125']
        return table('图包作图需求', images, [row]), table('箱规汇总表', sizes, [size])

    def test_sku_match_reads_n_and_aa_ad_and_converts_kg(self):
        from webapp.wecom import match_items
        image, size = self.fixtures()
        items = [{'sku': 'DEMO001', 'quantity': 7, 'unit_price': '3.00', 'image': 'old.png', 'length_cm': 999}]
        original = copy.deepcopy(items)
        found, errors = match_items(items, image, size)
        self.assertEqual(errors, [])
        self.assertEqual(items, original)
        self.assertEqual(found[0]['weight_g'], 125)
        self.assertEqual(found[0]['length_cm'], 10)
        self.assertEqual(found[0]['quantity'], 7)
        self.assertEqual(found[0]['unit_price'], '3.00')
        self.assertIsNone(found[0]['image'])
        self.assertEqual(found[0]['image_source'], '企微图包作图需求 N')
        self.assertIn('qpic.cn', found[0]['image_url'])

    def test_unmatched_wecom_image_uses_manifest_image_but_conflict_does_not(self):
        from webapp.wecom import match_items
        image, size = self.fixtures()
        missing = image.replace('DEMO001', 'OTHER001')
        found, errors = match_items([{'sku': 'DEMO001', 'image': 'manifest.png', 'weight_g': 999}], missing, size)
        self.assertEqual(errors, [])
        self.assertEqual(found[0]['image'], 'manifest.png')
        self.assertEqual(found[0]['image_source'], '装箱清单原图')
        self.assertIsNone(found[0]['image_url'])
        self.assertEqual(found[0]['weight_g'], 125)
        conflicting = image + '\n|产品|DEMO001||||||||||||![](https://wdcdn.qpic.cn/other.png)|'
        blocked, errors = match_items([{'sku': 'DEMO001', 'image': 'manifest.png'}], conflicting, size)
        self.assertTrue(errors)
        self.assertIsNone(blocked[0]['image'])
        self.assertEqual(blocked[0]['image_source'], '')

    def test_external_parse_extracts_floating_image_anchored_to_image_column(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'demo.xlsx'
            xlsx_with_floating_image(source)
            original = source.read_bytes()
            parsed = parse_manifest(source, Path(directory) / 'images', external_data=True, external_price=True)
            self.assertEqual(parsed['errors'], [])
            self.assertEqual(parsed['items'][0]['image'], 'row-2.png')
            self.assertEqual((Path(directory) / 'images' / 'row-2.png').read_bytes(), ZipFile(source).read('xl/media/image1.png'))
            self.assertEqual(source.read_bytes(), original)

    def test_external_parse_does_not_assign_floating_image_anchored_outside_image_column(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'demo.xlsx'
            xlsx_with_floating_image(source, anchor_column=4)
            parsed = parse_manifest(source, Path(directory) / 'images', external_data=True, external_price=True)
            self.assertIsNone(parsed['items'][0]['image'])

    def test_external_parse_detects_wps_image_column_and_floating_anchor(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'demo.xlsx'
            xlsx_with_floating_image(source, anchor_column=2, wps_cell_image=True)
            parsed = parse_manifest(source, Path(directory) / 'images', external_data=True, external_price=True)
            self.assertEqual([(item['sku'], item['image']) for item in parsed['items']], [
                ('DEMO001', 'row-2.png'),
                ('DEMO002', 'row-3.png'),
                ('DEMO003', 'row-4.png'),
            ])

    def test_external_csv_parse_does_not_require_manifest_images_or_sizes(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'demo.csv'
            source.write_text('SKU,总数量,开票单价\nDEMO001,7,3\n', encoding='utf-8')
            before = source.read_bytes()
            parsed = parse_manifest(source, Path(directory) / 'images', external_data=True)
            self.assertEqual(parsed['errors'], [])
            self.assertEqual(parsed['totals']['amount'], '21.00')
            self.assertEqual(source.read_bytes(), before)

    def test_write_tools_are_blocked_before_network(self):
        from webapp.wecom import call_tool
        with self.assertRaises(ValueError):
            call_tool('sheet_update_range_data', {})

    def test_image_download_rejects_non_wecom_hosts(self):
        from webapp.wecom import download_image
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                download_image('https://127.0.0.1/private', directory)

    def test_ambiguous_comma_decimals_and_truncated_duplicate_rows_block(self):
        from webapp.wecom import match_items
        image, size = self.fixtures()
        self.assertTrue(match_items([{'sku': 'DEMO001'}], image, size.replace('0.125', '0,125'))[1])
        short_row = '|' + '|'.join(['', '', 'DEMO001'] + [''] * 23 + ['11', '20', '30']) + '|'
        self.assertTrue(match_items([{'sku': 'DEMO001'}], image, size + '\n' + short_row)[1])

    def test_connected_upload_enriches_in_background_and_leaves_source_unchanged(self):
        from webapp.server import Store
        images, sizes = self.fixtures()
        sizes += '\n' + table('批次甲', ['产品', '名称', 'SKU', '葡语', '单价'], [['示例', '', 'DEMO001', '', '3']])
        raw = 'SKU,总数量,开票单价\nDEMO001,7,3\n'.encode()
        with tempfile.TemporaryDirectory() as directory:
            store = Store(directory)
            with patch('webapp.server.call_tool', return_value={'sheets': [{'title': '图包作图需求'}, {'title': '箱规汇总表'}]}):
                store.connect_wecom({'images_url': 'https://doc.weixin.qq.com/sheet/e3_demo_image', 'sizes_url': 'https://doc.weixin.qq.com/sheet/e3_demo_size'})
            with patch('webapp.server.read_document', side_effect=[images, sizes]), patch('webapp.server.download_image', return_value='wecom-demo.png'):
                job = store.upload('demo.csv', raw)
                for _ in range(200):
                    if job['status'] != 'matching':
                        break
                    time.sleep(0.01)
            self.assertEqual(job['status'], 'ready', job['manifest']['errors'])
            self.assertEqual(job['manifest']['items'][0]['weight_g'], 125)
            self.assertEqual(job['manifest']['items'][0]['image'], 'wecom-demo.png')
            self.assertEqual(job['manifest']['totals']['amount'], '21.00')
            self.assertEqual((Path(directory) / job['id'] / 'source/demo.csv').read_bytes(), raw)
            with store.lock:
                job['status'] = 'matching'
                store.save(job)
            reloaded = Store(directory).get(job['id'])
            self.assertEqual(reloaded['status'], 'invalid')
            self.assertTrue(reloaded['manifest']['errors'])

    def test_connected_upload_uses_embedded_image_when_wecom_has_no_sku_row(self):
        from webapp.server import Store
        images, sizes = self.fixtures()
        images = images.replace('DEMO001', 'OTHER001')
        sizes += '\n' + table('批次甲', ['SKU', '名称', '', '', '单价'], [['DEMO001', '', '', '', '3']])
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'demo.xlsx'
            xlsx_with_floating_image(source)
            original = source.read_bytes()
            store = Store(Path(directory) / 'state')
            store.wecom = {'enabled': True, 'images_url': 'images', 'sizes_url': 'sizes', 'price_sheets': ['批次甲']}
            with patch('webapp.server.read_document', side_effect=[images, sizes]):
                job = store.upload(source.name, original)
                for _ in range(200):
                    if job['status'] != 'matching':
                        break
                    time.sleep(0.01)
            self.assertEqual(job['status'], 'ready', job['manifest']['errors'])
            self.assertEqual(job['manifest']['items'][0]['image'], 'row-2.png')
            self.assertEqual(job['manifest']['items'][0]['image_source'], '装箱清单原图')
            self.assertTrue(any('使用清单原图' in warning for warning in job['manifest']['warnings']))
            self.assertEqual((Path(directory) / 'state' / job['id'] / 'source' / source.name).read_bytes(), original)

    def test_unmatched_old_upload_cannot_start_or_modify_completed_batch(self):
        from webapp.server import Store
        with tempfile.TemporaryDirectory() as directory:
            store = Store(directory)
            job = store.upload('demo.csv', 'SKU,总数量,开票单价,单套包装长,单套包装宽,单套包装高,重量(g)\nDEMO001,7,3,1,2,3,4\n'.encode())
            store.browser['ok'] = True
            store.settings.update(common_page=1, invoice_page=2, oms_page=3)
            with self.assertRaisesRegex(ValueError, '企微'):
                store.start(job['id'])
            store.wecom['enabled'] = True
            job['status'] = 'completed'
            with self.assertRaises(ValueError):
                store.match_wecom(job['id'])
