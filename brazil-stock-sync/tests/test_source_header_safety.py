import tempfile
import unittest
import zipfile
from pathlib import Path

from inventory_pipeline import load_sources, read_xlsx_rows


class SourceHeaderSafetyTests(unittest.TestCase):
    def workbook(self, directory, cells):
        path = Path(directory) / 'source.xlsx'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('xl/workbook.xml', '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="r1"/></sheets></workbook>')
            archive.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Target="worksheets/sheet1.xml"/></Relationships>')
            archive.writestr('xl/worksheets/sheet1.xml', '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>' + cells + '</sheetData></worksheet>')
        return path

    def test_source_rejects_populated_column_without_header(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.workbook(directory, '<row r="1"><c r="A1" t="inlineStr"><is><t>SKU</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>NW1</t></is></c><c r="V2"><v>283</v></c></row>')
            with self.assertRaisesRegex(ValueError, '表头.*V2'):
                read_xlsx_rows(path, strict_headers=True)
            with self.assertRaisesRegex(ValueError, '表头.*V2'):
                load_sources({'inventory': path})

    def test_source_rejects_duplicate_headers(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.workbook(directory, '<row r="1"><c r="A1" t="inlineStr"><is><t>SKU</t></is></c><c r="B1" t="inlineStr"><is><t>SKU</t></is></c></row>')
            with self.assertRaisesRegex(ValueError, '重复表头'):
                read_xlsx_rows(path, strict_headers=True)

    def test_blank_trailing_cells_are_allowed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.workbook(directory, '<row r="1"><c r="A1" t="inlineStr"><is><t>SKU</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>NW1</t></is></c><c r="V2"/></row>')
            rows, _ = read_xlsx_rows(path, strict_headers=True)
            self.assertEqual(rows, [{'SKU': 'NW1'}])
