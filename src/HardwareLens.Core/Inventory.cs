using System.Text.Json.Serialization;
namespace HardwareLens.Core;

/// <summary>Describes how one data source behaved during collection (shown in the app and exported).</summary>
public sealed class SourceStatus
{
    public string Name { get; set; } = "";
    public string Api { get; set; } = "";
    /// <summary>ok | partial | unavailable</summary>
    public string State { get; set; } = "ok";
    public string? Note { get; set; }
    public SourceStatus() { }
    public SourceStatus(string name, string api, string state, string? note = null) { Name = name; Api = api; State = state; Note = note; }
}

public sealed class UnavailableField
{
    public string Field { get; set; } = "";
    public string Reason { get; set; } = "";
    public UnavailableField() { }
    public UnavailableField(string field, string reason) { Field = field; Reason = reason; }
}

public sealed class Inventory
{
    public DateTimeOffset CollectedAt { get; set; }
    public double CollectionSeconds { get; set; }
    public string Elevation { get; set; } = "Standard user";
    public SystemInfo System { get; set; } = new();
    public List<CpuInfo> Cpus { get; set; } = new();
    public BoardInfo Board { get; set; } = new();
    public BiosInfo Bios { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public int HiddenSoftwareAdapters { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public int HiddenEmptyReaders { get; set; }
    public List<NicInfo> Nics { get; set; } = new();
    public BatteryInfo? Battery { get; set; }
    public string? BatteryNote { get; set; }
    public List<SourceStatus> Sources { get; set; } = new();
    public List<UnavailableField> Unavailable { get; set; } = new();
    /// <summary>Serial numbers, MAC addresses and similar. Never serialized by default; exported only on explicit request.</summary>
    [JsonIgnore] public Dictionary<string, string> Identifiers { get; set; } = new();
}

public sealed class SystemInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? FormFactor { get; set; }
    public string? OsName { get; set; }
    public string? OsEdition { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? OsArchitecture { get; set; }
    public DateTimeOffset? LastBoot { get; set; }
    public double? UptimeSeconds { get; set; }
    public ulong? UsableMemoryBytes { get; set; }
}

public sealed class CpuInfo
{
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    public int? Cores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? MaxClockMHz { get; set; }
    public string? Socket { get; set; }
    public int? L2CacheKB { get; set; }
    public int? L3CacheKB { get; set; }
}

public sealed class BoardInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Version { get; set; }
}

public sealed class BiosInfo
{
    public string? Vendor { get; set; }
    public string? Version { get; set; }
    public string? SmbiosVersion { get; set; }
    public DateTime? ReleaseDate { get; set; }
}

public sealed class MemoryInfo
{
    public ulong? TotalModuleBytes { get; set; }
    public int? SlotsTotal { get; set; }
    public int SlotsPopulated { get; set; }
    public ulong? MaxCapacityBytes { get; set; }
    public string? Type { get; set; }
    public List<MemoryModule> Modules { get; set; } = new();
}

public sealed class MemoryModule
{
    public string? Slot { get; set; }
    public string? Bank { get; set; }
    public string? Manufacturer { get; set; }
    public string? ManufacturerNote { get; set; }
    public string? PartNumber { get; set; }
    public ulong? CapacityBytes { get; set; }
    /// <summary>Configured (running) speed in mega-transfers per second (MT/s), SMBIOS "Configured Memory Speed".</summary>
    public int? ConfiguredSpeedMTs { get; set; }
    /// <summary>Module's rated speed in MT/s, SMBIOS "Speed".</summary>
    public int? RatedSpeedMTs { get; set; }
    public string? Type { get; set; }
    public string? FormFactor { get; set; }
    public int? ConfiguredVoltageMv { get; set; }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string? Vendor { get; set; }
    public string? PciVendorId { get; set; }
    public string? PciDeviceId { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public ulong? DedicatedVramBytes { get; set; }
    public ulong? SharedMemoryBytes { get; set; }
    /// <summary>Discrete | Integrated | Unknown</summary>
    public string Architecture { get; set; } = "Unknown";
    public string? ArchitectureNote { get; set; }
    public bool IsSoftware { get; set; }
    /// <summary>Normalized adapter LUID, e.g. 0x00000000_0x0000C1F3 — matches GPU performance counter instance names.</summary>
    public string LuidKey { get; set; } = "";
}

public sealed class DiskInfo
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    /// <summary>SSD | HDD | SCM | Not reported by Windows</summary>
    public string MediaType { get; set; } = "Not reported by Windows";
    public string Connection { get; set; } = "Unknown";
    public bool IsExternalOrRemovable { get; set; }
    public ulong SizeBytes { get; set; }
    public string? PartitionStyle { get; set; }
    public string? Firmware { get; set; }
    public int? SpindleRpm { get; set; }
    public string? WindowsHealthStatus { get; set; }
    public string? OperationalStatus { get; set; }
    public bool MediaPresent { get; set; } = true;
    /// <summary>From Windows' storage reliability counters. Null when the drive or driver does not report it (some need administrator rights).</summary>
    public int? TemperatureC { get; set; }
    public int? WearPercentUsed { get; set; }
    public uint? PowerOnHours { get; set; }
    public uint? ReadErrorsTotal { get; set; }
    public uint? WriteErrorsTotal { get; set; }
    public string? ReliabilityNote { get; set; }
    public List<VolumeInfo> Volumes { get; set; } = new();
}

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public ulong SizeBytes { get; set; }
    public ulong FreeBytes { get; set; }
}

public sealed class NicInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "Other";
    public string Status { get; set; } = "Unknown";
    public bool IsPhysical { get; set; }
    public bool IsVirtual { get; set; }
    public ulong? ReceiveLinkBps { get; set; }
    public ulong? TransmitLinkBps { get; set; }
}

public sealed class BatteryInfo
{
    public string? Name { get; set; }
    public int? ChargePercent { get; set; }
    public string? Status { get; set; }
    public string? Chemistry { get; set; }
    public int? DesignCapacityMWh { get; set; }
    public int? FullChargeCapacityMWh { get; set; }
    public double? WearPercent { get; set; }
    public int? CycleCount { get; set; }
    public int? EstimatedRuntimeMinutes { get; set; }
}
