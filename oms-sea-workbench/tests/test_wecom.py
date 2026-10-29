import copy
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch
from webapp.manifest import parse_manifest


def table(title, header, rows):
    return title + '\n' + '\n'.join('|' + '|'.join(row) + '|' for row in [header, ['---'] * len(header), *rows])


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
        self.assertIn('qpic.cn', found[0]['image_url'])

    def test_missing_or_conflicting_sources_do_not_fall_back(self):
        from webapp.wecom import match_items
        image, size = self.fixtures()
        found, errors = match_items([{'sku': 'ABSENT', 'image': 'old.png', 'weight_g': 999}], image, size)
        self.assertTrue(errors)
        self.assertIsNone(found[0]['image'])
        self.assertEqual(found[0]['weight_g'], 0)
        conflicting = size + '\n|' + '|'.join(['', '', 'DEMO001'] + [''] * 23 + ['11', '20', '30', '0.125']) + '|'
        self.assertTrue(match_items([{'sku': 'DEMO001'}], image, conflicting)[1])

    def test_external_parse_does_not_require_or_extract_manifest_images_and_sizes(self):
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
