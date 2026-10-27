using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Repricing.Api;
using Repricing.Execution;
using Microsoft.Extensions.DependencyInjection;
namespace Repricing.Tests;
public class ApiTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"repricing-api-"+Guid.NewGuid());
    WebApplicationFactory<Program> Factory()=>new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.UseEnvironment("Testing").UseContentRoot(Path.GetFullPath("../../../../../server",AppContext.BaseDirectory)).ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["DataDirectory"]=directory,["SimulationDelayMs"]="0"})));
    static async Task Token(HttpClient client){var response=await client.GetFromJsonAsync<JsonElement>("/api/session");client.DefaultRequestHeaders.Add("X-Local-Token",response.GetProperty("token").GetString());}
    [Fact] public async Task BatchMappingUploadRequiresLocalTokenAndIsReadBackWithoutSalesRequests()
    {
        await using var f=Factory();using var c=f.CreateClient(new(){BaseAddress=new("http://localhost:5188")});
        using var book=new ClosedXML.Excel.XLWorkbook();var sheet=book.AddWorksheet("批次");sheet.Cell(1,1).Value="SKU";sheet.Cell(1,2).Value="批次";sheet.Cell(2,1).Value="TEST-ONLY";sheet.Cell(2,2).Value="第九批";using var bytes=new MemoryStream();book.SaveAs(bytes);
        MultipartFormDataContent Upload(){var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(bytes.ToArray()),"file","批次.xlsx");return form;}
        using(var missingToken=Upload())Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsync("/api/dashboard/batches",missingToken)).StatusCode);
        await Token(c);using(var form=Upload())(await c.PostAsync("/api/dashboard/batches",form)).EnsureSuccessStatusCode();
        var mapping=(await c.GetFromJsonAsync<Repricing.Dashboard.BatchMapping>("/api/dashboard/batches"))!;Assert.Equal("第九批",mapping.Values["TEST-ONLY"]);Assert.Equal("批次.xlsx",mapping.FileName);
    }
    [Fact] public async Task ImportValidateCreateRunExport()
    {
        await using var f=Factory();using var c=f.CreateClient(new(){BaseAddress=new("http://localhost:5188")});await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("店铺,站点,SKU,目标售价\n示例店铺,BR,00123,75")),"file","价格.csv");
        var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var data=await imported.Content.ReadFromJsonAsync<JsonElement>();var id=data.GetProperty("importId").GetGuid();
        var validated=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new("店铺","SKU","目标售价","站点"),null));validated.EnsureSuccessStatusCode();var v=(await validated.Content.ReadFromJsonAsync<ValidationResult>())!;
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.Simulation));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;
        (await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(int i=0;i<100;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.Equal(RowStatus.Simulated,job.Rows[0].Status);
        Assert.Contains("模拟",await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/export"));
        Assert.True(job.Summary.HasSourceFile);Assert.Contains("示例店铺",job.Summary.StoreNames!);
        Assert.Equal("店铺,站点,SKU,目标售价\n示例店铺,BR,00123,75",Encoding.UTF8.GetString(await c.GetByteArrayAsync($"/api/jobs/{job.Summary.Id}/source")));
        Assert.Contains("开始模拟执行",await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/log"));
    }
    static JsonSerializerOptions JsonOptions {get;}=new(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
    [Fact] public async Task WeekendTemplateAndJobUseServerDatesWithoutActionColumn()
    {
        await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<IZiniaoReader>(new ReadFixture())));using var c=f.CreateClient(new(){BaseAddress=new("http://localhost:5188")});await Token(c);
        var period=(await c.GetFromJsonAsync<WeekendPeriod>("/api/weekend"))!;Assert.Equal(WeekendActivity.Current(DateTimeOffset.UtcNow),period);
        using var template=new MemoryStream(await c.GetByteArrayAsync("/api/template/weekend"));using var workbook=new ClosedXML.Excel.XLWorkbook(template);var headers=workbook.Worksheet(1).Row(1).CellsUsed().Select(x=>x.GetString()).ToArray();Assert.Equal(new[]{"产品","SKU","报名价格"},headers);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("产品,SKU,报名价格\n测试产品,SKU-A,75")),"file","weekend.csv");var uploaded=(await (await c.PostAsync("/api/imports",form)).Content.ReadFromJsonAsync<JsonElement>());var import=uploaded.GetProperty("importId").GetGuid();
        var val=(await (await c.PostAsJsonAsync($"/api/imports/{import}/validate",new ValidateImportRequest("CSV",new("","SKU","报名价格"),"BR",true,"123"))).Content.ReadFromJsonAsync<ValidationResult>())!;
        Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(val.ValidationId,ExecutionMode.WeekendEnrollment))).StatusCode);
        var response=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(val.ValidationId,ExecutionMode.WeekendEnrollment,ConfirmPriceCuts:true,ExpectedWeekendStart:period.Start));response.EnsureSuccessStatusCode();var job=(await response.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;Assert.Equal(period,job.Summary.Weekend);Assert.Null(job.Rows[0].Source.Input!.Action);Assert.False(job.Summary.BasePrices);Assert.False(job.Summary.SubsidyExits);Assert.True(job.Summary.HasSourceFile);
    }
    [Fact] public async Task RejectsCrossOriginMutation(){await using var f=Factory();using var c=f.CreateClient();await Token(c);c.DefaultRequestHeaders.Add("Origin","https://evil.example");Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync("/api/jobs",new{})).StatusCode);}
    [Fact] public async Task RejectsHostRebinding(){await using var f=Factory();using var c=f.CreateClient();c.DefaultRequestHeaders.Host="evil.example";Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/session")).StatusCode);}
    [Fact] public async Task RejectsUnsupportedUpload(){await using var f=Factory();using var c=f.CreateClient();await Token(c);using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent([1,2]),"file","x.xlsm");Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsync("/api/imports",form)).StatusCode);}
    [Fact] public async Task LiveModeCannotStart(){await using var f=Factory();using var c=f.CreateClient();await Token(c);Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/jobs",new{validationId=Guid.NewGuid(),mode="Live"})).StatusCode);}
    [Fact] public async Task PriceCutRequiresExplicitScopeAndSellerConfirmation(){await using var f=Factory();using var c=f.CreateClient();await Token(c);var response=await c.PostAsJsonAsync("/api/jobs",new{validationId=Guid.NewGuid(),mode="PriceCut"});Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("确认",await response.Content.ReadAsStringAsync());}
    sealed class ReadFixture:IZiniaoReader
    {
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","示例店铺","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoProduct("456","00123","测试商品","R$ 100",[new("C-1","活动","激活","R$ 80",true)])});
    }
    sealed class MixedFixture:IZiniaoPriceCutClient
    {
        public List<string> Exits=[],Saves=[],BaseSaves=[];readonly Dictionary<string,decimal> prices=new(){["A"]=80,["B"]=80,["C"]=80};readonly Dictionary<string,decimal> bases=new(){["A"]=100,["B"]=100,["C"]=100};readonly HashSet<string> inactive=[];
        ZiniaoProduct Product(string sku)=>new(sku=="A"?"456":sku=="B"?"789":"999",sku,"商品",$"R$ {bases[sku]:0.00}".Replace('.',','),[new("C-1","活动","",$"R$ {prices[sku]:0.00}".Replace('.',','),!inactive.Contains(sku),!inactive.Contains(sku),false,inactive.Contains(sku))]);
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","示例店铺","MercadoLibre-巴西")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(new[]{Product(input.Sku)});
        public Task PrepareExitAsync(PriceCutPlan p,CancellationToken ct)=>Task.CompletedTask;
        public Task ConfirmExitAsync(PriceCutPlan p,CancellationToken ct){Exits.Add(p.Input.Sku);inactive.Add(p.Input.Sku);prices[p.Input.Sku]=95;return Task.CompletedTask;}
        public Task PrepareAsync(PriceCutPlan p,CancellationToken ct)=>Task.CompletedTask;
        public Task SubmitAsync(PriceCutPlan p,CancellationToken ct){Saves.Add(p.Input.Sku);inactive.Remove(p.Input.Sku);prices[p.Input.Sku]=p.Input.TargetPrice;return Task.CompletedTask;}
        public Task PrepareBaseAsync(PriceCutPlan p,CancellationToken ct)=>Task.CompletedTask;
        public Task SubmitBaseAsync(PriceCutPlan p,CancellationToken ct){BaseSaves.Add(p.Input.Sku);bases[p.Input.Sku]=p.Input.TargetPrice;return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan p,CancellationToken ct)=>Task.FromResult(Product(p.Input.Sku));
    }
    [Fact] public async Task MixedSheetRunsIncreaseAndCutAndSkipsThirdCaseThroughRealJobRunner()
    {
        var fixture=new MixedFixture();await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>{s.AddSingleton<IZiniaoPriceCutClient>(fixture);s.AddSingleton<IZiniaoReader>(fixture);}));using var c=f.CreateClient();await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("SKU,调价动作,折扣价格\nA,降价,90\nB,提价,75\nC,,96")),"file","混合调价.csv");var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new(Sku:"SKU",TargetPrice:"折扣价格",Action:"调价动作"),null,true,"123"));response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.PriceCut,true,true));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;Assert.True(job.Summary.MixedPrices);(await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(var i=0;i<120;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.Equal(new[]{RowStatus.Succeeded,RowStatus.Succeeded,RowStatus.Skipped},job.Rows.Select(x=>x.Status));Assert.Equal(new[]{"A"},fixture.Exits);Assert.Equal(new[]{"A","B"},fixture.Saves);Assert.Equal("join",job.Rows[0].PriceCut!.Stage);Assert.Contains("第三种",job.Rows[2].Message);var export=await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/export");Assert.Contains("实际调价",export);Assert.Contains("提价提交阶段",export);Assert.Contains("\"join\"",export);
    }
    [Fact] public async Task ThreeRoutesAreExplicitlyConfirmedPersistedExecutedAndExported()
    {
        var fixture=new MixedFixture();await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>{s.AddSingleton<IZiniaoPriceCutClient>(fixture);s.AddSingleton<IZiniaoReader>(fixture);}));using var c=f.CreateClient();await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("SKU,折扣价格\nA,90\nB,75\nC,96")),"file","三种调价.csv");var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new(Sku:"SKU",TargetPrice:"折扣价格"),null,true,"123"));response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;
        var bad=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.PriceCut,true,false,true));Assert.Equal(HttpStatusCode.Conflict,bad.StatusCode);
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.PriceCut,true,true,true));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;Assert.True(job.Summary.BasePrices);Assert.True(job.Summary.IncomePriority);(await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(var i=0;i<120;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.All(job.Rows,r=>Assert.Equal(RowStatus.Succeeded,r.Status));Assert.Equal(new[]{"A","C"},fixture.Exits);Assert.Equal(new[]{"A","B"},fixture.Saves);Assert.Equal(new[]{"C"},fixture.BaseSaves);Assert.Equal("base-save",job.Rows[2].PriceCut!.Stage);Assert.Equal(96,job.Rows[2].Observation!.CurrentPrice);
        var export=await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/export");Assert.Contains("原价提交阶段",export);Assert.Contains("改前原价",export);Assert.Contains("\"base-save\"",export);
    }
    sealed class CutFixture:IZiniaoPriceCutClient
    {
        public int Saves;public Action? BeforeSave;
        ZiniaoProduct Product()=>new("456","00123","测试商品","R$ 100,00",[new("C-1","活动","激活",Saves==0?"R$ 80,00":"R$ 75,00",true,true,true)]);
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","示例店铺","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(new[]{Product()});
        public Task PrepareAsync(PriceCutPlan plan,CancellationToken ct)=>Task.CompletedTask;
        public Task SubmitAsync(PriceCutPlan plan,CancellationToken ct){BeforeSave?.Invoke();Saves++;return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct)=>Task.FromResult(Product());
    }
    sealed class RetryCutFixture:IZiniaoPriceCutClient
    {
        public List<string> Saves=[];readonly Dictionary<string,decimal> prices=new(){["A"]=80,["B"]=80};
        ZiniaoProduct Product(string sku)=>new(sku=="A"?"456":"789",sku,"商品","R$ 100",[new("C-1","活动","激活",$"R$ {prices[sku]:0},00",true,true,true)]);
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("123","示例店铺","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>Task.FromResult(new[]{Product(input.Sku)});
        public Task PrepareAsync(PriceCutPlan plan,CancellationToken ct)=>Task.CompletedTask;
        public Task SubmitAsync(PriceCutPlan plan,CancellationToken ct){Saves.Add(plan.Input.Sku);prices[plan.Input.Sku]=plan.Input.TargetPrice;if(plan.Input.Sku=="A")throw new AppException("保存响应断开");return Task.CompletedTask;}
        public Task<ZiniaoProduct> VerifyAsync(PriceCutPlan plan,CancellationToken ct)=>Task.FromResult(Product(plan.Input.Sku));
    }
    [Fact] public async Task PriceCutFinishesOtherSkusThenReconcilesUnknownSaveInNextRound()
    {
        var fixture=new RetryCutFixture();await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>{s.AddSingleton<IZiniaoPriceCutClient>(fixture);s.AddSingleton<IZiniaoReader>(fixture);}));using var c=f.CreateClient();await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("店铺,站点,SKU,调价动作,目标售价\n示例店铺,BR,A,降价,75\n示例店铺,BR,B,降价,75")),"file","重试测试.csv");
        var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new("店铺","SKU","目标售价","站点",Action:"调价动作"),null));response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.PriceCut,true));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;(await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(int i=0;i<100;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.Equal(2,job.Summary.Round);Assert.All(job.Rows,r=>Assert.Equal(RowStatus.Succeeded,r.Status));Assert.Equal(new[]{"A","B"},fixture.Saves);Assert.Equal(2,job.Rows[0].Attempts);Assert.Equal(1,job.Rows[1].Attempts);Assert.Equal("cut-reconciled",job.Rows[0].Checkpoint);
    }
    [Fact] public async Task ConfirmedPriceCutRunsThroughDurablePlanAndVerifiedExportUsingOnlyFixture()
    {
        var fixture=new CutFixture();await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>{s.AddSingleton<IZiniaoPriceCutClient>(fixture);s.AddSingleton<IZiniaoReader>(fixture);}));
        using var c=f.CreateClient();await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("店铺,站点,SKU,调价动作,目标售价\n示例店铺,BR,00123,降价,75")),"file","本地模拟页面.csv");
        var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new("店铺","SKU","目标售价","站点",Action:"调价动作"),null));response.EnsureSuccessStatusCode();var v=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.PriceCut,true));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;
        fixture.BeforeSave=()=>Assert.NotNull(f.Services.GetRequiredService<Storage.JobStore>().Get(job.Summary.Id).Rows[0].PriceCut);
        (await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(int i=0;i<100;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.Equal(RowStatus.Succeeded,job.Rows[0].Status);Assert.Equal(1,fixture.Saves);Assert.Equal(80,job.Rows[0].PriceCut!.Before);Assert.Equal(75,job.Rows[0].Observation!.CurrentPrice);
        var exported=await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/export");Assert.Contains("实际降价",exported);Assert.Contains("提交活动ID",exported);Assert.Contains("cut-verified",exported);
    }
    [Fact] public async Task FourColumnUploadNeedsNoStoreOrSiteAndTemplateIsHeaderOnly()
    {
        await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<IZiniaoReader>(new ReadFixture())));
        using var c=f.CreateClient();await Token(c);
        var template=await c.GetAsync("/api/template");template.EnsureSuccessStatusCode();Assert.Contains("spreadsheetml",template.Content.Headers.ContentType!.MediaType!);
        using(var stream=await template.Content.ReadAsStreamAsync())using(var book=new ClosedXML.Excel.XLWorkbook(stream)){Assert.Equal(new[]{"产品","sku","调价动作","折扣价格"},book.Worksheet(1).Row(1).CellsUsed().Select(cell=>cell.GetString()));Assert.Equal(1,book.Worksheet(1).LastRowUsed()!.RowNumber());}
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("产品,sku,调价动作,折扣价格\n产品A,00123,降价,75")),"file","四列表.csv");
        var uploaded=await c.PostAsync("/api/imports",form);uploaded.EnsureSuccessStatusCode();var data=await uploaded.Content.ReadFromJsonAsync<JsonElement>();var id=data.GetProperty("importId").GetGuid();
        var mapping=data.GetProperty("sheets")[0].GetProperty("suggestedMapping").Deserialize<ColumnMapping>(JsonOptions)!;
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",mapping,null,true));response.EnsureSuccessStatusCode();
        var result=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;Assert.Single(result.Stores!);Assert.Equal("降价",result.Rows[0].Input!.Action);Assert.Equal("MLB",result.Rows[0].Input!.Site);
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(result.ValidationId,ExecutionMode.ReadOnly));created.EnsureSuccessStatusCode();
    }
    sealed class StoreScopeFixture:IZiniaoReader
    {
        public Task<ZiniaoStore[]> StoresAsync(CancellationToken ct)=>Task.FromResult(new[]{new ZiniaoStore("1","店铺一","MercadoLibre-巴西-本土"),new ZiniaoStore("2","店铺二子账号","MercadoLibre-巴西-本土")});
        public Task<ZiniaoProduct[]> ReadAsync(ZiniaoStore store,PriceInput input,CancellationToken ct)=>throw new InvalidOperationException("Scope preview must not query products");
    }
    [Fact] public async Task DropdownStoreIdScopesPreviewAndSavedJobWithoutReadingProducts()
    {
        await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<IZiniaoReader>(new StoreScopeFixture())));using var c=f.CreateClient();await Token(c);
        var stores=(await c.GetFromJsonAsync<ZiniaoStore[]>("/api/ziniao/stores"))!;Assert.Equal(2,stores.Length);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("产品,sku,调价动作,折扣价格\n产品A,SKU-A,降价,75")),"file","四列.csv");
        var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var request=new ValidateImportRequest("CSV",new(Sku:"sku",TargetPrice:"折扣价格",Action:"调价动作"),null,true,"2");
        var response=await c.PostAsJsonAsync($"/api/imports/{id}/validate",request);response.EnsureSuccessStatusCode();var result=(await response.Content.ReadFromJsonAsync<ValidationResult>())!;
        Assert.Equal("2",Assert.Single(result.Rows).Input!.Store);Assert.Equal("店铺二子账号",Assert.Single(result.Stores!).Name);
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(result.ValidationId,ExecutionMode.ReadOnly));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;
        Assert.Equal("2",Assert.Single(job.Rows).Source.Input!.Store);
        var bad=await c.PostAsJsonAsync($"/api/imports/{id}/validate",request with{SelectedStoreId="deleted-store"});Assert.Equal(HttpStatusCode.Conflict,bad.StatusCode);
    }
    [Fact] public async Task ReadOnlyUploadThroughExportNeverReportsActualPriceChange()
    {
        await using var f=Factory().WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<IZiniaoReader>(new ReadFixture())));
        using var c=f.CreateClient();await Token(c);
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("店铺,站点,SKU,目标售价,商品ID\n示例店铺,BR,00123,75,456")),"file","只读核对.csv");
        var imported=await c.PostAsync("/api/imports",form);imported.EnsureSuccessStatusCode();var id=(await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importId").GetGuid();
        var validation=await c.PostAsJsonAsync($"/api/imports/{id}/validate",new ValidateImportRequest("CSV",new("店铺","SKU","目标售价","站点",ItemId:"商品ID"),null));validation.EnsureSuccessStatusCode();
        var v=(await validation.Content.ReadFromJsonAsync<ValidationResult>())!;
        var created=await c.PostAsJsonAsync("/api/jobs",new CreateJobRequest(v.ValidationId,ExecutionMode.ReadOnly));created.EnsureSuccessStatusCode();var job=(await created.Content.ReadFromJsonAsync<JobDetail>(JsonOptions))!;
        (await c.PostAsJsonAsync($"/api/jobs/{job.Summary.Id}/start",new{})).EnsureSuccessStatusCode();
        for(int i=0;i<100;i++){job=(await c.GetFromJsonAsync<JobDetail>($"/api/jobs/{job.Summary.Id}",JsonOptions))!;if(job.Summary.Status==JobStatus.Completed)break;await Task.Delay(30);}
        Assert.Equal(JobStatus.Completed,job.Summary.Status);Assert.Equal(RowStatus.Inspected,job.Rows[0].Status);Assert.Equal(80m,job.Rows[0].Observation!.CurrentPrice);Assert.Equal(-5m,job.Rows[0].Observation!.Difference);
        var export=await c.GetStringAsync($"/api/jobs/{job.Summary.Id}/export");Assert.Contains("只读核对",export);Assert.DoesNotContain("实际成功",export);
        var env=await c.GetFromJsonAsync<JsonElement>("/api/environment");Assert.False(env.GetProperty("liveEnabled").GetBoolean());
    }
    [Theory] [InlineData("=1+1")] [InlineData("+SUM(A1)")] [InlineData("-1+2")] [InlineData("@SUM(A1)")] [InlineData("\t=1")] [InlineData(" =1+1")]
    public void ExportNeutralizesFormulaText(string text)=>Assert.StartsWith("'",CsvExport.SafeText(text));
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
