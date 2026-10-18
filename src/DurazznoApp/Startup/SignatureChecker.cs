using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Durazzno.Startup;

internal enum SignatureState { Unknown, Verified, Unsigned, Missing }

internal sealed record Signature(SignatureState State, string Publisher)
{
    public bool IsMicrosoft => State == SignatureState.Verified && Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Authenticode check like Autoruns' "Verified" column: the file's embedded signature first, then the Windows
/// catalog store (where most system files are signed). No network revocation checks, so it stays fast and offline.
/// Results are cached per path.
/// </summary>
internal static class SignatureChecker
{
    private static readonly ConcurrentDictionary<string, Signature> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Guid _verifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

    public static Signature Check(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new Signature(SignatureState.Missing, "");

        return Cache.GetOrAdd(path, p =>
        {
            try
            {
                if (VerifyEmbedded(p))
                    return new Signature(SignatureState.Verified, Signer(p) ?? Company(p));
                if (VerifyCatalog(p, out var catalog))
                    return new Signature(SignatureState.Verified, Signer(catalog) ?? "Microsoft Windows");
                return new Signature(SignatureState.Unsigned, Company(p));
            }
            catch
            {
                return new Signature(SignatureState.Unknown, Company(p));
            }
        });
    }

    private static string Company(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>CN of the signing certificate (works for signed PE files and catalog files).</summary>
    private static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // ── Embedded signature ────────────────────────────────────────────────────────────────

    private static bool VerifyEmbedded(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = NewData(1 /* WTD_CHOICE_FILE */, filePtr);
            return WinVerifyTrust(IntPtr.Zero, ref _verifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    // ── Catalog signature ─────────────────────────────────────────────────────────────────

    private static bool VerifyCatalog(string path, out string catalog)
    {
        catalog = "";
        using var stream = File.OpenRead(path);
        var handle = stream.SafeFileHandle.DangerousGetHandle();

        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
                continue;
            try
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
                if (size == 0)
                    continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0))
                    continue;

                var prev = IntPtr.Zero;
                var info = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, ref prev);
                if (info == IntPtr.Zero)
                    continue;

                try
                {
                    var catInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                    if (!CryptCATCatalogInfoFromContext(info, ref catInfo, 0))
                        continue;

                    var tag = Convert.ToHexString(hash);
                    var member = new WintrustCatalogInfo
                    {
                        cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(),
                        pcwszCatalogFilePath = catInfo.wszCatalogFile,
                        pcwszMemberTag = tag,
                        pcwszMemberFilePath = path,
                        hMemberFile = handle,
                        hCatAdmin = admin,
                    };

                    var memberPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustCatalogInfo>());
                    try
                    {
                        Marshal.StructureToPtr(member, memberPtr, false);
                        var data = NewData(2 /* WTD_CHOICE_CATALOG */, memberPtr);
                        if (WinVerifyTrust(IntPtr.Zero, ref _verifyV2, ref data) == 0)
                        {
                            catalog = catInfo.wszCatalogFile;
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(memberPtr);
                    }
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, info, 0);
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }

        return false;
    }

    private static WintrustData NewData(uint choice, IntPtr union) => new()
    {
        cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
        dwUIChoice = 2,              // WTD_UI_NONE
        fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
        dwUnionChoice = choice,
        pUnion = union,
        dwStateAction = 0,
        dwProvFlags = 0x1000 | 0x80, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public string pcwszCatalogFilePath;
        public string pcwszMemberTag;
        public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, ref IntPtr prevCatInfo);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
