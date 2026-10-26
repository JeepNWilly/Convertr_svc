// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using Convertr.Core;

namespace Convertr.Svc.Server;

public sealed record DirEntry(string Name, string Path);
public sealed record DirListing(string Path, string? Parent, bool IsRoot, List<DirEntry> Dirs, long FreeBytes);

/// <summary>
/// Confinement to the configured AllowedRoots (DESIGN.md 6.4). Every path coming from a client is checked textually
/// (absolute, no ".." segments, no NUL) and then resolved to its real location (all symlinks followed) and must lie inside a root.
/// </summary>
public sealed class RootGuard
{
    private readonly List<string> _roots = new();
    public IReadOnlyList<string> Roots => _roots;
    public IReadOnlyList<string> Skipped { get; }

    public RootGuard(ServerOptions o)
    {
        var skipped = new List<string>();
        foreach (var r in o.AllowedRoots)
        {
            try
            {
                var full = Path.GetFullPath(r);
                if (!Directory.Exists(full)) { skipped.Add(r + " (does not exist)"); continue; }
                var real = RealPath(full);
                if (real != null && !_roots.Contains(real, PathCompare.Comparer)) _roots.Add(real);
            }
            catch (Exception ex) { skipped.Add(r + " (" + ex.Message + ")"); }
        }
        Skipped = skipped;
    }

    /// <summary>Follows every symlink in the path. Null when the path does not exist or loops.</summary>
    public static string? RealPath(string full, int depth = 0)
    {
        if (depth > 40) return null;
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(full) ?? "";
        var cur = root;
        foreach (var seg in full[root.Length..].Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(cur, seg);
            FileSystemInfo fi = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!fi.Exists) return null;
            if (fi.LinkTarget != null)
            {
                FileSystemInfo? target;
                try { target = fi.ResolveLinkTarget(true); } catch (IOException) { return null; }   // loop: "too many levels of symbolic links"
                if (target == null) return null;
                var t = RealPath(target.FullName, depth + 1);
                if (t == null) return null;
                cur = t;
            }
            else cur = next;
        }
        return cur.Length == 0 ? root : cur.TrimEnd(sep).Length == 0 ? cur : cur.TrimEnd(sep);
    }

    public bool IsInside(string realPath)
    {
        foreach (var r in _roots)
            if (PathCompare.Equal(realPath, r) || PathCompare.StartsWith(realPath, r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)) return true;
        return false;
    }

    public bool IsRoot(string realPath) => _roots.Contains(realPath, PathCompare.Comparer);

    /// <summary>Validates a client path. On success <paramref name="real"/> is the resolved absolute path inside a root.</summary>
    public bool TryResolve(string? input, out string real, out string error)
    {
        real = "";
        if (_roots.Count == 0) { error = "no folders are configured (set AllowedRoots in /etc/convertr-svc/appsettings.json)"; return false; }
        if (string.IsNullOrWhiteSpace(input)) { error = "a path is required"; return false; }
        if (input.Contains('\0')) { error = "invalid path"; return false; }
        if (!Path.IsPathRooted(input)) { error = "the path must be absolute"; return false; }
        if (input.Split(new[] { '/', '\\' }).Any(s => s == "..")) { error = "parent folder references ('..') are not allowed"; return false; }
        string full;
        try { full = Path.GetFullPath(input); } catch { error = "invalid path"; return false; }
        var rp = RealPath(full);
        if (rp == null) { error = "the folder does not exist"; return false; }
        if (!IsInside(rp)) { error = "that folder is outside the allowed folders"; return false; }
        real = rp; error = "";
        return true;
    }

    public bool TryResolveDirectory(string? input, out string real, out string error)
    {
        if (!TryResolve(input, out real, out error)) return false;
        if (!Directory.Exists(real)) { error = "that is not a folder"; return false; }
        return true;
    }

    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "@eaDir", "#recycle", "#snapshot", "@tmp", "lost+found", "_Convertr_Originals", "_Convertr_Undone" };

    /// <summary>Sub-folders only (never files). Hidden, system and housekeeping folders and links that lead outside the roots are not listed.</summary>
    public DirListing List(string real)
    {
        var dirs = new List<DirEntry>();
        try
        {
            foreach (var d in new DirectoryInfo(real).EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (d.Name.StartsWith('.') || Hidden.Contains(d.Name)) continue;
                var rp = d.LinkTarget != null ? RealPath(d.FullName) : d.FullName;
                if (rp == null || !IsInside(rp)) continue;
                dirs.Add(new DirEntry(d.Name, rp));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        string? parent = null;
        if (!IsRoot(real)) { var p = Path.GetDirectoryName(real); if (p != null && IsInside(p)) parent = p; }
        long free = 0;
        try { free = new DriveInfo(real).AvailableFreeSpace; } catch { }
        return new DirListing(real, parent, IsRoot(real), dirs, free);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int c_access(string path, int mode);

    /// <summary>True when the service can create the originals holding folder (a sibling of the Media folder): its nearest existing parent must be writable.</summary>
    public bool CanWriteHolding(string mediaFolder, string folderName = "_Convertr_Originals")
    {
        try
        {
            var p = HoldingArea.Root(mediaFolder, folderName);
            while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p) ?? "";
            if (string.IsNullOrEmpty(p)) return false;
            return !OperatingSystem.IsLinux() || c_access(p, 2) == 0;   // W_OK
        }
        catch { return false; }
    }
}
