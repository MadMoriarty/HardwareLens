using System.ServiceProcess;
using HardwareLens.Core;
namespace HardwareLens.Windows;

public sealed class ServiceRow
{
    public string Name = "", DisplayName = "", State = "", StartMode = "", Account = "", Description = "";
    public int Pid; public string? Path;
    public bool Running => State == "Running";
}

public static class ServiceManager
{
    // Services Windows itself depends on to stay up; HardwareLens will not stop them.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    { "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "BrokerInfrastructure", "CoreMessagingRegistrar", "SystemEventsBroker", "TimeBrokerSvc", "gpsvc", "ProfSvc", "Power", "PlugPlay", "EventLog", "Winmgmt", "nsi", "BFE", "mpssvc", "CryptSvc", "Dhcp", "Dnscache" };

    public static List<ServiceRow> List()
    {
        var res = new List<ServiceRow>();
        foreach (var r in Wmi.Query(@"root\cimv2", "SELECT Name, DisplayName, State, StartMode, ProcessId, Description, StartName, PathName FROM Win32_Service", 15))
            res.Add(new ServiceRow { Name = r.Str("Name") ?? "", DisplayName = r.Str("DisplayName") ?? r.Str("Name") ?? "", State = r.Str("State") ?? "", StartMode = r.Str("StartMode") ?? "", Pid = r.Int("ProcessId") ?? 0, Description = r.Str("Description") ?? "", Account = r.Str("StartName") ?? "", Path = r.Str("PathName") });
        return res.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? WhyNotStop(ServiceRow s) => Protected.Contains(s.Name) ? "Windows depends on this service. HardwareLens will not stop it." : null;

    public static (bool ok, string message) Control(ServiceRow s, string action)
    {
        if (!Elevation.IsElevated()) return (false, "Starting and stopping services needs administrator rights. Use “Restart as administrator”.");
        try
        {
            using var sc = new ServiceController(s.Name);
            var t = TimeSpan.FromSeconds(20);
            switch (action)
            {
                case "stop":
                    if (WhyNotStop(s) is { } why) return (false, why);
                    if (!sc.CanStop) return (false, "This service does not accept a stop request.");
                    sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, t); return (true, $"Stopped “{s.DisplayName}”.");
                case "start":
                    sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, t); return (true, $"Started “{s.DisplayName}”.");
                case "restart":
                    if (WhyNotStop(s) is { } why2) return (false, why2);
                    if (sc.Status != ServiceControllerStatus.Stopped) { if (!sc.CanStop) return (false, "This service does not accept a stop request."); sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, t); }
                    sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, t); return (true, $"Restarted “{s.DisplayName}”.");
            }
            return (false, "Unknown action.");
        }
        catch (System.ServiceProcess.TimeoutException) { return (false, "The service did not change state within 20 seconds."); }
        catch (Exception e) { return (false, "Windows refused: " + (e.InnerException?.Message ?? e.Message)); }
    }
}
