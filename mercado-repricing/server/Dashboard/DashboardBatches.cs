using System.Text.Json;
namespace Repricing.Dashboard;

public record BatchMapping(string FileName,DateTimeOffset? ImportedAt,int Unmatched,Dictionary<string,string> Values);

// A private workbook is bundled into local colleague packages; it never enters the public source snapshot.
public sealed class DashboardBatches(IConfiguration config)
{
    string DataDirectory=>config["DataDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing");
    string MappingFile=>Path.Combine(DataDirectory,"dashboard-batches.json");
    string BuiltInFile=>Path.Combine(config["Dashboard:AppDirectory"]??AppContext.BaseDirectory,"批次对应表.xlsx");
    public BatchMapping Read()
    {
        if(File.Exists(BuiltInFile))
        {
            var rows=LocalWorkbook.Rows(BuiltInFile);
            var header=Array.FindIndex(rows,r=>r.Values.Any(v=>v.Equals("SKU",StringComparison.OrdinalIgnoreCase))&&r.Values.Any(v=>v is "批次" or "顺序上架"));
            if(header<0)throw new InvalidDataException("内置批次对应表缺少 SKU 和批次（或顺序上架）列。");
            var skuColumn=rows[header].First(c=>c.Value.Equals("SKU",StringComparison.OrdinalIgnoreCase)).Key;
            var batchColumn=rows[header].First(c=>c.Value is "批次" or "顺序上架").Key;
            Dictionary<string,string> values=[];var unmatched=new HashSet<string>();
            foreach(var row in rows.Skip(header+1))
            {
                var sku=row.GetValueOrDefault(skuColumn,"").Trim().ToUpperInvariant();if(sku==""||sku.StartsWith('#'))continue;
                var batch=row.GetValueOrDefault(batchColumn,"").Trim();if(batch==""||batch.StartsWith('#')){unmatched.Add(sku);continue;}
                if(values.TryGetValue(sku,out var previous)&&previous!=batch)throw new InvalidDataException($"内置批次对应表中 SKU {sku} 对应多个不同批次。");
                values[sku]=batch;
            }
            if(values.Count==0)throw new InvalidDataException("内置批次对应表没有有效的 SKU 批次映射。");
            unmatched.ExceptWith(values.Keys);
            return new(Path.GetFileName(BuiltInFile),null,unmatched.Count,values);
        }
        return File.Exists(MappingFile)?JsonSerializer.Deserialize<BatchMapping>(File.ReadAllText(MappingFile))??throw new InvalidDataException("本机旧版批次映射文件无法读取。"):new("",null,0,[]);
    }
}
