namespace Durazzno.Hardware.Disks;

/// <summary>Turns raw S.M.A.R.T. data into a health verdict, the same way CrystalDiskInfo does.</summary>
internal static class SmartAnalysis
{
    // Raw counters that mean real damage when they are above zero (CrystalDiskInfo's "Caution" rule).
    private static readonly byte[] DamageCounters = [0x05, 0xC5, 0xC6];

    // Vendor attributes that report remaining SSD life as their normalized value (100 = new).
    private static readonly byte[] LifeAttributes = [0xE7, 0xA9, 0xCA, 0xE8, 0xAD, 0xB1];

    public static void Evaluate(DiskReport disk)
    {
        if (disk.Nvme is { } nvme)
            EvaluateNvme(disk, nvme);
        else if (disk.Attributes.Count > 0)
            EvaluateAta(disk);
        else
        {
            disk.Health = DiskHealth.Unknown;
            disk.HealthReason = disk.SmartError ?? "This drive does not report S.M.A.R.T. data.";
        }
    }

    private static void EvaluateNvme(DiskReport disk, NvmeHealthLog log)
    {
        disk.LifePercent = Math.Max(0, 100 - log.PercentageUsed);
        disk.PowerOnHours = (ulong)log.PowerOnHours;
        disk.PowerCycles = (ulong)log.PowerCycles;
        disk.HostWritesBytes = NvmeHealthLog.UnitsToBytes(log.DataUnitsWritten);
        disk.HostReadsBytes = NvmeHealthLog.UnitsToBytes(log.DataUnitsRead);
        disk.TemperatureC ??= log.TemperatureC;

        var w = log.CriticalWarning;
        if ((w & 0x01) != 0 || log.AvailableSpare < log.AvailableSpareThreshold)
            (disk.Health, disk.HealthReason) = (DiskHealth.Bad, "Spare capacity is below the drive's threshold.");
        else if ((w & 0x04) != 0)
            (disk.Health, disk.HealthReason) = (DiskHealth.Bad, "The drive reports degraded reliability.");
        else if ((w & 0x08) != 0)
            (disk.Health, disk.HealthReason) = (DiskHealth.Bad, "The drive has switched to read-only mode.");
        else if ((w & 0x02) != 0)
            (disk.Health, disk.HealthReason) = (DiskHealth.Caution, "Temperature is outside the safe range.");
        else if (log.MediaErrors > 0)
            (disk.Health, disk.HealthReason) = (DiskHealth.Caution, $"{log.MediaErrors} media / data integrity errors recorded.");
        else if (disk.LifePercent <= 10)
            (disk.Health, disk.HealthReason) = (DiskHealth.Caution, "Less than 10% of rated endurance left.");
        else
            (disk.Health, disk.HealthReason) = (DiskHealth.Good, "No problems reported by the drive.");
    }

    private static void EvaluateAta(DiskReport disk)
    {
        var byId = disk.Attributes.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());

        if (byId.TryGetValue(0x09, out var hours))
            disk.PowerOnHours = hours.Raw & 0xFFFFFFFF;
        if (byId.TryGetValue(0x0C, out var cycles))
            disk.PowerCycles = cycles.Raw & 0xFFFFFFFF;
        if (disk.TemperatureC is null && (byId.GetValueOrDefault((byte)0xC2) ?? byId.GetValueOrDefault((byte)0xBE)) is { } temp)
            disk.TemperatureC = (int)(temp.Raw & 0xFF);
        if (byId.TryGetValue(0xF1, out var written))
            disk.HostWritesBytes = written.Raw * 512d;
        if (byId.TryGetValue(0xF2, out var read))
            disk.HostReadsBytes = read.Raw * 512d;
        if (disk.IsSsd && LifeAttributes.Select(id => byId.GetValueOrDefault(id)).FirstOrDefault(a => a is not null) is { } life)
            disk.LifePercent = Math.Min(100, (int)life.Current);

        if (disk.Attributes.FirstOrDefault(a => a.IsFailing) is { } failing)
        {
            (disk.Health, disk.HealthReason) = (DiskHealth.Bad, $"{failing.Name} has crossed its failure threshold.");
            return;
        }

