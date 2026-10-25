const fs = require('node:fs/promises');
const { Workbook, SpreadsheetFile } = require('@oai/artifact-tool');

function columnName(index) {
  let value = index + 1;
  let name = '';
  while (value > 0) {
    const remainder = (value - 1) % 26;
    name = String.fromCharCode(65 + remainder) + name;
    value = Math.floor((value - 1) / 26);
  }
  return name;
}

function safeSheetName(title, used) {
  const base = String(title || 'Sheet').replace(/[\\/?*\[\]:]/g, '_').slice(0, 31) || 'Sheet';
  let name = base;
  let index = 2;
  while (used.has(name)) name = `${base.slice(0, 28)}_${index++}`;
  used.add(name);
  return name;
}

async function main() {
  const [inputPath, outputPath] = process.argv.slice(2);
  if (!inputPath || !outputPath) throw new Error('需要输入 JSON 路径和输出 XLSX 路径');
  const snapshot = JSON.parse(await fs.readFile(inputPath, 'utf8'));
  const workbook = Workbook.create();
  const used = new Set();
  for (const source of snapshot) {
    const headers = source.headers || [];
    const rows = source.rows || [];
    const sheet = workbook.worksheets.add(safeSheetName(source.title, used));
    sheet.showGridLines = false;
    if (!headers.length) continue;
    sheet.getRange(`A1:${columnName(headers.length - 1)}1`).values = [headers];
    sheet.getRange(`A1:${columnName(headers.length - 1)}1`).format = { fill: '#173E70', font: { bold: true, color: '#FFFFFF' }, wrapText: true };
    if (rows.length) sheet.getRange(`A2:${columnName(headers.length - 1)}${rows.length + 1}`).values = rows;
    sheet.freezePanes.freezeRows(1);
    sheet.getRange(`A1:${columnName(headers.length - 1)}${Math.max(rows.length + 1, 1)}`).format.autofitColumns();
  }
  const output = await SpreadsheetFile.exportXlsx(workbook);
  await output.save(outputPath);
}

main().catch((error) => { process.stderr.write(String(error.stack || error)); process.exitCode = 1; });
