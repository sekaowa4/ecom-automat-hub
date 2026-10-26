using System.Text.RegularExpressions;
namespace Repricing.Execution;

public static class WeekendActivity
{
    public static WeekendPeriod Current(DateTimeOffset now)
    {
        var date=DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var monday=date.AddDays(-(((int)date.DayOfWeek+6)%7));
        return new(monday.AddDays(4),monday.AddDays(6));
    }
    public static bool Matches(PromotionObservation p,WeekendPeriod period)
    {
        if(!p.Id.StartsWith("C-",StringComparison.Ordinal)||Regex.IsMatch(p.Name,"新提案|Nova proposta|New proposal|Nova oferta|限时抢购|Relâmpago",RegexOptions.IgnoreCase))return false;
        // Translated DOM can concatenate the campaign title and its date line.
        var name=Regex.Replace(p.Name,@"^(\d{4}\s*[-–—]\s*\d{4}|\d{1,2}[./]\d{1,2}\s*[-–—]\s*\d{1,2}[./]\d{1,2})(?=\d{1,2}(?:至|到|日|/))","$1\n");
        var title=name.Split('\n')[0].Trim();
        var range=Regex.Match(title,@"^(?<m1>\d{1,2})[./](?<d1>\d{1,2})\s*[-–—]\s*(?<m2>\d{1,2})[./](?<d2>\d{1,2})$");
        if(!range.Success)range=Regex.Match(title,@"^(?<m1>\d{2})(?<d1>\d{2})\s*[-–—]\s*(?<m2>\d{2})(?<d2>\d{2})$");
        if(!range.Success)return false;
        bool Equal(string key,int n)=>int.Parse(range.Groups[key].Value)==n;
        if(!Equal("m1",period.Start.Month)||!Equal("d1",period.Start.Day)||!Equal("m2",period.End.Month)||!Equal("d2",period.End.Day))return false;
        // If a numeric day/month date line is present, it must agree with the generated title.
        var details=string.Join('\n',name.Split('\n').Skip(1));
        var dates=Regex.Matches(details,@"(?<!\d)(\d{1,2})/(\d{1,2})(?:/(\d{4}))?(?!\d)");
        var dayRange=Regex.Match(details,@"(?<!\d)(?<d1>\d{1,2})\s*(?:日)?\s*(?:至|到|[-–—])\s*(?<d2>\d{1,2})\s*/\s*(?<m>\d{1,2})\s*月(?!\d)");
        if(dayRange.Success)
        {
            if(int.Parse(dayRange.Groups["d1"].Value)!=period.Start.Day||int.Parse(dayRange.Groups["d2"].Value)!=period.End.Day||int.Parse(dayRange.Groups["m"].Value)!=period.End.Month||period.Start.Month!=period.End.Month)return false;
            return dates.Count switch{0=>true,1=>DateMatches(dates[0],period.End),2=>DateMatches(dates[0],period.Start)&&DateMatches(dates[1],period.End),_=>false};
        }
        if(dates.Count==0)return true;
        if(dates.Count!=2)return false;
        bool DateMatches(Match m,DateOnly date)=>int.Parse(m.Groups[1].Value)==date.Day&&int.Parse(m.Groups[2].Value)==date.Month&&(!m.Groups[3].Success||int.Parse(m.Groups[3].Value)==date.Year);
        return DateMatches(dates[0],period.Start)&&DateMatches(dates[1],period.End);
    }
}
