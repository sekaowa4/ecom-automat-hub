using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
namespace Repricing.Dashboard;

// Read saved formula values only; never recalculate or modify operational workbooks.
public static class LocalWorkbook
{
    static readonly XNamespace Ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static Dictionary<string,string>[] Rows(string path,string? sheetName=null)
    {
        using var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        XDocument Read(string name){using var stream=(zip.GetEntry(name)??throw new InvalidDataException("工作簿结构不完整。")).Open();return XDocument.Load(stream);}
        var strings=zip.GetEntry("xl/sharedStrings.xml")!=null?Read("xl/sharedStrings.xml").Descendants(Ns+"si").Select(s=>string.Concat(s.Descendants(Ns+"t").Select(t=>t.Value))).ToArray():[];
        var sheets=Read("xl/workbook.xml").Descendants(Ns+"sheet");var sheet=sheetName==null?sheets.First():sheets.SingleOrDefault(s=>(string?)s.Attribute("name")==sheetName)??throw new InvalidDataException("利润表缺少 Meli 工作表。");
        var id=(string?)sheet.Attribute(XName.Get("id","http://schemas.openxmlformats.org/officeDocument/2006/relationships"));
        var target=(string?)Read("xl/_rels/workbook.xml.rels").Root!.Elements().Single(r=>(string?)r.Attribute("Id")==id).Attribute("Target")??throw new InvalidDataException();
        var name=target.StartsWith('/')?target.TrimStart('/') : "xl/"+target;
        return Read(name).Descendants(Ns+"row").Select(r=>r.Elements(Ns+"c").ToDictionary(c=>new string(((string?)c.Attribute("r")??"").TakeWhile(char.IsLetter).ToArray()),c=>{
            var value=c.Element(Ns+"v")?.Value??"";var type=(string?)c.Attribute("t");
            if(type=="s"&&value!="")return int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var index)&&index>=0&&index<strings.Length?strings[index].Trim():throw new InvalidDataException("工作簿字符串索引损坏。");
            return (type=="inlineStr"?string.Concat(c.Descendants(Ns+"t").Select(t=>t.Value)):value).Trim();
        })).ToArray();
    }
    public static Dictionary<string,string> Categories(string path)
    {
        var rows=Rows(path);var header=Array.FindIndex(rows,r=>r.ContainsValue("SKU")&&r.ContainsValue("产品定义"));
        if(header<0)throw new InvalidDataException("分类表缺少 SKU、产品定义列。");
        var sku=rows[header].First(v=>v.Value=="SKU").Key;var category=rows[header].First(v=>v.Value=="产品定义").Key;
        return rows.Skip(header+1).Where(r=>r.GetValueOrDefault(sku,"")!="").GroupBy(r=>r[sku].ToUpperInvariant()).ToDictionary(g=>g.Key,g=>{
            var values=g.Select(r=>r.GetValueOrDefault(category,"").ToUpperInvariant()).Where(v=>v!="").Distinct().ToArray();return values.Length==1&&values[0] is "A" or "B" or "KA" or "P"?values[0]:"未分类";
        });
    }
    public static Dictionary<string,decimal?> Margins(string path)=>Rows(path,"利润表-Meli").Where(r=>r.GetValueOrDefault("C","") is not "" and not "SKU").GroupBy(r=>r["C"].ToUpperInvariant()).ToDictionary(g=>g.Key,g=>{
        var values=g.Select(r=>decimal.TryParse(r.GetValueOrDefault("AB"),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?(decimal?)n:null).Distinct().ToArray();return values.Length==1?values[0]:null;
    });
}
