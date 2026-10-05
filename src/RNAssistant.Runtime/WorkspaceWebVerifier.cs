using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Runtime
{
    public enum WebVerificationStatus { Passed, Failed, NotRun }

    public sealed class WebVerificationResult
    {
        public WebVerificationStatus Status { get; internal set; }
        public string EntryPath { get; internal set; }
        public IReadOnlyList<string> CheckedFiles { get; internal set; }
        public IReadOnlyList<string> Errors { get; internal set; }
        public IReadOnlyList<string> Hints { get; internal set; }
        public string SnapshotSha256 { get; internal set; }
        public string Browser { get; internal set; }
        public IReadOnlyList<ResourceEvidence> Evidence { get; internal set; }
        public string SnapshotId { get; internal set; }
        public string VerificationId { get; internal set; }
        public ResourceRef VerificationReference { get; internal set; }
        public bool Historical { get; internal set; }
        public IReadOnlyList<WebCheckResult> CheckResults { get; internal set; }
    }

    // Development-only browser verifier. The browser receives an immutable,
    // bounded snapshot over loopback, never the writable workspace directory.
    public sealed class WorkspaceWebVerifier
    {
        private const int MaximumFiles = WorkspaceWebSnapshotStore.MaximumFiles;
        private const int MaximumSnapshotBytes = WorkspaceWebSnapshotStore.MaximumSnapshotBytes;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Regex ScriptTag = new Regex("<script\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex LinkTag = new Regex("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex Attribute = new Regex("(?<name>src|href|rel)\\s*=\\s*(?<quote>[\"'])(?<value>.*?)\\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex CssReference = new Regex("(?:url\\(\\s*|@import\\s+)(?<quote>[\"']?)(?<value>[^)\"'\\s]+)\\k<quote>\\s*\\)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex JsImport = new Regex("\\bimport\\s+(?:[^\"']*?\\s+from\\s+)?[\"'](?<value>[^\"']+)[\"']",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex HtmlId = new Regex("\\bid\\s*=\\s*(?<quote>[\"'])(?<value>[^\"']+)\\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex JsElementId = new Regex("\\bgetElementById\\s*\\(\\s*(?<quote>[\"'])(?<value>[^\"']+)\\k<quote>\\s*\\)",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private readonly ResourceProviderRouter<WorkspaceFileResourceProvider> _resources;
        private readonly WorkspaceWebSnapshotStore _snapshots;
        private readonly WorkspaceDescriptor _workspace;

        private sealed class SnapshotFile
        {
            public string Path;
            public byte[] Bytes;
            public string Sha256;
            public ResourceRef Reference;
            public ResourceEvidence Evidence;
        }

        public WorkspaceWebVerifier(ResourceProviderRouter<WorkspaceFileResourceProvider> resources,
            WorkspaceWebSnapshotStore snapshots, WorkspaceDescriptor workspace)
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public static string StatusCode(WebVerificationStatus status)
        { return status == WebVerificationStatus.NotRun ? "not-run" : status.ToString().ToLowerInvariant(); }

        public static string FindBrowserExecutable()
        {
            var configured = Environment.GetEnvironmentVariable("RNA_BROWSER_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.IsPathRooted(configured) && File.Exists(configured) ? configured : null;
            var candidates = new List<string> {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"
            };
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (Path.IsPathRooted(programFiles)) candidates.Add(Path.Combine(programFiles,
                "Google", "Chrome", "Application", "chrome.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        public async Task<WebVerificationResult> VerifyAsync(string entryPath,
            CancellationToken cancellationToken = default(CancellationToken), string snapshotId = null,
            WebVerificationOrigin origin = null, WebFunctionalChecks checks = null)
        {
            var checkResults = (checks?.NotRun() ?? new WebCheckResult[0]).ToList();
            var id = _snapshots.BeginVerification(_workspace, entryPath, snapshotId != null, origin, snapshotId, checks);
            WebVerificationResult result;
            try { result = await VerifyCoreAsync(entryPath, snapshotId, id, checks, checkResults, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _snapshots.CompleteVerification(_workspace, id, WebVerificationState.NotRun,
                    null, new[] { "Verification was cancelled." }, new string[0], checkResults);
                throw;
            }
            catch (Exception ex) when (ex is WorkspaceFileException || ex is IOException ||
                ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                result = new WebVerificationResult { Status = WebVerificationStatus.Failed, EntryPath = entryPath,
                    CheckedFiles = new string[0], Evidence = new ResourceEvidence[0],
                    Errors = BoundedErrors(new[] { ex.Message }), Hints = new string[0] };
            }
            result.VerificationId = id;
            result.Historical = snapshotId != null;
            result.CheckResults = checkResults.AsReadOnly();
            _snapshots.CompleteVerification(_workspace, id,
                result.Status == WebVerificationStatus.Passed ? WebVerificationState.Passed :
                result.Status == WebVerificationStatus.Failed ? WebVerificationState.Failed : WebVerificationState.NotRun,
                result.Browser, result.Errors, result.Hints, result.CheckResults);
            result.VerificationReference = _snapshots.ReadVerification(_workspace, id).Reference;
            return result;
        }

        private async Task<WebVerificationResult> VerifyCoreAsync(string entryPath, string snapshotId,
            string verificationId, WebFunctionalChecks checks, List<WebCheckResult> checkResults,
            CancellationToken cancellationToken)
        {
            var retained = snapshotId == null ? null : _snapshots.ReadSnapshot(_workspace, snapshotId);
            if (retained != null) entryPath = retained.EntryPath;
            try { if (retained == null) entryPath = NormalizeDependency("", entryPath); }
            catch (Exception ex) when (ex is WorkspaceFileException || ex is UriFormatException)
            {
                return new WebVerificationResult { Status = WebVerificationStatus.Failed,
                    EntryPath = entryPath, CheckedFiles = new string[0], Errors = new[] { ex.Message },
                    Hints = new string[0], SnapshotSha256 = null, Evidence = new ResourceEvidence[0] };
            }
            if (!entryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !entryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                return new WebVerificationResult { Status = WebVerificationStatus.Failed,
                    EntryPath = entryPath, CheckedFiles = new string[0],
                    Errors = new[] { "Static web entry must be an HTML file." },
                    Hints = new string[0], SnapshotSha256 = null, Evidence = new ResourceEvidence[0] };
            var errors = new List<string>();
            if (checks != null && checks.EntryPath != entryPath)
                throw new ArgumentException("Entry must match the accepted functional checks: " + checks.EntryPath);
            var snapshot = retained == null ? CaptureSnapshot(entryPath, errors, cancellationToken)
                : OpenSnapshot(retained, cancellationToken);
            var result = new WebVerificationResult { EntryPath = entryPath,
                CheckedFiles = snapshot.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                Errors = errors, SnapshotSha256 = SnapshotHash(snapshot), Browser = null,
                Evidence = snapshot.Values.Select(file => file.Evidence).ToArray(),
                Hints = new string[0] };
            if (errors.Count != 0)
            { result.Status = WebVerificationStatus.Failed; result.Errors = BoundedErrors(errors); return result; }
            retained = retained ?? _snapshots.PublishSnapshot(_workspace, entryPath,
                snapshot.Values.Select(file => new WebSnapshotFile(file.Path, file.Reference, file.Evidence.Payload)));
            _snapshots.AttachSnapshot(_workspace, verificationId, retained);
            result.SnapshotId = retained.Id;
            var browser = FindBrowserExecutable();
            if (browser == null)
            {
                result.Status = WebVerificationStatus.NotRun;
                result.Errors = new[] { "No isolated Chromium executable is available (RNA_BROWSER_EXECUTABLE)." };
                return result;
            }
            result.Browser = Path.GetFileName(browser);
            try
            {
                using (var server = new SnapshotServer(snapshot))
                {
                    await server.StartAsync().ConfigureAwait(false);
                    var browserErrors = await SmokeBrowserAsync(browser,
                        new Uri(server.BaseUrl, EscapePath(entryPath)), server.BaseUrl,
                        checks, checkResults, cancellationToken).ConfigureAwait(false);
                    errors.AddRange(browserErrors);
                    if (browserErrors.Any(error => error.StartsWith("JavaScript exception:", StringComparison.Ordinal)))
                        result.Hints = DomIdHints(snapshot);
                }
                result.Status = errors.Count == 0 ? WebVerificationStatus.Passed : WebVerificationStatus.Failed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.Status = WebVerificationStatus.NotRun;
                errors.Add("Browser verification timed out.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException || ex is HttpListenerException ||
                ex is WebSocketException || ex is HttpRequestException || ex is InvalidOperationException)
            {
                result.Status = WebVerificationStatus.NotRun;
                errors.Add("Browser verification did not complete: " + ex.Message);
            }
            result.Errors = BoundedErrors(errors);
            return result;
        }

        private static IReadOnlyList<string> BoundedErrors(IEnumerable<string> errors)
        {
            return errors.Take(16).Select(error => error.Length > 700
                ? error.Substring(0, 700) + "…" : error).ToArray();
        }

        private Dictionary<string, SnapshotFile> OpenSnapshot(RetainedWebSnapshot retained, CancellationToken token)
        {
            var result = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
            foreach (var file in retained.Files)
            {
                token.ThrowIfCancellationRequested();
                var read = _resources.ForUri(file.Reference.Uri).ReadSnapshot(retained, file.Path);
                read.RequireCompleteExactText();
                var bytes = StrictUtf8.GetBytes(read.Result.Text);
                if (bytes.LongLength != file.Payload.ByteLength || Hash(bytes) != file.Payload.Sha256)
                    throw new WorkspaceFileException("snapshot_unavailable", "Retained source bytes do not match the manifest.");
                result.Add(file.Path, new SnapshotFile { Path = file.Path, Bytes = bytes,
                    Sha256 = file.Payload.Sha256, Reference = file.Reference, Evidence = read.Evidence.Single() });
            }
            return result;
        }

        private static IReadOnlyList<string> DomIdHints(Dictionary<string, SnapshotFile> snapshot)
        {
            try
            {
                var htmlIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in snapshot.Values.Where(item => item.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                    item.Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
                    foreach (Match match in HtmlId.Matches(StrictUtf8.GetString(file.Bytes)))
                        htmlIds.Add(match.Groups["value"].Value);
                var hints = new List<string>();
                foreach (var file in snapshot.Values.Where(item => item.Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                    item.Path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)))
                    foreach (Match match in JsElementId.Matches(StrictUtf8.GetString(file.Bytes)))
                    {
                        var id = match.Groups["value"].Value;
                        if (!htmlIds.Contains(id) && !hints.Any(hint => hint.Contains("#" + id + " ")))
                            hints.Add(file.Path + " requests #" + id +
                                " but no captured HTML declares that ID; check whether it is created dynamically.");
                        if (hints.Count == 4) return hints;
                    }
                return hints;
            }
            catch (RegexMatchTimeoutException) { return new string[0]; }
        }

        private Dictionary<string, SnapshotFile> CaptureSnapshot(string entryPath,
            List<string> errors, CancellationToken cancellationToken)
        {
            var snapshot = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(entryPath);
            var total = 0;
            while (queue.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = queue.Dequeue();
                if (snapshot.ContainsKey(path)) continue;
                if (snapshot.Count >= MaximumFiles)
                { errors.Add("Snapshot exceeds the 32-file bound."); break; }
                if (!SupportedText(path))
                { errors.Add("Unsupported local asset in static snapshot: " + path); continue; }
                ResourceReadObservation observed;
                try
                {
                    var current = _resources.Select("file").Read(path);
                    current.RequireCompleteExactText();
                    var exact = current.Result.Resource.Reference;
                    observed = _resources.ForUri(exact.Uri).ReadExact(path, exact);
                    observed.RequireCompleteExactText();
                }
                catch (Exception ex) when (ex is WorkspaceFileException || ex is IOException ||
                    ex is UnauthorizedAccessException || ex is InvalidOperationException)
                { errors.Add("Cannot read " + path + ": " + ex.Message); continue; }
                var read = observed.Result;
                var bytes = StrictUtf8.GetBytes(read.Text);
                if (bytes.LongLength != read.CompleteViewPayload.ByteLength || Hash(bytes) != read.ContentSha256)
                { errors.Add("Snapshot bytes differ from the observed source: " + path); continue; }
                total += bytes.Length;
                if (total > MaximumSnapshotBytes)
                { errors.Add("Snapshot exceeds the four MiB bound."); break; }
                snapshot.Add(path, new SnapshotFile { Path = path, Bytes = bytes,
                    Sha256 = read.ContentSha256, Reference = read.Resource.Reference,
                    Evidence = observed.Evidence.Single() });
                try
                {
                    foreach (var dependency in Dependencies(path, read.Text, errors))
                        if (!snapshot.ContainsKey(dependency)) queue.Enqueue(dependency);
                }
                catch (RegexMatchTimeoutException)
                { errors.Add("Dependency scan timed out for " + path); }
            }
            foreach (var file in snapshot.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var current = _resources.Select("file").Read(file.Path).Result;
                    if (current.Resource.Reference.Uri != file.Reference.Uri ||
                        current.Resource.Reference.Revision != file.Reference.Revision ||
                        current.ContentSha256 != file.Sha256)
                        errors.Add("Source changed during snapshot: " + file.Path);
                }
                catch (Exception ex) when (ex is WorkspaceFileException || ex is IOException || ex is UnauthorizedAccessException)
                { errors.Add("Source became unavailable during snapshot: " + file.Path + ": " + ex.Message); }
            }
            if (errors.Count == 0)
            {
                try
                {
                    var frozen = _resources.Select("file").CaptureAuthority(snapshot.Values.Select(file => file.Evidence));
                    var reducer = new EvidenceStateReducer();
                    foreach (var file in snapshot.Values)
                        if (reducer.Reduce(file.Evidence, frozen).State != EvidenceState.Current)
                            errors.Add("Source authority is no longer current: " + file.Path);
                }
                catch (WorkspaceFileException ex)
                { errors.Add("Snapshot authority is unavailable: " + ex.Message); }
            }
            return snapshot;
        }

        private static IEnumerable<string> Dependencies(string owner, string text, List<string> errors)
        {
            if (owner.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                owner.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                owner.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                var matches = owner.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                    ? CssReference.Matches(text).Cast<Match>() : JsImport.Matches(text).Cast<Match>();
                foreach (var match in matches)
                {
                    var raw = match.Groups["value"].Value;
                    if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                    string dependency;
                    try { dependency = NormalizeDependency(owner, raw); }
                    catch (Exception ex) when (ex is WorkspaceFileException || ex is UriFormatException)
                    { errors.Add("Invalid dependency in " + owner + ": " + ex.Message); continue; }
                    yield return dependency;
                }
                yield break;
            }
            if (!owner.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !owner.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (Match tag in ScriptTag.Matches(text).Cast<Match>().Concat(LinkTag.Matches(text).Cast<Match>()))
            {
                var attributes = Attribute.Matches(tag.Value).Cast<Match>()
                    .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Groups["value"].Value,
                        StringComparer.OrdinalIgnoreCase);
                var script = tag.Value.StartsWith("<script", StringComparison.OrdinalIgnoreCase);
                string href;
                if (!attributes.TryGetValue(script ? "src" : "href", out href) ||
                    !script && (!attributes.TryGetValue("rel", out var rel) ||
                        !rel.Split(' ').Contains("stylesheet", StringComparer.OrdinalIgnoreCase))) continue;
                if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                string dependency;
                try { dependency = NormalizeDependency(owner, href); }
                catch (Exception ex) when (ex is WorkspaceFileException || ex is UriFormatException)
                { errors.Add("Invalid dependency in " + owner + ": " + ex.Message); continue; }
                yield return dependency;
            }
        }

        private static string NormalizeDependency(string owner, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("/", StringComparison.Ordinal) ||
                raw.Contains("\\") || raw.IndexOf(':') >= 0 || raw.Any(char.IsControl))
                throw new WorkspaceFileException("invalid_dependency", "Only local relative asset paths are supported: " + raw);
            var parts = new List<string>();
            var parent = owner.Replace('\\', '/').Split('/').ToList();
            if (parent.Count != 0) parent.RemoveAt(parent.Count - 1);
            parts.AddRange(parent.Where(part => part.Length != 0));
            var decoded = Uri.UnescapeDataString(raw);
            if (decoded.StartsWith("/", StringComparison.Ordinal) || decoded.Contains("\\") ||
                decoded.IndexOf(':') >= 0 || decoded.Any(char.IsControl))
                throw new WorkspaceFileException("invalid_dependency", "Encoded asset path is unsafe.");
            var suffix = decoded.IndexOfAny(new[] { '?', '#' });
            if (suffix >= 0) decoded = decoded.Substring(0, suffix);
            foreach (var part in decoded.Split('/'))
            {
                if (part == "..")
                {
                    if (parts.Count == 0) throw new WorkspaceFileException("invalid_dependency", "Asset escapes the project root.");
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (part != "." && part.Length != 0) parts.Add(part);
            }
            if (parts.Count == 0 || parts.Any(part => part == ".rnassistant" || part == ".git"))
                throw new WorkspaceFileException("invalid_dependency", "Asset path is unavailable.");
            return string.Join("/", parts);
        }

        private static bool SupportedText(string path)
        {
            var extension = Path.GetExtension(path);
            return new[] { ".html", ".htm", ".css", ".js", ".mjs", ".json", ".svg", ".txt", ".csv" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static string SnapshotHash(Dictionary<string, SnapshotFile> snapshot)
        {
            return WorkspaceWebSnapshotStore.Digest(snapshot.Values.Select(file =>
                new WebSnapshotFile(file.Path, file.Reference, file.Evidence.Payload)));
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string EscapePath(string path)
        { return string.Join("/", path.Split('/').Select(Uri.EscapeDataString)); }

        private sealed class SnapshotServer : IDisposable
        {
            private readonly Dictionary<string, SnapshotFile> _files;
            private HttpListener _listener;
            private Task _loop;
            public Uri BaseUrl { get; private set; }
            public SnapshotServer(Dictionary<string, SnapshotFile> files) { _files = files; }

            public Task StartAsync()
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var socket = new TcpListener(IPAddress.Loopback, 0);
                    socket.Start();
                    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                    socket.Stop();
                    var listener = new HttpListener();
                    BaseUrl = new Uri("http://127.0.0.1:" + port + "/");
                    listener.Prefixes.Add(BaseUrl.AbsoluteUri);
                    try { listener.Start(); _listener = listener; break; }
                    catch (HttpListenerException) { listener.Close(); }
                }
                if (_listener == null) throw new IOException("Could not reserve a loopback preview port.");
                _loop = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context;
                        try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                        catch (HttpListenerException) { break; }
                        catch (ObjectDisposedException) { break; }
                        _ = Task.Run(() => Reply(context));
                    }
                });
                return Task.CompletedTask;
            }

            private void Reply(HttpListenerContext context)
            {
                try
                {
                    var path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath.TrimStart('/'));
                    SnapshotFile file;
                    if (path == "favicon.ico") { context.Response.StatusCode = 204; return; }
                    if (context.Request.HttpMethod != "GET" || !_files.TryGetValue(path, out file))
                    { context.Response.StatusCode = 404; return; }
                    context.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data: blob:; object-src 'none'; base-uri 'none'";
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.ContentType = path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css" :
                        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ? "text/javascript" :
                        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ? "text/html" :
                        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "text/plain";
                    context.Response.ContentLength64 = file.Bytes.Length;
                    context.Response.OutputStream.Write(file.Bytes, 0, file.Bytes.Length);
                }
                catch (IOException) { }
                finally { try { context.Response.Close(); } catch (Exception ex) when
                    (ex is ObjectDisposedException || ex is HttpListenerException || ex is IOException) { } }
            }

            public void Dispose()
            {
                _listener?.Close();
                try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
            }
        }

        private static async Task<IReadOnlyList<string>> SmokeBrowserAsync(string executable, Uri page,
            Uri origin, WebFunctionalChecks checks, List<WebCheckResult> checkResults, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var profile = Path.Combine(Path.GetTempPath(), "rna-browser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            Process process = null;
            try
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var option in new[] { "--headless=new", "--disable-gpu", "--no-first-run",
                    "--no-default-browser-check", "--disable-background-networking", "--disable-extensions",
                    "--disable-sync", "--remote-debugging-port=0", "--user-data-dir=" + profile,
                    "--proxy-server=http://127.0.0.1:9", "about:blank" }) start.ArgumentList.Add(option);
                process = Process.Start(start);
                if (process == null) throw new IOException("Browser process could not start.");
                process.OutputDataReceived += (sender, args) => { };
                process.ErrorDataReceived += (sender, args) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var active = Path.Combine(profile, "DevToolsActivePort");
                for (var i = 0; i < 100 && !File.Exists(active); i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("Browser exited before DevTools was ready.");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                if (!File.Exists(active)) throw new IOException("Browser DevTools did not become ready.");
                var port = int.Parse(File.ReadLines(active).First());
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    var targets = JArray.Parse(await http.GetStringAsync(
                        "http://127.0.0.1:" + port + "/json/list", cancellationToken).ConfigureAwait(false));
                    var target = targets.Children<JObject>().FirstOrDefault(item => (string)item["type"] == "page");
                    if (target == null) throw new IOException("Browser page target is unavailable.");
                    using (var devtools = new DevToolsClient((string)target["webSocketDebuggerUrl"], origin))
                    {
                        await devtools.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        await devtools.CommandAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
                        await devtools.CommandAsync("Runtime.enable", null, cancellationToken).ConfigureAwait(false);
                        await devtools.CommandAsync("Log.enable", null, cancellationToken).ConfigureAwait(false);
                        await devtools.CommandAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
                        await devtools.CommandAsync("Network.setBypassServiceWorker",
                            new JObject { ["bypass"] = true }, cancellationToken).ConfigureAwait(false);
                        var navigate = await devtools.CommandAsync("Page.navigate",
                            new JObject { ["url"] = page.AbsoluteUri }, cancellationToken).ConfigureAwait(false);
                        if ((string)navigate["result"]?["errorText"] != null)
                            errors.Add("Navigation failed: " + (string)navigate["result"]["errorText"]);
                        else await devtools.WaitForLoadAsync(cancellationToken).ConfigureAwait(false);
                        var evaluated = await devtools.CommandAsync("Runtime.evaluate",
                            new JObject { ["expression"] = "document.readyState", ["returnByValue"] = true },
                            cancellationToken).ConfigureAwait(false);
                        if ((string)evaluated["result"]?["result"]?["value"] != "complete")
                            errors.Add("Browser document did not reach readyState=complete.");
                        if (checks != null && errors.Count == 0 && devtools.Errors.Count == 0)
                        {
                            devtools.RejectNavigation = true;
                            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                            {
                                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                                await RunChecksAsync(devtools, checks, checkResults, deadline.Token).ConfigureAwait(false);
                            }
                        }
                        await devtools.ObserveForAsync(TimeSpan.FromMilliseconds(1500),
                            cancellationToken).ConfigureAwait(false);
                        errors.AddRange(devtools.Errors);
                        errors.AddRange(checkResults.Where(check => check.Status == WebCheckStatus.Failed)
                            .Select(check => "Functional check " + check.Id + ": " + check.Error));
                        if (checks != null && errors.Count == 0 && checkResults.Any(check => check.Status != WebCheckStatus.Passed))
                            errors.Add("Required functional checks did not complete.");
                    }
                }
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    process.Dispose();
                }
                try { Directory.Delete(profile, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return errors.Take(16).ToArray();
        }

        private sealed class BrowserCheckTarget
        {
            public string Error { get; set; }
            public string Text { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
        }

        private static async Task RunChecksAsync(DevToolsClient devtools, WebFunctionalChecks checks,
            List<WebCheckResult> results, CancellationToken token)
        {
            for (var index = 0; index < checks.Steps.Count; index++)
            {
                var step = checks.Steps[index];
                var elapsed = Stopwatch.StartNew();
                BrowserCheckTarget target;
                do
                {
                    // Only selector data is interpolated, as a JSON string. The caller
                    // cannot supply executable code. Pointer events come from CDP.
                    var expression = "(() => { try { const nodes = document.querySelectorAll(" +
                        JsonConvert.SerializeObject(step.Selector) + "); " +
                        "if (nodes.length !== 1) return {Error:'Selector must match exactly one element; found ' + nodes.length}; " +
                        "const el = nodes[0]; if (!(el instanceof HTMLElement)) return {Error:'Target must be an HTML element'}; " +
                        "el.scrollIntoView({block:'center',inline:'center'}); const r = el.getBoundingClientRect(); " +
                        "const s = getComputedStyle(el); if (!r.width || !r.height || s.visibility !== 'visible' || Number(s.opacity) === 0) " +
                        "return {Error:'Target is not visible'}; " +
                        (step.Operation == WebCheckOperation.Click
                            ? "if (el.matches(':disabled') || el.getAttribute('aria-disabled') === 'true') return {Error:'Target is disabled'}; " +
                              "const x=r.x+r.width/2,y=r.y+r.height/2,hit=document.elementFromPoint(x,y); " +
                              "if (!hit || !(hit===el || el.contains(hit))) return {Error:'Target is obscured'}; return {X:x,Y:y}; "
                            : "return {Text:(el.textContent || '').trim().slice(0,700)}; ") +
                        "} catch (e) { return {Error:String(e).slice(0,700)}; } })()";
                    var evaluated = await devtools.CommandAsync("Runtime.evaluate", new JObject {
                        ["expression"] = expression, ["returnByValue"] = true, ["timeout"] = 1000 }, token).ConfigureAwait(false);
                    var evaluation = evaluated["result"];
                    target = evaluation?["exceptionDetails"] == null
                        ? evaluation?["result"]?["value"]?.ToObject<BrowserCheckTarget>() : null;
                    if (target == null) throw new IOException("Browser could not evaluate the functional check target.");
                    if (devtools.Errors.Count != 0)
                        target.Error = "Browser reported an error during functional checks.";
                    if (target.Error != null || step.Operation == WebCheckOperation.Click || target.Text == step.Expected) break;
                    await Task.Delay(50, token).ConfigureAwait(false);
                } while (elapsed.Elapsed < TimeSpan.FromSeconds(2));

                var error = target.Error;
                if (error == null && step.Operation == WebCheckOperation.TextEquals && target.Text != step.Expected)
                    error = "Text differs from expected: " + step.Expected;
                if (error != null)
                {
                    results[index] = new WebCheckResult(step.Id, WebCheckStatus.Failed, target.Text, error);
                    return; // Later steps have not run and cannot become successful.
                }
                if (step.Operation == WebCheckOperation.Click)
                    foreach (var type in new[] { "mousePressed", "mouseReleased" })
                        await devtools.CommandAsync("Input.dispatchMouseEvent", new JObject {
                            ["type"] = type, ["x"] = target.X, ["y"] = target.Y,
                            ["button"] = "left", ["clickCount"] = 1 }, token).ConfigureAwait(false);
                results[index] = new WebCheckResult(step.Id, WebCheckStatus.Passed, target.Text);
            }
        }

        private sealed class DevToolsClient : IDisposable
        {
            private readonly ClientWebSocket _socket = new ClientWebSocket();
            private readonly string _url;
            private readonly Uri _origin;
            private readonly List<string> _errors = new List<string>();
            private int _id;
            private bool _loaded;
            public bool RejectNavigation { get; set; }
            public IReadOnlyList<string> Errors { get { return _errors.Distinct(StringComparer.Ordinal).Take(16).ToArray(); } }
            public DevToolsClient(string url, Uri origin) { _url = url; _origin = origin; }
            public Task ConnectAsync(CancellationToken token)
            { return _socket.ConnectAsync(new Uri(_url), token); }

            public async Task<JObject> CommandAsync(string method, JObject parameters, CancellationToken token)
            {
                var id = ++_id;
                var message = new JObject { ["id"] = id, ["method"] = method };
                if (parameters != null) message["params"] = parameters;
                var bytes = StrictUtf8.GetBytes(message.ToString(Formatting.None));
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                    true, token).ConfigureAwait(false);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (true)
                    {
                        var received = await ReadAsync(deadline.Token).ConfigureAwait(false);
                        Observe(received);
                        if ((int?)received["id"] != id) continue;
                        if (received["error"] != null)
                            throw new IOException("Browser protocol rejected " + method + ": " +
                                (string)received["error"]["message"]);
                        return received;
                    }
                }
            }

            public async Task WaitForLoadAsync(CancellationToken token)
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    while (!_loaded) Observe(await ReadAsync(deadline.Token).ConfigureAwait(false));
                }
            }

            public async Task ObserveForAsync(TimeSpan duration, CancellationToken token)
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(duration);
                    try
                    {
                        while (true) Observe(await ReadAsync(deadline.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                }
            }

            private async Task<JObject> ReadAsync(CancellationToken token)
            {
                var bytes = new byte[8192];
                using (var output = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(new ArraySegment<byte>(bytes), token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close)
                            throw new IOException("Browser protocol connection closed.");
                        output.Write(bytes, 0, received.Count);
                        if (output.Length > 1024 * 1024) throw new IOException("Browser protocol message is too large.");
                    } while (!received.EndOfMessage);
                    return JObject.Parse(StrictUtf8.GetString(output.ToArray()));
                }
            }

            private void Observe(JObject message)
            {
                var method = (string)message["method"];
                var args = message["params"];
                if (method == "Page.loadEventFired") _loaded = true;
                else if (method == "Page.frameNavigated" && RejectNavigation && args?["frame"]?["parentId"] == null)
                    _errors.Add("Functional checks navigated away from the captured document.");
                else if (method == "Runtime.exceptionThrown")
                    _errors.Add("JavaScript exception: " +
                        ((string)args?["exceptionDetails"]?["exception"]?["description"] ??
                         (string)args?["exceptionDetails"]?["text"] ?? "unknown")
                        .Replace(_origin.AbsoluteUri, "/"));
                else if (method == "Runtime.consoleAPICalled" && (string)args?["type"] == "error")
                    _errors.Add("Console error: " + string.Join(" ",
                        args["args"].Children().Select(item => (string)item["value"] ?? (string)item["description"] ?? "error")));
                else if (method == "Log.entryAdded" && (string)args?["entry"]?["level"] == "error")
                    _errors.Add("Browser error: " + ((string)args["entry"]["text"] ?? "unknown")
                        .Replace(_origin.AbsoluteUri, "/"));
                else if (method == "Network.loadingFailed" && (string)args?["errorText"] != "net::ERR_ABORTED")
                    _errors.Add("Asset load failed: " + (string)args?["errorText"]);
                else if (method == "Network.responseReceived" && (int?)args?["response"]?["status"] >= 400)
                    _errors.Add("Asset HTTP " + (int?)args["response"]["status"] + ": " +
                        LocalPath((string)args["response"]["url"]));
                else if (method == "Network.requestWillBeSent")
                {
                    var url = (string)args?["request"]?["url"];
                    Uri parsed;
                    if (Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
                        !parsed.GetLeftPart(UriPartial.Authority).Equals(
                            _origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                        _errors.Add("External request attempted: " + SafeUrl(url));
                }
            }

            private static string SafeUrl(string value)
            {
                Uri uri;
                return Uri.TryCreate(value, UriKind.Absolute, out uri)
                    ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath : "unknown URL";
            }

            private string LocalPath(string value)
            {
                Uri uri;
                return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                    uri.GetLeftPart(UriPartial.Authority).Equals(
                        _origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                    ? uri.AbsolutePath : SafeUrl(value);
            }

            public void Dispose() { _socket.Dispose(); }
        }
    }
}
