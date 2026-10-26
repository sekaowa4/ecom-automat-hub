namespace Repricing.Execution;
public class SimulationExecutor(int delayMs=650) : IPriceExecutor
{
    public async Task<ExecutionResult> ExecuteAsync(PriceInput input,CancellationToken ct)
    {
        if(delayMs>0)await Task.Delay(delayMs,ct);
        if(input.TargetPrice==80)return new(RowStatus.Skipped,"无需调整","模拟：目标价与演示促销价 80 相同，已跳过。","simulation-skipped");
        if(input.TargetPrice is >=20 and <=95)
        {
            if(input.TargetPrice<80)return new(RowStatus.Simulated,"活动内降价","模拟完成：修改活动价；未操作真实店铺。","simulation-verified");
            if(input.AllowExit)return new(RowStatus.Simulated,"退出后重新报名","模拟完成：退出活动后重新报名；未操作真实店铺。","simulation-verified");
            return new(RowStatus.NeedsReview,"退出后重新报名","模拟待处理：需要明确允许退出活动。","permission-required");
        }
        if(input.AllowBaseChange&&input.BasePrice.HasValue)return new(RowStatus.Simulated,"修改基础售价","模拟完成：修改基础售价；活动及最终售价仍需联调验证。","simulation-base-only");
        return new(RowStatus.NeedsReview,"修改基础售价","模拟待处理：目标超出演示活动范围 20—95，需允许修改基础售价并填写新基础价。","base-rule-required");
    }
}
