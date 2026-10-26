"""Mercado Livre A4 landscape volume labels; keep original PDF vectors and sample crop boxes."""
import copy
import logging
import re
import sys
from pathlib import Path
import pdfplumber
from pypdf import PdfReader, PdfWriter
from pypdf.generic import RectangleObject
logging.getLogger('pypdf').setLevel(logging.ERROR)
W,H=841.8898,595.27563
CUTS=(22.6801,289.386,555.375,819.927)
BOTTOM,TOP=133.877,571.847

def crop(source:Path,destination:Path)->int:
 reader=PdfReader(source)
 if reader.is_encrypted:raise ValueError('加密 PDF 暂不支持，请先导出未加密文件。')
 if not 1<=len(reader.pages)<=200:raise ValueError('箱唛 PDF 需要包含1至200页。')
 writer=PdfWriter();seen=set();count=0
 with pdfplumber.open(source) as doc:
  for index,(page,layout) in enumerate(zip(reader.pages,doc.pages)):
   if page.rotation or abs(float(page.mediabox.width)-W)>1 or abs(float(page.mediabox.height)-H)>1 or any(abs(float(a)-float(b))>.5 for a,b in zip(page.mediabox,page.cropbox)):
    raise ValueError(f'第{index+1}页不是原始A4横向三列箱唛，请勿上传已剪裁文件。')
   sx,sy=layout.width/W,layout.height/H
   if abs(float(page.mediabox.left))>.1 or abs(float(page.mediabox.bottom))>.1:raise ValueError('箱唛页面坐标不受支持。')
   all_ids=set(re.findall(r'Envio:\s*(\d+/\d+)',layout.extract_text() or ''))
   found=set();page_count=0
   for column in range(3):
    x=(30+265.1181*column)*sx
    borders=[r for r in layout.rects if abs(r['x0']-x)<1*sx and abs(r['y0']-143.685*sy)<1*sy and abs(r['width']-252.8504*sx)<1*sx and abs(r['height']-421.5118*sy)<1*sy]
    bounds=(CUTS[column]*sx,BOTTOM*sy,CUTS[column+1]*sx,TOP*sy)
    area=layout.crop((bounds[0],layout.height-bounds[3],bounds[2],layout.height-bounds[1]))
    text=area.extract_text() or '';ids=set(re.findall(r'Envio:\s*(\d+/\d+)',text))
    if not borders:
     if ids:raise ValueError(f'第{index+1}页第{column+1}列未识别到完整箱唛边框。')
     continue
    if len(ids)!=1 or 'VOLUMES' not in text:raise ValueError(f'第{index+1}页第{column+1}列箱号或底部信息不完整。')
    ident=next(iter(ids))
    if ident in seen:raise ValueError('存在重复箱号，请检查原文件。')
    found.add(ident);seen.add(ident)
    output=copy.copy(page);output.cropbox=RectangleObject(bounds)
    writer.add_page(output);count+=1;page_count+=1
   if not page_count or found!=all_ids:raise ValueError(f'第{index+1}页有未完整识别的箱唛，未生成文件。')
 if count>600:raise ValueError('一次最多处理600张箱唛。')
 destination.parent.mkdir(parents=True,exist_ok=True)
 with destination.open('wb') as f:writer.write(f)
 return count

def main():
 if len(sys.argv)!=3:raise ValueError('需要输入和输出文件夹。')
 source=Path(sys.argv[1])/'input.pdf';out=Path(sys.argv[2])/'input.pdf'
 labels=crop(source,out)
 print(f'input.pdf: {labels} pages',flush=True)
if __name__=='__main__':
 try:main()
 except Exception as e:
  print(str(e),file=sys.stderr);sys.exit(1)
