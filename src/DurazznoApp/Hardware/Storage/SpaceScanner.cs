using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Durazzno.Hardware.Storage;

/// <summary>
/// A folder in the scanned tree. Sizes include everything below it and grow live while the scan runs: each folder
/// publishes its finished lists in one step and adds its own bytes to every ancestor atomically, so the map can be
/// drawn from another thread mid-scan.
/// </summary>
internal sealed class DirNode(string name, DirNode? parent)
{
    private long _size, _fileCount;

    public string Name { get; } = name;
    public DirNode? Parent { get; set; } = parent;

    /// <summary>Sub-folders. Replaced as a whole (never mutated) while scanning.</summary>
    public List<DirNode> Dirs { get; private set; } = [];

    /// <summary>Files at or above <see cref="SpaceScanner.KeepFileBytes"/> (kept individually for the map).</summary>
    public List<FileLeaf> Files { get; private set; } = [];

    /// <summary>Smaller files directly in this folder, aggregated.</summary>
    public long SmallFilesSize { get; set; }
    public int SmallFilesCount { get; set; }

    public long Size
    {
        get => Interlocked.Read(ref _size);
        set => Interlocked.Exchange(ref _size, value);
    }

    public long FileCount
    {
        get => Interlocked.Read(ref _fileCount);
        set => Interlocked.Exchange(ref _fileCount, value);
    }

    public long DirCount { get; set; }
    public bool Denied { get; set; }

    /// <summary>Called once by the worker that scanned this folder.</summary>
    public void Publish(List<DirNode> dirs, List<FileLeaf> files, long smallSize, int smallCount)
    {
        SmallFilesSize = smallSize;
        SmallFilesCount = smallCount;
        Files = files;
        Dirs = dirs;

        long bytes = smallSize, count = smallCount + files.Count;
        foreach (var f in files)
            bytes += f.Size;
        for (var n = this; n is not null; n = n.Parent)
        {
            Interlocked.Add(ref n._size, bytes);
            Interlocked.Add(ref n._fileCount, count);
        }
    }

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    public bool IsAncestorOf(DirNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n == this)
                return true;
        return false;
    }
}

internal readonly record struct FileLeaf(string Name, long Size, FileCategory Category);

internal sealed record CategoryTotal(FileCategory Category, long Size, long Count);

internal sealed record ScanResult(DirNode Root, IReadOnlyList<CategoryTotal> Categories, TimeSpan Duration, long DeniedFolders);

/// <summary>
/// Parallel directory scanner. Uses FileSystemEnumerable (NtQueryDirectoryFile with large buffers, no per-file
/// stat calls), several workers pulling folders from a channel, skips junctions / symlinks, and does not count
/// cloud-only (OneDrive placeholder) files as used space. Progress counters can be polled from any thread.
/// </summary>
internal sealed class SpaceScanner
{
    public const long KeepFileBytes = 512 * 1024;

    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | FileAttributes.Offline;

    private long _files, _dirs, _bytes, _denied;

    public long Files => Interlocked.Read(ref _files);
    public long Dirs => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long DeniedFolders => Interlocked.Read(ref _denied);
    public string CurrentPath { get; private set; } = "";

    /// <summary>The tree being built; available as soon as <see cref="ScanAsync"/> is called and filled in live.</summary>
    public DirNode? Root { get; private set; }

    public async Task<ScanResult> ScanAsync(string rootPath, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var root = Root = new DirNode(rootPath, null);
        var queue = Channel.CreateUnbounded<(DirNode Node, string Path)>();
        var pending = 1L;
        queue.Writer.TryWrite((root, rootPath));

        var workers = Math.Clamp(Environment.ProcessorCount, 4, 16);
        var perWorkerTypes = new Dictionary<FileCategory, (long Size, long Count)>[workers];

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            var types = perWorkerTypes[w] = new();
            await foreach (var (node, path) in queue.Reader.ReadAllAsync(ct))
            {
                var children = ScanFolder(node, path, types, ct);
                if (children.Count > 0)
                {
                    Interlocked.Add(ref pending, children.Count);
                    foreach (var child in children)
                        queue.Writer.TryWrite((child, Path.Combine(path, child.Name)));
                }

                if (Interlocked.Decrement(ref pending) == 0)
                    queue.Writer.TryComplete();
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        await Task.Run(() => Finish(root), ct);

        var categories = perWorkerTypes.Where(t => t is not null)
            .SelectMany(t => t)
            .GroupBy(kv => kv.Key)
            .Select(g => new CategoryTotal(g.Key, g.Sum(x => x.Value.Size), g.Sum(x => x.Value.Count)))
            .OrderByDescending(c => c.Size)
            .ToList();

        return new ScanResult(root, categories, watch.Elapsed, DeniedFolders);
    }

    private List<DirNode> ScanFolder(DirNode node, string path, Dictionary<FileCategory, (long Size, long Count)> types, CancellationToken ct)
    {
        CurrentPath = path;
        var children = new List<DirNode>();
        var files = new List<FileLeaf>();
        long smallSize = 0;
        var smallCount = 0;
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        try
        {
            var entries = new FileSystemEnumerable<(string Name, bool IsDir, long Length, FileAttributes Attributes)>(
                path, (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes), options);

            foreach (var (name, isDir, length, attributes) in entries)
            {
                if (isDir)
                {
                    // Junctions and symlinks point elsewhere on the disk; following them would double count.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    var child = new DirNode(name, node);
                    children.Add(child);
                    Interlocked.Increment(ref _dirs);
                    continue;
                }

                var size = (attributes & CloudOnly) != 0 ? 0 : length;
                var category = FileTypes.Categorize(name);
                var t = types.GetValueOrDefault(category);
                types[category] = (t.Size + size, t.Count + 1);

                if (size >= KeepFileBytes)
                    files.Add(new FileLeaf(name, size, category));
                else
                {
                    smallSize += size;
                    smallCount++;
                }

                Interlocked.Increment(ref _files);
                Interlocked.Add(ref _bytes, size);
                if ((_files & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Denied = true;
            Interlocked.Increment(ref _denied);
        }

        // Publish the finished folder in one step so the live map never sees a half-filled list.
        node.Publish(children, files, smallSize, smallCount);
        return children;
    }

    /// <summary>Totals bottom-up and sorts every level by size (biggest first).</summary>
    private static void Finish(DirNode root)
    {
        var order = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var d in n.Dirs)
                stack.Push(d);
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            n.Size = n.SmallFilesSize + n.Files.Sum(f => f.Size) + n.Dirs.Sum(d => d.Size);
            n.FileCount = n.SmallFilesCount + n.Files.Count + n.Dirs.Sum(d => d.FileCount);
            n.DirCount = n.Dirs.Count + n.Dirs.Sum(d => d.DirCount);
            n.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
            n.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }

    /// <summary>Removes a deleted folder or file from the tree and fixes the totals above it.</summary>
    public static void Remove(DirNode owner, DirNode? dir, FileLeaf? file)
    {
        long size, files, dirs;
        if (dir is not null)
        {
            owner.Dirs.Remove(dir);
            (size, files, dirs) = (dir.Size, dir.FileCount, dir.DirCount + 1);
        }
        else if (file is { } f)
        {
            owner.Files.Remove(f);
            (size, files, dirs) = (f.Size, 1, 0);
        }
        else
        {
            return;
        }

        for (var n = owner; n is not null; n = n.Parent)
        {
            n.Size -= size;
            n.FileCount -= files;
            n.DirCount -= dirs;
        }
    }
}
