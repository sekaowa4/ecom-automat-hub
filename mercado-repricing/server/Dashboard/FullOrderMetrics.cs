using System.Globalization;
using System.Text.Json;

namespace Repricing.Dashboard;

public record FullOrderSummary(decimal Sales,int Orders,int SkuCount,RankedSku[] Ranking);

public static class FullOrderMetrics
{
    public static FullOrderSummary Build(JsonElement data,IReadOnlyDictionary<string,string> categories,IReadOnlyDictionary<string,decimal?> margins)
    {
        if(!data.TryGetProperty("list",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw new Repricing.AppException("UpSeller Full 订单列表格式无法识别。",502);
        return Build(rows.EnumerateArray().ToArray(),categories,margins);
    }
    public static FullOrderSummary Build(IEnumerable<JsonElement> rows,IReadOnlyDictionary<string,string> categories,IReadOnlyDictionary<string,decimal?> margins)
    {
        var orders=new HashSet<string>(StringComparer.Ordinal);var grouped=new Dictionary<string,(string Name,decimal Sales,int Units,HashSet<string> Orders)>(StringComparer.Ordinal);decimal total=0;
        foreach(var order in rows)
        {
            if(!order.TryGetProperty("fulfillByPlatform",out var full))throw new Repricing.AppException("UpSeller Full 订单缺少履约类型字段，未确认数据范围。",409);
            if(full.ToString() is "0" or "false")continue;
            if(full.ToString() is not "1" and not "true")throw new Repricing.AppException("UpSeller Full 订单履约类型无法识别。",409);
            var orderId=Text(order,"orderId","platformOrderId","orderNo","orderNumber","id");
            if(orderId==""||!orders.Add(orderId))throw new Repricing.AppException("UpSeller Full 订单缺少唯一订单号或分页重复，未使用不完整数据。",409);
            if(!order.TryGetProperty("orderItemList",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength()==0)throw new Repricing.AppException("UpSeller Full 订单缺少商品明细，未生成 SKU 销售额。",409);
            foreach(var item in items.EnumerateArray())
            {
                var sku=Text(item,"variationSku","sku","productSku").Trim().ToUpperInvariant();
                var name=Text(item,"productName","productTitle","itemName","variationName","title","name");
                var quantity=Number(item,"productCount","quantity","count","orderUnit");var price=Number(item,"price","unitPrice","salePrice","productPrice","itemPrice");
                if(sku==""||quantity<=0||quantity!=decimal.Truncate(quantity)||price<0)throw new Repricing.AppException("UpSeller Full 商品明细缺少有效 SKU、单价或件数。",409);
                var units=checked((int)quantity);var sales=price*quantity;total+=sales;
                if(grouped.TryGetValue(sku,out var current))
                {
                    current.Orders.Add(orderId);grouped[sku]=(current.Name==""?name:current.Name,current.Sales+sales,checked(current.Units+units),current.Orders);
                }
                else grouped[sku]=(name,sales,units,[orderId]);
            }
        }
        var ranking=grouped.Select(pair=>new RankedSku(pair.Key,pair.Value.Name==""?pair.Key:pair.Value.Name,pair.Value.Sales,pair.Value.Orders.Count,pair.Value.Units,margins.GetValueOrDefault(pair.Key),categories.GetValueOrDefault(pair.Key,"未分类"))).OrderByDescending(r=>r.Sales).ThenBy(r=>r.Sku).ToArray();
        return new(total,orders.Count,ranking.Length,ranking);
    }

    static string Text(JsonElement row,params string[] fields)
    {
        foreach(var field in fields)if(row.TryGetProperty(field,out var value)&&value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var text=value.ToString().Trim();if(text!="")return text;
        }
        return "";
    }
    static decimal Number(JsonElement row,params string[] fields)
    {
        foreach(var field in fields)if(row.TryGetProperty(field,out var value)&&decimal.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var number))return number;
        throw new Repricing.AppException("UpSeller Full 商品明细缺少有效数值字段："+fields[0],409);
    }
}
