using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService : IDisposable
    {
        private readonly AppDataPaths _paths;
        private readonly string _webRoot;
        private readonly IWorkspaceWebVerifierFactory _webHost;
        private readonly PluginStore _store;
        private readonly WorkspaceStore _workspaces;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Candidate> _candidates = new Dictionary<string, Candidate>();
        private readonly Dictionary<string, PluginInstance> _instances = new Dictionary<string, PluginInstance>();
        private readonly Dictionary<string, PluginDataGrant> _data = new Dictionary<string, PluginDataGrant>();
        private readonly Dictionary<string, PluginInstance> _calls = new Dictionary<string, PluginInstance>();
        private readonly Dictionary<string, string> _chatIds = new Dictionary<string, string>();
        private readonly HashSet<string> _revoked = new HashSet<string>();
        private bool _disposed;
        private Task _closing;
        private readonly PluginResultStore _results;
        private sealed class Candidate { public PluginReleaseRecord Package; public string ExpectedRelease; }

        public string StartupError { get; set; }
        public VendorCatalog Vendors { get; private set; }
        public WorkspaceConversationService Conversations { get; private set; }
        public IPluginProjectPicker ProjectPicker { get; set; }
        public IPluginCommandExecutor Executor { get; set; }

        public PluginApplicationService(AppDataPaths paths, string webRoot = null, IWorkspaceWebVerifierFactory webHost = null)
        {
            _paths = paths; _webRoot = webRoot; _webHost = webHost; _store = new PluginStore(paths); _workspaces = new WorkspaceStore(paths);
            Vendors = new VendorCatalog(paths, webRoot); _results = new PluginResultStore(paths);
            Conversations = new WorkspaceConversationService(paths, webHost, CaptureTools);
        }

        public PluginCatalogDto List()
        { using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync) { RequireOpen(); return new PluginCatalogDto { Error = StartupError, Items = _store.List().Select(Item).ToArray() }; } }
        public void ResetAfterRuntimeDataClear()
        {
            lock (_sync)
            {
                RequireOpen();
                if (_instances.Count != 0 || _calls.Count != 0) throw new PluginException("plugins_busy", "Plugin instances must drain before runtime clear.");
                foreach (var grant in _data.Values) grant.Dispose();
                _data.Clear(); _candidates.Clear(); _chatIds.Clear(); _revoked.Clear();
                Conversations = new WorkspaceConversationService(_paths, _webHost, CaptureTools);
            }
        }
        public PluginItemDto Connect(PluginConnectRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            lock (_sync)
            {
                RequireOpen();
                if (request == null) throw new ArgumentNullException(nameof(request));
                var source = new PluginSourceWorkspace(_workspaces.Open(request.RootPath, false, true));
                return Item(_store.Connect(source));
            }
        }

        public PluginItemDto Create(PluginCreateRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 ||
                string.IsNullOrWhiteSpace(request.RootPath) || (request.Description?.Length ?? 0) > 8000)
                throw new PluginException("plugin_create_invalid", "Name, a new source folder and a bounded description are required.");
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (Directory.Exists(request.RootPath) || File.Exists(request.RootPath)) throw new PluginException("target_exists", "Create requires a new source directory.");
                var workspace = _workspaces.Open(request.RootPath, true, true);
                var files = new WorkspaceFileService(_paths);
                var manifest = new PluginManifestV1 { FormatVersion = 1, HostProtocol = 1, Id = "plugin-" + Guid.NewGuid().ToString("N"),
                    Name = request.Name, Version = "1.0.0", Entry = "index.html", Icon = "puzzle",
                    Files = new[] { "plugin.json", "index.html", "app.js", "style.css", "commands.js", "README.md" },
                    Commands = new[] { new PluginCommandDefinition { Id = "echo", Description = "Return a greeting.", Entry = "commands.js",
                        ArgumentsSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Greeting text\"}},\"required\":[\"text\"],\"additionalProperties\":false}"),
                        ResultSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Greeting text\"}},\"required\":[\"text\"],\"additionalProperties\":false}") } } };
                files.CreateText(workspace, "plugin.json", JsonConvert.SerializeObject(manifest, Formatting.Indented));
                files.CreateText(workspace, "index.html", "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"style.css\"><script defer src=\"/.host/sdk.js\"></script><script defer src=\"app.js\"></script><title>Plugin</title></head><body><main><h1 id=\"title\"></h1><p>Workspace плагина готов к разработке.</p><button id=\"run\">Проверить команду</button><output id=\"result\"></output></main></body></html>");
                files.CreateText(workspace, "app.js", "'use strict';\ndocument.getElementById('title').textContent = " + JsonConvert.SerializeObject(request.Name) + ";\ndocument.getElementById('run').onclick = async function () { try { var value = await RNPlugin.invoke('echo', {text:'Плагин работает'}); document.getElementById('result').textContent = value.text; } catch (error) { document.getElementById('result').textContent = error.message; } };\n");
                files.CreateText(workspace, "commands.js", "'use strict';\nRNPluginCommands.register('echo', async function (args) { return {text: args.text}; });\n");
                files.CreateText(workspace, "style.css", "body{margin:0;background:#f5f7f4;color:#25352c;font:14px system-ui}main{padding:24px}button{padding:8px 12px;border:1px solid #c8d5cc;border-radius:6px;background:white}output{display:block;margin-top:16px}\n");
                files.CreateText(workspace, "README.md", "# " + request.Name + "\n\n" + (request.Description ?? "") +
                    "\n\nPlugin protocol v1. Edit ordinary source files; include new files in plugin.json. UI uses RNPlugin.invoke(command,args). Worker command modules register handlers with RNPluginCommands.register(id,async(args,context)=>result). context.files is a scoped project filesystem; no DOM/Office/network. Manifest capabilities project.read/project.write request host access, never grant it. Source workspace and user data projects are separate. Use plugins.check and plugins.apply after edits; checking validates package structure/dependencies, not business correctness. The preview gets a separate scratch data folder. Vendor packages use exact versions and mounts. No npm/build step.\n" +
                    "\nSDK reference:\n- UI loads /.host/sdk.js. await RNPlugin.pickProject() returns a scoped directory handle. Selection is explicit; do not substitute the source workspace.\n- RNPlugin.invoke(id,args,onProgress) returns a promise with cancel(). Commands run outside the screen in a managed Worker.\n- Worker context.files.readText(path), writeText(path,text), open(path), createWriter(path) and root() use project-relative paths. open() returns size/name/readRange(offset,count)/close(). Close readers in finally.\n- Writers accept text/bytes through write(), then close() commits or abort() discards staging. Existing target reads are pinned and rechecked; uncertain writes must be inspected with files.reconcile(path), never retried automatically.\n- context.results.create(relativeName) stages a temporary result; context.results.open(relativeName) returns its retained file. Return semantic names in command results, not runtime transport tokens.\n- context.progress(message) reports bounded progress. Maximum command time is five minutes; no Office, parent bridge, network, local native paths or arbitrary filesystem access.\n- Command schemas use type:object, properties, required and additionalProperties:false; describe every top-level field. A writing command also needs writesData:true and both project.read/project.write capabilities.\n- Vendor dependencies declare exact id/version/mount. Include transitive dependencies explicitly. Changing vendor bytes requires a new version. Manifest and locks do not grant rights.\n- plugins.check and plugins.apply take empty arguments. Runtime owns the prepared candidate and confirmation. Verify the UI in preview before applying.\n");
                return Item(_store.Connect(new PluginSourceWorkspace(workspace)));
            }
        }

        public void EnsureDataTemplates()
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (_webRoot == null || _store.Get("data-templates") != null) return;
                var shipped = Path.Combine(_webRoot, "data-templates");
                var manifest = PluginPackageFiles.ParseManifest(PluginPackageFiles.Utf8.GetString(PluginPackageFiles.Read(shipped, "plugin.json")));
                var destination = Path.Combine(_paths.Root, "plugin-workspaces", "data-templates");
                if (Directory.Exists(destination)) throw new PluginException("plugin_install_incomplete", "Inspect the existing Data Templates source folder before reconnecting: " + destination);
                var captured = manifest.Files.ToDictionary(file => file, file => PluginPackageFiles.Read(shipped, file));
                foreach (var file in captured)
                {
                    var target = PluginPackageFiles.Resolve(destination, file.Key, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(file.Value, 0, file.Value.Length);
                }
                Connect(new PluginConnectRequest { RootPath = destination });
                var checkedPackage = Check("data-templates");
                Apply(new PluginApplyRequest { PluginId = "data-templates", CandidateId = checkedPackage.CandidateId });
            }
        }

        public PluginCandidateDto Check(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen(); var registration = RequireRegistration(pluginId);
                var package = _store.Capture(Source(registration), Vendors);
                if (package.PluginId != pluginId) throw new PluginException("plugin_identity_changed", "Changing plugin.json ID requires a new connection.");
                var id = Guid.NewGuid().ToString("N");
                foreach (var old in _candidates.Where(item => item.Value.Package.PluginId == pluginId).Select(item => item.Key).ToArray()) _candidates.Remove(old);
                _candidates.Add(id, new Candidate { Package = package, ExpectedRelease = registration.ActiveReleaseId });
                return new PluginCandidateDto { PluginId = pluginId, CandidateId = id, Files = package.Files.Count,
                    Bytes = package.Files.Sum(file => file.Payload.ByteLength), Checks = new[] { "manifest", "protocol", "file-integrity", "plugin-vendor-admission" } };
            }
        }

        public PluginItemDto Apply(PluginApplyRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen(); var candidate = RequireCandidate(request.PluginId, request.CandidateId);
                var result = _store.Apply(candidate.Package, candidate.ExpectedRelease); _candidates.Remove(request.CandidateId);
                _revoked.Remove(request.PluginId);
                return Item(result);
            }
        }

        public PluginViewDto Open(PluginOpenRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen(); var registration = RequireRegistration(request.PluginId);
                var preview = !string.IsNullOrEmpty(request.CandidateId);
                if (!preview && _revoked.Contains(request.PluginId)) throw new PluginException("plugin_disabled", "Apply a checked version to enable this plugin again.");
                if (!preview && (!registration.Enabled || registration.ActiveReleaseId == null)) throw new PluginException("plugin_disabled", "Apply a checked version before opening this plugin.");
                var release = preview ? RequireCandidate(request.PluginId, request.CandidateId).Package :
                    _store.ReadRelease(new PluginReleaseIdentity(request.PluginId, registration.ActiveReleaseId));
                var instance = new PluginInstance(_paths, release, preview) { Results = _results }; _instances.Add(instance.Id, instance);
                return new PluginViewDto { InstanceId = instance.Id, PluginId = request.PluginId,
                    ReleaseId = release.ReleaseId, Preview = preview, EntryUrl = instance.EntryUrl };
            }
        }

        public PluginInstance FindInstance(string origin)
        { lock (_sync) { return _disposed ? null : _instances.Values.FirstOrDefault(instance => instance.Origin == origin && !instance.Token.IsCancellationRequested); } }
        public Stream DownloadResult(PluginInstance instance, string id) { return _results.OpenDownload(instance,id); }
        public byte[] ReadResult(PluginInstance instance, string id, long offset, int count) { return _results.Read(instance, id, offset, count, instance.Token); }
        public byte[] ReadPackageFile(PluginInstance instance, string path) { using (PluginStore.AcquireRuntimeUse(_paths)) return _store.ReadFile(instance.Release, path); }
        public async Task CloseInstanceAsync(string instanceId)
        {
            PluginInstance instance;
            lock (_sync) { if (!_instances.TryGetValue(instanceId, out instance)) return; _instances.Remove(instanceId); }
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            await instance.CloseAsync().ConfigureAwait(false);
            lock (_sync)
                if (!_instances.Values.Any(value => value.ResultOwnerId == instance.ResultOwnerId)) _results.ReleaseOwner(instance.ResultOwnerId);
            }
        }
        public async Task<PluginItemDto> DisableAsync(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            PluginInstance[] instances; PluginRegistration registration;
            lock (_sync)
            {
                _revoked.Add(pluginId);
                foreach (var key in _data.Keys.Where(key => key.StartsWith(pluginId + ":", StringComparison.Ordinal)).ToArray()) { _data[key].Dispose(); _data.Remove(key); }
                foreach (var key in _candidates.Where(item => item.Value.Package.PluginId == pluginId).Select(item => item.Key).ToArray()) _candidates.Remove(key);
                instances = _instances.Values.Where(instance => instance.Manifest.Id == pluginId).ToArray();
                foreach (var instance in instances) instance.Dispose();
                registration = _store.Disable(pluginId);
            }
            foreach (var instance in instances) await CloseInstanceAsync(instance.Id).ConfigureAwait(false);
            await Task.WhenAll(instances.Select(instance => instance.CommandCompleted)).ConfigureAwait(false);
            return Item(registration);
            }
        }

        public async Task<PluginFrameResponse> DispatchAsync(PluginInstance instance, PluginFrameRequest request, CancellationToken token, Action<string> progress = null)
        {
            var response = new PluginFrameResponse { RequestId = request?.RequestId };
            try
            {
                if (request == null || request.Version != 1 || string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 80 ||
                    !Enum.IsDefined(typeof(PluginMethod), request.Method) || instance.Token.IsCancellationRequested)
                    throw new PluginException("plugin_protocol_invalid", "Invalid or revoked plugin request.");
                if (request.Method == PluginMethod.Ready) response.Result = new PluginFrameResult { Name = instance.Manifest.Name, Handle = instance.Data?.ProjectId };
                else if (request.Method == PluginMethod.ReportError) throw new PluginException("plugin_script_error", request.Message ?? "Plugin script failed.");
                else if (request.Method == PluginMethod.PickProject)
                {
                    if (instance.CommandOperation) throw new PluginException("project_required", "Commands cannot change their bound project.");
                    var source = Source(RequireRegistration(instance.Manifest.Id));
                    PluginDataSelection selection;
                    if (instance.Preview)
                        selection = new PluginDataSelection { RootPath = Path.Combine(_paths.Root, "plugin-preview-data", instance.Id), Writable = true };
                    else
                    {
                        if (ProjectPicker == null) throw new PluginException("project_picker_unavailable", "Project selection is unavailable in this host.");
                        selection = await ProjectPicker.PickAsync(token).ConfigureAwait(false);
                        if (selection == null) throw new OperationCanceledException();
                    }
                    PluginDataGrant.RequireSeparateRoot(selection.RootPath, source);
                    token.ThrowIfCancellationRequested(); instance.Token.ThrowIfCancellationRequested();
                    PluginPackageFiles.Resolve(selection.RootPath,"grant-probe",false);
                    var workspace = _workspaces.Open(selection.RootPath, instance.Preview, selection.Writable);
                    var grant = new PluginDataGrant(workspace, source, selection.Writable);
                    await instance.BindAsync(grant).ConfigureAwait(false);
                    if (!instance.Preview)
                    {
                        var key = instance.Manifest.Id + ":" + instance.Release.ReleaseId;
                        PluginDataGrant previous;
                        lock (_sync) { _data.TryGetValue(key, out previous); _data[key] = grant; }
                        if (previous != null && previous != grant) previous.Dispose();
                    }
                    response.Result = new PluginFrameResult { Name = workspace.Name, Kind = "directory", Handle = grant.ProjectId, Writable = grant.Writable };
                }
                else if (request.Method == PluginMethod.Invoke)
                {
                    if (instance.CommandOperation) throw new PluginException("plugin_method_denied", "Commands cannot start nested command instances.");
                    var result = await InvokeAsync(instance, request.Command, request.ArgumentsJson, CancellationToken.None, progress, request.RequestId).ConfigureAwait(false);
                    response.Result = new PluginFrameResult { Json = result.Json };
                }
                else if (request.Method == PluginMethod.Cancel)
                {
                    PluginInstance operation;
                    lock (_sync) _calls.TryGetValue(instance.Id + ":" + request.InvocationId, out operation);
                    if (operation != null) { await CloseInstanceAsync(operation.Id).ConfigureAwait(false); await operation.CommandCompleted.ConfigureAwait(false); }
                    response.Result = new PluginFrameResult();
                }
                else response.Result = await instance.FilesAsync(request, token).ConfigureAwait(false);
                response.Ok = true;
            }
            catch (PluginException error) { response.ErrorCode = error.Code; response.Message = error.Message; }
            catch (WorkspaceFileException error) { response.ErrorCode = error.Code; response.Message = error.Message; }
            catch (FileNotFoundException error) { response.ErrorCode = "not_found"; response.Message = error.Message; }
            catch (OperationCanceledException) { response.ErrorCode = instance.EffectUnknown ? "effect_unknown" : "cancelled"; response.Message = "Plugin operation was cancelled."; }
            catch (Exception error) { response.ErrorCode = instance.EffectUnknown ? "effect_unknown" : "plugin_failed"; response.Message = error.Message; }
            return response;
        }

        public async Task<PluginCommandResult> InvokeAsync(PluginInstance context, string commandId, string argumentsJson, CancellationToken token,
            Action<string> progress = null, string callId = null, Action onDispatch = null)
        {
            context.Token.ThrowIfCancellationRequested();
            if (!context.Preview) { var active = RequireRegistration(context.Manifest.Id);
                if (!active.Enabled || active.ActiveReleaseId != context.Release.ReleaseId) throw new PluginException("plugin_version_changed", "Reopen the active plugin before starting a new command."); }
            if (Executor == null) throw new PluginException("plugin_executor_unavailable", "This host has no plugin command executor.");
            var command = context.Manifest.Commands.SingleOrDefault(value => value.Id == commandId);
            if (command == null) throw new PluginException("plugin_command_missing", "Command is not part of this exact plugin package.");
            ValidateJson(argumentsJson, command.ArgumentsSchema);
            var operation = new PluginInstance(_paths, context.Release, context.Preview, true, command.WritesData) { OnDispatch = onDispatch, Results = _results, ResultOwnerId = context.ResultOwnerId };
            if (context.Data != null) { await operation.BindAsync(context.Data).ConfigureAwait(false); await operation.InheritSourcesAsync(context).ConfigureAwait(false); }
            var tracked = false;
            try
            {
                lock (_sync)
                {
                    RequireOpen();
                    context.Token.ThrowIfCancellationRequested();
                    if (!context.Preview)
                    {
                        var current = RequireRegistration(context.Manifest.Id);
                        if (_revoked.Contains(context.Manifest.Id) || !current.Enabled || current.ActiveReleaseId != context.Release.ReleaseId)
                            throw new PluginException("plugin_version_changed", "The plugin changed before command acceptance.");
                    }
                    if (callId != null && _calls.ContainsKey(context.Id + ":" + callId)) throw new PluginException("plugin_call_pending", "This request ID already has an active command.");
                    _instances.Add(operation.Id, operation); tracked = true;
                    if (callId != null) _calls.Add(context.Id + ":" + callId, operation);
                }
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token,
                    context.Data?.Lifetime.Token ?? CancellationToken.None))
                {
                    linked.CancelAfter(TimeSpan.FromMinutes(5));
                    var json = await Executor.ExecuteAsync(operation, command, argumentsJson, linked.Token, progress).ConfigureAwait(false);
                    if (operation.EffectUnknown) throw new PluginException("effect_unknown", "A command cannot report success after an uncertain file effect. Inspect it before retrying.");
                    ValidateJson(json, command.ResultSchema);
                    return new PluginCommandResult { Json = json, HadVerifiedWrites = operation.HasVerifiedWrites, EffectUnknown = operation.EffectUnknown };
                }
            }
            catch (Exception error) when (operation.EffectUnknown)
            { throw new PluginException("effect_unknown", "Plugin command has an unverified file effect. Inspect before retrying. " + error.Message); }
            finally
            {
                try
                {
                    lock (_sync) { if (tracked && callId != null) _calls.Remove(context.Id + ":" + callId); }
                    if (tracked) await CloseInstanceAsync(operation.Id).ConfigureAwait(false);
                    else await operation.CloseAsync().ConfigureAwait(false);
                }
                finally { operation.CompleteCommand(); }
            }
        }

        internal PluginInstance CaptureCommandContext(string pluginId, string expectedProjectId = null, bool requirePreparedProject = false)
        {
            lock (_sync)
            {
                var registration = RequireRegistration(pluginId);
                PluginDataGrant grant;
                _data.TryGetValue(pluginId + ":" + registration.ActiveReleaseId, out grant);
                if (requirePreparedProject && grant?.ProjectId != expectedProjectId)
                    throw new PluginException("plugin_project_changed", "The selected data project changed while waiting. Request a new operation.");
                if (grant != null) grant.Lifetime.Token.ThrowIfCancellationRequested();
                var view = Open(new PluginOpenRequest { PluginId = pluginId });
                var instance = _instances[view.InstanceId];
                if (grant != null) instance.BindAsync(grant).GetAwaiter().GetResult();
                return instance;
            }
        }
        public PluginChatStateDto ChatState(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                var source = Source(RequireRegistration(pluginId)); string sessionId;
                if (!_chatIds.TryGetValue(pluginId, out sessionId))
                {
                    var previous = Conversations.ListSessions(source.Workspace).FirstOrDefault();
                    sessionId = previous?.Id ?? Conversations.CreateSession(source.Workspace, "Разработка: " + RequireRegistration(pluginId).Name).Id;
                    _chatIds[pluginId] = sessionId;
                }
                var session = Conversations.GetSession(source.Workspace, sessionId);
                return new PluginChatStateDto { PluginId = pluginId, SessionId = sessionId,
                    PendingId = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.PendingId,
                    PendingTool = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.Name,
                    PendingArguments = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.ArgumentsJson,
                    State = Conversations.ReadState(source.Workspace, sessionId), Messages = session.Messages.Where(message => message.Role == "user" || message.Role == "assistant")
                        .Reverse().Take(100).Reverse().Select(message => new PluginChatMessageDto { Role = message.Role, Text = message.Content }).ToArray() };
            }
        }
        public async Task<PluginChatStateDto> SubmitChatAsync(PluginChatRequest request, AppSettings settings, Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var state = ChatState(request.PluginId); var source = Source(RequireRegistration(request.PluginId));
            if (request.Input == null || request.Input.SessionId != null && request.Input.SessionId != state.SessionId)
                throw new PluginException("plugin_chat_changed", "The development chat changed.");
            request.Input.SessionId = state.SessionId;
            await Conversations.SubmitInputAsync(source.Workspace, request.Input, settings, key, cancellationToken: token).ConfigureAwait(false);
            return ChatState(request.PluginId);
            }
        }

        public async Task<PluginChatStateDto> ControlChatAsync(PluginChatControlRequest request, bool cancel, AppSettings settings, Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var state = ChatState(request.PluginId); var source = Source(RequireRegistration(request.PluginId));
            if (cancel) await Conversations.CancelRunAsync(source.Workspace, new RNAssistant.Runtime.Contracts.WorkspaceRunControlRequest {
                SessionId = state.SessionId, RunId = request.RunId, Cursor = request.Cursor }, token).ConfigureAwait(false);
            else await Conversations.ResolvePendingAsync(source.Workspace, state.SessionId, request.PendingId, request.Approve, settings, key,
                cancellationToken: token, modelDigest: Conversations.GetSession(source.Workspace,state.SessionId).LastRun?.ModelConfiguration?.Digest).ConfigureAwait(false);
            return ChatState(request.PluginId);
            }
        }

        private PluginDataGrant CurrentDataProject()
        {
            lock (_sync)
            {
                RequireOpen();
                var registration = _store.Get("data-templates");
                if (registration == null || !registration.Enabled || registration.ActiveReleaseId == null) return null;
                PluginDataGrant grant;
                _data.TryGetValue("data-templates:" + registration.ActiveReleaseId, out grant);
                return grant != null && !grant.Lifetime.IsCancellationRequested ? grant : null;
            }
        }

        private PluginDataGrant RequireDataProject(string projectId)
        {
            var grant = CurrentDataProject();
            if (grant == null || string.IsNullOrEmpty(projectId) || grant.ProjectId != projectId)
                throw new PluginException("plugin_project_changed", "The selected data project changed. Open it again before continuing the chat.");
            return grant;
        }

        private static ProjectChatProjectDto ProjectChatProject(PluginDataGrant grant)
        { return new ProjectChatProjectDto { ProjectId = grant.ProjectId, WorkspaceId = grant.DataWorkspaceId, Name = grant.Workspace.Name }; }

        public ProjectChatCatalogDto ProjectChats()
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = CurrentDataProject();
            if (grant == null) return new ProjectChatCatalogDto { Chats = Array.Empty<ChatSessionHeader>() };
            var chats = Conversations.ListSessions(grant.Workspace);
            RequireDataProject(grant.ProjectId);
            return new ProjectChatCatalogDto { Project = ProjectChatProject(grant), Chats = chats };
            }
        }

        public ProjectChatStateDto ProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
                throw new PluginException("project_chat_invalid", "A project chat is required.");
            var grant = RequireDataProject(request.ProjectId);
            var session = Conversations.GetSession(grant.Workspace, request.SessionId);
            if (session == null) throw new PluginException("project_chat_missing", "The chat is not in the selected data project.");
            var snapshot = Conversations.ReadState(grant.Workspace, session.Id);
            RequireDataProject(request.ProjectId);
            return new ProjectChatStateDto { Project = ProjectChatProject(grant), SessionId = session.Id,
                State = snapshot,
                PendingId = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.PendingId,
                PendingTool = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.Name,
                PendingArguments = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.ArgumentsJson,
                Messages = session.Messages.Where(message => message.Role == "user" || message.Role == "assistant")
                    .Reverse().Take(100).Reverse().Select(message => new PluginChatMessageDto { Role = message.Role, Text = message.Content }).ToArray() };
            }
        }

        public ProjectChatStateDto CreateProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = RequireDataProject(request?.ProjectId);
            var title = string.IsNullOrWhiteSpace(request.Title) ? "Новый чат" : request.Title.Trim();
            if (title.Length > 200) throw new PluginException("project_chat_invalid", "The chat title is too long.");
            var session = Conversations.CreateSession(grant.Workspace, title);
            return ProjectChat(new ProjectChatRequest { ProjectId = request.ProjectId, SessionId = session.Id });
            }
        }

        public ProjectChatCatalogDto RenameProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = RequireDataProject(request?.ProjectId);
            Conversations.RenameSession(grant.Workspace, request.SessionId, request.Title);
            return ProjectChats();
            }
        }

        public ProjectChatCatalogDto DeleteProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = RequireDataProject(request?.ProjectId);
            Conversations.DeleteSession(grant.Workspace, request.SessionId);
            return ProjectChats();
            }
        }

        public async Task<ProjectChatStateDto> SubmitProjectChatAsync(ProjectChatRequest request, AppSettings settings,
            Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = RequireDataProject(request?.ProjectId);
            if (request.Input == null || request.Input.SessionId != request.SessionId)
                throw new PluginException("project_chat_invalid", "The submitted chat identity changed.");
            if (Conversations.GetSession(grant.Workspace, request.SessionId) == null)
                throw new PluginException("project_chat_missing", "The chat is not in the selected data project.");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, grant.Lifetime.Token))
                await Conversations.SubmitInputAsync(grant.Workspace, request.Input, settings, key,
                    cancellationToken: linked.Token).ConfigureAwait(false);
            return ProjectChat(request);
            }
        }

        public async Task<ProjectChatStateDto> ControlProjectChatAsync(ProjectChatRequest request, bool cancel,
            AppSettings settings, Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var grant = RequireDataProject(request?.ProjectId);
            var session = Conversations.GetSession(grant.Workspace, request.SessionId);
            if (session == null) throw new PluginException("project_chat_missing", "The chat is not in the selected data project.");
            RNAssistant.Runtime.Contracts.WorkspaceCancellationReceipt cancellation = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, grant.Lifetime.Token))
            {
                if (cancel) cancellation = await Conversations.CancelRunAsync(grant.Workspace, new RNAssistant.Runtime.Contracts.WorkspaceRunControlRequest {
                    SessionId = session.Id, RunId = request.RunId, Cursor = request.Cursor }, linked.Token).ConfigureAwait(false);
                else await Conversations.ResolvePendingAsync(grant.Workspace, session.Id, request.PendingId, request.Approve,
                    settings, key, cancellationToken: linked.Token, modelDigest: session.LastRun?.ModelConfiguration?.Digest).ConfigureAwait(false);
            }
            if (cancellation?.Status == RNAssistant.Runtime.Contracts.WorkspaceCancellationStatus.StaleState)
                throw new PluginException("project_chat_stale", "The run changed. Refresh the chat before requesting cancellation again.");
            if (cancellation?.Status == RNAssistant.Runtime.Contracts.WorkspaceCancellationStatus.Unconfirmed)
                throw new PluginException("project_chat_cancel_unknown", "Cancellation could not be confirmed. Inspect the current run state.");
            var state = ProjectChat(request);
            if (cancellation != null) state.CancelStatus = cancellation.Status;
            return state;
            }
        }
        public HtmlVendorSourceDto ResolveHtmlVendor(PluginVendorRequest request)
        {
            var scripts = new List<string>(); var styles = new List<string>(); var seen = new HashSet<string>();
            Action<string,string> include = null;
            include = (id, version) => {
                if (!seen.Add(id + "@" + version)) return;
                var package = Vendors.Resolve(id, version); VendorAdmission.RequireHtml(package.Manifest);
                if (package.Manifest.EntryPoints.Any(entry => entry.Kind == VendorEntryKind.Asset))
                    throw new PluginException("html_vendor_denied", "Inline HTML vendors cannot require external font/image assets.");
                foreach (var dependency in package.Manifest.Dependencies) include(dependency.Id,dependency.Version);
                scripts.Add("/* " + package.Manifest.Id + "@" + package.Manifest.Version + "\n" +
                    string.Join("\n", package.Manifest.Licenses.Select(file => PluginPackageFiles.Utf8.GetString(package.Files[file]))).Replace("*/","* /") + "\n*/");
                foreach(var entry in package.Manifest.EntryPoints)
                    (entry.Kind == VendorEntryKind.Style ? styles : scripts).Add(PluginPackageFiles.Utf8.GetString(package.Files[entry.Path]));
            };
            include(request.Id,request.Version);
            return new HtmlVendorSourceDto { Js=string.Join("\n;\n", scripts),Css=string.Join("\n", styles) };
        }

        private static void ValidateJson(string json, JObject schema)
        {
            if (json == null || json.Length > 1024 * 1024) throw new PluginException("plugin_payload_invalid", "Command JSON exceeds its bound.");
            string error; JObject value;
            try { value = JObject.Parse(json); } catch (JsonException ex) { throw new PluginException("plugin_payload_invalid", ex.Message); }
            if (!ToolSchemaSupport.ValidateArguments(value, schema, false, out error)) throw new PluginException("plugin_payload_invalid", error);
        }
        private Candidate RequireCandidate(string pluginId, string id)
        { Candidate value; if (id == null || !_candidates.TryGetValue(id, out value) || value.Package.PluginId != pluginId) throw new PluginException("plugin_candidate_missing", "Check this plugin again before applying or previewing."); return value; }
        private PluginRegistration RequireRegistration(string id)
        { return _store.Get(id) ?? throw new PluginException("plugin_missing", "Plugin is not connected."); }
        private PluginSourceWorkspace Source(PluginRegistration registration)
        {
            var workspace = _workspaces.Open(registration.SourceRoot, false, true);
            if (workspace.WorkspaceId != registration.SourceWorkspaceId) throw new PluginException("plugin_source_changed", "Source workspace identity changed.");
            return new PluginSourceWorkspace(workspace);
        }
        private void RequireOpen() { if (_disposed) throw new ObjectDisposedException(nameof(PluginApplicationService)); }
        private static PluginItemDto Item(PluginRegistration value)
        { return new PluginItemDto { PluginId = value.PluginId, Name = value.Name, Icon = value.Icon, SourceWorkspaceId = value.SourceWorkspaceId,
            SourceRoot = value.SourceRoot, ReleaseId = value.ActiveReleaseId, Enabled = value.Enabled }; }
        public Task CloseAsync()
        {
            lock (_sync)
            {
                if (_closing != null) return _closing;
                _disposed = true;
                var instances = _instances.Values.ToArray(); _instances.Clear();
                foreach (var instance in instances) instance.Dispose();
                foreach (var data in _data.Values) data.Dispose();
                _results.Dispose();
                _closing = CloseInstancesAsync(instances); return _closing;
            }
        }
        private async Task CloseInstancesAsync(PluginInstance[] instances)
        {
            if (instances.Length == 0) { _results.Clear(); return; }
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            foreach (var instance in instances) await instance.CloseAsync().ConfigureAwait(false);
            await Task.WhenAll(instances.Select(instance => instance.CommandCompleted)).ConfigureAwait(false);
            _results.Clear();
            }
        }
        public void Dispose() { CloseAsync(); }
    }
}
