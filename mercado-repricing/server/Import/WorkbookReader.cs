using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace Repricing.Import;
public class WorkbookReader
{
    public const int MaxBytes=10*1024*1024, MaxRows=5000;
    public async Task<ImportSheet[]> ReadAsync(Stream source,string fileName,CancellationToken ct)
    {
        if(source.CanSeek && source.Length>MaxBytes)throw new InvalidDataException("文件不能超过 10 MB。");
        var extension=Path.GetExtension(fileName).ToLowerInvariant();
        if(extension is not ".csv" and not ".xlsx")throw new InvalidDataException("请上传 .xlsx 或 UTF-8 CSV 文件。");
        using var buffer=new MemoryStream();
        var chunk=new byte[81920];int read;
        while((read=await source.ReadAsync(chunk,ct))>0){if(buffer.Length+read>MaxBytes)throw new InvalidDataException("文件不能超过 10 MB。");await buffer.WriteAsync(chunk.AsMemory(0,read),ct);}
        buffer.Position=0;
        try{return extension==".csv"?ReadCsv(buffer):ReadXlsx(buffer,ct);}
        catch(InvalidDataException){throw;}
        catch(Exception e) when(e is not OperationCanceledException){throw new InvalidDataException("无法读取表格，请检查文件格式、表头和 UTF-8 编码。",e);}
    }
    static string[] CheckHeaders(string[] headers)
    {
        if(headers.Length==0 || headers.Length>100 || headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=headers.Length)
            throw new InvalidDataException("首行必须是非空且不重复的表头，最多支持 100 列。");
        return headers;
    }
    static ImportSheet[] ReadCsv(Stream stream)
    {
        using var text=new StreamReader(stream,new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false,leaveOpen:true);
        using var csv=new CsvReader(text,new CsvConfiguration(CultureInfo.InvariantCulture){HasHeaderRecord=true,IgnoreBlankLines=true,DetectColumnCountChanges=true});
        if(!csv.Read())throw new InvalidDataException("表格为空。");
        csv.ReadHeader();var headers=CheckHeaders(csv.HeaderRecord!.Select(h=>h.Trim().TrimStart('\ufeff')).ToArray());
        var rows=new List<RawRow>();
        while(csv.Read())
        {
            var values=Enumerable.Range(0,headers.Length).Select(i=>csv.GetField(i)??"").ToArray();
            if(values.All(string.IsNullOrWhiteSpace))continue;
            if(rows.Count>=MaxRows)throw new InvalidDataException("每个工作表最多支持 5,000 条数据。");
            rows.Add(new(csv.Context.Parser!.RawRow,headers.Zip(values).ToDictionary(x=>x.First,x=>new RawCell(x.Second,false,false))));
        }
        return [new("CSV",headers,rows.ToArray())];
    }
    static ImportSheet[] ReadXlsx(Stream stream,CancellationToken ct)
    {
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Read,true))
        {
            if(zip.Entries.Count>2000 || zip.Entries.Sum(e=>e.Length)>64L*1024*1024)throw new InvalidDataException("工作簿解压大小超限，请只保留需要调价的数据。");
        }
        stream.Position=0;
        using var book=new XLWorkbook(stream);
        var result=new List<ImportSheet>();int total=0;
        foreach(var sheet in book.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            var range=sheet.RangeUsed(XLCellsUsedOptions.Contents);
            if(range is null)continue;
            var first=range.RangeAddress.FirstAddress.RowNumber;var last=range.RangeAddress.LastAddress.RowNumber;
            var columns=range.RangeAddress.LastAddress.ColumnNumber;
            if(columns>100 || last-first>MaxRows)throw new InvalidDataException("工作表最多支持 100 列、5,000 条数据，请删除多余内容。");
            var headers=CheckHeaders(Enumerable.Range(1,columns).Select(c=>sheet.Cell(first,c).GetString().Trim()).ToArray());
            var rows=new List<RawRow>();
            for(int r=first+1;r<=last;r++)
            {
                var cells=new Dictionary<string,RawCell>();
                for(int c=1;c<=columns;c++)
                {
                    var cell=sheet.Cell(r,c);bool formula=cell.HasFormula;
                    var value=formula?"="+cell.FormulaA1:cell.DataType==XLDataType.Number?cell.GetDouble().ToString("G15",CultureInfo.InvariantCulture):cell.GetString();
                    var display=!formula&&cell.DataType==XLDataType.Number?cell.GetFormattedString(CultureInfo.InvariantCulture):null;
                    cells[headers[c-1]]=new(value,formula,cell.DataType==XLDataType.Number,display);
                }
                if(cells.Values.All(c=>string.IsNullOrWhiteSpace(c.Text)))continue;
                if(++total>MaxRows)throw new InvalidDataException("整个文件最多支持 5,000 条数据。");
                rows.Add(new(r,cells));
            }
            result.Add(new(sheet.Name,headers,rows.ToArray()));
        }
        if(result.Count==0)throw new InvalidDataException("工作簿没有数据。");
        return result.ToArray();
    }
}
