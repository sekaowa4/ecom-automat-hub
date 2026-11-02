using System.Globalization;
using System.Text.Json;
namespace Repricing.Dashboard;

public record StrategyContribution(string Name,int SkuCount,decimal Sales,decimal? Profit,decimal? Margin);
public record OrderContribution(string Name,int Orders,int SkuCount,decimal Sales,decimal? Profit,decimal? Margin,StrategyContribution[] Strategies);
public record OrderComparison(OrderContribution[] Groups,int ExcludedOrders,string[] Issues);

public static class OrderComparisonMetrics
{
    sealed class Bucket{public decimal Sales,Profit;public bool Missing;public HashSet<string> Skus=[];}
    public static string Strategy(string sku,IReadOnlyDictionary<string,string> strategies)=>strategies.GetValueOrDefault(sku) switch{"紧急清仓" or "重要清仓" or "一般清仓"=>"清仓","强制起量"=>"强制起量",_=>"其他"};
    public static OrderComparison Build(IEnumerable<JsonElement> rows,IReadOnlyDictionary<string,decimal?> margins,IReadOnlyDictionary<string,string> strategies)
    {
        string[] names=["Full","非Full"],labels=["清仓","强制起量","其他"];var buckets=names.ToDictionary(n=>n,_=>labels.ToDictionary(l=>l,_=>new Bucket()));var counts=names.ToDictionary(n=>n,_=>0);var seen=new HashSet<string>();var issues=new HashSet<string>();int excluded=0;
        foreach(var order in rows)
        {
            var id=Text(order,"orderId","platformOrderId","id");if(id==""||!seen.Add(id))throw new AppException("订单对比缺少唯一订单号或出现重复。",409);
            var flag=Text(order,"fulfillByPlatform");var group=flag switch{"1" or "true"=>"Full","0" or "false"=>"非Full",_=>throw new AppException("订单履约类型无法识别，未归入非Full。",409)};
            if(Text(order,"orderStatePlatform").StartsWith("cancelled",StringComparison.OrdinalIgnoreCase)||Text(order,"orderState")=="cancelled"||Text(order,"isVoided") is "1" or "true"){excluded++;continue;}
            if(!order.TryGetProperty("orderItemList",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength()==0)throw new AppException("订单对比缺少商品明细。",409);
            bool included=false;
            foreach(var item in items.EnumerateArray())
            {
                if(Text(item,"isCancel") is "1" or "true")continue;
                var sku=Text(item,"variationSku","productSku","sku").Trim().ToUpperInvariant();var price=Number(item,"price");var quantity=Number(item,"productCount");
                if(sku==""||price<0||quantity<=0||quantity!=decimal.Truncate(quantity))throw new AppException("订单对比缺少有效SKU、单价或件数。",409);
                included=true;var bucket=buckets[group][Strategy(sku,strategies)];var sales=price*quantity;bucket.Sales+=sales;bucket.Skus.Add(sku);
                if(!margins.TryGetValue(sku,out var margin)||margin==null){bucket.Missing=true;issues.Add(sku+"：预估毛利率缺失或冲突");}else bucket.Profit+=sales*margin.Value;
            }
            if(included)counts[group]++;else excluded++;
        }
        var groups=names.Select(name=>{
            var values=labels.Select(label=>{var b=buckets[name][label];decimal? profit=b.Missing?null:b.Profit;return new StrategyContribution(label,b.Skus.Count,b.Sales,profit,b.Sales>0?profit/b.Sales:null);}).ToArray();
            var sales=values.Sum(v=>v.Sales);decimal? profit=values.Any(v=>v.Profit==null)?null:values.Sum(v=>v.Profit!.Value);
            return new OrderContribution(name,counts[name],buckets[name].Values.SelectMany(b=>b.Skus).Distinct().Count(),sales,profit,sales>0?profit/sales:null,values);
        }).ToArray();return new(groups,excluded,issues.OrderBy(x=>x).ToArray());
    }
    static string Text(JsonElement e,params string[] fields){foreach(var f in fields)if(e.TryGetProperty(f,out var v)&&v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined&&v.ToString().Trim()!="")return v.ToString().Trim();return "";}
    static decimal Number(JsonElement e,string field)=>decimal.TryParse(Text(e,field),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:throw new AppException("订单对比数值字段无效："+field,409);
}
