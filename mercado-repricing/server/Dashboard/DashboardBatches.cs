using System.Text.Json;
namespace Repricing.Dashboard;

public record BatchMapping(string FileName,DateTimeOffset? ImportedAt,int Unmatched,Dictionary<string,string> Values);

// Operational mappings stay in the local data directory, outside the public source snapshot.
public sealed class DashboardBatches(IConfiguration config)
{
    string DataDirectory=>config["DataDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing");
    string MappingFile=>Path.Combine(DataDirectory,"dashboard-batches.json");
    readonly SemaphoreSlim gate=new(1,1);
    public BatchMapping Read()=>File.Exists(MappingFile)?JsonSerializer.Deserialize<BatchMapping>(File.ReadAllText(MappingFile))??throw new InvalidDataException("批次映射文件无法读取，请重新导入。"):new("",null,0,[]);
    public async Task<BatchMapping> ImportAsync(Stream stream,string fileName,CancellationToken ct)
    {
        if(!Path.GetExtension(fileName).Equals(".xlsx",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("批次对应表请使用 .xlsx 文件。");
        await gate.WaitAsync(ct);
        var temporary=Path.Combine(DataDirectory,"batch-import-"+Guid.NewGuid()+".xlsx");var pending=temporary+".json";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            await using(var target=File.Create(temporary))await stream.CopyToAsync(target,ct);
            var rows=LocalWorkbook.Rows(temporary);
            var header=Array.FindIndex(rows,r=>r.Values.Any(v=>v.Equals("SKU",StringComparison.OrdinalIgnoreCase))&&r.Values.Any(v=>v is "批次" or "顺序上架"));
            if(header<0)throw new InvalidDataException("批次对应表缺少 SKU 和批次（或顺序上架）列。");
            var skuColumn=rows[header].First(c=>c.Value.Equals("SKU",StringComparison.OrdinalIgnoreCase)).Key;
            var batchColumn=rows[header].First(c=>c.Value is "批次" or "顺序上架").Key;
            Dictionary<string,string> values=[];var unmatched=new HashSet<string>();
            foreach(var row in rows.Skip(header+1))
            {
                var sku=row.GetValueOrDefault(skuColumn,"").Trim().ToUpperInvariant();if(sku==""||sku.StartsWith('#'))continue;
                var batch=row.GetValueOrDefault(batchColumn,"").Trim();if(batch==""||batch.StartsWith('#')){unmatched.Add(sku);continue;}
                if(values.TryGetValue(sku,out var previous)&&previous!=batch)throw new InvalidDataException($"SKU {sku} 对应多个不同批次，未替换原映射。");
                values[sku]=batch;
            }
            if(values.Count==0)throw new InvalidDataException("批次对应表没有有效的 SKU 批次映射。");
            unmatched.ExceptWith(values.Keys);
            var result=new BatchMapping(Path.GetFileName(fileName),DateTimeOffset.UtcNow,unmatched.Count,values);
            await File.WriteAllTextAsync(pending,JsonSerializer.Serialize(result),ct);File.Move(pending,MappingFile,true);return result;
        }
        finally{File.Delete(temporary);File.Delete(pending);gate.Release();}
    }
}
