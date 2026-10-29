using System.Text.RegularExpressions;
namespace Repricing.Execution;

public static class ZiniaoPortDiscovery
{
    public static int[] Parse(string table,IEnumerable<int> processIds)
    {
        var owners=processIds.ToHashSet();var ports=new HashSet<int>();
        foreach(var line in table.Split('\n'))
        {
            var row=Regex.Match(line,@"^\s*TCP\s+127\.0\.0\.1:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$",RegexOptions.CultureInvariant);
            if(row.Success&&int.TryParse(row.Groups[1].Value,out var port)&&port is >0 and <=65535&&int.TryParse(row.Groups[2].Value,out var pid)&&owners.Contains(pid))ports.Add(port);
        }
        return ports.Order().ToArray();
    }
}
