using System.Globalization;
using System.Text;
namespace Repricing.Api;
public static class CsvExport
{
    public static string SafeText(string value)
    {
        var trimmed=value.TrimStart();
        return value.Length>0 && ("\t\r\n".Contains(value[0]) || trimmed.Length>0 && "=+-@".Contains(trimmed[0]))?"'"+value:value;
    }
    static string Quote(string value)=>"\""+SafeText(value).Replace("\"","\"\"")+"\"";
    public static byte[] Export(JobDetail job)
    {
        var text=new StringBuilder("源工作表,源行号,店铺,站点,SKU,调价动作,目标售价,模式,结果,处理路径,说明,商品ID,基础价原文,当前售价,目标差额,读取时间,活动详情,账号核验说明,改前活动价,提交活动ID,商品确认方式,执行检查点,尝试次数,执行轮次,提价提交阶段,报名报价,原价提交阶段,改前原价,计划新原价,重新报名活动ID\r\n");
        foreach(var row in job.Rows)
        {
            var input=row.Source.Input;
            string Raw(params string[] keys)=>keys.Select(k=>row.Source.Original.GetValueOrDefault(k)).FirstOrDefault(v=>v!=null)??"";
            string Mapped(string field,params string[] keys)=>row.Source.Mapped?.GetValueOrDefault(field)??Raw(keys);
            string[] values=[row.Source.Sheet,row.Source.SourceRow.ToString(),input?.StoreName??input?.Store??Mapped("store","店铺","store"),input?.Site??Mapped("site","站点","site"),input?.Sku??Mapped("sku","SKU","sku"),input?.Action??Mapped("action","调价动作","action"),input?.TargetPrice.ToString(CultureInfo.InvariantCulture)??Mapped("targetPrice","目标售价","target_price"),job.Summary.Mode==ExecutionMode.WeekendEnrollment?$"周末活动报名 {job.Summary.Weekend?.Label}":job.Summary.Mode==ExecutionMode.Simulation?"模拟":job.Summary.Mode==ExecutionMode.PriceCut?(job.Summary.MixedPrices?"实际调价":"实际降价"):"只读核对",row.Checkpoint=="subsidy-income-kept"?"已保留高收益补贴":Labels.Row(row.Status),row.Route,row.Message,row.Observation?.ItemId??"",row.Observation?.BasePriceText??"",row.Observation?.CurrentPrice?.ToString(CultureInfo.InvariantCulture)??"",row.Observation?.Difference?.ToString(CultureInfo.InvariantCulture)??"",row.Observation?.CheckedAt.ToString("O")??"",row.Observation==null?"":string.Join("；",row.Observation.Promotions.Select(p=>$"{p.Id} {p.Name} {p.Status} {p.PriceText}")),row.Observation?.IdentityNote??"",row.PriceCut?.Before.ToString(CultureInfo.InvariantCulture)??"",row.PriceCut?.PromotionId??"",row.PriceCut?.IdentityMethod??"",row.Checkpoint,row.Attempts.ToString(),job.Summary.Round.ToString(),row.PriceCut?.Rejoin==true?row.PriceCut.Stage:"",row.PriceCut?.OfferPrice?.ToString(CultureInfo.InvariantCulture)??"",row.PriceCut?.ChangeBase==true?row.PriceCut.Stage:"",row.PriceCut?.ChangeBase==true?row.PriceCut.BasePrice.ToString(CultureInfo.InvariantCulture):"",row.PriceCut?.DiscountBase?.ToString(CultureInfo.InvariantCulture)??"",row.PriceCut?.DiscountPromotionId??""];
            text.AppendLine(string.Join(',',values.Select(Quote)));
        }
        return [..Encoding.UTF8.GetPreamble(),..Encoding.UTF8.GetBytes(text.ToString())];
    }
}
public static class Labels
{
    public static string Row(RowStatus s)=>s switch{RowStatus.Invalid=>"数据异常",RowStatus.RetryPending=>"待下轮重试",RowStatus.Pending=>"待执行",RowStatus.Running=>"执行中",RowStatus.Inspected=>"指定商品已读取（未改价）",RowStatus.Simulated=>"模拟完成",RowStatus.Succeeded=>"实际成功",RowStatus.Skipped=>"已跳过",RowStatus.Failed=>"执行失败",_=>"待核对"};
}
