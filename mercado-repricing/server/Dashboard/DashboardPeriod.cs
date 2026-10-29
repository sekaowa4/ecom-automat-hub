namespace Repricing.Dashboard;

public record DashboardPeriod(DateOnly ReportDate,DateOnly MonthStart,int RemainingDays)
{
    public static DashboardPeriod For(DateOnly today,DateOnly? selectedDate=null)
    {
        var report=selectedDate??today.AddDays(-1);
        if(report>=today)throw new Repricing.AppException("只能查看已结束的完整日期。",400);
        return new(report,new(report.Year,report.Month,1),DateTime.DaysInMonth(report.Year,report.Month)-report.Day);
    }
}
