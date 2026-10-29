using HardwareLens.Core;
using Xunit;
namespace HardwareLens.Tests;

public class AppGrouperTests
{
    [Fact] public void Svchost_is_named_after_its_services()
    {
        var m = new ProcMeta { Resolved = true, Path = @"C:\Windows\System32\svchost.exe", InWindowsDir = true, Services = { "Windows Audio", "Bluetooth Support" } };
        Assert.Equal("Windows Audio and 1 more service", AppGrouper.DisplayName(100, "svchost.exe", m));
    }
    [Fact] public void Known_system_process_without_a_readable_path_gets_a_plain_name()
        => Assert.Equal("Desktop Window Manager", AppGrouper.DisplayName(200, "dwm.exe", new ProcMeta { Resolved = true, Path = null }));
    [Fact] public void Product_name_wins_over_exe_name()
        => Assert.Equal("Google Chrome", AppGrouper.DisplayName(300, "chrome.exe", new ProcMeta { Resolved = true, Path = @"C:\x\chrome.exe", ProductName = "Google Chrome®" }));
    [Fact] public void Unresolved_process_falls_back_to_exe_name()
        => Assert.Equal("mystery", AppGrouper.DisplayName(400, "mystery.exe", null));
}

public class HardwareCheckTests
{
    static Inventory Inv(params MemoryModule[] mods) => new Inventory { CollectedAt = DateTimeOffset.Now, Memory = new MemoryInfo { Modules = mods.ToList(), SlotsPopulated = mods.Length, SlotsTotal = 4 } };
    static MemoryModule Mod(int configured, int rated) => new() { CapacityBytes = 32UL << 30, ConfiguredSpeedMTs = configured, RatedSpeedMTs = rated };

    [Fact] public void Flags_memory_running_below_rated_speed() => Assert.Contains(HardwareChecks.Evaluate(Inv(Mod(4800, 6400), Mod(4800, 6400))), f => f.Title.Contains("slower"));
    [Fact] public void No_memory_finding_when_speeds_match() => Assert.DoesNotContain(HardwareChecks.Evaluate(Inv(Mod(6400, 6400), Mod(6400, 6400))), f => f.Title.Contains("slower"));
    [Fact] public void Single_module_is_informational_not_a_fault()
    {
        var f = HardwareChecks.Evaluate(Inv(Mod(6400, 6400))).Single(x => x.Title.Contains("one memory module"));
        Assert.Equal(ObservationLevel.Info, f.Level);
    }
}

public class ReportDiffTests
{
    [Fact] public void Reports_changed_added_and_removed_but_ignores_volatile_fields()
    {
        string a = "{\"generatedAt\":\"1\",\"cpu\":{\"model\":\"A\"},\"disks\":[{\"name\":\"d1\",\"size\":5},{\"name\":\"d2\",\"size\":6}],\"live\":{\"x\":1}}";
        string b = "{\"generatedAt\":\"2\",\"cpu\":{\"model\":\"B\"},\"disks\":[{\"name\":\"d1\",\"size\":5},{\"name\":\"d3\",\"size\":7}],\"live\":{\"x\":2}}";
        var d = ReportDiff.Compare(a, b);
        Assert.Contains(d, x => x.Kind == "changed" && x.Path.Contains("model") && x.Before == "A" && x.After == "B");
        Assert.Contains(d, x => x.Kind == "removed" && x.Path.Contains("d2"));
        Assert.Contains(d, x => x.Kind == "added" && x.Path.Contains("d3"));
        Assert.DoesNotContain(d, x => x.Path.Contains("generatedAt") || x.Path.Contains("live"));
    }
    [Fact] public void Identical_reports_have_no_differences() => Assert.Empty(ReportDiff.Compare("{\"a\":1}", "{\"a\":1}"));
}

public class FolderScannerTests
{
    [Fact] public void Adds_up_file_sizes_and_never_modifies_anything()
    {
        string root = Path.Combine(Path.GetTempPath(), "hl-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "big")); Directory.CreateDirectory(Path.Combine(root, "small"));
        File.WriteAllBytes(Path.Combine(root, "big", "a.bin"), new byte[5000]); File.WriteAllBytes(Path.Combine(root, "small", "b.bin"), new byte[100]); File.WriteAllBytes(Path.Combine(root, "c.bin"), new byte[10]);
        try
        {
            var n = FolderScanner.Scan(root, 2, CancellationToken.None);
            Assert.Equal(5110, n.Bytes); Assert.Equal(3, n.Files); Assert.Equal("big", n.Children[0].Name);
            Assert.True(File.Exists(Path.Combine(root, "big", "a.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void Scan_can_be_cancelled()
    {
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FolderScanner.Scan(Path.GetTempPath(), 1, cts.Token));
    }
}
