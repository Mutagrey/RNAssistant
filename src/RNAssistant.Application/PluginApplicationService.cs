using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
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
        private readonly PluginIconLibrary _icons;
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
            _paths = paths; _webRoot = webRoot; _webHost = webHost; _store = new PluginStore(paths); _icons = new PluginIconLibrary(paths); _workspaces = new WorkspaceStore(paths);
            Vendors = new VendorCatalog(paths, webRoot); _results = new PluginResultStore(paths);
            Conversations = new WorkspaceConversationService(paths, webHost, CaptureTools);
        }

        public PluginCatalogDto List()
        { using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync) { RequireOpen(); return new PluginCatalogDto { Error = StartupError, DefaultSourceParent = DefaultSourceParent, Items = _store.List().Where(item => !item.Hidden).Select(Item).ToArray() }; } }
        private string DefaultSourceParent { get { return Path.Combine(_paths.Root, "plugin-workspaces"); } }
        private string NewDefaultSourcePath(string name)
        {
            StorageFileSystem.EnsureRegularDirectory(DefaultSourceParent);
            var leaf = Regex.Replace(name.Trim(), "[<>:\"/\\\\|?*\\x00-\\x1F]", "-").Trim(' ', '.');
            if (leaf.Length > 48) leaf = leaf.Substring(0, 48).Trim(' ', '.');
            if (leaf.Length == 0) leaf = "Плагин";
            string path;
            do { path = Path.Combine(DefaultSourceParent, leaf + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }
            while (Directory.Exists(path) || File.Exists(path));
            return path;
        }
        public PluginCatalogDto ListRemoved()
        { using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync) { RequireOpen(); return new PluginCatalogDto { Items = _store.List().Where(item => item.Hidden).Select(Item).ToArray() }; } }
        public PluginIconCatalogDto ListIcons()
        { using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync) { RequireOpen(); return new PluginIconCatalogDto { Items = _icons.List() }; } }
        public PluginIconRecord SaveIcon(PluginIconSaveRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (request == null || request.Svg == null) throw new PluginException("plugin_icon_invalid", "Provide SVG source.");
                return _icons.Save(request.Name, PluginPackageFiles.Utf8.GetBytes(request.Svg));
            }
        }
        public PluginIconCatalogDto DeleteIcon(PluginIconDeleteRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen(); _icons.Delete(request?.Id);
                return new PluginIconCatalogDto { Items = _icons.List() };
            }
        }
        public PluginItemDto UpdateAppearance(PluginAppearanceRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                if (request == null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 || request.Name.Any(char.IsControl))
                    throw new PluginException("plugin_name_invalid", "Укажите название плагина до 120 символов.");
                var registration = RequireRegistration(request.PluginId);
                var current = Item(registration);
                var appearance = new PluginAppearance { Name = request.Name.Trim(), Icon = current.Icon, IconPaths = current.IconPaths };
                if (request.IconId != null)
                {
                    if (request.IconId.StartsWith("icon-", StringComparison.Ordinal))
                    { var saved = _icons.Get(request.IconId); appearance.Icon = saved.Id; appearance.IconPaths = saved.Paths.ToArray(); }
                    else if (Regex.IsMatch(request.IconId, "^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant))
                    { appearance.Icon = request.IconId; appearance.IconPaths = null; }
                    else throw new PluginException("plugin_icon_invalid", "Выберите значок из каталога.");
                }
                return Item(_store.SetAppearance(request.PluginId, appearance));
            }
        }
        public Task<PluginIconRecord> DrawIconAsync(PluginIconDrawRequest request, AppSettings settings,
            Func<string> apiKeyProvider, CancellationToken token)
        { return DrawIconAsync(request, settings, new LlmClient(apiKeyProvider).CompleteAsync, token); }

        internal async Task<PluginIconRecord> DrawIconAsync(PluginIconDrawRequest request, AppSettings settings,
            LlmCompletionDelegate complete, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 ||
                string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
                throw new PluginException("plugin_icon_invalid", "Provide a plugin name and an icon description of at most 500 characters.");
            var configuration = (settings ?? new AppSettings()).Clone();
            configuration.MaxTokens = 4096;
            configuration.RequestTimeoutSeconds = Math.Min(configuration.RequestTimeoutSeconds, 75);
            configuration.Temperature = 0.35;
            var messages = new List<ChatMessage> {
                    new ChatMessage { Role = "system", Content =
                        "Draw one clear, balanced outline icon as an SVG document with viewBox=\"0 0 24 24\". " +
                        "Use path, circle, ellipse, rect, line, polyline, polygon and g elements. " +
                        "Use fill=\"none\" and stroke=\"currentColor\"; the app supplies themed outline styling. " +
                        "Keep geometry within 2–22. Do not use transforms, text, scripts, style, references, images or external URLs. " +
                        "Return only the SVG, at most 4096 UTF-8 bytes. No image-generation tool is needed." },
                    new ChatMessage { Role = "user", Content = "Plugin: " + request.Name.Trim() + "\nIcon brief: " + request.Description.Trim() }
                };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var completion = await complete(configuration, messages,
                    new LlmRequestOptions { ResponseFormat = LlmResponseFormats.Text, ReasoningEnabled = false },
                    null, token).ConfigureAwait(false);
                try
                {
                    var svg = ExtractModelSvg(completion?.Content);
                    PluginPackageFiles.ReadIconPaths(PluginPackageFiles.Utf8.GetBytes(svg));
                    token.ThrowIfCancellationRequested();
                    return SaveIcon(new PluginIconSaveRequest { Name = request.Name.Trim(), Svg = svg });
                }
                catch (PluginException error) when (error.Code == "plugin_icon_invalid")
                {
                    if (attempt == 1) throw new PluginException("plugin_icon_invalid",
                        "Модель вернула некорректный рисунок: " + error.Message);
                    if (!string.IsNullOrWhiteSpace(completion?.Content))
                        messages.Add(new ChatMessage { Role = "assistant", Content = completion.Content.Substring(0, Math.Min(completion.Content.Length, 8192)) });
                    messages.Add(new ChatMessage { Role = "user", Content =
                        "The previous icon was rejected: " + error.Message +
                        " Return one complete outline SVG with viewBox=\"0 0 24 24\", fill=\"none\" and supported shapes. Keep it under 4096 bytes." });
                }
            }
            throw new PluginException("plugin_icon_invalid", "Модель не смогла нарисовать корректный SVG. Сократите описание или добавьте готовый SVG.");
        }
        public static string ExtractModelSvg(string content)
        {
            var source = (content ?? string.Empty).Trim();
            var matches = Regex.Matches(source, @"<svg(?=\s|>)[\s\S]*?</svg\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return matches.Count == 1 ? matches[0].Value : source;
        }
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
                var connected = _store.Connect(source);
                _revoked.Remove(connected.PluginId);
                return Item(connected);
            }
        }

        public PluginItemDto Create(PluginCreateRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 ||
                (request.Description?.Length ?? 0) > 8000 ||
                (request.IconDescription?.Length ?? 0) > 500)
                throw new PluginException("plugin_create_invalid", "Укажите название плагина и краткое описание задачи.");
            if (request.RootPath != null && (string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathRooted(request.RootPath.Trim())))
                throw new PluginException("plugin_create_invalid", "Укажите полный путь к другой папке исходников.");
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                var rootPath = request.RootPath == null ? NewDefaultSourcePath(request.Name) : request.RootPath.Trim();
                if (File.Exists(rootPath) || Directory.Exists(rootPath) && Directory.EnumerateFileSystemEntries(rootPath).Any())
                    throw new PluginException("target_exists", "Папка уже содержит файлы. Укажите пустую папку или подключите готовый плагин с plugin.json.");
                var chosenIcon = request.IconId ?? "draw";
                PluginIconRecord savedIcon = null;
                if (chosenIcon.StartsWith("icon-", StringComparison.Ordinal)) savedIcon = _icons.Get(chosenIcon);
                else if (chosenIcon != "draw" &&
                    !System.Text.RegularExpressions.Regex.IsMatch(chosenIcon, "^[A-Za-z][A-Za-z0-9_-]{0,63}$"))
                    throw new PluginException("plugin_icon_invalid", "Choose a built-in or saved icon.");
                var workspace = _workspaces.Open(rootPath, true, true);
                var files = new WorkspaceFileService(_paths);
                var manifest = new PluginManifestV1 { FormatVersion = 1, HostProtocol = 1, Id = "plugin-" + Guid.NewGuid().ToString("N"),
                    Name = request.Name, Version = "1.0.0", Entry = "index.html", Icon = savedIcon == null ? chosenIcon == "draw" ? "puzzle" : chosenIcon : "icon.svg",
                    Files = savedIcon == null ? new[] { "plugin.json", "index.html", "app.js", "style.css", "commands.js", "README.md" } :
                        new[] { "plugin.json", "index.html", "app.js", "style.css", "commands.js", "README.md", "icon.svg" },
                    Commands = new[] { new PluginCommandDefinition { Id = "echo", Description = "Return a greeting.", Entry = "commands.js",
                        ArgumentsSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Greeting text\"}},\"required\":[\"text\"],\"additionalProperties\":false}"),
                        ResultSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Greeting text\"}},\"required\":[\"text\"],\"additionalProperties\":false}") } } };
                files.CreateText(workspace, "plugin.json", JsonConvert.SerializeObject(manifest, Formatting.Indented));
                files.CreateText(workspace, "index.html", "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"style.css\"><script defer src=\"/.host/sdk.js\"></script><script defer src=\"app.js\"></script><title>Plugin</title></head><body><main><h1 id=\"title\"></h1><p>Workspace плагина готов к разработке.</p><button id=\"run\">Проверить команду</button><output id=\"result\"></output></main></body></html>");
                files.CreateText(workspace, "app.js", "'use strict';\ndocument.getElementById('title').textContent = " + JsonConvert.SerializeObject(request.Name) + ";\ndocument.getElementById('run').onclick = async function () { try { var value = await RNPlugin.invoke('echo', {text:'Плагин работает'}); document.getElementById('result').textContent = value.text; } catch (error) { document.getElementById('result').textContent = error.message; } };\n");
                files.CreateText(workspace, "commands.js", "'use strict';\nRNPluginCommands.register('echo', async function (args) { return {text: args.text}; });\n");
                files.CreateText(workspace, "style.css", "body{margin:0;background:#f5f7f4;color:#25352c;font:14px system-ui}main{padding:24px}button{padding:8px 12px;border:1px solid #c8d5cc;border-radius:6px;background:white}output{display:block;margin-top:16px}\n");
                if (savedIcon != null) files.CreateText(workspace, "icon.svg", PluginIconLibrary.Svg(savedIcon));
                files.CreateText(workspace, "README.md", "# " + request.Name + "\n\n" + (request.Description ?? "") +
                    (chosenIcon == "draw" ? "\n\nIcon brief: " + (request.IconDescription ?? "Use the plugin name and purpose.") +
                    "\nThe source chat can load workspace.plugin_authoring to draw a 24x24 SVG icon as ordinary text in icon.svg. Set plugin.json icon to icon.svg and add that path to files; checking validates the vector paths. No image-generation model is required.\n" :
                    "\n\nSelected icon: " + (savedIcon == null ? chosenIcon : savedIcon.Name) + ". Keep this icon unless the user asks to change it.\n") +
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
                var manifest = PluginPackageFiles.ParseManifest(package.ManifestJson);
                if (PluginPackageFiles.IconIsSvgPath(manifest.Icon))
                    _icons.Save(manifest.Name, _store.ReadFile(package, manifest.Icon));
                var id = Guid.NewGuid().ToString("N");
                foreach (var old in _candidates.Where(item => item.Value.Package.PluginId == pluginId).Select(item => item.Key).ToArray()) _candidates.Remove(old);
                _candidates.Add(id, new Candidate { Package = package, ExpectedRelease = registration.ActiveReleaseId });
                return new PluginCandidateDto { PluginId = pluginId, CandidateId = id, Files = package.Files.Count,
                    Bytes = package.Files.Sum(file => file.Payload.ByteLength), Checks = new[] { "manifest", "protocol", "file-integrity", "plugin-vendor-admission" } };
            }
        }

        // A cheap change token for the declared package members. The immutable
        // candidate is still created by Check; this token grants no file access.
        public PluginSourceStateDto SourceState(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                var root = Source(RequireRegistration(pluginId)).Workspace.RootPath;
                var stamp = new StringBuilder();
                byte[] manifestBytes;
                try { manifestBytes = PluginPackageFiles.Read(root, "plugin.json", 1024 * 1024); }
                catch (PluginException error) { return new PluginSourceStateDto { PluginId = pluginId, Stamp = error.Code }; }
                using (var hash = SHA256.Create())
                    stamp.Append(Convert.ToBase64String(hash.ComputeHash(manifestBytes)));
                try
                {
                    var manifest = PluginPackageFiles.ParseManifest(PluginPackageFiles.Utf8.GetString(manifestBytes));
                    foreach (var member in manifest.Files.OrderBy(path => path, StringComparer.Ordinal))
                    {
                        stamp.Append('|').Append(member);
                        try
                        {
                            var file = new FileInfo(PluginPackageFiles.Resolve(root, member, false));
                            stamp.Append(':').Append(file.Exists ? file.Length.ToString() + ":" + file.LastWriteTimeUtc.Ticks : "missing");
                        }
                        catch (PluginException error) { stamp.Append(':').Append(error.Code); }
                    }
                }
                catch (Exception error) when (error is PluginException || error is DecoderFallbackException)
                { stamp.Append("|manifest-invalid"); }
                using (var hash = SHA256.Create())
                    return new PluginSourceStateDto { PluginId = pluginId,
                        Stamp = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(stamp.ToString()))) };
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

        public async Task<PluginVisibilityDto> RemoveAsync(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                PluginInstance[] instances;
                lock (_sync)
                {
                    RequireOpen(); RequireRegistration(pluginId);
                    _revoked.Add(pluginId);
                    foreach (var key in _data.Keys.Where(key => key.StartsWith(pluginId + ":", StringComparison.Ordinal)).ToArray())
                    { _data[key].Dispose(); _data.Remove(key); }
                    foreach (var key in _candidates.Where(item => item.Value.Package.PluginId == pluginId).Select(item => item.Key).ToArray())
                        _candidates.Remove(key);
                    instances = _instances.Values.Where(instance => instance.Manifest.Id == pluginId).ToArray();
                    foreach (var instance in instances) instance.Dispose();
                }
                foreach (var instance in instances) await CloseInstanceAsync(instance.Id).ConfigureAwait(false);
                await Task.WhenAll(instances.Select(instance => instance.CommandCompleted)).ConfigureAwait(false);
                lock (_sync)
                {
                    try { _store.SetHidden(pluginId, true); }
                    catch
                    {
                        try { if (_store.Get(pluginId)?.Hidden == false) _revoked.Remove(pluginId); }
                        catch { }
                        throw;
                    }
                }
                return new PluginVisibilityDto { PluginId = pluginId, Hidden = true };
            }
        }
        public PluginItemDto Restore(string pluginId)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync)
            {
                RequireOpen();
                var restored = _store.SetHidden(pluginId, false);
                _revoked.Remove(pluginId);
                return Item(restored);
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
                        selection = new PluginDataSelection { RootPath = Path.Combine(_paths.Root, "plugin-preview-data", instance.Manifest.Id), Writable = true };
                    else
                    {
                        if (ProjectPicker == null) throw new PluginException("project_picker_unavailable", "Project selection is unavailable in this host.");
                        selection = await ProjectPicker.PickAsync(token).ConfigureAwait(false);
                        if (selection == null) throw new OperationCanceledException();
                    }
                    PluginDataGrant.RequireSeparateRoot(selection.RootPath, source);
                    if (!instance.Preview && PluginDataGrant.PathsOverlap(selection.RootPath, Path.Combine(_paths.Root, "plugin-preview-data")))
                        throw new PluginException("preview_is_not_active", "Choose a data folder outside plugin preview data.");
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
                        .Reverse().Take(100).Reverse().Select(PresentWorkspaceMessage).ToArray() };
            }
        }
        internal static PluginChatMessageDto PresentWorkspaceMessage(ChatMessage message)
        {
            if (message.Role == "assistant" && message.ResponseProtocolVersion == ConversationResponse.ProtocolVersion)
            {
                var parsed = ConversationResponseHistoryReader.Read(message);
                if (!parsed.Success)
                    return new PluginChatMessageDto { Role = "assistant", Text = "Сообщение модели не удалось отобразить: " + parsed.Error };
                return new PluginChatMessageDto { Role = "assistant", Text = parsed.Response.Message,
                    Actions = parsed.Response.ToolCalls.Select(call => call.Name).ToArray() };
            }
            return new PluginChatMessageDto { Role = message.Role, Text = message.Content };
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

        private WorkspaceDescriptor RequireProjectWorkspace(string workspaceId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
                throw new PluginException("project_chat_invalid", "Select a connected workspace.");
            return _workspaces.OpenConnected(workspaceId);
        }

        private static ProjectChatProjectDto ProjectChatProject(WorkspaceConnection item)
        { return new ProjectChatProjectDto { WorkspaceId = item.WorkspaceId, Name = item.Name,
            RootPath = item.RootPath, Available = item.Available, Chats = item.Chats }; }

        private static ProjectChatProjectDto ProjectChatProject(WorkspaceDescriptor workspace)
        { return new ProjectChatProjectDto { WorkspaceId = workspace.WorkspaceId, Name = workspace.Name,
            RootPath = workspace.RootPath, Available = true }; }

        public ProjectChatCatalogDto ProjectChats()
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                return new ProjectChatCatalogDto { Projects = Conversations.ListConnectedWorkspaces().Select(ProjectChatProject).ToArray() };
            }
        }

        private static string ProjectRoot(ProjectConnectionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathRooted(request.RootPath))
                throw new PluginException("project_root_invalid", "Use a full path to the project folder.");
            return Path.GetFullPath(request.RootPath);
        }

        public ProjectChatCatalogDto ConnectProjectWorkspace(ProjectConnectionRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                var workspace = _workspaces.ConnectProject(ProjectRoot(request), false);
                var catalog = ProjectChats(); catalog.SelectedWorkspaceId = workspace.WorkspaceId;
                return catalog;
            }
        }

        public ProjectChatCatalogDto CreateProjectWorkspace(ProjectConnectionRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                var root = ProjectRoot(request);
                if (Directory.Exists(root) || File.Exists(root))
                    throw new PluginException("project_root_exists", "Choose a new folder for this workspace.");
                var workspace = _workspaces.ConnectProject(root, true);
                var catalog = ProjectChats(); catalog.SelectedWorkspaceId = workspace.WorkspaceId;
                return catalog;
            }
        }

        public ProjectChatStateDto ProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
                throw new PluginException("project_chat_invalid", "A project chat is required.");
            var workspace = RequireProjectWorkspace(request.WorkspaceId);
            var session = Conversations.GetSession(workspace, request.SessionId);
            if (session == null) throw new PluginException("project_chat_missing", "The chat is not in this workspace.");
            var snapshot = Conversations.ReadState(workspace, session.Id);
            return new ProjectChatStateDto { Project = ProjectChatProject(workspace), SessionId = session.Id,
                State = snapshot,
                PendingId = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.PendingId,
                PendingTool = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.Name,
                PendingArguments = session.LastRun?.KernelState?.Summary?.PendingConfirmation?.Call?.ArgumentsJson,
                Messages = session.Messages.Where(message => message.Role == "user" || message.Role == "assistant")
                    .Reverse().Take(100).Reverse().Select(PresentWorkspaceMessage).ToArray() };
            }
        }

        public ProjectChatStateDto CreateProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var workspace = RequireProjectWorkspace(request?.WorkspaceId);
            var title = string.IsNullOrWhiteSpace(request.Title) ? "Новый чат" : request.Title.Trim();
            if (title.Length > 200) throw new PluginException("project_chat_invalid", "The chat title is too long.");
            var session = Conversations.CreateSession(workspace, title);
            return ProjectChat(new ProjectChatRequest { WorkspaceId = request.WorkspaceId, SessionId = session.Id });
            }
        }

        public ProjectChatCatalogDto RenameProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var workspace = RequireProjectWorkspace(request?.WorkspaceId);
            Conversations.RenameSession(workspace, request.SessionId, request.Title);
            return ProjectChats();
            }
        }

        public ProjectChatCatalogDto DeleteProjectChat(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var workspace = RequireProjectWorkspace(request?.WorkspaceId);
            if (!Conversations.DeleteSession(workspace, request.SessionId))
                throw new PluginException("project_chat_missing", "The chat is not in this workspace.");
            return ProjectChats();
            }
        }

        public ProjectChatCatalogDto DeleteProjectHistory(ProjectChatRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                var workspace = _workspaces.OpenConnectedHistory(request?.WorkspaceId);
                var chats = Conversations.ListSessions(workspace);
                if (chats.Any(item => {
                    var lifecycle = Conversations.GetSession(workspace, item.Id)?.LastRun?.KernelState?.Summary?.Lifecycle;
                    return lifecycle == RunLifecycle.Running || lifecycle == RunLifecycle.AwaitingConfirmation;
                })) throw new PluginException("project_chat_busy", "Stop or resolve active project chats before deleting their history.");
                foreach (var chat in chats)
                    if (!Conversations.DeleteSession(workspace, chat.Id))
                        throw new PluginException("project_chat_changed", "A project chat changed while deleting history. Refresh the tree.");
                _workspaces.HideProject(workspace.WorkspaceId);
                return ProjectChats();
            }
        }

        public async Task<ProjectChatStateDto> SubmitProjectChatAsync(ProjectChatRequest request, AppSettings settings,
            Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var workspace = RequireProjectWorkspace(request?.WorkspaceId);
            if (request.Input == null || request.Input.SessionId != request.SessionId)
                throw new PluginException("project_chat_invalid", "The submitted chat identity changed.");
            if (Conversations.GetSession(workspace, request.SessionId) == null)
                throw new PluginException("project_chat_missing", "The chat is not in this workspace.");
            await Conversations.SubmitInputAsync(workspace, request.Input, settings, key,
                cancellationToken: token).ConfigureAwait(false);
            return ProjectChat(request);
            }
        }

        public async Task<ProjectChatStateDto> ControlProjectChatAsync(ProjectChatRequest request, bool cancel,
            AppSettings settings, Func<string> key, CancellationToken token)
        {
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
            var workspace = RequireProjectWorkspace(request?.WorkspaceId);
            var session = Conversations.GetSession(workspace, request.SessionId);
            if (session == null) throw new PluginException("project_chat_missing", "The chat is not in this workspace.");
            RNAssistant.Runtime.Contracts.WorkspaceCancellationReceipt cancellation = null;
            if (cancel) cancellation = await Conversations.CancelRunAsync(workspace, new RNAssistant.Runtime.Contracts.WorkspaceRunControlRequest {
                SessionId = session.Id, RunId = request.RunId, Cursor = request.Cursor }, token).ConfigureAwait(false);
            else await Conversations.ResolvePendingAsync(workspace, session.Id, request.PendingId, request.Approve,
                settings, key, cancellationToken: token, modelDigest: session.LastRun?.ModelConfiguration?.Digest).ConfigureAwait(false);
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
        {
            var registration = _store.Get(id);
            if (registration == null || registration.Hidden) throw new PluginException("plugin_missing", "Plugin is not on the panel.");
            return registration;
        }
        private PluginSourceWorkspace Source(PluginRegistration registration)
        {
            var workspace = _workspaces.Open(registration.SourceRoot, false, true);
            if (workspace.WorkspaceId != registration.SourceWorkspaceId) throw new PluginException("plugin_source_changed", "Source workspace identity changed.");
            return new PluginSourceWorkspace(workspace);
        }
        private void RequireOpen() { if (_disposed) throw new ObjectDisposedException(nameof(PluginApplicationService)); }
        private PluginItemDto Item(PluginRegistration value)
        {
            var item = new PluginItemDto { PluginId = value.PluginId, Name = value.Appearance?.Name ?? value.Name,
                Icon = value.Appearance?.Icon ?? value.Icon, IconPaths = value.Appearance?.IconPaths,
                SourceWorkspaceId = value.SourceWorkspaceId, SourceRoot = value.SourceRoot,
                ReleaseId = value.ActiveReleaseId, Enabled = value.Enabled, Hidden = value.Hidden };
            if (item.IconPaths == null && PluginPackageFiles.IconIsSvgPath(item.Icon))
            {
                try
                {
                    var bytes = value.Enabled && value.ActiveReleaseId != null
                        ? _store.ReadFile(_store.ReadRelease(new PluginReleaseIdentity(value.PluginId, value.ActiveReleaseId)), item.Icon)
                        : PluginPackageFiles.Read(value.SourceRoot, item.Icon, 4096);
                    item.IconPaths = PluginPackageFiles.ReadIconPaths(bytes);
                }
                catch (PluginException error) { item.IconError = error.Message; }
            }
            return item;
        }
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
