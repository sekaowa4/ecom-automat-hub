using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Repricing.Api;
namespace Repricing.Tests;

[CollectionDefinition("Real label engine",DisableParallelization=true)]
public class RealLabelEngineCollection {}

[Collection("Real label engine")]
public class LabelTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"label-api-"+Guid.NewGuid());
    WebApplicationFactory<Program> Factory()=>new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.UseEnvironment("Testing").ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["DataDirectory"]=directory})));
    static async Task Authorize(HttpClient c){var session=await c.GetFromJsonAsync<JsonElement>("/api/session");c.DefaultRequestHeaders.Add("X-Local-Token",session.GetProperty("token").GetString());}
    [Theory]
    [InlineData("test.txt","%PDF-1.7\n","仅支持 PDF")]
    [InlineData("test.pdf","not a pdf","文件内容不是 PDF")]
    [InlineData("test.pdf","","文件为空")]
    public async Task RejectsInvalidInputsBeforeStartingEngine(string name,string content,string expected)
    {
        await using var factory=Factory();using var client=factory.CreateClient();await Authorize(client);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes(content)),"file",name);
        var r=await client.PostAsync("/api/labels/product/crop",form);
        Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Contains(expected,await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task CartonRequiresPdfUpload()
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);
        var r=await c.PostAsync("/api/labels/carton/crop",null);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Contains("请选择 PDF",await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task CartonToolReturnsOrderedLabelsAndNoPriceJobs()
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);using var form=new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Fixture("carton-two-pages.pdf"))),"file","箱唛.pdf");
        var r=await c.PostAsync("/api/labels/carton/crop",form);Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());
        Assert.Equal("4",r.Headers.GetValues("X-Label-Count").Single());Assert.StartsWith("%PDF-",Encoding.ASCII.GetString(await r.Content.ReadAsByteArrayAsync()));Assert.Equal("[]",await c.GetStringAsync("/api/jobs"));
    }
    [Fact] public async Task CropRequiresLocalToken()
    {
        await using var f=Factory();using var c=f.CreateClient();Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsync("/api/labels/product/crop",null)).StatusCode);
    }
    [Fact] public async Task RejectsMultipleFilesInOneRequest()
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);using var form=new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF-1.7\n"u8.ToArray()),"file","a.pdf");form.Add(new ByteArrayContent("%PDF-1.7\n"u8.ToArray()),"file","b.pdf");
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsync("/api/labels/product/crop",form)).StatusCode);
    }
    static string Fixture(string name)=>Path.GetFullPath("../../../../../tests/fixtures/labels/"+name,AppContext.BaseDirectory);
    [Fact] public async Task RealToolReturnsFourLabelsWithoutCreatingPriceJobs()
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);using var form=new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Fixture("four-labels.pdf"))),"file","四个标签.pdf");
        var response=await c.PostAsync("/api/labels/product/crop",form);
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        Assert.Equal("application/pdf",response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("4",response.Headers.GetValues("X-Label-Count").Single());
        Assert.StartsWith("%PDF-",Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
        Assert.Contains("已剪裁.pdf",Uri.UnescapeDataString(response.Content.Headers.ContentDisposition!.FileNameStar!));
        Assert.Equal("[]",await c.GetStringAsync("/api/jobs"));
    }
    [Theory]
    [InlineData("two-pages.pdf","仅支持单页 PDF")]
    [InlineData("no-border.pdf","未找到标签边框")]
    public async Task RealToolExplainsUnsupportedPdf(string filename,string expected)
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);using var form=new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Fixture(filename))),"file",filename);
        var r=await c.PostAsync("/api/labels/product/crop",form);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Contains(expected,await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task OversizedFileRejected()
    {
        await using var f=Factory();using var c=f.CreateClient();await Authorize(c);using var form=new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[10*1024*1024+1]),"file","too-large.pdf");
        var r=await c.PostAsync("/api/labels/product/crop",form);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Contains("10 MB",await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task CancellationReleasesWorkerForNextFile()
    {
        var extractions=Directory.GetDirectories(Path.GetTempPath(),"_MEI*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var f=Factory();using var scope=f.Services.CreateScope();var service=scope.ServiceProvider.GetRequiredService<Repricing.Labels.ProductLabelCropper>();
        var data=await File.ReadAllBytesAsync(Fixture("four-labels.pdf"));
        using var input=new MemoryStream(data);var file=new Microsoft.AspNetCore.Http.FormFile(input,0,data.Length,"file","cancel.pdf");
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var work=service.CropAsync(file,cancel.Token);
        for(var i=0;i<500&&!work.IsCompleted;i++)
        {
            var outer=Directory.GetDirectories(Path.GetTempPath(),"_MEI*").Any(p=>!extractions.Contains(p));
            var root=Path.Combine(Path.GetTempPath(),"MercadoWorkbench-labels");
            var inner=Directory.Exists(root)&&Directory.GetDirectories(root).Any(p=>Directory.Exists(p)&&Directory.GetDirectories(p,"_MEI*").Length>0);
            if(outer||inner)break;
            await Task.Delay(10);
        }
        if(!work.IsCompleted)cancel.Cancel();
        try{var completed=await work.WaitAsync(TimeSpan.FromSeconds(30));Assert.Equal(4,completed.Labels);}
        catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(),"_MEI*"),p=>!extractions.Contains(p));
        using var next=new MemoryStream(data);var result=await service.CropAsync(new Microsoft.AspNetCore.Http.FormFile(next,0,data.Length,"file","next.pdf"),CancellationToken.None);
        Assert.Equal(4,result.Labels);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