        var damaged = DamageCounters.Select(id => byId.GetValueOrDefault(id)).Where(a => a is { Raw: > 0 }).ToList();
        if (damaged.Count > 0)
            (disk.Health, disk.HealthReason) = (DiskHealth.Caution, string.Join(", ", damaged.Select(a => $"{a!.Name}: {a.Raw & 0xFFFFFFFF}")));
        else if (disk.LifePercent <= 10)
            (disk.Health, disk.HealthReason) = (DiskHealth.Caution, "Less than 10% of rated endurance left.");
        else
            (disk.Health, disk.HealthReason) = (DiskHealth.Good, "No problems reported by the drive.");
    }

    /// <summary>True for attributes whose raw value is worth highlighting when non-zero.</summary>
    public static bool IsDamageCounter(byte id) => DamageCounters.Contains(id);

    public static string AttributeName(byte id) => id switch
    {
        0x01 => "Read Error Rate",
        0x02 => "Throughput Performance",
        0x03 => "Spin-Up Time",
        0x04 => "Start/Stop Count",
        0x05 => "Reallocated Sectors Count",
        0x06 => "Read Channel Margin",
        0x07 => "Seek Error Rate",
        0x08 => "Seek Time Performance",
        0x09 => "Power-On Hours",
        0x0A => "Spin Retry Count",
        0x0B => "Recalibration Retries",
        0x0C => "Power Cycle Count",
        0x0D => "Soft Read Error Rate",
        0x16 => "Current Helium Level",
        0x64 => "Erase/Program Cycles",
        0xAA => "Available Reserved Space",
        0xAB => "Program Fail Count",
        0xAC => "Erase Fail Count",
        0xAD => "Wear Leveling Count",
        0xAE => "Unexpected Power Loss Count",
        0xAF => "Power Loss Protection Failure",
        0xB1 => "Wear Range Delta",
        0xB3 => "Used Reserved Block Count",
        0xB4 => "Unused Reserved Block Count",
        0xB5 => "Program Fail Count Total",
        0xB6 => "Erase Fail Count Total",
        0xB7 => "SATA Downshift Error Count",
        0xB8 => "End-to-End Error",
        0xBB => "Reported Uncorrectable Errors",
        0xBC => "Command Timeout",
        0xBD => "High Fly Writes",
        0xBE => "Airflow Temperature",
        0xBF => "G-Sense Error Rate",
        0xC0 => "Power-off Retract Count",
        0xC1 => "Load/Unload Cycle Count",
        0xC2 => "Temperature",
        0xC3 => "Hardware ECC Recovered",
        0xC4 => "Reallocation Event Count",
        0xC5 => "Current Pending Sector Count",
        0xC6 => "Uncorrectable Sector Count",
        0xC7 => "UltraDMA CRC Error Count",
        0xC8 => "Write Error Rate",
        0xC9 => "Soft Read Error Rate",
        0xCA => "Data Address Mark Errors",
        0xCB => "Run Out Cancel",
        0xCC => "Soft ECC Correction",
        0xCD => "Thermal Asperity Rate",
        0xCE => "Flying Height",
        0xCF => "Spin High Current",
        0xD0 => "Spin Buzz",
        0xD1 => "Offline Seek Performance",
        0xDC => "Disk Shift",
        0xDD => "G-Sense Error Rate",
        0xDE => "Loaded Hours",
        0xDF => "Load/Unload Retry Count",
        0xE0 => "Load Friction",
        0xE1 => "Load/Unload Cycle Count",
        0xE2 => "Load 'In'-time",
        0xE3 => "Torque Amplification Count",
        0xE4 => "Power-Off Retract Cycle",
        0xE6 => "GMR Head Amplitude / Drive Life Protection",
        0xE7 => "SSD Life Left",
        0xE8 => "Endurance Remaining",
        0xE9 => "Media Wearout Indicator",
        0xEA => "Average Erase Count",
        0xEB => "Good Block Count",
        0xF0 => "Head Flying Hours",
        0xF1 => "Total Host Writes",
        0xF2 => "Total Host Reads",
        0xF3 => "Total Host Writes (Expanded)",
        0xF4 => "Total Host Reads (Expanded)",
        0xF9 => "NAND Writes (1 GiB)",
        0xFA => "Read Error Retry Rate",
        0xFE => "Free Fall Protection",
        _ => "Vendor Specific",
    };
}
