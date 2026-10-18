using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Durazzno.Hardware.Disks;

/// <summary>Which transport answered S.M.A.R.T. for a disk (shown in the UI, useful for bug reports).</summary>
internal enum SmartTransport
{
    None,
    NvmeStorageQuery,
    AtaSmartIoctl,
    AtaPassThrough,
    Sat,
    NvmeJMicron,
    NvmeASMedia,
    NvmeRealtek,
    NvmeIntelRst,
}

/// <summary>
/// Low-level command transports, modelled on CrystalDiskInfo 9.9.3 (AtaSmart.cpp): SCSI pass-through with
/// ATA PASS-THROUGH(12) for USB-SATA bridges (SAT), vendor CDBs for the JMicron / ASMedia / Realtek USB-NVMe
/// bridges, IOCTL_ATA_PASS_THROUGH for SATA drivers, and the "IntelNvm" miniport call for NVMe behind Intel RST.
/// All of these need a read/write handle, i.e. an elevated process.
/// </summary>
internal static class Passthrough
{
    private const uint IoctlScsiPassThrough = 0x0004D004;
    private const uint IoctlScsiMiniport = 0x0004D008;
    private const uint IoctlAtaPassThrough = 0x0004D02C;
    private const uint IoctlScsiGetAddress = 0x00041018;
    private const uint IoctlIntelNvmePassThrough = 0xF0002808; // CTL_CODE(0xF000, 0xA02, METHOD_BUFFERED, FILE_ANY_ACCESS)

    private const byte DataOut = 0, DataIn = 1;
    private const byte AtaSmart = 0xB0, AtaIdentify = 0xEC;
    public const byte SmartReadAttributes = 0xD0, SmartReadThresholds = 0xD1;

    // ── SCSI pass-through (x64 SCSI_PASS_THROUGH layout, 56 bytes) ────────────────────────

    private const int SptSize = 56, SenseOffset = 56, SenseSize = 32, DataOffset = 88;

