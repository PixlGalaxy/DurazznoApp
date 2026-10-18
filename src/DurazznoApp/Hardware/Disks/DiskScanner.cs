using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Durazzno.Core;
using Microsoft.Win32.SafeHandles;

namespace Durazzno.Hardware.Disks;

/// <summary>
/// Reads disk identity, temperature and S.M.A.R.T. straight from the Windows storage stack (the same IOCTLs
/// CrystalDiskInfo uses). Identity and NVMe health work without admin rights on most systems; ATA/SATA S.M.A.R.T.
/// needs an elevated process.
/// </summary>
internal static class DiskScanner
{
    private const int MaxDisks = 32;

    public static bool IsElevated { get; } = CheckElevated();

    public static List<DiskReport> Scan()
    {
        var letters = MapDriveLetters();
        var disks = new List<DiskReport>();

        for (var i = 0; i < MaxDisks; i++)
        {
            using var handle = Native.Open($@"\\.\PhysicalDrive{i}", 0);
            if (handle.IsInvalid)
                continue;

            try
            {
                var disk = ReadIdentity(i, handle, letters.GetValueOrDefault(i) ?? []);
                if (disk is null)
                    continue;

                ReadTemperature(disk, handle);
                ReadSmart(disk, handle);
                SmartAnalysis.Evaluate(disk);
                disks.Add(disk);
            }
            catch (Exception ex)
            {
                Log.Warn($"PhysicalDrive{i}: {ex.Message}");
            }
        }

        return disks;
    }

    // ── Identity ──────────────────────────────────────────────────────────────────────────

