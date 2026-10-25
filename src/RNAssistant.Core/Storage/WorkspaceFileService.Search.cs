using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed class WorkspaceSearchPaths
    {
        public List<string> Paths { get; } = new List<string>();
        public List<string> Unavailable { get; } = new List<string>();
        public bool Complete { get; internal set; } = true;
    }

    public sealed partial class WorkspaceFileService
    {
        // Metadata only. No body capture or authority publication during traversal.
        public WorkspaceSearchPaths FindPaths(WorkspaceDescriptor workspace, string directory, bool recursive,
            string[] include, string[] exclude, CancellationToken token)
        {
            var filters = new ResourcePathFilter(include, exclude);
            ResolvePath(workspace, directory, true);
            var result = new WorkspaceSearchPaths();
            var pending = new Stack<string>(); pending.Push(directory ?? "");
            var scanned = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var relative = pending.Pop();
                try
                {
                    var path = ResolvePath(workspace, relative, true);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++scanned > 5000) { result.Complete = false; result.Paths.Sort(StringComparer.Ordinal); return result; }
                        var name = Path.GetFileName(entry);
                        if (!IsVisibleFileName(name) || StorageFileSystem.IsReparsePoint(entry)) continue;
                        var target = string.IsNullOrEmpty(relative) ? name : relative.TrimEnd('/', '\\') + "/" + name;
                        // Recheck every component, including renamed parent directories.
                        var checkedPath = ResolvePath(workspace, target, false);
                        if (Directory.Exists(checkedPath))
                        { if (recursive) pending.Push(target); }
                        else if (filters.Matches(target)) result.Paths.Add(target);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { result.Complete = false; result.Unavailable.Add(relative); }
            }
            result.Paths.Sort(StringComparer.Ordinal);
            return result;
        }
    }
}
