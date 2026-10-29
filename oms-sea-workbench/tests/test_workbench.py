import io
import hashlib
import json
import tempfile
import threading
import unittest
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError
from http.server import ThreadingHTTPServer
from zipfile import ZipFile
from xml.etree import ElementTree as ET
from unittest.mock import patch
from webapp.manifest import S, parse_manifest, number, read_workbook, update_oms_export, read_catalog, verify_oms, prepare_attachments
from webapp.server import Store, Handler, within


CSV = '产品,SKU,葡语名称,总数量,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）,开票单价\n示例,DEMO0001SA,Produto,80,10,20,30,0.5,12\n示例,DEMO0001SA,Produto,76,10,20,30,0.5,12\n'


def workbook(path, rows):
    root = ET.Element(S + 'worksheet')
    ET.SubElement(root, S + 'dimension', {'ref': 'A1:A1'})
    data = ET.SubElement(root, S + 'sheetData')
    for rn, values in rows.items():
        row = ET.SubElement(data, S + 'row', {'r': str(rn)})
        for col, value in values.items():
            cell = ET.SubElement(row, S + 'c', {'r': f'{col}{rn}', 't': 'inlineStr'})
            ET.SubElement(ET.SubElement(cell, S + 'is'), S + 't').text = str(value)
    with ZipFile(path, 'w') as archive:
        archive.writestr('xl/workbook.xml', '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="商品" sheetId="1" r:id="rId1"/></sheets></workbook>')
        archive.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>')
        archive.writestr('xl/worksheets/sheet1.xml', ET.tostring(root))
        archive.writestr('xl/unchanged.xml', '<preserve>原有内容</preserve>')


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def parse(self, content=CSV):
        source = self.root / '第十六批.csv'
        source.write_text(content, encoding='utf-8-sig')
        return parse_manifest(source, self.root / 'images')

    def test_duplicates_merge_pieces_and_exact_price_without_twenty_percent(self):
        batch = self.parse()
        self.assertEqual(batch['errors'], [])
        self.assertEqual(batch['totals']['quantity'], 156)
        self.assertEqual(batch['totals']['amount'], '1872.00')
        self.assertEqual(batch['items'][0]['weight_g'], 500)
        self.assertEqual(batch['items'][0]['unit_price'], '12.00')
        self.assertEqual(batch['items'][0]['source_rows'], [2, 3])

    def test_price_conflicts_and_fractional_quantity_block_execution(self):
        batch = self.parse(CSV.replace('76,10,20,30,0.5,12', '76.5,10,20,30,0.5,13'))
        self.assertTrue(any('整数' in e for e in batch['errors']))
        self.assertTrue(any('unit_price' in e for e in batch['errors']))

    def test_invalid_numeric_values_never_become_zero_success(self):
        for value in ['NaN', 'Infinity', '-1', '', '0']:
            with self.assertRaises(ValueError):
                number(value, '测试')

    def test_reads_actual_rows_despite_dimension_a1_and_uses_k_not_i_or_j(self):
        source = self.root / 'test.xlsx'
        workbook(source, {1: {'B': 'SKU', 'E': '产品', 'K': '总数量\nPCS'}, 2: {'B': 'NW123', 'E': '产品', 'I': '2', 'J': '3', 'K': '156', 'W': '10', 'X': '20', 'Y': '30', 'Z': '0.5', 'AA': '12'}, 49: {'E': '合计', 'K': '156'}})
        batch = parse_manifest(source, self.root / 'images')
        self.assertEqual(batch['errors'], [])
        self.assertEqual(batch['totals']['quantity'], 156)

    def test_total_mismatch_blocks(self):
        batch = self.parse(CSV + '合计,,,200,,,,,\n')
        self.assertTrue(any('总计' in e for e in batch['errors']))

    def test_product_name_containing_total_characters_is_not_a_footer(self):
        batch = self.parse(CSV.replace('示例,', '组合计数器,'))
        self.assertEqual(batch['totals']['quantity'], 156)
        self.assertEqual(batch['errors'], [])

    def test_oms_update_changes_only_d_to_g_and_preserves_portuguese(self):
        source, output = self.root / 'oms.xlsx', self.root / 'patched.xlsx'
        workbook(source, {1: {'A': '商品SKU', 'B': '商品名称'}, 2: {'A': 'NW123', 'B': 'Produto original', 'D': '0', 'E': '0', 'F': '0', 'G': '0', 'H': 'cm/g', 'O': '保留'}})
        item = {'sku': 'NW123', 'length_cm': 10, 'width_cm': 20, 'height_cm': 30, 'weight_g': 500}
        update_oms_export(source, output, [item])
        self.assertEqual(verify_oms(source, output, [item]), {'verified': 1})
        self.assertEqual(read_workbook(output)[0]['rows'][2]['B'], 'Produto original')
        with ZipFile(source) as before, ZipFile(output) as after:
            self.assertEqual(before.read('xl/unchanged.xml'), after.read('xl/unchanged.xml'))

    def test_ncm_comes_from_ag_and_preserves_leading_zero(self):
        source = self.root / 'catalog.xlsx'
        workbook(source, {1: {'A': 'SKU', 'B': '别名', 'D': '标题', 'AG': 'NCM'}, 2: {'A': 'NW123', 'D': 'Produto', 'AG': '1234567', 'AB': 'https://example.com/p.png'}})
        catalog = read_catalog(source)
        self.assertEqual(catalog['NW123']['ncm'], '01234567')
        self.assertEqual(catalog['NW123']['name_pt'], 'Produto')

    def test_xml_zip_contains_unchanged_xml_and_manifest_keeps_name(self):
        source, pdf, xml = self.root / '清单.csv', self.root / 'a.pdf', self.root / 'a.xml'
        source.write_text(CSV, encoding='utf-8')
        pdf.write_bytes(b'%PDF-1.7\nunit test')
        raw = b'<?xml version="1.0"?><nfe><nNF>7</nNF></nfe>'
        xml.write_bytes(raw)
        files = prepare_attachments(source, pdf, xml, self.root / 'out', '第十六批')
        self.assertEqual(Path(files[1]).name, source.name)
        with ZipFile(files[2]) as archive:
            self.assertEqual(archive.namelist(), ['第十六批入库发票.xml'])
            self.assertEqual(archive.read(archive.namelist()[0]), raw)


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = Store(self.tmp.name)
        self.job = self.store.upload('第十七批.csv', CSV.encode())

    def tearDown(self):
        self.tmp.cleanup()

    def test_reupload_returns_existing_job(self):
        self.assertEqual(self.store.upload('第十七批.csv', CSV.encode())['id'], self.job['id'])
        self.assertEqual(len(self.store.jobs), 1)
        with self.assertRaises(ValueError):
            self.store.upload('另一批.csv', CSV.encode())
        with patch('webapp.server.load_config', return_value={'batch_price_sheets': {'第十七批.csv': '另一批单价'}}):
            with self.assertRaises(ValueError):
                self.store.upload('第十七批.csv', CSV.encode())

    def test_corrupt_xlsx_is_rejected_without_creating_a_job(self):
        with self.assertRaisesRegex(ValueError, '无法读取清单'):
            self.store.upload('bad.xlsx', b'not a zip archive')
        self.assertEqual(len(self.store.jobs), 1)

    def test_restart_invalidates_confirmation_and_keeps_progress(self):
        self.store.event(self.job, {'type': 'step_done', 'step': 'catalog'})
        self.store.event(self.job, {'type': 'confirmation', 'id': 'nonce', 'step': 'submit', 'title': '提交', 'details': []})
        reloaded = Store(self.tmp.name).get(self.job['id'])
        self.assertEqual(reloaded['status'], 'paused')
        self.assertIsNone(reloaded['gate'])
        self.assertEqual(reloaded['steps'][0]['status'], 'done')

    def test_stale_approval_is_rejected_and_valid_gate_sent_once(self):
        class Process:
            stdin = io.StringIO()
            def poll(self): return None
        process = Process()
        self.store.processes[self.job['id']] = process
        self.store.event(self.job, {'type': 'confirmation', 'id': 'nonce', 'step': 'submit', 'title': '提交', 'details': []})
        with self.assertRaises(ValueError): self.store.approve(self.job['id'], 'old')
        self.assertEqual(process.stdin.getvalue(), '')
        self.store.approve(self.job['id'], 'nonce')
        self.assertEqual(json.loads(process.stdin.getvalue()), {'type': 'approve', 'id': 'nonce'})
        with self.assertRaises(ValueError): self.store.approve(self.job['id'], 'nonce')

    def test_file_paths_cannot_escape_batch_directory(self):
        with self.assertRaises(ValueError): within(self.tmp.name, '../../other.txt')

    def test_account_mapping_cannot_reuse_same_account(self):
        self.store.browser = {'ok': True, 'pages': [{'id': 1, 'site': 'upseller', 'account': 'same'}, {'id': 2, 'site': 'upseller', 'account': 'same'}, {'id': 3, 'site': 'oms'}]}
        with self.assertRaisesRegex(ValueError, '同一账号'):
            self.store.set_settings({'common_page': 1, 'invoice_page': 2, 'oms_page': 3})

    def test_known_completed_batch_cannot_be_recreated_with_different_data(self):
        root = Path(self.tmp.name)
        (root / 'webapp').mkdir()
        history = {'第十六批': {'sku_hash': hashlib.sha256(b'DEMO0001SA').hexdigest(), 'skus': 1, 'quantity': 156, 'amount': '1872.00', 'records': {'inbound_no': 'DEMO-INBOUND'}, 'completed_at': '2026-01-01'}}
        (root / 'webapp/completed_batches.json').write_text(json.dumps(history), encoding='utf-8')
        with patch('webapp.server.ROOT', root):
            old = self.store.upload('第16批.csv', CSV.replace('80,10', '81,10').encode())
        self.assertEqual(old['status'], 'invalid')
        self.assertTrue(any('完成记录' in e for e in old['manifest']['errors']))
        self.store.wecom['enabled'] = True
        with patch('webapp.server.ROOT', root):
            with self.assertRaises(ValueError):
                self.store.match_wecom(old['id'])
            old['status'] = 'ready'
            old['manifest']['errors'] = []
            old['manifest']['external_source'] = {'kind': 'wecom'}
            self.store.browser['ok'] = True
            self.store.settings.update(common_page=1, invoice_page=2, oms_page=3)
            with self.assertRaises(ValueError):
                self.store.start(old['id'])

        with patch('webapp.server.ROOT', root):
            matching = Store(root / 'another-state').upload('第16批.csv', CSV.encode())
        self.assertEqual(matching['status'], 'completed')
        self.assertEqual(matching['records']['inbound_no'], 'DEMO-INBOUND')

    def test_pending_login_cannot_be_selected_for_execution(self):
        self.store.browser = {'ok': True, 'pages': [{'id': 0, 'site': 'upseller', 'login_required': True}, {'id': 1, 'site': 'upseller'}, {'id': 2, 'site': 'oms'}]}
        with self.assertRaisesRegex(ValueError, '登录验证'):
            self.store.set_settings({'common_page': 0, 'invoice_page': 1, 'oms_page': 2})

    def test_http_upload_and_origin_boundary(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        server.store = self.store
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = f'http://127.0.0.1:{server.server_port}'
        try:
            with urlopen(url + '/api/state') as response:
                self.assertEqual(len(json.load(response)['jobs']), 1)
            request = Request(url + '/api/jobs?filename=new.csv', data=CSV.encode(), headers={'Origin': 'https://evil.example'})
            with self.assertRaises(HTTPError) as error: urlopen(request)
            self.assertEqual(error.exception.code, 400)
            with self.assertRaises(HTTPError): urlopen(url + f'/api/jobs/{self.job["id"]}/state.json')
        finally:
            server.shutdown(); server.server_close(); thread.join()


class ExistingBatchChecks(unittest.TestCase):
    def test_real_batch16_history_and_attachment_integrity_when_sources_exist(self):
        from webapp.config import load_config
        files = load_config().get('acceptance_files', {})
        if not all(files.get(key) for key in ['source', 'pdf', 'xml']):
            self.skipTest('未配置本机历史验收文件')
        source, pdf, xml = [Path(files[key]) for key in ['source', 'pdf', 'xml']]
        history = json.loads((Path(__file__).resolve().parent.parent / 'webapp/completed_batches.json').read_text(encoding='utf-8'))['第十六批']
        if not all(p.exists() for p in [source, pdf, xml]):
            self.skipTest('当前机器没有第 16 批历史验收文件')
        with tempfile.TemporaryDirectory() as directory:
            store = Store(Path(directory) / 'state')
            job = store.upload(source.name, source.read_bytes())
            self.assertEqual(job['manifest']['errors'], [])
            self.assertEqual(job['manifest']['totals']['quantity'], history['quantity'])
            self.assertEqual(job['manifest']['totals']['amount'], history['amount'])
            self.assertEqual(job['status'], 'completed')
            self.assertEqual(job['records']['inbound_no'], history['records']['inbound_no'])
            with self.assertRaises(ValueError): store.start(job['id'])
            files = prepare_attachments(source, pdf, xml, Path(directory) / 'files', '第十六批')
            compressed = Path(files[1])
            self.assertEqual(compressed.name, source.name)
            self.assertLess(compressed.stat().st_size, 10_000_000)
            with ZipFile(source) as before, ZipFile(compressed) as after:
                self.assertEqual(before.namelist(), after.namelist())
                for name in before.namelist():
                    if not name.startswith('xl/media/'):
                        self.assertEqual(before.read(name), after.read(name), name)


if __name__ == '__main__':
    unittest.main()
