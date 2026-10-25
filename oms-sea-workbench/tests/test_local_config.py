import json
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path


class LocalConfigTests(unittest.TestCase):
    def test_missing_config_does_not_supply_a_real_recipient(self):
        from webapp.config import load_config, billing_config
        with tempfile.TemporaryDirectory() as directory:
            config = load_config(Path(directory) / 'missing.json')
            with self.assertRaises(ValueError):
                billing_config(config)

    def test_local_billing_is_validated_and_preserved(self):
        from webapp.config import load_config, billing_config
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / 'local.json'
            filename.write_text(json.dumps({'billing': {'shop': 'Demo shop', 'name': 'Demo', 'cnpj': '00.000.000/0001-00', 'company': 'Demo company'}}))
            billing = billing_config(load_config(filename))
            self.assertEqual(billing['cnpj'], '00.000.000/0001-00')
            filename.write_text(json.dumps({'billing': {'shop': 'Demo'}}))
            with self.assertRaises(ValueError):
                billing_config(load_config(filename))

    def test_fresh_checkout_can_upload_without_private_history(self):
        from webapp.server import Store
        with tempfile.TemporaryDirectory() as directory:
            with patch('webapp.server.ROOT', Path(directory)):
                store = Store(Path(directory) / 'state')
                data = '产品,SKU,葡语名称,总数量,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）,开票单价\n示例,DEMO001,Produto,2,10,20,30,0.5,12\n'
                job = store.upload('demo.csv', data.encode())
                self.assertEqual(job['status'], 'ready')
                self.assertEqual(job['manifest']['totals']['amount'], '24.00')
