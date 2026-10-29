using Repricing.Dashboard;
namespace Repricing.Api;
public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/api/dashboard/batches",(DashboardBatches batches)=>batches.Read());
        app.MapPost("/api/dashboard/batches",async(HttpRequest request,DashboardBatches batches,CancellationToken ct)=>
        {
            if(!request.HasFormContentType)throw new AppException("请上传批次对应表。");
            var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file")??throw new AppException("请选择批次对应表。");
            if(file.Length==0||file.Length>10*1024*1024)throw new AppException("批次对应表应为非空且不超过 10 MB 的 .xlsx 文件。");
            await using var stream=file.OpenReadStream();return await batches.ImportAsync(stream,file.FileName,ct);
        }).DisableAntiforgery();
        app.MapGet("/api/dashboard/source",(UpSellerDashboard dashboard)=>new{directory=dashboard.DirectoryPath});
        app.MapPost("/api/dashboard/source",(UpSellerDashboard.SourceSettings source,UpSellerDashboard dashboard)=>{dashboard.SetDirectory(source.Directory);return Results.Ok(new{saved=true});});
        app.MapGet("/api/dashboard/stores",(UpSellerDashboard dashboard)=>dashboard.Shops());
        app.MapGet("/api/dashboard/data",async(string shop,bool? refresh,DateOnly? reportDate,UpSellerDashboard dashboard,CancellationToken ct)=>await dashboard.ReadAsync(shop,refresh??false,reportDate,ct));
    }
}
