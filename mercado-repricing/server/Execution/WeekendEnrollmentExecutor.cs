namespace Repricing.Execution;

public class WeekendEnrollmentExecutor(IZiniaoPriceCutClient client)
{
    public async Task<ExecutionResult> ExecuteAsync(PriceInput input,WeekendPeriod period,Action<PriceCutPlan> checkpoint,CancellationToken ct,PriceCutPlan? previous=null,IReadOnlyList<PriceInput>? batchInputs=null)
    {
        ExecutionResult Review(string message,bool retry=false)=>new(RowStatus.NeedsReview,"周末活动报名",message,"weekend-review",Retryable:retry);
        bool submitted=false;
        try
        {
            if(input.Site!="MLB"||input.Currency!="BRL"||input.AllowExit||input.AllowBaseChange||input.BasePrice!=null||input.VariationId!=null||input.Action!=null)return Review("周末报名只接受 SKU 与报名价格，不执行调价动作、退出或改原价。");
            if(DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime)>period.End)return Review($"本批活动 {period.Label} 已过期，请重新上传本周末报名表。");
            var stores=await ZiniaoReadOnlyExecutor.FindStoresAsync(client,input.Store,ct);if(stores.Length!=1)return Review("店铺配置未唯一确认。",true);
            if(previous!=null&&(previous.Stage!="weekend-enroll"||previous.Input!=input||previous.Store.Id!=stores[0].Id))return Review("原报名检查点与本行不一致，未提交。");
            ZiniaoProduct[] products;
            try{products=await client.ReadWeekendAsync(stores[0],previous==null?input:input with{ItemId=previous.Product.ItemId},ct);}
            catch(AppException e)when(e.Message.Contains("超时：等待分组子商品展开",StringComparison.Ordinal))
            {
                var managed=await client.ReadManagerAsync(stores[0],input,ct);
                var blocked=managed.Where(p=>p.Sku==input.Sku&&(input.ItemId==null||p.ItemId==input.ItemId)&&ZiniaoReadOnlyExecutor.UnderReview(p)).ToArray();
                if(blocked.Length!=1)throw;
                var p=blocked[0];
                return new(RowStatus.NeedsReview,"周末活动报名","商品需审核未激活，促销页未返回子商品："+p.ListingStatus,"weekend-review",new(stores[0].Id,p.ItemId,p.Title,p.BasePriceText,null,null,p.Promotions,DateTimeOffset.UtcNow));
            }
            if(previous==null&&input.ItemId==null&&products.Any(p=>p.Sku==input.Sku&&p.SharedSkus is {Length:>1}))
            {
                var managed=await client.ReadManagerAsync(stores[0],input,ct);
                var children=managed.Where(p=>p.Sku==input.Sku&&p.SharedSkus is not {Length:>1}&&System.Text.RegularExpressions.Regex.IsMatch(p.ItemId,@"^\d{1,30}$")&&!products.Any(old=>old.ItemId==p.ItemId&&old.SharedSkus is {Length:>1})).ToArray();
                if(children.Length>0)
                {
                    var resolved=new List<ZiniaoProduct>();
                    foreach(var child in children)
                    {
                        var fresh=await client.ReadWeekendAsync(stores[0],input with{ItemId=child.ItemId},ct);
                        var exact=fresh.Where(p=>p.ItemId==child.ItemId&&p.Sku==input.Sku&&p.SharedSkus is not {Length:>1}).ToArray();
                        if(exact.Length==0)return Review("已展开找到独立SKU商品，但活动页尚未核对一致，未按旧父链接报名。",true);
                        resolved.AddRange(exact);
                    }
                    products=resolved.ToArray();
                }
            }
            var groups=products.Where(p=>p.Sku==input.Sku&&(input.ItemId==null||p.ItemId==input.ItemId)).GroupBy(p=>p.ItemId).ToArray();
            if(groups.Any(g=>g.Select(p=>System.Text.Json.JsonSerializer.Serialize(p)).Distinct().Count()>1))return Review("同一商品重复展示的活动或价格不一致，未选择其中一条报名。");
            var rows=groups.Select(g=>g.First()).ToArray();
            if(rows.Length==0)return previous==null?new(RowStatus.Skipped,"周末活动报名","该店铺未找到指定 SKU，未报名。","weekend-missing-sku"):Review("原提交商品暂未读取到，先保留报名结果未知。",true);
            if(rows.Length>1&&previous==null&&input.ItemId==null)return new(RowStatus.Skipped,"按商品拆分","同 SKU 的独立商品分别报名及核验。","weekend-split",SplitInputs:rows.Select(p=>input with{ItemId=p.ItemId}).ToArray());
            if(rows.Length!=1)return Review("SKU 未对应唯一商品，未报名。");var product=rows[0];
            PriceObservation Observe()=>new(stores[0].Id,product.ItemId,product.Title,product.BasePriceText,null,null,product.Promotions,DateTimeOffset.UtcNow);
            ExecutionResult ProductReview(string message,bool retry=false)=>new(RowStatus.NeedsReview,"周末活动报名",message,"weekend-review",Observe(),Retryable:retry);
            if(product.SharedSkus is {Length:>1} siblings)
            {
                if(!siblings.Contains(input.Sku,StringComparer.Ordinal))return ProductReview("旧式共享链接未包含当前 SKU，未按父链接报名。");
                var sharedRows=(batchInputs??[]).Where(r=>r.Store==input.Store&&r.Site==input.Site&&r.Currency==input.Currency&&siblings.Contains(r.Sku,StringComparer.Ordinal)&&(r.ItemId==null||r.ItemId==product.ItemId)).ToArray();
                var missing=siblings.Where(s=>!sharedRows.Any(r=>r.Sku==s)).ToArray();
                if(missing.Length>0)return ProductReview($"旧式共享链接需按父链接统一报名，但本批缺少关联 SKU：{string.Join("、",missing)}；未提交。");
                if(sharedRows.Select(r=>r.TargetPrice).Distinct().Count()!=1)return ProductReview("旧式共享链接由多个 SKU 共用报名价格，但本批目标价不一致；未提交。");
            }
            if(ZiniaoReadOnlyExecutor.UnderReview(product))return ProductReview("商品需审核，尚未激活，未报名。");
            var campaigns=product.Promotions.Where(p=>WeekendActivity.Matches(p,period)&&(p.Joinable||p.Active||ZiniaoReadOnlyExecutor.Scheduled(p))).ToArray();
            if(campaigns.Length!=1)return ProductReview(campaigns.Length==0?$"未找到日期为 {period.Label} 的普通活动，未选其他活动。":$"发现多个日期为 {period.Label} 的普通活动，无法唯一确定，未报名。");
            var campaign=campaigns[0];
            bool Enrolled(ZiniaoProduct p)=>p.ItemId==product.ItemId&&p.Sku==input.Sku&&!ZiniaoReadOnlyExecutor.UnderReview(p)&&ZiniaoReadOnlyExecutor.Money(product.BasePriceText) is decimal expectedBase&&ZiniaoReadOnlyExecutor.Money(p.BasePriceText)==expectedBase&&p.Promotions.Count(x=>x.Id==campaign.Id&&WeekendActivity.Matches(x,period)&&(x.Active||ZiniaoReadOnlyExecutor.Scheduled(x))&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==input.TargetPrice)==1;
            ExecutionResult Done(ZiniaoProduct p)=>new(previous==null&&!submitted?RowStatus.Skipped:RowStatus.Succeeded,"周末活动报名",$"活动 {period.Label} 已按 BRL {input.TargetPrice:0.00} 报名并核验（激活或已预约）。报名成功不代表当前售价已切换，其他活动未退出。","weekend-verified",new(stores[0].Id,p.ItemId,p.Title,p.BasePriceText,null,null,p.Promotions,DateTimeOffset.UtcNow));
            if(Enrolled(product))return Done(product);
            if(previous!=null)
            {
                string Preserved(ZiniaoProduct p)=>System.Text.Json.JsonSerializer.Serialize(p.Promotions.Where(x=>x.Active||ZiniaoReadOnlyExecutor.Scheduled(x)).OrderBy(x=>x.Id).Select(x=>new{x.Id,x.Status,x.PriceText}));
                bool StillUnsubmitted(ZiniaoProduct p)=>p.ItemId==previous.Product.ItemId&&p.Sku==input.Sku&&ZiniaoReadOnlyExecutor.Money(p.BasePriceText)==previous.BasePrice&&!ZiniaoReadOnlyExecutor.UnderReview(p)&&Preserved(p)==Preserved(previous.Product)&&p.Promotions.Count(x=>x.Id==previous.PromotionId&&WeekendActivity.Matches(x,period)&&x.Joinable&&!x.Active&&!ZiniaoReadOnlyExecutor.Scheduled(x)&&ZiniaoReadOnlyExecutor.Money(x.PriceText)==ZiniaoReadOnlyExecutor.Money(previous.Product.Promotions.Single(y=>y.Id==previous.PromotionId).PriceText))==1;
                if(!StillUnsubmitted(product))return ProductReview("上次周末报名已记录提交，但未回读到同活动目标价；保存结果未知，不重复报名。",true);
                await Task.Delay(1500,ct);
                var reread=await client.ReadWeekendAsync(stores[0],input with{ItemId=product.ItemId},ct);
                var exact=reread.Where(p=>p.ItemId==product.ItemId&&p.Sku==input.Sku).ToArray();
                if(exact.Length==1&&Enrolled(exact[0]))return Done(exact[0]);
                if(exact.Length!=1||!StillUnsubmitted(exact[0])||System.Text.Json.JsonSerializer.Serialize(exact[0])!=System.Text.Json.JsonSerializer.Serialize(product))return ProductReview("两次回读未能确认原报名未生效，保留结果未知，不重复报名。",true);
            }
            if(campaign.Active||ZiniaoReadOnlyExecutor.Scheduled(campaign))return ProductReview($"该周末活动已报名，但价格不是目标 {input.TargetPrice:0.00}；未退出重报或修改其他活动。");
            var basePrice=ZiniaoReadOnlyExecutor.Money(product.BasePriceText);var offer=ZiniaoReadOnlyExecutor.Money(campaign.PriceText);
            if(basePrice!=null&&input.TargetPrice>=basePrice)return ProductReview($"表格报名价 BRL {input.TargetPrice:0.00} 不低于当前原价 BRL {basePrice:0.00}，无法作为折扣价报名；未改原价。 ");
            if(!campaign.Joinable||basePrice==null||offer==null||input.TargetPrice<=0||decimal.Round(input.TargetPrice,2)!=input.TargetPrice)return ProductReview("本周末活动不可报名或价格不是明确有效金额；未改原价。");
            var plan=previous??new PriceCutPlan(stores[0],input,product,campaign.Id,basePrice.Value,basePrice.Value,Join:true,Stage:"weekend-enroll");
            try{await client.PrepareAsync(plan,ct);}
            catch(AppException e)when(e.Status==422){return ProductReview(e.Message);}
            checkpoint(plan);submitted=true;await client.SubmitAsync(plan,ct);
            for(var i=0;i<8;i++)
            {
                await Task.Delay(1500,ct);var fresh=await client.ReadWeekendAsync(stores[0],input with{ItemId=product.ItemId},ct);
                var matching=fresh.Where(p=>p.ItemId==product.ItemId&&p.Sku==input.Sku).ToArray();if(matching.Length==1&&Enrolled(matching[0]))return Done(matching[0]);
            }
            return Review("已提交周末报名，但尚未回读到同活动目标价；下轮先核对，不盲目重交。",true);
        }
        catch(VerificationRequiredException){throw;}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is AppException or IOException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
        {return Review(submitted?"周末报名提交后读取中断，结果未知；下轮先回读。":e is AppException?e.Message:"周末活动读取或连接中断，未继续提交。",e is not AppException{Status:422});}
        finally{await client.CloseEditorAsync();}
    }
}
