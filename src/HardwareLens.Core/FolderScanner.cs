namespace HardwareLens.Core;

/// <summary>One folder in a size scan. Sizes are file sizes (not size on disk) of everything beneath it that could be read.</summary>
public sealed class FolderNode
{
    public string Path = "", Name = "";
    public long Bytes; public long Files; public int Skipped;
    public List<FolderNode> Children = new();
    public long OwnFilesBytes;
}

/// <summary>
/// User-initiated, read-only folder size scan. Never deletes or modifies anything, does not follow reparse points
/// (junctions/symlinks/OneDrive placeholders), is cancellable and keeps only the largest children per folder.
/// </summary>
public static class FolderScanner
{
    public static FolderNode Scan(string root, int depth, CancellationToken ct, Action<string, long>? progress = null, int keepChildren = 40)
    {
        long counted = 0;
        FolderNode Walk(string path, int d)
        {
            var node = new FolderNode { Path = path, Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path };
            ct.ThrowIfCancellationRequested();
            var kids = new List<FolderNode>();
            try
            {
                var di = new DirectoryInfo(path);
                foreach (var fi in di.EnumerateFiles())
                {
                    try { if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue; node.OwnFilesBytes += fi.Length; node.Files++; counted++; }
                    catch { node.Skipped++; }
                }
                foreach (var sub in di.EnumerateDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var child = Walk(sub.FullName, d + 1);
                        node.Skipped += child.Skipped; node.Files += child.Files; kids.Add(child);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { node.Skipped++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { node.Skipped++; }
            node.Bytes = node.OwnFilesBytes + kids.Sum(k => k.Bytes);
            if (d < depth) node.Children = kids.OrderByDescending(k => k.Bytes).Take(keepChildren).ToList();
            if (counted % 2000 == 0) progress?.Invoke(path, counted);
            return node;
        }
        return Walk(root, 0);
    }
}
