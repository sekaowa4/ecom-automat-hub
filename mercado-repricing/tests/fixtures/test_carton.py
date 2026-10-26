from pathlib import Path
import unittest, tempfile
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'tools/carton'))
from carton_crop import crop
from pypdf import PdfReader
class CartonTests(unittest.TestCase):
 def test_order_and_crop(self):
  with tempfile.TemporaryDirectory() as d:
   out=Path(d)/'result.pdf';self.assertEqual(crop(Path('tests/fixtures/labels/carton-two-pages.pdf'),out),4)
   p=PdfReader(out).pages;self.assertEqual(len(p),4)
   self.assertAlmostEqual(float(p[0].cropbox.left),22.6801,places=3);self.assertAlmostEqual(float(p[1].cropbox.left),289.386,places=3)
   self.assertAlmostEqual(float(p[3].cropbox.left),22.6801,places=3)
 def test_rejects_wrong_layout(self):
  with tempfile.TemporaryDirectory() as d:
   with self.assertRaises(ValueError):crop(Path('tests/fixtures/labels/no-border.pdf'),Path(d)/'out.pdf')
if __name__=='__main__':unittest.main()
