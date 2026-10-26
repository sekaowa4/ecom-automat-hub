namespace Repricing.Dashboard;

public record DashboardPeriod(DateOnly ReportDate,DateOnly MonthStart,int RemainingDays)
{
    public static DashboardPeriod For(DateOnly today)
    {
        var report=today.AddDays(-1);
        return new(report,new(report.Year,report.Month,1),DateTime.DaysInMonth(report.Year,report.Month)-report.Day);
    }
}
