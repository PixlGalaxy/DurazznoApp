namespace Durazzno.Hardware.Storage;

internal enum FileCategory : byte
{
    Other,
    Video,
    Image,
    Audio,
    Archive,
    DiskImage,
    Program,
    Document,
    Code,
    Game,
    System,
}

/// <summary>Groups extensions into a few colour-coded categories for the map and the type breakdown.</summary>
internal static class FileTypes
{
    private static readonly Dictionary<string, FileCategory> Map = Build(new()
    {
        [FileCategory.Video] = "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts 3gp vob",
        [FileCategory.Image] = "jpg jpeg png gif bmp webp tif tiff heic heif raw cr2 cr3 nef arw dng psd svg ico avif",
        [FileCategory.Audio] = "mp3 wav flac aac ogg m4a wma opus aiff alac mid midi",
        [FileCategory.Archive] = "zip rar 7z tar gz tgz bz2 xz zst cab lz4",
        [FileCategory.DiskImage] = "iso img vhd vhdx vmdk vdi qcow2 wim esd swm",
        [FileCategory.Program] = "exe msi msix appx appxbundle msixbundle dll ocx sys drv efi com scr",
        [FileCategory.Document] = "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub",
        [FileCategory.Code] = "cs js ts py java c cpp h hpp go rs json xml yaml yml html css sql ps1 sh bat cmd lua php rb kt swift",
        [FileCategory.Game] = "pak vpk bsa ba2 upk uasset ucas utoc bundle assets resource forge dat2 big rpf",
        [FileCategory.System] = "log tmp temp bak etl evtx dmp mdmp pf db sqlite ldf mdf edb cache hiberfil pagefile swapfile blf regtrans-ms",
    });

    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> Lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();

    public static FileCategory Categorize(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
            return FileCategory.Other;

        // Span lookup avoids allocating an extension string for every file.
        return Lookup.TryGetValue(fileName.AsSpan(dot + 1), out var category) ? category : FileCategory.Other;
    }

    public static string Name(FileCategory c) => c switch
    {
        FileCategory.Video => "Video",
        FileCategory.Image => "Images",
        FileCategory.Audio => "Audio",
        FileCategory.Archive => "Archives",
        FileCategory.DiskImage => "Disk images",
        FileCategory.Program => "Programs",
        FileCategory.Document => "Documents",
        FileCategory.Code => "Code",
        FileCategory.Game => "Game data",
        FileCategory.System => "System & temp",
        _ => "Other",
    };

    /// <summary>Tile colours, tuned to stay distinct on the dark UI.</summary>
    public static string Color(FileCategory c) => c switch
    {
        FileCategory.Video => "#8B5CF6",
        FileCategory.Image => "#EC4899",
        FileCategory.Audio => "#F59E0B",
        FileCategory.Archive => "#EF4444",
        FileCategory.DiskImage => "#84CC16",
        FileCategory.Program => "#3B82F6",
        FileCategory.Document => "#10B981",
        FileCategory.Code => "#22D3EE",
        FileCategory.Game => "#F97316",
        FileCategory.System => "#64748B",
        _ => "#94A3B8",
    };

    private static Dictionary<string, FileCategory> Build(Dictionary<FileCategory, string> lists)
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, list) in lists)
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = category;
        return map;
    }
}
