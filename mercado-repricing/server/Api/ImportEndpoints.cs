using Repricing.Import;
using Repricing.Execution;
namespace Repricing.Api;
public static class ImportEndpoints
{
    static byte[] TemplateBytes(){using var stream=typeof(ImportEndpoints).Assembly.GetManifestResourceStream("MeilTemplate.xlsx")??throw new AppException("模板资源缺失。",500);using var buffer=new MemoryStream();stream.CopyTo(buffer);return buffer.ToArray();}
    public static void MapImports(this WebApplication app)
    {
        app.MapGet("/api/weekend",()=>WeekendActivity.Current(DateTimeOffset.UtcNow));
        app.MapGet("/api/template/weekend",()=>{
            using var book=new ClosedXML.Excel.XLWorkbook();var sheet=book.AddWorksheet("周末活动报名");sheet.Cell(1,1).Value="产品";sheet.Cell(1,2).Value="SKU";sheet.Cell(1,3).Value="报名价格";sheet.Range(1,1,1,3).Style.Font.Bold=true;sheet.Columns(1,3).Width=24;using var buffer=new MemoryStream();book.SaveAs(buffer);return Results.File(buffer.ToArray(),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","周末活动报名模板.xlsx");
        });
        app.MapPost("/api/imports",async (HttpRequest request,WorkbookReader reader,ImportSessionStore sessions,PriceValidator validator,CancellationToken ct)=>
        {
            if(!request.HasFormContentType)throw new AppException("请上传表格文件。");
            var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file")??throw new AppException("请选择一个文件。");
            if(file.Length>WorkbookReader.MaxBytes)throw new AppException("文件不能超过 10 MB。");
            await using var stream=file.OpenReadStream();using var buffer=new MemoryStream();await stream.CopyToAsync(buffer,ct);var bytes=buffer.ToArray();buffer.Position=0;var sheets=await reader.ReadAsync(buffer,file.FileName,ct);var id=sessions.Add(file.FileName,sheets,bytes);
            return Results.Ok(new{importId=id,fileName=Path.GetFileName(file.FileName),sheets=sheets.Select(s=>new{name=s.Name,headers=s.Headers,rowCount=s.Rows.Length,suggestedMapping=validator.SuggestMapping(s.Headers)})});
        }).DisableAntiforgery();
        app.MapPost("/api/imports/{id:guid}/validate",async(Guid id,ValidateImportRequest request,ImportSessionStore sessions,IZiniaoReader ziniao,CancellationToken ct)=>sessions.Validate(id,request,request.AllStores?await ziniao.StoresAsync(ct):null));
        app.MapGet("/api/template",()=>Results.File(TemplateBytes(),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","Meil价格调整表-模板.xlsx"));
    }
}