    /// <summary>Sends a CDB. For data-out, <paramref name="data"/> is sent; for data-in, the response is returned.</summary>
    private static byte[]? Scsi(SafeFileHandle handle, byte[] cdb, byte direction, int length, byte[]? data = null)
    {
        var buffer = new byte[DataOffset + length];
        var s = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, SptSize);
        buffer[6] = (byte)cdb.Length;     // CdbLength
        buffer[7] = 24;                   // SenseInfoLength
        buffer[8] = direction;            // DataIn
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 2); // TimeOutValue (s)
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], SenseOffset);
        cdb.CopyTo(buffer, 36);
        data?.CopyTo(buffer, DataOffset);

        if (!Native.DeviceIoControl(handle, IoctlScsiPassThrough, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        if (buffer[2] != 0) // ScsiStatus: CHECK CONDITION etc.
            return null;

        return buffer.AsSpan(DataOffset, length).ToArray();
    }

    private static bool AllZero(byte[]? data) => data is null || !data.AsSpan().ContainsAnyExcept((byte)0);

    // ── ATA via SAT: ATA PASS-THROUGH(12), PIO data-in, 512 bytes ──────────────────────────

    private static byte[]? SatCommand(SafeFileHandle handle, byte command, byte feature, byte lbaLow, byte lbaMid, byte lbaHigh)
    {
        var cdb = new byte[12];
        cdb[0] = 0xA1;                         // ATA PASS-THROUGH(12)
        cdb[1] = 4 << 1;                       // PROTOCOL = PIO Data-In
        cdb[2] = (1 << 3) | (1 << 2) | 2;      // T_DIR = from device, BYTE_BLOCK, T_LENGTH = sector count
        cdb[3] = feature;
        cdb[4] = 1;                            // SECTOR_COUNT
        cdb[5] = lbaLow;
        cdb[6] = lbaMid;
        cdb[7] = lbaHigh;
        cdb[8] = 0xA0;                         // DEVICE
        cdb[9] = command;
        var data = Scsi(handle, cdb, DataIn, 512);
        return AllZero(data) ? null : data;
    }

    public static byte[]? SatIdentify(SafeFileHandle handle) => SatCommand(handle, AtaIdentify, 0, 0, 0, 0);

    public static byte[]? SatSmart(SafeFileHandle handle, byte feature) => SatCommand(handle, AtaSmart, feature, 1, 0x4F, 0xC2);

    // ── ATA via IOCTL_ATA_PASS_THROUGH (x64 ATA_PASS_THROUGH_EX, 48 bytes) ────────────────

    public static byte[]? AtaSmartCommand(SafeFileHandle handle, byte feature)
    {
        const int dataOffset = 56;
        var buffer = new byte[dataOffset + 512];
        var s = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 48);    // Length
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 0x03); // ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 512);  // DataTransferLength
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 2);   // TimeOutValue
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], dataOffset);
        buffer[40] = feature;   // CurrentTaskFile: Features
        buffer[41] = 1;         // SectorCount
        buffer[42] = 1;         // SectorNumber
        buffer[43] = 0x4F;      // CylLow
        buffer[44] = 0xC2;      // CylHigh
        buffer[45] = 0xA0;      // DriveHead
        buffer[46] = AtaSmart;  // Command

        if (!Native.DeviceIoControl(handle, IoctlAtaPassThrough, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        var data = buffer.AsSpan(dataOffset, 512).ToArray();
        return AllZero(data) ? null : data;
    }

    // ── USB-NVMe bridges ──────────────────────────────────────────────────────────────────

    /// <summary>JMicron JMS583 and family: an "NVME" command block is sent first, then the data is read back.</summary>
    private static byte[]? JMicron(SafeFileHandle handle, byte opcode, byte cns, int length)
    {
        var command = new byte[512];
        "NVME"u8.CopyTo(command);
        command[8] = opcode;
        if (opcode == 0x02) // Get Log Page: SMART / health, NSID = all
        {
            command[10] = 0x56;
            command[12] = command[13] = command[14] = command[15] = 0xFF;
            command[0x21] = 0x40;
            command[0x22] = 0x7A;
            command[0x30] = 0x02;
            command[0x32] = 0x7F;
        }
        else
        {
            command[0x30] = cns;
        }

        var cdbOut = new byte[12];
        cdbOut[0] = 0xA1;
        cdbOut[1] = 0x80; // admin
        cdbOut[4] = 0x02;
        if (Scsi(handle, cdbOut, DataOut, 512, command) is null)
            return null;

        var cdbIn = new byte[12];
        cdbIn[0] = 0xA1;
        cdbIn[1] = 0x82; // admin + DMA-in
        cdbIn[4] = (byte)(length >> 8);
        var data = Scsi(handle, cdbIn, DataIn, length);
        return AllZero(data) ? null : data;
    }

    /// <summary>ASMedia ASM2362 / ASM2364: vendor CDB 0xE6.</summary>
    private static byte[]? ASMedia(SafeFileHandle handle, byte opcode, byte arg, int length)
    {
        var cdb = new byte[16];
        cdb[0] = 0xE6;
        cdb[1] = opcode;
        cdb[3] = arg;
        if (opcode == 0x02)
            cdb[7] = 0x7F;
        var data = Scsi(handle, cdb, DataIn, length);
        return AllZero(data) ? null : data;
    }

    /// <summary>Realtek RTL9210 / RTL9211: vendor CDB 0xE4 with the transfer length in the CDB.</summary>
    private static byte[]? Realtek(SafeFileHandle handle, byte opcode, byte arg, int length)
    {
        var cdb = new byte[16];
        cdb[0] = 0xE4;
        cdb[1] = (byte)length;
        cdb[2] = (byte)(length >> 8);
        cdb[3] = opcode;
        cdb[4] = arg;
        var data = Scsi(handle, cdb, DataIn, length);
        return AllZero(data) ? null : data;
    }

    /// <summary>Tries the known USB-NVMe bridges. Returns the identify-controller page, the health log and the bridge.</summary>
    public static (byte[] Identify, byte[] Health, SmartTransport Transport)? UsbNvme(SafeFileHandle handle)
    {
        if (JMicron(handle, 0x06, 0x01, 4096) is { } jmId && JMicron(handle, 0x02, 0, 512) is { } jmLog)
            return (jmId, jmLog, SmartTransport.NvmeJMicron);
        if (ASMedia(handle, 0x06, 0x01, 4096) is { } asId && ASMedia(handle, 0x02, 0x02, 512) is { } asLog)
            return (asId, asLog, SmartTransport.NvmeASMedia);
        if (Realtek(handle, 0x06, 0x01, 4096) is { } rtId && Realtek(handle, 0x02, 0x02, 512) is { } rtLog)
            return (rtId, rtLog, SmartTransport.NvmeRealtek);
        return null;
    }

    // ── NVMe behind Intel RST (iaStorAC / iaStorVD) ───────────────────────────────────────

    /// <summary>NVMe Get Log Page / Identify through the RST miniport ("IntelNvm" SRB). Opens \\.\ScsiN: itself.</summary>
    public static byte[]? IntelRst(int diskIndex, byte opcode, byte cdw10Low, uint numd)
    {
        using var disk = Native.Open($@"\\.\PhysicalDrive{diskIndex}", 0);
        var address = new byte[8];
        if (disk.IsInvalid || !Native.DeviceIoControl(disk, IoctlScsiGetAddress, null, 0, address, address.Length, out _, IntPtr.Zero))
            return null;

        using var port = Native.Open($@"\\.\Scsi{address[4]}:", 0xC0000000);
        if (port.IsInvalid)
            return null;

        // INTEL_NVME_PASS_THROUGH (packed): SRB_IO_CONTROL[0x1C], payload[0x88], data[0x1000]
        const int payload = 0x1C, data = 0xA4;
        var buffer = new byte[data + 0x1000];
        var s = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x1C);                  // HeaderLength
        "IntelNvm"u8.CopyTo(s[4..]);                                         // Signature
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 10);               // Timeout
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], IoctlIntelNvmePassThrough);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)(buffer.Length - 0x1C)); // Length
        buffer[payload] = 1;                                                 // Version
        buffer[payload + 1] = address[5];                                    // PathId
        buffer[0x20] = opcode;                                               // CDW0.Opcode
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], opcode == 0x02 ? 0xFFFFFFFF : 0); // NSID
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x48..], cdw10Low | (numd << 16));          // CDW10
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x74..], 0xA4);           // ParamBufLen
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x78..], 0x1000);         // ReturnBufferLen

        if (!Native.DeviceIoControl(port, IoctlScsiMiniport, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        var result = buffer.AsSpan(data, opcode == 0x02 ? 512 : 4096).ToArray();
        return AllZero(result) ? null : result;
    }

    // ── IDENTIFY parsing ──────────────────────────────────────────────────────────────────

    /// <summary>ATA IDENTIFY DEVICE strings are byte-swapped per 16-bit word.</summary>
    public static (string Model, string Serial, string Firmware) ParseAtaIdentify(byte[] d)
    {
        static string Swapped(byte[] d, int offset, int length)
        {
            var chars = new byte[length];
            for (var i = 0; i < length; i += 2)
            {
                chars[i] = d[offset + i + 1];
                chars[i + 1] = d[offset + i];
            }
            return Encoding.ASCII.GetString(chars).Trim(' ', '\0');
        }

        return (Swapped(d, 54, 40), Swapped(d, 20, 20), Swapped(d, 46, 8));
    }

    public static (string Model, string Serial, string Firmware) ParseNvmeIdentify(byte[] d)
    {
        static string Ascii(byte[] d, int offset, int length) => Encoding.ASCII.GetString(d, offset, length).Trim(' ', '\0');
        return (Ascii(d, 24, 40), Ascii(d, 4, 20), Ascii(d, 64, 8));
    }

    private static class Native
    {
        public static SafeFileHandle Open(string path, uint access) =>
            CreateFileW(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    }
}
