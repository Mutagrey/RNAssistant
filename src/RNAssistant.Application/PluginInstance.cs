using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Issued only by the application after explicit selection. Never serialized into
    // a plugin manifest, copied from a code workspace, or accepted from frame messages.
    public sealed class PluginDataGrant : IDisposable
    {
        internal WorkspaceDescriptor Workspace { get; private set; }
        internal CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();
        public string DataWorkspaceId { get { return Workspace.WorkspaceId; } }
        public string ProjectId { get; } = "data_" + Guid.NewGuid().ToString("N");
        public bool Writable { get; private set; }
        internal PluginDataGrant(WorkspaceDescriptor workspace, PluginSourceWorkspace source, bool writable)
        {
            if (workspace.WorkspaceId == source.SourceWorkspaceId || PathsOverlap(workspace.RootPath, source.Workspace.RootPath))
                throw new PluginException("source_is_not_data", "Choose a data folder outside the plugin source workspace.");
            Workspace = workspace; Writable = writable && !workspace.ReadOnly;
        }
        internal static void RequireSeparateRoot(string selected, PluginSourceWorkspace source)
        { if (PathsOverlap(selected,source.Workspace.RootPath)) throw new PluginException("source_is_not_data","Choose a data folder outside the plugin source workspace."); }
        internal static bool PathsOverlap(string left, string right)
        {
            left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return left.StartsWith(right, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose() { if (!Lifetime.IsCancellationRequested) Lifetime.Cancel(); }
    }

    public sealed class PluginInstance : IDisposable
    {
        public const long MaximumDataBytes = 256L * 1024 * 1024;
        public const int MaximumChunkBytes = 256 * 1024;
        private readonly WorkspaceFileService _files;
        private readonly IDisposable _runtimeUse;
        private readonly string _stagingRoot;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _commandCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, WorkspaceFileSnapshot> _reads = new Dictionary<string, WorkspaceFileSnapshot>();
        private readonly Dictionary<string, WorkspaceFileSnapshot> _observed = new Dictionary<string, WorkspaceFileSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, Write> _writes = new Dictionary<string, Write>();
        private bool _closed;
        private sealed class Write
        { public string Path; public string StagedPath; public FileStream Stream; public ResourceRef Expected; public bool Result; }

        public string ResultOwnerId { get; internal set; }
        internal PluginResultStore Results { get; set; }
        public string Id { get; } = "instance_" + Guid.NewGuid().ToString("N");
        public string Origin { get { return "https://" + Id.Replace("_", "-") + ".rnassistant-plugin.invalid"; } }
        public string EntryUrl { get { return Origin + "/" + Manifest.Entry; } }
        public PluginReleaseRecord Release { get; private set; }
        public PluginManifestV1 Manifest { get; private set; }
        public bool Preview { get; private set; }
        public bool CommandOperation { get; private set; }
        public bool MayWrite { get; private set; }
        public bool HasVerifiedWrites { get; private set; }
        public bool EffectUnknown { get; private set; }
        public CancellationToken Token { get { return _lifetime.Token; } }
        internal Action OnDispatch { get; set; }
        internal PluginDataGrant Data { get; private set; }
        internal Task CommandCompleted { get { return CommandOperation ? _commandCompleted.Task : Task.CompletedTask; } }
        internal void CompleteCommand() { _commandCompleted.TrySetResult(true); }

        internal PluginInstance(AppDataPaths paths, PluginReleaseRecord release, bool preview, bool command = false, bool mayWrite = true)
        {
            ResultOwnerId = Id; Release = release; Manifest = PluginPackageFiles.ParseManifest(release.ManifestJson);
            Preview = preview; CommandOperation = command; MayWrite = mayWrite;
            _files = new WorkspaceFileService(paths);
            _stagingRoot = Path.Combine(paths.Root, "plugin-staging", Id);
            _runtimeUse = PluginStore.AcquireRuntimeUse(paths);
        }

        internal async Task BindAsync(PluginDataGrant grant)
        {
            PluginDataGrant previous = null;
            await _gate.WaitAsync(Token).ConfigureAwait(false);
            try
            {
                if (_writes.Count > 0) throw new PluginException("plugin_busy", "Finish or abort pending writes before changing the project.");
                previous = Data;
                _reads.Clear(); _observed.Clear(); Data = grant;
            }
            finally { _gate.Release(); }
            if (Preview && !CommandOperation && previous != null && previous != grant) previous.Dispose();
        }

        internal async Task InheritSourcesAsync(PluginInstance context)
        {
            await context._gate.WaitAsync(context.Token).ConfigureAwait(false);
            try { if (Data == context.Data) foreach (var entry in context._observed) _observed[entry.Key] = entry.Value; }
            finally { context._gate.Release(); }
        }

        internal async Task<PluginFrameResult> FilesAsync(PluginFrameRequest request, CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Token, Data?.Lifetime.Token ?? CancellationToken.None))
            {
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (request.Method == PluginMethod.OpenResult) return Results.Describe(this, request.Path);
                    if (request.Method == PluginMethod.DropResult) { Results.Drop(this, request.Path); return new PluginFrameResult(); }
                    if (request.Method == PluginMethod.BeginResult)
                    {
                        if (!CommandOperation || _writes.Count >= 4) throw new PluginException("plugin_result_denied", "Results are created by bounded command operations.");
                        if (!PluginPackageFiles.ValidPath(request.Path) || request.Path.Length > 240) throw new PluginException("plugin_result_name", "A relative result name is required.");
                        Directory.CreateDirectory(_stagingRoot); var resultId = Guid.NewGuid().ToString("N"); var path = Path.Combine(_stagingRoot,resultId);
                        _writes.Add(resultId,new Write { Result=true,Path=request.Path,StagedPath=path,Stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None) });
                        return new PluginFrameResult {Handle=resultId,Url=Origin+"/.result/write/"+resultId};
                    }
                    if (request.Method == PluginMethod.CommitResult)
                    {
                        var output=FindWrite(request.Handle); if(!output.Result)throw new PluginException("plugin_result_denied","Not a result writer.");
                        output.Stream.Flush(true);output.Stream.Dispose();
                        try{return Results.Publish(this,output.Path,output.StagedPath,linked.Token);}finally{AbortWrite(request.Handle);}
                    }
                    if (request.Method == PluginMethod.AbortWrite) { AbortWrite(request.Handle); return new PluginFrameResult(); }
                    RequireData(false); Data.Lifetime.Token.ThrowIfCancellationRequested();
                    if (request.ProjectId != Data.ProjectId) throw new PluginException("project_changed", "The selected data project changed. Open its current handles again.");
                    var workspace = Data.Workspace;
                    switch (request.Method)
                    {
                        case PluginMethod.ReconcileFile:
                            RequireData(true);
                            var recovery = _files.ReconcileUncertain(workspace,request.Path);
                            _observed.Remove(request.Path);
                            return new PluginFrameResult {Json=Newtonsoft.Json.JsonConvert.SerializeObject(recovery)};
                        case PluginMethod.List:
                            var page = _files.ListPage(workspace, request.Path ?? "", null, 200, request.Offset, linked.Token);
                            if (page.ScanLimitReached) throw new PluginException("directory_limit", "Narrow the directory; the scan limit was reached.");
                            return new PluginFrameResult { Items = page.Entries.Select(item => new WorkspaceFileEntryDto {
                                Name = item.Name, Path = string.IsNullOrEmpty(request.Path) ? item.Name : request.Path + "/" + item.Name,
                                IsDirectory = item.IsDirectory, ByteLength = item.ByteLength }).ToArray(),
                                NextOffset = page.Offset + page.Entries.Count < page.MatchedEntries ? (int?)(page.Offset + page.Entries.Count) : null };
                        case PluginMethod.Stat:
                            var statPath = PluginPackageFiles.Resolve(workspace.RootPath, request.Path, false);
                            if (Directory.Exists(statPath)) return new PluginFrameResult { Kind = "directory", Name = Path.GetFileName(statPath) };
                            if (File.Exists(statPath)) return new PluginFrameResult { Kind = "file", Name = Path.GetFileName(statPath), ByteLength = new FileInfo(statPath).Length };
                            throw new PluginException("not_found", "Project member does not exist.");
                        case PluginMethod.OpenFile:
                            if (_reads.Count >= 256) throw new PluginException("read_limit", "Close unused file readers first.");
                            WorkspaceFileSnapshot pin;
                            if (!CommandOperation || !_observed.TryGetValue(request.Path, out pin)) pin = _files.CaptureFile(workspace, request.Path, MaximumDataBytes, linked.Token);
                            var handle = Guid.NewGuid().ToString("N"); _reads.Add(handle, pin); _observed[request.Path] = pin;
                            return new PluginFrameResult { Handle = handle, ByteLength = pin.ByteLength, Name = Path.GetFileName(request.Path), Url = Origin + "/.data/read/" + handle };
                        case PluginMethod.CloseFile:
                            _reads.Remove(request.Handle ?? ""); return new PluginFrameResult();
                        case PluginMethod.MakeDirectory:
                            RequireData(true);
                            var priorDirectoryUnknown = EffectUnknown;
                            var created = _files.CreateDirectory(workspace, request.Path, () => { EffectUnknown = true; OnDispatch?.Invoke(); });
                            EffectUnknown = priorDirectoryUnknown; HasVerifiedWrites |= created; return new PluginFrameResult();
                        case PluginMethod.BeginWrite:
                            RequireData(true);
                            if (_writes.Count >= 4) throw new PluginException("write_limit", "Close pending writes first.");
                            var absolute = PluginPackageFiles.Resolve(workspace.RootPath, request.Path, false);
                            WorkspaceFileSnapshot observed; _observed.TryGetValue(request.Path, out observed);
                            if (File.Exists(absolute) && observed == null) throw new PluginException("read_required", "Read the existing file before replacing it.");
                            if (_writes.Values.Any(write => write.Path == request.Path)) throw new PluginException("write_pending", "This file already has a pending writer.");
                            Directory.CreateDirectory(_stagingRoot);
                            var writeId = Guid.NewGuid().ToString("N"); var staged = Path.Combine(_stagingRoot, writeId);
                            _writes.Add(writeId, new Write { Path = request.Path, StagedPath = staged, Expected = observed?.Reference,
                                Stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None) });
                            return new PluginFrameResult { Handle = writeId, Url = Origin + "/.data/write/" + writeId };
                        case PluginMethod.AbortWrite:
                            AbortWrite(request.Handle); return new PluginFrameResult();
                        case PluginMethod.CommitWrite:
                            RequireData(true);
                            var writer = FindWrite(request.Handle);
                            writer.Stream.Flush(true); writer.Stream.Dispose();
                            try
                            {
                                var priorWriteUnknown = EffectUnknown;
                                var saved = _files.CommitStagedFile(workspace, writer.Path, writer.StagedPath, writer.Expected, MaximumDataBytes,
                                    () => { EffectUnknown = true; OnDispatch?.Invoke(); }, linked.Token);
                                EffectUnknown = priorWriteUnknown; HasVerifiedWrites = true; _observed[writer.Path] = saved;
                                return new PluginFrameResult { ByteLength = saved.ByteLength };
                            }
                            finally { AbortWrite(request.Handle); }
                        default: throw new PluginException("plugin_method_denied", "This is not a file operation.");
                    }
                }
                finally { _gate.Release(); }
            }
        }

        public async Task<byte[]> ReadRangeAsync(string handle, long offset, int count, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                RequireData(false); Token.ThrowIfCancellationRequested(); Data.Lifetime.Token.ThrowIfCancellationRequested();
                WorkspaceFileSnapshot pin;
                if (!_reads.TryGetValue(handle, out pin) || offset < 0 || count < 0 || count > MaximumChunkBytes || offset > pin.ByteLength - count)
                    throw new PluginException("source_range_invalid", "The source handle or bounded range is invalid.");
                using (var stream = _files.OpenExactRead(Data.Workspace, pin.RelativePath, pin.Reference, MaximumDataBytes, token))
                {
                    stream.Seek(offset, SeekOrigin.Begin); var bytes = new byte[count]; var read = 0;
                    while (read < count) { var n = stream.Read(bytes, read, count - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                    return bytes;
                }
            }
            finally { _gate.Release(); }
        }

        public async Task<long> WriteChunkAsync(string handle, long offset, byte[] bytes, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Token.ThrowIfCancellationRequested();
                var writer = FindWrite(handle);
                if (!writer.Result) { RequireData(true); Data.Lifetime.Token.ThrowIfCancellationRequested(); }
                if (bytes == null || bytes.Length > MaximumChunkBytes || offset != writer.Stream.Length || offset > MaximumDataBytes - bytes.Length)
                    throw new PluginException("write_range_invalid", "Staging requires sequential bounded chunks.");
                writer.Stream.Write(bytes, 0, bytes.Length); return writer.Stream.Length;
            }
            finally { _gate.Release(); }
        }

        private Write FindWrite(string handle)
        { Write write; if (handle == null || !_writes.TryGetValue(handle, out write)) throw new PluginException("write_missing", "Writer is unavailable."); return write; }
        private void AbortWrite(string handle)
        {
            Write write;
            if (handle == null || !_writes.TryGetValue(handle, out write)) return;
            write.Stream.Dispose(); _writes.Remove(handle);
            if (File.Exists(write.StagedPath)) File.Delete(write.StagedPath);
        }
        private void RequireData(bool write)
        {
            if (_closed || Token.IsCancellationRequested) throw new PluginException("plugin_revoked", "Plugin instance was revoked.");
            if (Data == null || Data.Lifetime.IsCancellationRequested) throw new PluginException("project_required", "Select a separate data project first.");
            if (!Manifest.Capabilities.Contains("project.read") || write && (!MayWrite || !Data.Writable || !Manifest.Capabilities.Contains("project.write")))
                throw new PluginException("plugin_capability_denied", "This operation has no project permission.");
        }
        public void Dispose()
        {
            if (_closed) return;
            _closed = true; _lifetime.Cancel();
            if (Preview && !CommandOperation) Data?.Dispose();
        }
        public async Task CloseAsync()
        {
            Dispose(); await _gate.WaitAsync().ConfigureAwait(false);
            try { foreach (var handle in _writes.Keys.ToArray()) AbortWrite(handle); _reads.Clear(); _observed.Clear();
                if (Directory.Exists(_stagingRoot) && !Directory.EnumerateFileSystemEntries(_stagingRoot).Any()) Directory.Delete(_stagingRoot); }
            finally { _runtimeUse.Dispose(); _gate.Release(); }
        }
    }
}
