using Repricing.Labels;
namespace Repricing.Api;

public static class LabelEndpoints
{
    public static void MapLabels(this WebApplication app)
    {
        foreach(var kind in new[]{"product","carton"})
        app.MapPost($"/api/labels/{kind}/crop",async(HttpRequest request,HttpResponse response,ProductLabelCropper cropper,CancellationToken ct)=>
        {
            if(!request.HasFormContentType)throw new AppException("请选择 PDF 文件。",400);
            var form=await request.ReadFormAsync(ct);
            if(form.Files.Count!=1||form.Files[0].Name!="file")throw new AppException("每次请求仅处理一个 PDF 文件。",400);
            var result=await cropper.CropAsync(form.Files[0],ct,carton:kind=="carton");
            response.Headers["X-Label-Count"]=result.Labels.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.File(result.Pdf,"application/pdf",result.FileName);
        });
    }
}
