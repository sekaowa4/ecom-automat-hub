using System.Text.Json;
namespace Repricing.Import;
public class ImportSessionStore(PriceValidator validator)
{
    readonly object gate=new();
    readonly Dictionary<Guid,ImportSession> sessions=[];
    readonly Dictionary<Guid,(Guid ImportId,ValidationSnapshot Snapshot)> validations=[];
    void Cleanup(){foreach(var id in sessions.Values.Where(s=>DateTimeOffset.UtcNow-s.CreatedAt>TimeSpan.FromMinutes(30)).Select(s=>s.Id).ToArray()){sessions.Remove(id);foreach(var v in validations.Where(p=>p.Value.ImportId==id).Select(p=>p.Key).ToArray())validations.Remove(v);}}
    public Guid Add(string fileName,ImportSheet[] sheets,byte[]? sourceFile=null)
    {
        lock(gate){Cleanup();if(sessions.Count>=10)throw new AppException("导入会话已满，请稍后再试或重启程序。",429);if(JsonSerializer.SerializeToUtf8Bytes(sheets).Length>5*1024*1024)throw new AppException("表格文本过多，请拆分为较小文件。");var id=Guid.NewGuid();sessions[id]=new(id,Path.GetFileName(fileName),sheets,DateTimeOffset.UtcNow,sourceFile);return id;}
    }
    public ImportSession Get(Guid id){lock(gate){Cleanup();return sessions.GetValueOrDefault(id)??throw new AppException("导入已过期，请重新上传。",404);}}
    public ValidationResult Validate(Guid id,ValidateImportRequest request,ZiniaoStore[]? stores=null)
    {
        lock(gate){var session=Get(id);var sheet=session.Sheets.FirstOrDefault(s=>s.Name==request.Sheet)??throw new AppException("工作表不存在。");ValidatedRow[] rows;ZiniaoStore[]? scope=null;string[]? warnings=null;
            if(request.AllStores)
            {
                if(!string.IsNullOrEmpty(request.Mapping.Store)||!string.IsNullOrEmpty(request.Mapping.Site))throw new AppException("跨店模板只需 SKU、目标售价（调价动作可选），不映射店铺或站点。");
                var mercado=(stores??[]).Where(s=>s.Platform.Contains("MercadoLibre",StringComparison.OrdinalIgnoreCase)).ToArray();
                scope=mercado.Where(s=>s.Platform.Contains("巴西")).GroupBy(s=>s.Id).Select(g=>g.First()).ToArray();
                warnings=mercado.Where(s=>!s.Platform.Contains("巴西")).Select(s=>$"{s.Name}：当前不支持此站点，未纳入任务").ToArray();
                if(request.SelectedStoreId!=null)
                {
                    scope=scope.Where(s=>s.Id==request.SelectedStoreId).ToArray();
                    if(scope.Length!=1)throw new AppException("所选店铺已不可用或不支持当前站点，请刷新店铺列表后重新选择。",409);
                    warnings=[];
                }
                if(scope.Length==0)throw new AppException("未读取到支持的美客多巴西店铺，请检查紫鸟连接。",409);
                if((long)scope.Length*sheet.Rows.Length>20000)throw new AppException("跨店展开超过20,000项，请拆分表格后上传。");
                rows=scope.SelectMany(store=>validator.Validate(sheet,request.Mapping,"BR",store)).ToArray();
            }
            else {if(request.SelectedStoreId!=null)throw new AppException("带店铺列的旧表格使用表内店铺；请使用四列模板后在工作台选择店铺。");rows=validator.Validate(sheet,request.Mapping,request.DefaultSite);}foreach(var key in validations.Where(p=>p.Value.ImportId==id).Select(p=>p.Key).ToArray())validations.Remove(key);var vid=Guid.NewGuid();validations[vid]=(id,new(vid,session.FileName,rows,session.SourceFile));return new(vid,id,rows,scope,warnings);}
    }
    public ValidationSnapshot GetValidation(Guid id){lock(gate){Cleanup();return validations.TryGetValue(id,out var v)?v.Snapshot:throw new AppException("检查结果已失效，请重新检查表格。",409);}}
}
