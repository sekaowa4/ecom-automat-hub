using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace Repricing.Execution;

// Owns only the dedicated target it creates; user tabs are never enumerated for reuse or closed.
public sealed class ReadOnlyCdp: IAsyncDisposable
{
    readonly ClientWebSocket socket=new();int sequence;
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    async Task Connect(Uri uri,CancellationToken ct)
    {
        if(uri.Scheme!="ws"||uri.Host!="127.0.0.1")throw new AppException("拒绝连接非本机紫鸟端口。");
        await socket.ConnectAsync(uri,ct);
    }
    async Task<JsonElement> Send(string method,object parameters,CancellationToken ct)
    {
        var id=++sequence;var bytes=JsonSerializer.SerializeToUtf8Bytes(new{id,method,@params=parameters});
        await socket.SendAsync(bytes,WebSocketMessageType.Text,true,ct);
        var buffer=new byte[16384];
        while(true)
        {
            using var message=new MemoryStream();WebSocketReceiveResult part;
            do{part=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),ct);if(part.MessageType==WebSocketMessageType.Close)throw new AppException("紫鸟页面连接已关闭。",409);message.Write(buffer,0,part.Count);if(message.Length>4_000_000)throw new AppException("页面读取结果超出上限。");}while(!part.EndOfMessage);
            using var doc=JsonDocument.Parse(message.ToArray());var root=doc.RootElement;
            if(!root.TryGetProperty("id",out var response)||response.GetInt32()!=id)continue;
            if(root.TryGetProperty("error",out _))throw new AppException("紫鸟页面控制命令未完成，请检查客户端。",409);
            return root.GetProperty("result").Clone();
        }
    }
    public sealed class Session: IAsyncDisposable
    {
        public Action<VerificationUpdate>? VerificationReporter {get;set;}
        bool manualVerificationRequired,connectionInterrupted;
        public bool RequiresReconnect=>manualVerificationRequired||connectionInterrupted;
        public int CommandCount=>page.sequence;
        public int NavigationCount {get;private set;}
        public void ResetVerificationWait()=>manualVerificationRequired=false;
        string operation="读取页面";
        void Report(string phase,string message)=>VerificationReporter?.Invoke(new(phase,operation,message));
        async Task<JsonElement> PageCommand(string method,object args,CancellationToken ct)
        {
            if(method is "Page.navigate" or "Page.reload")NavigationCount++;
            try{return await page.Send(method,args,ct);}
            catch(Exception e)when(e is WebSocketException or IOException or OperationCanceledException)
            {
                connectionInterrupted=true;
                Report("connection-failed",$"页面命令 {method} 的控制通道已中断；释放旧通道后先重新读取，不重放提交。");
                if(e is OperationCanceledException)throw;
                throw new AppException("紫鸟页面控制通道已中断；下次操作重新连接并先回读。",409);
            }
            catch(AppException)
            {
                Report("checking",$"页面命令 {method} 失败，检查验证状态；不重放该命令。");
                if(await WaitAutomaticVerificationAsync(ct))throw new AppException($"页面命令 {method} 被验证跳转打断；验证已通过，先回读原操作结果，禁止重放保存。",409);
                Report("checked","当前未发现验证页；保留原错误，保存结果先回读。");
                throw;
            }
        }
        readonly ReadOnlyCdp browser,page;readonly string target;string? currentUrl;string? loader;string? baseEditorEntry;string lastBaseStep="";readonly Dictionary<string,(string EditorUrl,LegacyVariant[] Variants)> legacyVariantIdentityCache=new(StringComparer.Ordinal);
        internal Session(ReadOnlyCdp browser,ReadOnlyCdp page,string target){this.browser=browser;this.page=page;this.target=target;}
        public Task<ZiniaoProduct[]> ReadWeekendAsync(PriceInput input,CancellationToken ct)=>ReadAsync(input,ct,expandMorePromotions:true);
        public async Task<ZiniaoProduct[]> ReadAsync(PriceInput input,CancellationToken ct,bool resolveIdentity=true,bool expandMorePromotions=false,bool reuseDocument=false)
        {
            operation="读取促销活动";var caller=ct;var stage="加载搜索结果";
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.PageRead);ct=timeout.Token;
            try
            {
            var initial=await PageCommand("Page.getFrameTree",new{},ct);
            if(await WaitAutomaticVerificationAsync(ct,initial))initial=await PageCommand("Page.getFrameTree",new{},ct);
            var initialFrame=initial.GetProperty("frameTree").GetProperty("frame");
            string? previousLoader=initialFrame.GetProperty("loaderId").GetString();
            var query=input.ItemId??input.Sku;
            var requestedUrl="https://vendedores.mercadolivre.com.br/anuncios/lista/promos?page=1&search="+Uri.EscapeDataString(query);
            var reuse=reuseDocument&&currentUrl==requestedUrl&&loader!=null&&initialFrame.GetProperty("url").GetString()==requestedUrl&&previousLoader==loader;
            var navigationPending=!reuse;
            currentUrl=requestedUrl;
            if(!reuse)
            {
                var navigation=await PageCommand("Page.navigate",new{url=currentUrl},ct);
                if(navigation.TryGetProperty("errorText",out _))throw new AppException("美客多工作页面未能打开。",409);
                loader=navigation.TryGetProperty("loaderId",out var l)?l.GetString():null;
                if(loader==null||loader==previousLoader)await PageCommand("Page.reload",new{ignoreCache=true},ct);
            }
            bool expanded=false,morePromotionsExpanded=false;int groupCount=0,errorReloads=0,poll=0;long lastExpand=0,groupHandlerWaitStarted=0;
            Task Poll()=>Task.Delay(ExecutionTiming.ReadPollDelay(poll++),ct);
            while(true)
            {
                // Loader guards stop the previous SKU's document being mistaken for a freshly read result.
                var tree=await PageCommand("Page.getFrameTree",new{},ct);
                if(await WaitAutomaticVerificationAsync(ct,tree)){tree=await PageCommand("Page.getFrameTree",new{},ct);loader=tree.GetProperty("frameTree").GetProperty("frame").GetProperty("loaderId").GetString();}
                var frame=tree.GetProperty("frameTree").GetProperty("frame");
                // A same-URL navigation can still expose the previous document until commit.
                if(navigationPending&&(frame.GetProperty("loaderId").GetString()==previousLoader||frame.GetProperty("url").GetString()!=currentUrl)){await Poll();continue;}
                navigationPending=false;
                if(loader!=null&&frame.GetProperty("loaderId").GetString()!=loader)
                {
                    // A same-search reload has a new loader; waiting for the discarded loader never completes.
                    if(frame.GetProperty("url").GetString()!=currentUrl){await Task.Delay(300,ct);continue;}
                    loader=frame.GetProperty("loaderId").GetString();expanded=false;morePromotionsExpanded=false;groupCount=0;
                }
                // This frame was just checked above; retain the post-evaluation verification guard.
                var value=await EvaluateAfterVerification(SnapshotScript,ct);
                if(reuse&&value.TryGetProperty("editorOpen",out var editing)&&editing.GetBoolean())throw new AppException("核验页面仍有编辑框，结果未知；未重放提交。",409);
                var href=value.GetProperty("url").GetString()??"";
                if(href!="about:blank"&&!href.StartsWith("https://vendedores.mercadolivre.com.br/anuncios/lista/promos",StringComparison.Ordinal))throw new AppException("店铺页面跳转至登录或其他页面，请在紫鸟确认登录后继续。",409);
                if(href==currentUrl&&value.GetProperty("pageError").GetBoolean())
                {
                    if(errorReloads++>=2)throw new AppException("平台返回访问错误页，刷新两次仍未恢复；未提交价格，下轮重试。",409);
                    await Task.Delay(3000,ct);
                    previousLoader=frame.GetProperty("loaderId").GetString();navigationPending=true;
                    var retryNavigation=await PageCommand("Page.navigate",new{url=currentUrl},ct);
                    loader=retryNavigation.TryGetProperty("loaderId",out var retryLoader)?retryLoader.GetString():null;
                    if(loader==null||loader==previousLoader)await PageCommand("Page.reload",new{ignoreCache=true},ct);
                    expanded=false;morePromotionsExpanded=false;groupCount=0;continue;
                }
                if(href==currentUrl&&value.GetProperty("ready").GetBoolean())
                {
                    if(value.GetProperty("incomplete").GetBoolean())throw new AppException("搜索结果超过一页，不能确认 SKU 唯一性；请补充商品 ID。",409);
                    if(!expanded)
                    {
                        if(value.TryGetProperty("groupHandlersPending",out var pendingHandlers)&&pendingHandlers.GetInt32()>0)
                        {
                            if(groupHandlerWaitStarted==0)groupHandlerWaitStarted=Environment.TickCount64;
                            if(Environment.TickCount64-groupHandlerWaitStarted>=30000)throw new AppException("分组展开按钮尚未加载完成；未提交价格。",409);
                            stage="等待分组展开按钮就绪";await Poll();continue;
                        }
                        groupHandlerWaitStarted=0;
                        groupCount=value.TryGetProperty("expandableGroups",out var groups)&&groups.GetInt32()==0?0:(await Evaluate(ExpandScript,ct)).GetInt32();expanded=true;lastExpand=Environment.TickCount64;
                        if(groupCount>0){stage="等待分组子商品展开";await Poll();continue;}
                    }
                    if(groupCount>0&&value.GetProperty("expandedGroups").GetInt32()<groupCount)
                    {
                        if(Environment.TickCount64-lastExpand>=ExecutionTiming.GroupExpansionWait.TotalMilliseconds)throw new AppException("商品分组展开未响应：无法读取对应 SKU 的子商品；未提交价格。",409);
                        await Poll();continue;
                    }
                    if(expandMorePromotions&&!morePromotionsExpanded)
                    {
                        var script="("+WeekendPromotionExpandScript+")("+JsonSerializer.Serialize(new{sku=input.Sku,itemId=input.ItemId},Json)+")";
                        var count=(await Evaluate(script,ct)).GetInt32();morePromotionsExpanded=true;
                        if(count>0){stage="等待目标商品更多促销展开";continue;}
                    }
                    var products=JsonSerializer.Deserialize<ZiniaoProduct[]>(value.GetProperty("products"),Json)??[];
                    return resolveIdentity&&products.Any(p=>p.Sku=="")&&!products.Any(p=>p.Sku==input.Sku)?await ResolveIdentityAsync(input,products,ct,expandMorePromotions):products;
                }
                await Poll();
            }
            }
            catch(OperationCanceledException)when(!caller.IsCancellationRequested){throw new AppException($"读取 {input.Sku} 超时：{stage}；未提交价格。",409);}
        }
        public async Task<ZiniaoProduct[]> ReadManagerAsync(PriceInput input,CancellationToken ct,bool reuseDocument=false)
        {
            operation="读取商品管理";var caller=ct;var stage="加载商品管理搜索结果";
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.PageRead);ct=timeout.Token;
            try
            {
                var initial=await PageCommand("Page.getFrameTree",new{},ct);
                if(await WaitAutomaticVerificationAsync(ct,initial))initial=await PageCommand("Page.getFrameTree",new{},ct);
                var initialFrame=initial.GetProperty("frameTree").GetProperty("frame");
                var previousLoader=initialFrame.GetProperty("loaderId").GetString();
                var requestedUrl="https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search="+Uri.EscapeDataString(input.ItemId??input.Sku);
                var reuse=reuseDocument&&currentUrl==requestedUrl&&loader!=null&&initialFrame.GetProperty("url").GetString()==requestedUrl&&previousLoader==loader;
                var navigationPending=!reuse;
                currentUrl=requestedUrl;
                if(!reuse)
                {
                    var navigation=await PageCommand("Page.navigate",new{url=currentUrl},ct);
                    if(navigation.TryGetProperty("errorText",out _))throw new AppException("商品管理页面未能打开。",409);
                    loader=navigation.TryGetProperty("loaderId",out var nextLoader)?nextLoader.GetString():null;
                    if(loader==null||loader==previousLoader)await PageCommand("Page.reload",new{ignoreCache=true},ct);
                }
                JsonElement managed;
                while(true)
                {
                    await WaitAutomaticVerificationAsync(ct);
                    if(navigationPending)
                    {
                        var tree=await PageCommand("Page.getFrameTree",new{},ct);
                        if(await WaitAutomaticVerificationAsync(ct,tree))tree=await PageCommand("Page.getFrameTree",new{},ct);
                        var frame=tree.GetProperty("frameTree").GetProperty("frame");
                        var frameLoader=frame.GetProperty("loaderId").GetString();
                        if(frameLoader==previousLoader||frame.GetProperty("url").GetString()!=currentUrl){await Task.Delay(ExecutionTiming.PollMs,ct);continue;}
                        loader=frameLoader;
                        navigationPending=false;
                    }
                    if(reuse&&(await EvaluateAfterVerification(SnapshotScript,ct)).GetProperty("editorOpen").GetBoolean())throw new AppException("核验页面仍有编辑框，结果未知；未重放提交。",409);
                    managed=await Identity("manager",input,null,ct);
                    if(managed.GetProperty("ready").GetBoolean())break;
                    await Task.Delay(ExecutionTiming.PollMs,ct);
                }
                var rows=managed.GetProperty("products").EnumerateArray().ToArray();
                string? Text(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
                string NormalizeId(string? value)=>(value??"").Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-');
                bool ItemMatches(JsonElement row)=>input.ItemId==null||NormalizeId(Text(row,"itemId"))==NormalizeId(input.ItemId);
                var exact=rows.Where(row=>Text(row,"sku")==input.Sku&&ItemMatches(row)).ToArray();
                if(exact.Length>0)return exact.Select(row=>JsonSerializer.Deserialize<ZiniaoProduct>(row,Json)!).ToArray();
                var legacy=rows.Where(row=>Text(row,"sku")==""&&ItemMatches(row)&&!string.IsNullOrWhiteSpace(Text(row,"editorLink"))).ToArray();
                if(legacy.Length!=1)return [];
                var itemId=Text(legacy[0],"itemId")!;
                var editorUrl=Text(legacy[0],"editorLink")!;
                if(reuseDocument&&legacyVariantIdentityCache.TryGetValue(itemId,out var cachedIdentity)&&cachedIdentity.EditorUrl==editorUrl)
                {
                    var cachedMatch=cachedIdentity.Variants.Where(v=>v.Sku==input.Sku).ToArray();
                    if(cachedMatch.Length==1)return [new ZiniaoProduct(itemId,input.Sku,Text(legacy[0],"title")??"",Text(legacy[0],"basePriceText")??"",[],cachedIdentity.Variants.Select(v=>v.Sku).Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray(),Text(legacy[0],"managedPriceText"),Text(legacy[0],"listingStatus"),cachedMatch[0].Label,cachedIdentity.Variants)];
                }
                stage="读取旧式商品规格与库存";
                currentUrl=editorUrl;
                await PageCommand("Page.navigate",new{url=currentUrl},ct);
                JsonElement detail;
                while(true)
                {
                    await WaitAutomaticVerificationAsync(ct);
                    detail=await Identity("legacy",input,itemId,ct);
                    if(detail.GetProperty("ready").GetBoolean())break;
                    await Task.Delay(ExecutionTiming.PollMs,ct);
                }
                var variants=detail.GetProperty("variants").EnumerateArray().Select(v=>new LegacyVariant(v.GetProperty("sku").GetString()??"",v.GetProperty("label").GetString(),v.GetProperty("stock").GetString())).ToArray();
                var matched=variants.Where(v=>v.Sku==input.Sku).ToArray();
                if(matched.Length!=1)return [];
                legacyVariantIdentityCache[itemId]=(editorUrl,variants);
                return [new ZiniaoProduct(itemId,input.Sku,Text(legacy[0],"title")??"",Text(legacy[0],"basePriceText")??"",[],variants.Select(v=>v.Sku).Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray(),Text(legacy[0],"managedPriceText"),Text(legacy[0],"listingStatus"),matched[0].Label,variants)];
            }
            catch(OperationCanceledException)when(!caller.IsCancellationRequested){throw new AppException($"商品管理读取 {input.Sku} 超时：{stage}；未提交价格。",409);}
        }
        async Task<JsonElement> Identity(string action,PriceInput input,string? id,CancellationToken ct)
        {
            var args=JsonSerializer.Serialize(new{action,timing=ExecutionTiming.Browser,url=currentUrl,itemId=id,sku=input.Sku},Json);
            return await Evaluate("("+ProductIdentityScript+")("+args+")",ct);
        }
        async Task<ZiniaoProduct[]> ResolveIdentityAsync(PriceInput input,ZiniaoProduct[] candidates,CancellationToken ct,bool expandMorePromotions)
        {
            currentUrl="https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search="+Uri.EscapeDataString(input.Sku);
            await PageCommand("Page.navigate",new{url=currentUrl},ct);
            JsonElement managed;
            while(true){await WaitAutomaticVerificationAsync(ct);managed=await Identity("manager",input,null,ct);if(managed.GetProperty("ready").GetBoolean())break;await Task.Delay(ExecutionTiming.PollMs,ct);}
            var rows=managed.GetProperty("products").EnumerateArray().ToArray();
            var exact=rows.Where(p=>p.GetProperty("sku").GetString()==input.Sku&&(input.ItemId==null||p.GetProperty("itemId").GetString()==input.ItemId.Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-'))).ToArray();
            if(exact.Length>0)
            {
                var independent=new List<ZiniaoProduct>();
                foreach(var row in exact)
                {
                    var id=row.GetProperty("itemId").GetString()!;
                    var childRead=await ReadAsync(input with{ItemId=id},ct,false,expandMorePromotions);
                    var matched=childRead.Where(p=>p.ItemId==id&&p.Sku==input.Sku).ToArray();
                    if(matched.Length==0)return exact.Select(p=>JsonSerializer.Deserialize<ZiniaoProduct>(p,Json)!).ToArray();
                    independent.AddRange(matched);
                }
                return independent.Count>0?independent.ToArray():exact.Select(p=>JsonSerializer.Deserialize<ZiniaoProduct>(p,Json)!).ToArray();
            }
            var aliases=new Dictionary<string,string[]>();
            var variantLabels=new Dictionary<string,string>();
            var sharedVariants=new Dictionary<string,LegacyVariant[]>();
            foreach(var row in rows.Where(p=>p.GetProperty("sku").GetString()==""&&p.GetProperty("editorLink").ValueKind==JsonValueKind.String))
            {
                var id=row.GetProperty("itemId").GetString()!;
                if(!candidates.Any(p=>p.ItemId==id&&p.Sku==""))continue;
                await PageCommand("Page.navigate",new{url=row.GetProperty("editorLink").GetString()},ct);
                JsonElement detail;
                while(true){await WaitAutomaticVerificationAsync(ct);detail=await Identity("legacy",input,id,ct);if(detail.GetProperty("ready").GetBoolean())break;await Task.Delay(ExecutionTiming.PollMs,ct);}
                var skus=detail.GetProperty("skus").EnumerateArray().Select(s=>s.GetString()!).ToArray();
                if(skus.Contains(input.Sku,StringComparer.Ordinal))
                {
                    aliases.Add(id,skus);
                    if(detail.TryGetProperty("variants",out var variants))
                    {
                        sharedVariants[id]=variants.EnumerateArray().Select(v=>new LegacyVariant(v.GetProperty("sku").GetString()??"",v.GetProperty("label").GetString(),v.GetProperty("stock").GetString())).ToArray();
                        var matched=variants.EnumerateArray().Where(v=>v.GetProperty("sku").GetString()==input.Sku).ToArray();
                        if(matched.Length==1&&matched[0].GetProperty("label").GetString() is {Length:>0} label)variantLabels[id]=label;
                    }
                }
            }
            // Return to a fresh promotion document before any editor can be opened.
            var fresh=await ReadAsync(input,ct,false,expandMorePromotions);
            return fresh.Select(p=>p.Sku==""&&aliases.TryGetValue(p.ItemId,out var skus)?p with{Sku=input.Sku,SharedSkus=skus,VariantLabel=variantLabels.GetValueOrDefault(p.ItemId),SharedVariants=sharedVariants.GetValueOrDefault(p.ItemId)}:p).ToArray();
        }
        public async Task<JsonElement> Evaluate(string expression,CancellationToken ct)
        {
            if(await WaitAutomaticVerificationAsync(ct))throw new AppException("验证已通过，原页面上下文已失效；先重新读取，不继续原提交。",409);
            return await EvaluateAfterVerification(expression,ct);
        }
        async Task<JsonElement> EvaluateAfterVerification(string expression,CancellationToken ct)
        {
            JsonElement result;
            try{result=await PageCommand("Runtime.evaluate",new{expression,returnByValue=true,awaitPromise=true},ct);}
            catch(AppException)when(!connectionInterrupted){await WaitAutomaticVerificationAsync(ct);throw;}
            if(await WaitAutomaticVerificationAsync(ct))throw new AppException("页面操作期间发生验证跳转；验证已通过，保存结果须回读。",409);
            if(result.TryGetProperty("exceptionDetails",out _)||!result.GetProperty("result").TryGetProperty("value",out var value))throw new AppException("页面结构或表单状态无法确认；未继续提交。",409);
            return value.Clone();
        }
        public static bool IsVerificationUrl(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme=="https"&&(u.Host=="vendedores.mercadolivre.com.br"||u.Host=="www.mercadolivre.com.br"||u.Host=="www.mercadolivre.com"||u.Host=="accounts.mercadolivre.com.br")&&(u.AbsolutePath.StartsWith("/totp/",StringComparison.Ordinal)||u.AbsolutePath.StartsWith("/jms/",StringComparison.Ordinal)||u.AbsolutePath.StartsWith("/login",StringComparison.Ordinal)||u.AbsolutePath.StartsWith("/authentication",StringComparison.Ordinal)||u.AbsolutePath.StartsWith("/verification",StringComparison.Ordinal));
        async Task<bool> WaitAutomaticVerificationAsync(CancellationToken ct,JsonElement? checkedTree=null)
        {
            if(manualVerificationRequired)throw new VerificationRequiredException();
            bool entered=false;
            try{
            var tree=checkedTree??await page.Send("Page.getFrameTree",new{},ct);
            var frame=tree.GetProperty("frameTree").GetProperty("frame");
            if(frame.TryGetProperty("url",out var u)&&IsVerificationUrl(u.GetString()??""))
            {
                entered=true;Report("entered",$"发现验证页面 {new Uri(u.GetString()!).AbsolutePath}；整批停止改价，等待紫鸟自动填充，最长75秒。");
                var started=DateTimeOffset.UtcNow;bool clicked=false;
                while(DateTimeOffset.UtcNow-started<TimeSpan.FromSeconds(75))
                {
                    var latest=await page.Send("Page.getFrameTree",new{},ct);
                    var url=latest.GetProperty("frameTree").GetProperty("frame").GetProperty("url").GetString()??"";
                    if(!IsVerificationUrl(url)){Report("passed","已离开验证页；恢复前重新读取页面与原提交结果。");return true;}
                    if(!clicked&&new Uri(url).AbsolutePath.StartsWith("/totp/",StringComparison.Ordinal))
                    {
                        var response=await page.Send("Runtime.evaluate",new{expression="""
                        (()=>{
                          const visible=e=>!!(e.getClientRects().length)&&!e.disabled;
                          const inputs=[...document.querySelectorAll('input:not([type=hidden])')].filter(visible);
                          const filled=inputs.some(e=>/^\d{6}$/.test(e.value)&&(/one-time-code/.test(e.autocomplete)||/numeric/.test(e.inputMode)))||inputs.filter(e=>/^\d$/.test(e.value)).length===6;
                          const buttons=[...document.querySelectorAll('button[type=submit],input[type=submit]')].filter(visible);
                          if(!filled||buttons.length!==1)return false;
                          buttons[0].click();return true;
                        })()
                        """,returnByValue=true},ct);
                        clicked=response.GetProperty("result").TryGetProperty("value",out var value)&&value.ValueKind==JsonValueKind.True;
                        if(clicked)Report("confirmed","已检测自动填充完成并单次点击确认；未记录验证码。");
                    }
                    await Task.Delay(1000,ct);
                }
                manualVerificationRequired=true;Report("failed","自动验证75秒内未完成，转人工暂停整批。" );throw new VerificationRequiredException();
            }
            return false;
            }catch(VerificationRequiredException){throw;}
            catch(OperationCanceledException)when(entered){manualVerificationRequired=true;Report("failed","验证等待被当前操作超时中断，转人工暂停整批，不继续改价。");throw new VerificationRequiredException("验证尚未确认完成，操作等待已超时；已暂停整批，请验证后继续并回读原提交结果。");}
            catch(Exception e)when(e is AppException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if(!entered)
                {
                    connectionInterrupted=true;
                    Report("connection-failed","页面控制连接中断，尚未确认出现验证页；本项中断，重连后先回读原提交结果。");
                    throw new AppException("紫鸟页面连接中断，尚未确认出现验证页；本项停止，重新连接后先回读，不重复提交。",409);
                }
                manualVerificationRequired=true;Report("failed","验证期间页面控制中断，暂停整批等待核验。");
                throw new VerificationRequiredException("验证期间页面连接中断；已暂停整批，请完成验证后继续并回读原提交结果。");
            }
        }
        async Task ExitAction(string action,PriceCutPlan plan,CancellationToken ct,bool recoverVerification=true)
        {
            operation="退出活动 "+action;
            var args=JsonSerializer.Serialize(new{action,timing=ExecutionTiming.Browser,url=currentUrl,itemId=plan.Product.ItemId,sku=plan.Input.Sku,sharedSkus=plan.Product.SharedSkus,promotionId=plan.PromotionId,promotionPeriod=string.Join("\n",plan.Product.Promotions.Single(p=>p.Id==plan.PromotionId).Name.Split('\n').Skip(1)).Trim(),promotionName=plan.Product.Promotions.Single(p=>p.Id==plan.PromotionId).Name.Split('\n')[0].Trim(),before=plan.Before,basePrice=plan.BasePrice,target=plan.Input.TargetPrice,changeBase=plan.ChangeBase,replacementPromotionId=plan.RejoinPromotionId,allowDirectStop=plan.Input.AllowExit,stage=plan.Stage,preservedCampaigns=plan.Product.Promotions.Where(p=>p.Id!=plan.PromotionId&&OrdinaryCampaignIncreaseExecutor.Enrolled(p)).Select(p=>new{id=p.Id,price=ZiniaoReadOnlyExecutor.Money(p.PriceText)}).ToArray(),incomeSubsidyId=plan.IncomeSubsidyId,incomeOrdinaryId=plan.IncomeOrdinaryId,incomeOrdinaryPrice=plan.IncomeOrdinaryPrice,incomeSubsidyNet=plan.IncomeSubsidyNet,incomeOrdinaryNet=plan.IncomeOrdinaryNet},Json);
            JsonElement result;
            try{result=await Evaluate("(async()=>{try{await ("+StopParticipationScript+")("+args+");return {ok:true};}catch(e){const m=String(e?.message||'');return {ok:false,error:m.startsWith('REPRICING_GUARD:')?m.slice(16,196):'页面脚本执行失败'};}})()",ct);}
            catch(AppException e)when(action=="open"&&recoverVerification&&e.Message.StartsWith("页面命令 Runtime.evaluate 被验证跳转打断；验证已通过",StringComparison.Ordinal))
            {
                var rows=await ReadAsync(plan.Input with{ItemId=null},ct);
                var fresh=rows.Where(p=>p.ItemId==plan.Product.ItemId&&p.Sku==plan.Input.Sku).ToArray();
                if(fresh.Length!=1||!OrdinaryCampaignIncreaseExecutor.Stable(plan,fresh[0])||fresh[0].Promotions.Count(p=>p.Id==plan.PromotionId&&OrdinaryCampaignIncreaseExecutor.Enrolled(p)&&p.Editable&&ZiniaoReadOnlyExecutor.Money(p.PriceText)==plan.Before)!=1)
                    throw new AppException("验证后商品或目标活动已变化；未继续退出或保存。",409);
                await ExitAction("open",plan with{Product=fresh[0]},ct,false);return;
            }
            catch(AppException)when(action=="open"&&recoverVerification)
            {
                if(!await WaitAutomaticVerificationAsync(ct))throw;
                await ReadAsync(plan.Input with{ItemId=null},ct);await ExitAction("open",plan,ct,false);return;
            }
            if(!result.GetProperty("ok").GetBoolean())
            {
                if(action=="open"&&recoverVerification&&await WaitAutomaticVerificationAsync(ct)){await ReadAsync(plan.Input with{ItemId=null},ct);await ExitAction("open",plan,ct,false);return;}
                var stage=action switch{"open"=>"打开活动编辑框","check"=>"校验退出前状态","stop"=>"打开退出确认框",_=>"确认退出活动"};
                throw new AppException($"{stage}：{result.GetProperty("error").GetString()}；未继续提交。",409);
            }
        }
        public async Task PrepareExitAsync(PriceCutPlan plan,CancellationToken ct)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.PrepareExit);ct=timeout.Token;
            await ExitAction("open",plan,ct);await ExitAction("check",plan,ct);
        }
        public async Task ConfirmExitAsync(PriceCutPlan plan,CancellationToken ct)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.ConfirmExit);await ExitAction("stop",plan,timeout.Token);await ExitAction("confirm",plan,timeout.Token);
        }
        public async Task PrepareAsync(PriceCutPlan plan,CancellationToken ct)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.PreparePrice);ct=timeout.Token;
            await Action("open",plan,ct);await Action("draft",plan,ct);
        }
        public async Task<decimal?> PreviewNetAsync(PriceCutPlan plan,CancellationToken ct)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.PreparePrice);ct=timeout.Token;
            try{
                await Action("open",plan,ct,true);await Action("draft",plan,ct,true);
                decimal? previous=null;var stable=0;
                for(var i=0;i<20;i++){
                    var r=await Action("net",plan,ct,true);var net=r.GetProperty("net");decimal? current=net.ValueKind==JsonValueKind.Number?net.GetDecimal():null;
                    stable=current!=null&&current==previous?stable+1:0;previous=current;if(stable>=2)return current;
                    await Task.Delay(500,ct);
                }
                return null;
            }finally{await CancelAsync();}
        }
        public async Task SubmitAsync(PriceCutPlan plan,CancellationToken ct)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.ConfirmExit);
            await Action("submit",plan,timeout.Token);
        }
        async Task<JsonElement> BaseAction(string action,PriceCutPlan plan,CancellationToken ct)
        {
            lastBaseStep=action;operation="原价 "+action;
            var args=JsonSerializer.Serialize(new{action,timing=ExecutionTiming.Browser,url=currentUrl,itemId=plan.Product.ItemId,sku=plan.Input.Sku,basePrice=plan.BasePrice,target=plan.DiscountBase??plan.Input.TargetPrice,finalPrice=plan.Input.TargetPrice,discountBase=plan.DiscountBase,changeBase=plan.ChangeBase,directBaseOnly=plan.DirectBaseOnly,editorEntry=baseEditorEntry},Json);
            var result=await Evaluate("(async()=>{try{return {ok:true,value:await ("+BasePriceScript+")("+args+")};}catch(e){const m=String(e?.message||'');return {ok:false,error:m.startsWith('REPRICING_GUARD:')?m.slice(16,196):'原价页面执行失败'};}})()",ct);
            if(!result.GetProperty("ok").GetBoolean())
            {
                var error=result.GetProperty("error").GetString();
                var unknownSave=action=="submit"&&error=="确认平台商品更新后果后弹框未关闭，保存结果需回读";
                throw new AppException($"原价{action}：{error}；未继续提交。",unknownSave?409:422);
            }
            return result.GetProperty("value");
        }
        async Task<JsonElement> WaitBase(string action,PriceCutPlan plan,CancellationToken ct,TimeSpan? settleLimit=null)
        {
            var started=System.Diagnostics.Stopwatch.StartNew();
            while(true)
            {
                await WaitAutomaticVerificationAsync(ct);
                var value=await BaseAction(action,plan,ct);if(value.GetProperty("ready").GetBoolean())return value;
                if(value.TryGetProperty("reason",out var reason))lastBaseStep=action+" / "+reason.GetString();
                // A saved sales panel can remain expanded. Reopen independently instead of waiting for a collapse forever.
                if(settleLimit.HasValue&&started.Elapsed>=settleLimit.Value)return value;
                await Task.Delay(ExecutionTiming.PollMs,ct);
            }
        }
        public async Task PrepareBaseAsync(PriceCutPlan plan,CancellationToken ct)
        {
            var caller=ct;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.BaseEdit);ct=timeout.Token;
            try{
            var link=(await BaseManager("locate",plan,ct)).GetProperty("href").GetString()!;
            baseEditorEntry=link;
            await PageCommand("Page.navigate",new{url=link},ct);
            var editor=await WaitBase("open",plan,ct);currentUrl=editor.GetProperty("url").GetString();
            if(editor.TryGetProperty("wholesaleEnabled",out var enabled)&&enabled.GetBoolean())return;
            await BaseAction("draft",plan,ct);
            await WaitBase("check",plan,ct);
            }catch(OperationCanceledException)when(!caller.IsCancellationRequested){throw new AppException("原价准备等待超时，阶段："+lastBaseStep+"；未继续保存，下一轮先回读。",409);}
        }
        async Task<JsonElement> BaseManager(string action,PriceCutPlan plan,CancellationToken ct)
        {
            baseEditorEntry=null;
            currentUrl="https://vendedores.mercadolivre.com.br/anuncios?page=1&sort=DEFAULT&search="+Uri.EscapeDataString(plan.Input.ItemId??plan.Input.Sku);
            await PageCommand("Page.navigate",new{url=currentUrl},ct);
            return await WaitBase(action,plan,ct);
        }
        public async Task<bool> VerifyBaseAsync(PriceCutPlan plan,CancellationToken ct)=>(await BaseManager("verify",plan,ct)).GetProperty("reached").GetBoolean();
        public async Task SubmitBaseAsync(PriceCutPlan plan,CancellationToken ct)
        {
            var caller=ct;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(ExecutionTiming.BaseEdit);ct=timeout.Token;
            try{
            var state=await BaseAction("wholesale-state",plan,ct);
            if(state.TryGetProperty("wholesaleEnabled",out var enabled)&&enabled.GetBoolean())
            {
                await BaseAction("wholesale-off",plan,ct);await WaitBase("wholesale-check",plan,ct);await BaseAction("wholesale-submit",plan,ct);
                await WaitBase("wholesale-saved",plan,ct,TimeSpan.FromSeconds(30));await PageCommand("Page.navigate",new{url=baseEditorEntry},ct);
                var fresh=await WaitBase("open",plan,ct);currentUrl=fresh.GetProperty("url").GetString();
                if(!fresh.TryGetProperty("wholesaleCount",out var count)||count.GetInt32()!=1||!fresh.TryGetProperty("wholesaleChecked",out var checkedCount)||checkedCount.GetInt32()!=0)throw new AppException("独立重新打开后未确认唯一批发价格选项已关闭，未改原价；下一轮先重新读取。",409);
                await BaseAction("draft",plan,ct);await WaitBase("check",plan,ct);
            }
            await BaseAction("submit",plan,ct);
            }catch(OperationCanceledException)when(!caller.IsCancellationRequested){throw new AppException("原价保存等待超时，阶段："+lastBaseStep+"；保存结果需回读，禁止盲目重复提交。",409);}
        }
        async Task<JsonElement> Action(string action,PriceCutPlan plan,CancellationToken ct,bool preview=false)
        {
            operation="活动 "+action;
            var args=JsonSerializer.Serialize(new{action,preview,stage=plan.Stage,preservedCampaigns=plan.Product.Promotions.Where(p=>p.Id!=plan.PromotionId&&OrdinaryCampaignIncreaseExecutor.Enrolled(p)).Select(p=>new{id=p.Id,price=ZiniaoReadOnlyExecutor.Money(p.PriceText)}).ToArray(),incomeSubsidyId=plan.IncomeSubsidyId,incomeOrdinaryId=plan.IncomeOrdinaryId,incomeOrdinaryPrice=plan.IncomeOrdinaryPrice,incomeSubsidyNet=plan.IncomeSubsidyNet,incomeOrdinaryNet=plan.IncomeOrdinaryNet,timing=ExecutionTiming.Browser,url=currentUrl,itemId=plan.Product.ItemId,sku=plan.Input.Sku,promotionId=plan.PromotionId,promotionPeriod=string.Join("\n",plan.Product.Promotions.Single(p=>p.Id==plan.PromotionId).Name.Split('\n').Skip(1)).Trim(),promotionName=plan.Product.Promotions.Single(p=>p.Id==plan.PromotionId).Name,before=plan.Before,basePrice=plan.BasePrice,target=plan.Input.TargetPrice,join=plan.Join,preservedActive=plan.Join||preview||plan.Stage is "income-join" or "scheduled-edit"?plan.Product.Promotions.Where(p=>p.Active).Select(p=>new{id=p.Id,price=ZiniaoReadOnlyExecutor.Money(p.PriceText)}).ToArray():[],offerPrice=ZiniaoReadOnlyExecutor.Money(plan.Product.Promotions.Single(p=>p.Id==plan.PromotionId).PriceText)},Json);
            var wrapped=await Evaluate("(async()=>{try{return {ok:true,value:await ("+PriceCutScript+")("+args+")};}catch(e){const m=String(e?.message||'');return {ok:false,error:m.startsWith('REPRICING_GUARD:')?m.slice(16,196):'页面脚本执行失败'};}})()",ct);
            if(!wrapped.GetProperty("ok").GetBoolean())throw new AppException($"活动{action}：{wrapped.GetProperty("error").GetString()}；未继续提交。",409);
            var result=wrapped.GetProperty("value");
            if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("priceValidationError",out var error)&&!preview)throw new AppException(error.GetString()!,422);
            return result;
        }
        public async Task CancelAsync()
        {
            if(manualVerificationRequired||connectionInterrupted)return;
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await Evaluate("(async () => { const prompt=document.querySelector('.sc-list-prompt-modal [data-testid=\"cdp--simple-modal--cancel\"]'); if(prompt)prompt.click(); const d=document.querySelector('.promotion-modal-cdp2026'); const b=d?.querySelector('[data-testid=\"cdp--promotion-modal--cancel\"]'); if(b)b.click(); const until=performance.now()+2500; while(document.querySelector('.promotion-modal-cdp2026,.sc-list-prompt-modal [data-testid=\"cdp--simple-modal--cancel\"]')){if(performance.now()>=until)return false;await new Promise(resolve=>setTimeout(resolve,100));} return true; })()",cleanup.Token);}catch(VerificationRequiredException){throw;}catch{}
        }
        public async ValueTask DisposeAsync()
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await browser.Send("Target.closeTarget",new{targetId=target},cleanup.Token);}catch{}
            await page.DisposeAsync();await browser.DisposeAsync();
        }
    }
    public static async Task<Session> OpenAsync(int port,CancellationToken ct)
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
        using var version=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version",ct));
        var browser=new ReadOnlyCdp();await browser.Connect(new(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!),ct);
        var created=await browser.Send("Target.createTarget",new{url="about:blank"},ct);var target=created.GetProperty("targetId").GetString()!;
        var page=new ReadOnlyCdp();
        try
        {
            using var targets=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list",ct));
            var info=targets.RootElement.EnumerateArray().Single(t=>t.GetProperty("id").GetString()==target);
            await page.Connect(new(info.GetProperty("webSocketDebuggerUrl").GetString()!),ct);
            return new Session(browser,page,target);
        }
        catch
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try{await browser.Send("Target.closeTarget",new{targetId=target},cleanup.Token);}catch{}
            await page.DisposeAsync();await browser.DisposeAsync();throw;
        }
    }
    public const string ExpandScript="""
    (() => { const groups=[...document.querySelectorAll('.sc-list-row-group')].filter(g=>g.querySelector('.sc-list-row-group__aside:not(.sc-list-row-group__aside--no-chevron)'));
      for(const g of groups) {
        if(g.querySelector('.sc-list-row-group__aside .sc-ui-chevron--down')) {
          const button=g.querySelector('.sc-list-row-group__aside [role="button"]');
          if(!button)throw Error('未找到商品分组展开按钮');
          button.click();
        }
      }
      return groups.length;
    })()
    """;
    static string Resource(string name){using var reader=new StreamReader(typeof(ReadOnlyCdp).Assembly.GetManifestResourceStream(name)??throw new InvalidOperationException("缺少页面脚本资源："+name));return reader.ReadToEnd();}
    static string PageScript(string name)=>"async a => ("+Resource(name)+")(a,("+Resource("PromotionName.js")+")())";
    public static string StopParticipationScript{get;}=PageScript("StopParticipationPage.js");
    public static string BasePriceScript{get;}=new StreamReader(typeof(ReadOnlyCdp).Assembly.GetManifestResourceStream("BasePricePage.js")!).ReadToEnd();
    public static string ProductIdentityScript{get;}=new StreamReader(typeof(ReadOnlyCdp).Assembly.GetManifestResourceStream("ProductIdentityPage.js")!).ReadToEnd();
    public static string WeekendPromotionExpandScript{get;}=new StreamReader(typeof(ReadOnlyCdp).Assembly.GetManifestResourceStream("WeekendPromotionExpandPage.js")!).ReadToEnd();
    public static string PriceCutScript{get;}=PageScript("PriceCutPage.js");
    public const string SnapshotScript="""
    (() => {
      const text=e=>(e?.innerText||'').trim();
      const rows=[...document.querySelectorAll('.sc-list-row')];
      const quantity=text(document.querySelector('.sc-list-navbar-right__results-quantity'));
      const match=quantity.match(/\d[\d.,]*/);const total=match?Number(match[0].replace(/[.,]/g,'')):null;
      const groups=[...document.querySelectorAll('.sc-list-row-group')].filter(g=>g.querySelector('.sc-list-row-group__aside:not(.sc-list-row-group__aside--no-chevron)'));
      return {url:location.href,pageError:rows.length===0&&/Hubo un error accediendo a esta p[aá]gina|Erro ao acessar esta p[aá]gina|访问此页面时出错/i.test(text(document.body)),ready:document.readyState!=='loading'&&total!==null&&(rows.length>0||total===0),incomplete:total!==null&&total>rows.length,expandedGroups:groups.filter(g=>{const children=[...g.querySelectorAll('.sc-list-family-row .sc-list-row')];return children.length>0&&children.every(r=>text(r.querySelector('.sc-list-description__id'))&&text(r.querySelector('.sc-list-description__price-text'))&&r.querySelector('button[data-testid^="cdp--promotion-box-button--"]'));}).length,
        expandableGroups:groups.length,groupHandlersPending:groups.filter(g=>{if(!g.querySelector('.sc-list-row-group__aside .sc-ui-chevron--down'))return false;const button=g.querySelector('.sc-list-row-group__aside [role="button"]');return !button||!Object.keys(button).some(key=>key.startsWith('__reactProps$')&&typeof button[key]?.onClick==='function');}).length,editorOpen:!!document.querySelector('.promotion-modal-cdp2026,.sc-list-prompt-modal'),
        products:rows.map(row=>({
          itemId:text(row.querySelector('.sc-list-description__id')).replace(/^#/,''),
          sku:text(row.querySelector('.sc-list-description__sku')).replace(/^SKU\s+/,''),
          title:text(row.querySelector('.sc-list-description__title')),
          basePriceText:text(row.querySelector('.sc-list-description__price-text')),
          promotions:[...row.querySelectorAll('.sc-list-collapsible-row__promotion-box')].map(box=>{
            const action=box.querySelector('button[data-testid^="cdp--promotion-box-button--"]');
            const item=text(row.querySelector('.sc-list-description__id')).replace(/^#/,'');
            const key=action?.getAttribute('data-testid')||'';
            const rawName=text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__1 .sc-list-collapsible-row__promotion-box__column__line__promo-title'))||text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__1 .sc-list-collapsible-row__promotion-box__column__line__text-tail > span'));
            const name=rawName.replace(/^(百分比折扣|Desconto percentual|Desconto por porcentagem|Percentage discount)(?=\d)/i,'$1\n');
            const status=[...box.querySelectorAll('.andes-badge--green')].map(text).find(s=>/^(激活|Ativa|Ativo|Active)$/i.test(s))||[...box.querySelectorAll('.andes-badge__content')].map(text).find(s=>/^(已预约|Agendada|Agendado|Scheduled)$/i.test(s))||text(box.querySelector('.andes-badge__content'));
            const sellerDiscount=key===`cdp--promotion-box-button---${item}`&&/^(百分比折扣|Desconto percentual|Desconto por porcentagem|Percentage discount)\n\S+/i.test(name)&&/^(激活|Ativa|Ativo|Active|已预约|Agendada|Agendado|Scheduled)$/i.test(status);
            return {id:sellerDiscount?'D-PRICE-DISCOUNT':key.startsWith('cdp--promotion-box-button--')?key.slice('cdp--promotion-box-button--'.length).replace(new RegExp('-'+item+'$'),''):'',name,status,
              netAmountText:(()=>{const cell=box.querySelector('.sc-list-collapsible-row__promotion-box__column__4'),main=cell?.querySelectorAll('.ps-total-charges-with-tooltip__value-container > span')||[];if(main.length)return main.length===1&&/^R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,\d{2})?$/.test(text(main[0]))?text(main[0]):null;const t=text(cell);const m=t.match(/^R\$\s*[\d.,]+/);return m&&!/^\s*(至|a\s+R\$|até|–|—)/i.test(t.slice(m[0].length))?m[0]:null;})(),
              priceText:text(box.querySelector('.sc-list-collapsible-row__promotion-box__column__3 > div > div > span')),
              active:!!box.querySelector('.andes-badge--green')&&/^(激活|Ativa|Ativo|Active)$/i.test(status),
              editable:!!action&&!action.disabled&&/^(修改|Modificar|Alterar)$/i.test(text(action)),
              attractive:/诱人(价格|折扣)|preço atrativo|preço atraente|desconto atrativo/i.test(text(box)),
              joinable:key.startsWith('cdp--promotion-box-button--C-')&&!!action&&!action.disabled&&/^(立即参与|Participar|Participar agora)$/i.test(text(action))&&!/新提案|Nova proposta|New proposal|Nova oferta/i.test(text(box))&&!box.querySelector('.andes-badge--green')};
          })
        })).filter((p,i,all)=>all.findIndex(x=>JSON.stringify(x)===JSON.stringify(p))===i)};
    })()
    """;
    public ValueTask DisposeAsync(){socket.Dispose();return ValueTask.CompletedTask;}
}
