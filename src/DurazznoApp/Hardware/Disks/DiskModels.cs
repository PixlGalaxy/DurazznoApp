namespace Durazzno.Hardware.Disks;

internal enum DiskHealth
{
    Unknown,
    Good,
    Caution,
    Bad,
}

/// <summary>One ATA S.M.A.R.T. attribute (attribute table + threshold table).</summary>
internal sealed record SmartAttribute(
    byte Id,
    string Name,
    byte Current,
    byte Worst,
    byte Threshold,
    ulong Raw,
    bool PreFail)
{
    /// <summary>Normalized value at or below the vendor threshold: the drive itself predicts failure.</summary>
    public bool IsFailing => Threshold > 0 && Current <= Threshold;
}

/// <summary>NVMe SMART / Health Information log (log page 0x02).</summary>
internal sealed record NvmeHealthLog(
    byte CriticalWarning,
    int TemperatureC,
    byte AvailableSpare,
    byte AvailableSpareThreshold,
    byte PercentageUsed,
    UInt128 DataUnitsRead,
    UInt128 DataUnitsWritten,
    UInt128 HostReadCommands,
    UInt128 HostWriteCommands,
    UInt128 ControllerBusyMinutes,
    UInt128 PowerCycles,
    UInt128 PowerOnHours,
    UInt128 UnsafeShutdowns,
    UInt128 MediaErrors,
    UInt128 ErrorLogEntries,
    uint WarningTempMinutes,
    uint CriticalTempMinutes,
    int[] SensorTemperaturesC)
{
    /// <summary>NVMe "data units" are 1000 × 512 bytes.</summary>
    public static double UnitsToBytes(UInt128 units) => (double)units * 512_000d;
}

/// <summary>Everything Durazzno knows about one physical disk.</summary>
internal sealed class DiskReport
{
    public int Index { get; init; }
    public string Model { get; set; } = "";
    public string Vendor { get; init; } = "";
    public string Firmware { get; set; } = "";
    public string Serial { get; set; } = "";
    public string BusType { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool IsSsd { get; init; }
    public bool IsRemovable { get; init; }
    public bool? TrimEnabled { get; init; }
    public bool IsVirtual { get; init; }
    public List<string> DriveLetters { get; init; } = [];

    public int? TemperatureC { get; set; }
    public int? WarningTemperatureC { get; set; }
    public int? CriticalTemperatureC { get; set; }

    /// <summary>Set when S.M.A.R.T. could not be read (typically: needs administrator rights).</summary>
    public string? SmartError { get; set; }
    public bool SmartNeedsAdmin { get; set; }

    public SmartTransport Transport { get; set; }
    public List<SmartAttribute> Attributes { get; set; } = [];
    public NvmeHealthLog? Nvme { get; set; }

    public DiskHealth Health { get; set; } = DiskHealth.Unknown;
    public string HealthReason { get; set; } = "";
    /// <summary>Remaining life in percent, when the drive reports it.</summary>
    public int? LifePercent { get; set; }
    public ulong? PowerOnHours { get; set; }
    public ulong? PowerCycles { get; set; }
    public double? HostWritesBytes { get; set; }
    public double? HostReadsBytes { get; set; }

    public bool HasSmart => Attributes.Count > 0 || Nvme is not null;

    public string Kind => BusType == "NVMe" ? "NVMe SSD" : IsSsd ? "SSD" : IsVirtual ? "Virtual disk" : "HDD";
}
