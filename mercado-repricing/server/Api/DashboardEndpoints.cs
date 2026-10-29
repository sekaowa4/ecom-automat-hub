using Repricing.Dashboard;
namespace Repricing.Api;
public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/api/dashboard/batches",(DashboardBatches batches)=>batches.Read());
        app.MapGet("/api/dashboard/source",(UpSellerDashboard dashboard)=>new{directory=dashboard.DirectoryPath});
        app.MapPost("/api/dashboard/source",(UpSellerDashboard.SourceSettings source,UpSellerDashboard dashboard)=>{dashboard.SetDirectory(source.Directory);return Results.Ok(new{saved=true});});
        app.MapGet("/api/dashboard/stores",(UpSellerDashboard dashboard)=>dashboard.Shops());
        app.MapGet("/api/dashboard/data",async(string shop,bool? refresh,DateOnly? reportDate,UpSellerDashboard dashboard,CancellationToken ct)=>await dashboard.ReadAsync(shop,refresh??false,reportDate,ct));
    }
}