    private static DiskReport? ReadIdentity(int index, SafeFileHandle handle, List<string> letters)
    {
        var desc = Native.QueryProperty(handle, Native.StorageDeviceProperty, 1024);
        if (desc is null)
            return null;

        string Str(int offsetField)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(offsetField));
            if (offset <= 0 || offset >= desc.Length)
                return "";
            var end = Array.IndexOf(desc, (byte)0, offset);
            return Encoding.ASCII.GetString(desc, offset, (end < 0 ? desc.Length : end) - offset).Trim();
        }

        var bus = BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(28));
        var vendor = Str(12);
        var product = Str(16);
        var model = string.IsNullOrEmpty(vendor) || product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : $"{vendor} {product}";

        var seek = Native.QueryProperty(handle, Native.StorageDeviceSeekPenaltyProperty, 12);
        var trim = Native.QueryProperty(handle, Native.StorageDeviceTrimProperty, 12);

        return new DiskReport
        {
            Index = index,
            Model = model.Trim(),
            Vendor = vendor,
            Firmware = Str(20),
            Serial = Str(24).Trim('.', ' '),
            BusType = BusName(bus),
            IsVirtual = bus is 14 or 15 or 16,
            IsRemovable = desc[10] != 0,
            SizeBytes = Native.DiskSize(handle),
            IsSsd = bus == 17 || seek is not null && seek[8] == 0,
            TrimEnabled = trim is null ? null : trim[8] != 0,
            DriveLetters = letters,
        };
    }

    private static void ReadTemperature(DiskReport disk, SafeFileHandle handle)
    {
        var data = Native.QueryProperty(handle, Native.StorageDeviceTemperatureProperty, 256);
        if (data is null || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) == 0)
            return;

        var temp = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(26));
        if (temp is > -40 and < 150)
            disk.TemperatureC = temp;

        var warning = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(10));
        var critical = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8));
        if (warning is > 0 and < 150)
            disk.WarningTemperatureC = warning;
        if (critical is > 0 and < 150)
            disk.CriticalTemperatureC = critical;
    }

    // ── S.M.A.R.T. ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries the transports CrystalDiskInfo uses, in the order it uses them for this bus type, and keeps the first
    /// one that returns data. Only the in-box NVMe query works without elevation.
    /// </summary>
    private static void ReadSmart(DiskReport disk, SafeFileHandle readOnlyHandle)
    {
        if (disk.IsVirtual)
        {
            disk.SmartError = "Virtual disks have no S.M.A.R.T. data.";
            return;
        }

        if (disk.BusType is "NVMe" or "RAID" or "SCSI" && Native.NvmeHealthLog(readOnlyHandle) is { } inboxLog)
        {
            UseNvme(disk, inboxLog, null, SmartTransport.NvmeStorageQuery);
            return;
        }

        if (!IsElevated)
        {
            SetSmartError(disk);
            return;
        }

        using var handle = Native.Open($@"\\.\PhysicalDrive{disk.Index}", Native.GenericRead | Native.GenericWrite);
        if (handle.IsInvalid)
        {
            SetSmartError(disk);
            return;
        }

        var ok = disk.BusType switch
        {
            "NVMe" => TryNvmeInbox(disk, handle) || TryIntelRst(disk),
            "RAID" or "SCSI" or "SAS" => TryNvmeInbox(disk, handle) || TryIntelRst(disk) || TrySat(disk, handle) || TryAtaIoctl(disk, handle),
            "USB" when disk.IsSsd => TryUsbNvme(disk, handle) || TrySat(disk, handle),
            "USB" => TrySat(disk, handle) || TryUsbNvme(disk, handle),
            _ => TryAtaIoctl(disk, handle) || TryAtaPassThrough(disk, handle) || TrySat(disk, handle),
        };

        if (!ok)
        {
            disk.SmartError = disk.BusType == "USB"
                ? "This USB enclosure does not pass S.M.A.R.T. through."
                : "The drive did not return S.M.A.R.T. data.";
        }
    }

    private static bool TryNvmeInbox(DiskReport disk, SafeFileHandle handle)
    {
        if (Native.NvmeHealthLog(handle) is not { } log)
            return false;
        UseNvme(disk, log, null, SmartTransport.NvmeStorageQuery);
        return true;
    }

    private static bool TryIntelRst(DiskReport disk)
    {
        if (Passthrough.IntelRst(disk.Index, 0x02, 0x02, 0x7F) is not { } log)
            return false;
        UseNvme(disk, log, Passthrough.IntelRst(disk.Index, 0x06, 0x01, 0), SmartTransport.NvmeIntelRst);
        return true;
    }

    private static bool TryUsbNvme(DiskReport disk, SafeFileHandle handle)
    {
        if (Passthrough.UsbNvme(handle) is not { } result)
            return false;
        UseNvme(disk, result.Health, result.Identify, result.Transport);
        return true;
    }

    private static bool TryAtaIoctl(DiskReport disk, SafeFileHandle handle) =>
        UseAta(disk, Native.AtaSmartCommand(handle, disk.Index, Native.SmartReadAttributes),
            () => Native.AtaSmartCommand(handle, disk.Index, Native.SmartReadThresholds), null, SmartTransport.AtaSmartIoctl);

    private static bool TryAtaPassThrough(DiskReport disk, SafeFileHandle handle) =>
        UseAta(disk, Passthrough.AtaSmartCommand(handle, Passthrough.SmartReadAttributes),
            () => Passthrough.AtaSmartCommand(handle, Passthrough.SmartReadThresholds), null, SmartTransport.AtaPassThrough);

    private static bool TrySat(DiskReport disk, SafeFileHandle handle)
    {
        var identify = Passthrough.SatIdentify(handle);
        if (identify is null)
            return false;
        return UseAta(disk, Passthrough.SatSmart(handle, Passthrough.SmartReadAttributes),
            () => Passthrough.SatSmart(handle, Passthrough.SmartReadThresholds), identify, SmartTransport.Sat);
    }

    private static void UseNvme(DiskReport disk, byte[] log, byte[]? identify, SmartTransport transport)
    {
        disk.Nvme = ParseNvmeLog(log);
        disk.Transport = transport;
        if (identify is not null)
            ApplyIdentity(disk, Passthrough.ParseNvmeIdentify(identify));
    }

    private static bool UseAta(DiskReport disk, byte[]? values, Func<byte[]?> thresholds, byte[]? identify, SmartTransport transport)
    {
        if (values is null)
            return false;

        var attributes = ParseAtaAttributes(values, thresholds());
        if (attributes.Count == 0)
            return false;

        disk.Attributes = attributes;
        disk.Transport = transport;
        if (identify is not null)
            ApplyIdentity(disk, Passthrough.ParseAtaIdentify(identify));
        return true;
    }

    /// <summary>Behind a bridge the descriptor names the enclosure; the drive's own IDENTIFY names the drive.</summary>
    private static void ApplyIdentity(DiskReport disk, (string Model, string Serial, string Firmware) id)
    {
        if (id.Model.Length > 0)
            disk.Model = id.Model;
        if (id.Serial.Length > 0)
            disk.Serial = id.Serial;
        if (id.Firmware.Length > 0)
            disk.Firmware = id.Firmware;
    }

    private static void SetSmartError(DiskReport disk)
    {
        if (IsElevated)
        {
            disk.SmartError = "The drive did not return S.M.A.R.T. data.";
        }
        else
        {
            disk.SmartNeedsAdmin = true;
            disk.SmartError = "Reading S.M.A.R.T. from this drive needs administrator access.";
        }
    }

    private static NvmeHealthLog ParseNvmeLog(byte[] d)
    {
        UInt128 U128(int offset) => BinaryPrimitives.ReadUInt128LittleEndian(d.AsSpan(offset, 16));
        static int Kelvin(int k) => k == 0 ? 0 : k - 273;

        var sensors = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var k = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(200 + i * 2));
            if (k != 0)
                sensors.Add(Kelvin(k));
        }

        return new NvmeHealthLog(
            CriticalWarning: d[0],
            TemperatureC: Kelvin(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1))),
            AvailableSpare: d[3],
            AvailableSpareThreshold: d[4],
            PercentageUsed: d[5],
            DataUnitsRead: U128(32),
            DataUnitsWritten: U128(48),
            HostReadCommands: U128(64),
            HostWriteCommands: U128(80),
            ControllerBusyMinutes: U128(96),
            PowerCycles: U128(112),
            PowerOnHours: U128(128),
            UnsafeShutdowns: U128(144),
            MediaErrors: U128(160),
            ErrorLogEntries: U128(176),
            WarningTempMinutes: BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(192)),
            CriticalTempMinutes: BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(196)),
            SensorTemperaturesC: sensors.ToArray());
    }

    private static List<SmartAttribute> ParseAtaAttributes(byte[] values, byte[]? thresholds)
    {
        var limits = new Dictionary<byte, byte>();
        if (thresholds is not null)
        {
            for (var i = 0; i < 30; i++)
            {
                var o = 2 + i * 12;
                if (thresholds[o] != 0)
                    limits[thresholds[o]] = thresholds[o + 1];
            }
        }

        var list = new List<SmartAttribute>();
        for (var i = 0; i < 30; i++)
        {
            var o = 2 + i * 12;
            var id = values[o];
            if (id == 0)
                continue;

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(values.AsSpan(o + 1));
            ulong raw = 0;
            for (var b = 5; b >= 0; b--)
                raw = (raw << 8) | values[o + 5 + b];

            list.Add(new SmartAttribute(id, SmartAnalysis.AttributeName(id), values[o + 3], values[o + 4],
                limits.GetValueOrDefault(id), raw, (flags & 1) != 0));
        }

        return list;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>Disk number → drive letters, via each volume's disk extents.</summary>
    private static Dictionary<int, List<string>> MapDriveLetters()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\');
            using var handle = Native.Open($@"\\.\{letter}", 0);
            if (handle.IsInvalid)
                continue;

            foreach (var disk in Native.VolumeDiskNumbers(handle))
            {
                if (!map.TryGetValue(disk, out var list))
                    map[disk] = list = [];
                list.Add(letter);
            }
        }

        return map;
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 or 15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    private static bool CheckElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Raw Win32 calls. Buffers follow the layouts in ntddstor.h / ntdddisk.h / nvme.h.</summary>
    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;

        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7;
        public const int StorageDeviceTrimProperty = 8;
        public const int StorageDeviceProtocolSpecificProperty = 50;
        public const int StorageDeviceTemperatureProperty = 52;

        public const byte SmartReadAttributes = 0xD0;
        public const byte SmartReadThresholds = 0xD1;

        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
        private const uint SmartRcvDriveData = 0x0007C088;

        public static SafeFileHandle Open(string path, uint access) =>
            CreateFileW(path, access, 3 /* read | write share */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

        /// <summary>STORAGE_PROPERTY_QUERY with PropertyStandardQuery. Returns the descriptor or null.</summary>
        public static byte[]? QueryProperty(SafeFileHandle handle, int propertyId, int outputSize)
        {
            var query = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
            var output = new byte[outputSize];
            return Ioctl(handle, IoctlStorageQueryProperty, query, output) >= 8 ? output : null;
        }

        public static long DiskSize(SafeFileHandle handle)
        {
            var output = new byte[256];
            return Ioctl(handle, IoctlDiskGetDriveGeometryEx, null, output) >= 32
                ? BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24))
                : 0;
        }

        public static IEnumerable<int> VolumeDiskNumbers(SafeFileHandle handle)
        {
            var output = new byte[8 + 24 * 16];
            if (Ioctl(handle, IoctlVolumeGetVolumeDiskExtents, null, output) < 8)
                yield break;

            var count = BinaryPrimitives.ReadInt32LittleEndian(output);
            for (var i = 0; i < count && 8 + i * 24 + 4 <= output.Length; i++)
                yield return BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + i * 24));
        }

        /// <summary>NVMe Get Log Page 0x02 (SMART / Health Information, 512 bytes) via the in-box driver.</summary>
        public static byte[]? NvmeHealthLog(SafeFileHandle handle)
        {
            const int specificSize = 40; // STORAGE_PROTOCOL_SPECIFIC_DATA
            const int logSize = 512;
            var buffer = new byte[8 + specificSize + logSize];
            var s = buffer.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(s, StorageDeviceProtocolSpecificProperty);
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], 0);       // PropertyStandardQuery
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], 3);       // ProtocolTypeNvme
            BinaryPrimitives.WriteInt32LittleEndian(s[12..], 2);      // NVMeDataTypeLogPage
            BinaryPrimitives.WriteInt32LittleEndian(s[16..], 0x02);   // Health information log
            BinaryPrimitives.WriteInt32LittleEndian(s[20..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(s[24..], specificSize); // ProtocolDataOffset
            BinaryPrimitives.WriteInt32LittleEndian(s[28..], logSize);      // ProtocolDataLength

            if (Ioctl(handle, IoctlStorageQueryProperty, buffer, buffer) < 8 + specificSize)
                return null;

            // Output: STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version, Size, ProtocolSpecificData }.
            var offset = BinaryPrimitives.ReadInt32LittleEndian(s[(8 + 16)..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(s[(8 + 20)..]);
            if (length < logSize || 8 + offset + logSize > buffer.Length)
                return null;

            return buffer.AsSpan(8 + offset, logSize).ToArray();
        }

        /// <summary>ATA SMART READ DATA / READ THRESHOLDS via SMART_RCV_DRIVE_DATA (needs a read/write handle).</summary>
        public static byte[]? AtaSmartCommand(SafeFileHandle handle, int driveNumber, byte feature)
        {
            // SENDCMDINPARAMS (packed): cBufferSize, IDEREGS[8], bDriveNumber, bReserved[3], dwReserved[4], bBuffer[1]
            var input = new byte[33];
            BinaryPrimitives.WriteInt32LittleEndian(input, 512);
            input[4] = feature;                                // bFeaturesReg
            input[5] = 1;                                      // bSectorCountReg
            input[6] = 1;                                      // bSectorNumberReg
            input[7] = 0x4F;                                   // bCylLowReg  (SMART signature)
            input[8] = 0xC2;                                   // bCylHighReg (SMART signature)
            input[9] = (byte)(0xA0 | ((driveNumber & 1) << 4)); // bDriveHeadReg
            input[10] = 0xB0;                                  // bCommandReg = SMART
            input[12] = (byte)driveNumber;

            // SENDCMDOUTPARAMS: cBufferSize, DRIVERSTATUS[12], bBuffer[512]
            var output = new byte[16 + 512];
            if (Ioctl(handle, SmartRcvDriveData, input, output) < 16 + 362 || output[4] != 0)
                return null;

            return output.AsSpan(16, 512).ToArray();
        }

        private static int Ioctl(SafeFileHandle handle, uint code, byte[]? input, byte[] output)
        {
            var ok = DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out var returned, IntPtr.Zero);
            return ok ? returned : -Marshal.GetLastWin32Error();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }
}
