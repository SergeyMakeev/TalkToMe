using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace TalkToMe;

internal static class WindowsCrashEvents
{
    internal static string Report()
    {
        var reports = new List<string>();
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[(EventID=1000 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 604800000]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 200 && reports.Count < 5 && elapsed.Elapsed < TimeSpan.FromSeconds(2); i++)
            {
                using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(250));
                if (record == null) break;
                if (Summarize(record.ToXml()) is { } summary) reports.Add(summary);
            }
        }
        catch (Exception error) when (error is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        { return $"Windows Application Error records unavailable ({error.GetType().Name}; 0x{error.HResult:X8})."; }
        return reports.Count == 0 ? "No recent TalkToMe Application Error records found." : string.Join("\n\n", reports);
    }

    internal static string? Summarize(string xml)
    {
        var root = XElement.Parse(xml);
        XNamespace ns = root.Name.Namespace;
        var fields = root.Element(ns + "EventData")?.Elements(ns + "Data").ToArray() ?? [];
        if ((string?)root.Element(ns + "System")?.Element(ns + "EventID") == "1026")
        {
            var lines = string.Join("\n", fields.Select(f => f.Value)).Split('\n').Select(s => s.Trim()).ToArray();
            if (!lines.Contains("Application: TalkToMe.exe", StringComparer.OrdinalIgnoreCase)) return null;
            var stack = new List<string> { "Windows .NET Runtime error (1026); UTC: " +
                (string?)root.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime") };
            foreach (var line in lines)
            {
                if (line.StartsWith("Application:") || line.StartsWith("CoreCLR Version:") || line.StartsWith(".NET Version:")) stack.Add(line);
                else if (line.StartsWith("Exception Info:"))
                {
                    string type = line["Exception Info:".Length..].Trim().Split(':')[0];
                    if (type.All(c => char.IsAsciiLetterOrDigit(c) || "._+`[]".Contains(c))) stack.Add("Exception type: " + type);
                }
                else if (line.StartsWith("at ")) stack.Add(line.Split(" in ", 2)[0]);
            }
            return string.Join("\n", stack.Take(80));
        }
        string[] names = ["AppName", "AppVersion", "AppTimeStamp", "ModuleName", "ModuleVersion", "ModuleTimeStamp",
            "ExceptionCode", "FaultingOffset", "ProcessId", "ProcessCreationTime"];
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Length; i++)
        {
            string? name = (string?)fields[i].Attribute("Name") ?? (i < names.Length ? names[i] : null);
            if (name != null && names.Contains(name, StringComparer.OrdinalIgnoreCase)) data[name] = fields[i].Value;
        }
        if (!data.TryGetValue("AppName", out var app) || !(app.Equals("TalkToMe.exe", StringComparison.OrdinalIgnoreCase) ||
            app.Equals("TalkToMe.FocusHost.exe", StringComparison.OrdinalIgnoreCase))) return null;
        var result = new List<string> { "Windows Application Error (1000); UTC: " +
            (string?)root.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime") };
        foreach (string name in names)
            if (data.TryGetValue(name, out var value)) result.Add(name + ": " + (name == "ModuleName" ? Path.GetFileName(value) : value));
        return string.Join("\n", result);
    }
}
