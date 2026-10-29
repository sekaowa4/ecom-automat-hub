using System.Globalization;
using System.Text.Json;
namespace Repricing.Dashboard;

public sealed class UpSellerDashboard(IConfiguration config,HttpClient? client=null):IDisposable
{
    readonly HttpClient http=client??new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(50)};
    readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<string,DashboardData> cache=[];
    string SettingsFile=>Path.Combine(config["DataDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MercadoRepricing"),"dashboard-source.json");
    public string DirectoryPath
    {
        get
        {
            if(File.Exists(SettingsFile))return JsonSerializer.Deserialize<SourceSettings>(File.ReadAllText(SettingsFile))?.Directory??"";
            return config["Dashboard:DailyDirectory"]??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"每日消息推送");
        }
    }
    public record SourceSettings(string Directory);
    public void SetDirectory(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\"))throw new AppException("请选择本机每日推送配置文件夹。",400);
        if(!File.Exists(Path.Combine(path,"config.json")))throw new AppException("该文件夹未找到每日推送 config.json。",400);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);File.WriteAllText(SettingsFile,JsonSerializer.Serialize(new SourceSettings(Path.GetFullPath(path))));
        // Cache keys include the source directory; changing sources cannot reuse another source's data.
    }
    JsonDocument LocalConfig(string? directory=null)=>File.Exists(Path.Combine(directory??DirectoryPath,"config.json"))?JsonDocument.Parse(File.ReadAllText(Path.Combine(directory??DirectoryPath,"config.json")).TrimStart('\uFEFF')):throw new AppException("尚未连接 UpSeller 数据源，请在连接设置中指定每日推送配置文件夹。",409);
    static string Text(JsonElement e,params string[] names){foreach(var name in names)if(e.TryGetProperty(name,out var v)&&v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined&&v.ToString()!="")return v.ToString();return "";}
    static decimal Number(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&decimal.TryParse(v.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:throw new AppException("UpSeller 统计字段不完整："+name,409);
    public DashboardShop[] Shops()=>Shops(DirectoryPath);
    DashboardShop[] Shops(string directory)
    {
        using var local=LocalConfig(directory);return ParseShops(local.RootElement);
    }
    static DashboardShop[] ParseShops(JsonElement settings)=>settings.GetProperty("shops").EnumerateObject().Where(p=>p.Name.StartsWith("Meli",StringComparison.OrdinalIgnoreCase)).Select(p=>new DashboardShop(p.Name,Text(p.Value,"upseller_name"),Number(p.Value,"target_cny"))).Where(s=>s.Name!="").ToArray();
    static string Key(string s)=>string.Concat(s.Where(c=>!char.IsWhiteSpace(c))).ToUpperInvariant();
    static DateOnly Today()=>DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)); // Match the existing daily-push reporting calendar (Asia/Shanghai).
    public Task<DashboardData> ReadAsync(string id,bool refresh,CancellationToken ct)=>ReadAsync(id,refresh,null,ct);
    public async Task<DashboardData> ReadAsync(string id,bool refresh,DateOnly? selectedDate,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var sourceDirectory=DirectoryPath;
            using var local=LocalConfig(sourceDirectory);var settings=local.RootElement;
            var shop=ParseShops(settings).SingleOrDefault(s=>s.Id==id)??throw new AppException("所选店铺未配置 UpSeller 统计映射。",400);
            var configurationHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.GetRawText())));
            var today=Today();var period=DashboardPeriod.For(today,selectedDate);var reportDate=period.ReportDate;var cacheKey=sourceDirectory+"|"+id+"|"+configurationHash+"|"+reportDate.ToString("yyyy-MM-dd");
            if(!refresh&&cache.TryGetValue(cacheKey,out var saved))return saved;
            var credentialPath=new[]{".upseller_cookie",".upseller_cookie.txt"}.Select(n=>Path.Combine(sourceDirectory,n)).FirstOrDefault(File.Exists)??throw new AppException("本机每日推送缺少 UpSeller 登录凭据，请先更新该工具的登录状态。",409);
            var credential=(await File.ReadAllTextAsync(credentialPath,ct)).Trim().TrimStart('\uFEFF');
            if(credential.Length==0||credential.Contains('\r')||credential.Contains('\n'))throw new AppException("本机 UpSeller 登录凭据无效，请重新登录。",409);
            var monthStart=period.MonthStart;
            var monthly=await Aggregate(monthStart,reportDate,shop,null,credential,ct);var shopId=Text(monthly,"shopId");
            if(!long.TryParse(shopId,out _))throw new AppException("未能确认 UpSeller 店铺 ID。",409);
            var days=new List<DailySales>();
            for(int i=6;i>=0;i--){var day=reportDate.AddDays(-i);var row=await Aggregate(day,day,shop,shopId,credential,ct);days.Add(new(day.ToString("yyyy-MM-dd"),Number(row,"validSales"),checked((int)Number(row,"validOrders"))));}
            var dailyRows=await Variants(reportDate,reportDate,shop,shopId,credential,ct);
            var monthRows=await Variants(monthStart,reportDate,shop,shopId,credential,ct);
            Dictionary<string,string> categories=[];Dictionary<string,decimal?> margins=[];var warnings=new List<string>();
            string LocalFile(string field){var p=Text(settings,field);if(p=="")throw new FileNotFoundException();return Path.IsPathFullyQualified(p)?p:Path.Combine(sourceDirectory,p);}
            try{categories=LocalWorkbook.Categories(LocalFile("sku_classification_file"));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or InvalidOperationException){warnings.Add("分类表不可用，相关 SKU 显示未分类。");}
            try{margins=LocalWorkbook.Margins(LocalFile("profit_workbook_file"));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or InvalidOperationException){warnings.Add("利润工作簿不可用，预估毛利率暂不可用。");}
            var full=await FullOrders(shopId,reportDate,credential,categories,margins,ct);
            var result=DashboardMetrics.Build(shop,Number(settings,"brl_to_cny"),today,days.ToArray(),Number(monthly,"validSales"),checked((int)Number(monthly,"validOrders")),dailyRows,monthRows,categories,margins,full,reportDate);
            result=result with{ProfitIssues=result.ProfitIssues.Concat(warnings).ToArray()};if(cache.Count>=32)cache.Clear();cache[cacheKey]=result;return result;
        }
        catch(HttpRequestException){throw new AppException("UpSeller 连接失败，请检查网络后刷新。",502);}
        catch(TaskCanceledException)when(!ct.IsCancellationRequested){throw new AppException("UpSeller 读取超时，请稍后刷新。",504);}
        catch(JsonException){throw new AppException("UpSeller 返回的数据格式无法识别，请检查登录状态。",502);}
        finally{gate.Release();}
    }
    async Task<JsonElement> Get(string path,Dictionary<string,string> query,string credential,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://app.upseller.com"+path+"?"+string.Join('&',query.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value))));
        request.Headers.Add("Cookie","us_u="+credential);request.Headers.Add("Accept","application/json");
        using var response=await http.SendAsync(request,ct);
        if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden||(int)response.StatusCode is >=300 and <400)throw new AppException("UpSeller 登录已过期，请更新每日推送的登录凭据。",409);
        response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!data.RootElement.TryGetProperty("code",out var code)||code.ToString()!="0")throw new AppException("UpSeller 未返回有效统计数据，请检查登录状态后重试。",409);
        return data.RootElement.GetProperty("data").Clone();
    }
    async Task<JsonElement> PostForm(string path,Dictionary<string,string> fields,string credential,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://app.upseller.com"+path){Content=new FormUrlEncodedContent(fields)};
        request.Headers.Add("Cookie","us_u="+credential);request.Headers.Add("Accept","application/json");
        using var response=await http.SendAsync(request,ct);
        if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden||(int)response.StatusCode is >=300 and <400)throw new AppException("UpSeller 登录已过期，请更新每日推送的登录凭据。",409);
        response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!data.RootElement.TryGetProperty("code",out var code)||code.ToString()!="0")throw new AppException("UpSeller 未返回有效 Full 订单数据，请检查登录状态后重试。",409);
        return data.RootElement.GetProperty("data").Clone();
    }
    async Task<FullOrderSummary> FullOrders(string shopId,DateOnly day,string credential,IReadOnlyDictionary<string,string> categories,IReadOnlyDictionary<string,decimal?> margins,CancellationToken ct)
    {
        var orders=new List<JsonElement>();var identities=new HashSet<string>(StringComparer.Ordinal);int received=0;decimal? expected=null;
        for(int page=1;page<=100;page++)
        {
            var fields=new Dictionary<string,string>
            {
                ["platform"]="mercado",["shopIdList"]=JsonSerializer.Serialize(new[]{shopId}),["timeType"]="0",
                ["startTime"]=day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" 00:00:00",
                ["endTime"]=day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" 23:59:59",
                ["searchType"]="0",["searchValue"]="",["sortName"]="orderCreateTime",["sortValue"]="desc",
                ["pageNum"]=page.ToString(CultureInfo.InvariantCulture),["pageSize"]="100"
            };
            var data=await PostForm("/api/fulfillment-order/index",fields,credential,ct);var total=Number(data,"total");
            if(total<0||Number(data,"pageNum")!=page||(expected!=null&&total!=expected))throw new AppException("UpSeller Full 订单分页发生变化，请刷新重新读取。",409);expected=total;
            if(!data.TryGetProperty("list",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw new AppException("UpSeller Full 订单列表格式无法识别。",502);
            var pageRows=rows.EnumerateArray().ToArray();
            foreach(var order in pageRows)
            {
                var rowShopId=Text(order,"shopId");if(rowShopId!=""&&rowShopId!=shopId)throw new AppException("Full 订单店铺 ID 与所选店铺不一致。",409);
                var identity=Text(order,"orderId","platformOrderId","orderNo","orderNumber","id");
                if(identity==""||!identities.Add(identity))throw new AppException("UpSeller Full 订单分页缺少唯一身份或重复返回，未使用不完整数据。",409);
                orders.Add(order.Clone());
            }
            received+=pageRows.Length;if(received>total)throw new AppException("UpSeller Full 订单返回条数超过报表总数，请重新读取。",409);
            if(received==total)return FullOrderMetrics.Build(orders,categories,margins);
            if(pageRows.Length==0)throw new AppException("UpSeller Full 订单分页提前结束。",409);
        }
        throw new AppException("Full 订单分页超出读取上限。",409);
    }
    static Dictionary<string,string> Query(DateOnly start,DateOnly end,int size)=>new(){["beginDate"]=start.ToString("yyyy-MM-dd"),["endDate"]=end.ToString("yyyy-MM-dd"),["pageSize"]=size.ToString(),["pageNum"]="1",["sortName"]="0",["sortValue"]="0"};
    async Task<JsonElement> Aggregate(DateOnly start,DateOnly end,DashboardShop shop,string? id,string credential,CancellationToken ct)
    {
        var query=Query(start,end,50);query["searchValue"]=shop.Name;query["sortName"]="2";if(id!=null)query["shopId"]=id;
        var data=await Get("/api/statistics/sale-data/list-shop",query,credential,ct);
        var rows=data.GetProperty("list").EnumerateArray().Where(r=>Key(Text(r,"shopName"))==Key(shop.Name)&&(id==null||Text(r,"shopId")==id)).ToArray();
        if(rows.Length!=1)throw new AppException("UpSeller 未唯一返回所选店铺的统计，未使用其他店铺数据。",409);
        if(Text(rows[0],"currency") is not "" and not "BRL")throw new AppException("所选店铺统计币种不是 BRL。",409);
        return rows[0].Clone();
    }
    async Task<SkuSale[]> Variants(DateOnly start,DateOnly end,DashboardShop shop,string id,string credential,CancellationToken ct)
    {
        var result=new List<SkuSale>();var seen=new HashSet<string>();var allIdentities=new HashSet<string>();int received=0;decimal? expected=null;
        for(int page=1;page<=100;page++)
        {
            var query=Query(start,end,10000);query["pageNum"]=page.ToString();query["shopId"]=id;query["searchType"]="";query["searchValue"]="";
            var data=await Get("/api/statistics/variation-sale-all-list",query,credential,ct);var total=Number(data,"total");
            if(total<0||Number(data,"pageNum")!=page||(expected!=null&&total!=expected))throw new AppException("UpSeller 分页数据发生变化，请刷新重新读取。",409);expected=total;
            var rows=data.GetProperty("list").EnumerateArray().ToArray();
            foreach(var raw in rows)
            {
                var identity=Text(raw,"idStr");if(identity=="")identity=Text(raw,"id");
                if(identity==""||!allIdentities.Add(identity))throw new AppException("UpSeller SKU 分页缺少唯一身份或重复返回，未使用不完整数据。",409);
            }
            received+=rows.Length;if(received>total)throw new AppException("UpSeller SKU 返回条数超过报表总数，请重新读取。",409);
            foreach(var r in rows.Where(r=>Key(Text(r,"shopName"))==Key(shop.Name)))
            {
                if(Text(r,"shopId") is var rowId&&rowId!=""&&rowId!=id)throw new AppException("SKU 店铺 ID 与所选店铺不一致。",409);
                var identity=Text(r,"idStr");if(identity=="")identity=Text(r,"id");
                if(identity==""||!seen.Add(identity)||Text(r,"currency")!="BRL")throw new AppException("SKU 统计身份、分页或币种异常。",409);
                var sku=Text(r,"variationSku");if(sku=="")sku=Text(r,"productSku");sku=sku.Trim().ToUpperInvariant();
                var sales=Number(r,"sales");var orders=Number(r,"validOrders");var units=Number(r,"productSales");
                if(sales==0&&orders==0&&units==0)continue;
                if(sku==""||orders!=decimal.Truncate(orders)||units!=decimal.Truncate(units))throw new AppException("SKU 或销量字段不完整。",409);
                var name=new[]{"productName","title","productTitle","name","variationName"}.Select(k=>Text(r,k)).FirstOrDefault(s=>s!="")??sku;
                result.Add(new(sku,name,sales,checked((int)orders),checked((int)units),Number(r,"averagePrice")));
            }
            if(received==total)return result.ToArray();
            if(rows.Length==0)throw new AppException("UpSeller SKU 分页提前结束。",409);
        }
        throw new AppException("SKU 分页超出读取上限。",409);
    }
    public void Dispose(){http.Dispose();gate.Dispose();}
}
