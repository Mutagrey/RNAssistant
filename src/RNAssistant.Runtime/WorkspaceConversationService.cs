using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Runtime
{
    // The CLI host supplies input/output only. This service uses the same kernel,
    // materialized model protocol, ToolRuntime and canonical stores as Office.
    public sealed class WorkspaceConversationService
    {
        private readonly AppDataPaths _paths;
        private readonly ChatStore _chats;
        private readonly WorkspaceStore _workspaces;
        private readonly WorkspaceFileService _files;

        public WorkspaceConversationService(AppDataPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _chats = new ChatStore(paths);
            _workspaces = new WorkspaceStore(paths);
            _files = new WorkspaceFileService(paths);
        }

        public ChatSession GetSession(WorkspaceDescriptor workspace, string sessionId)
        {
            return _workspaces.LoadSession(_chats, workspace, sessionId);
        }

        public ChatSession PrepareResume(WorkspaceDescriptor workspace, string sessionId)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Workspace and session are required.");
            using (AcquireSessionLease(sessionId))
            {
                var session = _workspaces.LoadSession(_chats, workspace, sessionId);
                if (session == null) throw new InvalidOperationException("Workspace session was not found.");
                InterruptAbandonedRun(session);
                return session;
            }
        }

        public WorkspaceDescriptor OpenWorkspace(string rootPath, bool createIfMissing,
            bool permitManifestWrite)
        {
            return _workspaces.Open(rootPath, createIfMissing, permitManifestWrite);
        }

        public ChatSession CreateSession(WorkspaceDescriptor workspace, string title)
        {
            return _workspaces.CreateSession(_chats, workspace, title);
        }

        public ChatSessionHeader[] ListSessions(WorkspaceDescriptor workspace)
        {
            return _workspaces.ListSessions(_chats, workspace);
        }

        public IReadOnlyList<string> AvailableToolIds(WorkspaceDescriptor workspace)
        {
            return new WorkspacePorts(_chats, _files, workspace, null, new AppSettings(), null, null)
                .ToolIds;
        }

        public WorkspaceFileRecoveryResult ReconcileFile(WorkspaceDescriptor workspace, string relativePath)
        {
            return _files.ReconcileUncertain(workspace, relativePath);
        }

        public Task<WebVerificationResult> VerifyWebAsync(WorkspaceDescriptor workspace,
            string entryPath, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new WorkspaceWebVerifier(_files).VerifyAsync(workspace, entryPath, cancellationToken);
        }

        public IReadOnlyList<string> MissingExpectedFiles(WorkspaceDescriptor workspace,
            IEnumerable<string> relativePaths)
        {
            var missing = new List<string>();
            foreach (var path in relativePaths ?? Enumerable.Empty<string>())
            {
                try { _files.ReadText(workspace, path); }
                catch (FileNotFoundException) { missing.Add(path); }
                catch (DirectoryNotFoundException) { missing.Add(path); }
            }
            return missing;
        }

        public async Task<WorkspaceRunResult> RunAsync(WorkspaceDescriptor workspace, string sessionId,
            string message, AppSettings settings, Func<string> apiKeyProvider,
            Action<WorkspaceRunEvent> progress = null, CancellationToken cancellationToken = default(CancellationToken),
            WorkspaceRunAcceptance acceptance = null, bool? reasoningEnabled = null, string modelDigest = null)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A task message is required.", nameof(message));
            if (settings == null || string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
                throw new ArgumentException("An LLM endpoint and model are required.", nameof(settings));
            var acceptedContract = PrepareAcceptance(acceptance);
            var session = string.IsNullOrWhiteSpace(sessionId)
                ? _workspaces.CreateSession(_chats, workspace, "Workspace chat")
                : _workspaces.LoadSession(_chats, workspace, sessionId);
            if (session == null) throw new InvalidOperationException("Workspace session was not found.");
            using (AcquireSessionLease(session.Id))
            {
                // Another process may have finished after the initial lookup.
                if (!string.IsNullOrWhiteSpace(sessionId))
                    session = _workspaces.LoadSession(_chats, workspace, sessionId);
                if (session == null) throw new InvalidOperationException("Workspace session was removed.");
                if (session.LastRun?.KernelState?.Summary?.Lifecycle == RunLifecycle.AwaitingConfirmation)
                    throw new InvalidOperationException("The pending approval must be resolved before another turn.");
                InterruptAbandonedRun(session);

                if (reasoningEnabled.HasValue) session.ReasoningEnabled = reasoningEnabled.Value;

                var previous = RestoreAcceptedHistory(session);
                var ports = new WorkspacePorts(_chats, _files, workspace, session, settings, apiKeyProvider,
                    progress, acceptedContract);
                var runId = Guid.NewGuid().ToString("N");
                session.LastRun = new ChatRunRecord
                { RunId = runId, TurnId = runId, StartedUtc = DateTime.UtcNow,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    ModelConfiguration = new ModelRunMetadata
                    {
                        Model = settings.Model,
                        Digest = modelDigest,
                        EndpointSha256 = EndpointSha256(settings.BaseUrl),
                        ContextWindowTokens = ModelContextBudget.ContextWindowTokens(settings),
                        AgentResponseMode = settings.AgentResponseMode,
                        ReasoningRequestMode = settings.ReasoningRequestMode,
                        ReasoningEnabled = session.ReasoningEnabled
                    },
                    WorkspaceAcceptance = acceptedContract };
                session.Model = settings.Model;
                session.Mode = "agent";
                session.Messages.Add(new ChatMessage { Role = "user", Content = message, RunId = runId });
                _chats.Save(session);

                var result = await ConversationRunCoordinator.RunAsync(
                    new AgentRunRequest(runId, runId, message,
                        new AgentRunLimits(Math.Max(1, settings.MaxAgentIterations), Math.Max(1, settings.MaxAgentToolSteps)), previous),
                    ports, ports.Tools, ports, cancellationToken).ConfigureAwait(false);
                if (acceptedContract.Requested)
                {
                    AssessAcceptance(workspace, session, result.Summary, acceptedContract);
                    session.LastRun.WorkspaceAcceptance = acceptedContract;
                    _chats.Save(session);
                }
                return new WorkspaceRunResult(session.Id, result.Summary, acceptedContract);
            }
        }

        public async Task<WorkspaceRunResult> ResolvePendingAsync(WorkspaceDescriptor workspace,
            string sessionId, string pendingId, bool approve, AppSettings settings,
            Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress = null,
            CancellationToken cancellationToken = default(CancellationToken), string modelDigest = null)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(pendingId))
                throw new ArgumentException("Workspace, session and pending confirmation are required.");
            if (approve && (settings == null || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                string.IsNullOrWhiteSpace(settings.Model)))
                throw new ArgumentException("The LLM endpoint and model are required after approval.", nameof(settings));
            using (AcquireSessionLease(sessionId))
            {
                var session = _workspaces.LoadSession(_chats, workspace, sessionId);
                var run = session?.LastRun?.KernelState;
                if (run?.Summary.Lifecycle != RunLifecycle.AwaitingConfirmation ||
                    run.Summary.PendingConfirmation?.PendingId != pendingId)
                    throw new InvalidOperationException("Pending confirmation was not found or was already resolved.");
                if (approve && !string.Equals(settings.Model, session.Model, StringComparison.Ordinal))
                    throw new InvalidOperationException("Approval must continue with the accepted run's model.");
                var modelConfiguration = session.LastRun.ModelConfiguration;
                if (approve && modelConfiguration != null &&
                    (ModelContextBudget.ContextWindowTokens(settings) != modelConfiguration.ContextWindowTokens ||
                     EndpointSha256(settings.BaseUrl) != modelConfiguration.EndpointSha256 ||
                     !string.Equals(modelDigest, modelConfiguration.Digest, StringComparison.Ordinal) ||
                     !string.Equals(settings.AgentResponseMode,
                         modelConfiguration.AgentResponseMode ?? AgentResponseModes.JsonSchema,
                         StringComparison.Ordinal) ||
                     !string.Equals(settings.ReasoningRequestMode, modelConfiguration.ReasoningRequestMode,
                         StringComparison.Ordinal)))
                    throw new InvalidOperationException("Approval must continue with the accepted run's endpoint, model digest, context and reasoning mode.");
                var history = RestoreAcceptedHistory(session);
                var continuation = AgentRunContinuation.Restore(run.Summary, run.Limits, 0, history);
                var acceptance = session.LastRun.WorkspaceAcceptance ?? new WorkspaceRunAcceptance();
                var ports = new WorkspacePorts(_chats, _files, workspace, session,
                    settings ?? new AppSettings(), apiKeyProvider, progress, acceptance, approve);
                var result = await ConversationRunCoordinator.ResumeAsync(session.LastRun.RunId,
                    pendingId, continuation, ports, ports.Tools, ports, cancellationToken,
                    rejectPending: !approve).ConfigureAwait(false);
                if (acceptance.Requested)
                {
                    AssessAcceptance(workspace, session, result.Summary, acceptance);
                    session.LastRun.WorkspaceAcceptance = acceptance;
                    _chats.Save(session);
                }
                return new WorkspaceRunResult(session.Id, result.Summary, acceptance);
            }
        }

        private static WorkspaceRunAcceptance PrepareAcceptance(WorkspaceRunAcceptance requested)
        {
            var files = requested?.ExpectedFiles?.ToArray() ?? new string[0];
            if (files.Length > 32 || files.Any(string.IsNullOrWhiteSpace) ||
                files.Distinct(StringComparer.Ordinal).Count() != files.Length ||
                requested != null && (requested.MinimumVerifiedReads < 0 || requested.MinimumVerifiedWrites < 0))
                throw new ArgumentException("Invalid workspace run acceptance criteria.", nameof(requested));
            var result = new WorkspaceRunAcceptance
            {
                ExpectedFiles = files.ToList(),
                MinimumVerifiedReads = requested?.MinimumVerifiedReads ?? 0,
                MinimumVerifiedWrites = requested?.MinimumVerifiedWrites ?? 0,
                RequireWebVerification = requested?.RequireWebVerification ?? false
            };
            result.State = result.Requested ? WorkspaceAcceptanceState.Pending : WorkspaceAcceptanceState.NotRequested;
            return result;
        }

        private void AssessAcceptance(WorkspaceDescriptor workspace, ChatSession session, RunSummary summary,
            WorkspaceRunAcceptance acceptance)
        {
            var completeReads = new List<ResourceEvidence>();
            var changed = 0;
            foreach (var fact in session.Messages.Where(item => item.RunId == summary.RunId &&
                item.ProtocolMessage && !string.IsNullOrWhiteSpace(item.ToolCallId)))
            {
                var wire = ToolResultWire.Read(fact.Content);
                if (!wire.Success || wire.Result.Status != ToolResultStatus.Ok) continue;
                if (fact.ToolName == "common.resources_read")
                    foreach (var evidence in fact.ResourceEvidence ?? new List<ResourceEvidence>())
                        if (evidence.Complete && evidence.View == "text") completeReads.Add(evidence);
                if (fact.ToolName != null && fact.ToolName.StartsWith("files.", StringComparison.Ordinal) &&
                    (fact.ResourceEffect?.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                        fact.ResourceEffect?.Outcome == ResourceEffectOutcome.Restored))
                    changed++;
            }
            acceptance.VerifiedFileChanges = changed;
            try
            {
                if (summary.Lifecycle != RunLifecycle.AwaitingConfirmation)
                    acceptance.MissingFiles = MissingExpectedFiles(workspace, acceptance.ExpectedFiles).ToList();
                var frozen = _files.CaptureAuthority(completeReads);
                var reducer = new EvidenceStateReducer();
                acceptance.AcceptedCompleteFileReads = completeReads.Where(evidence =>
                    reducer.Reduce(evidence, frozen).State == EvidenceState.Current)
                    .Select(evidence => evidence.Resource.Uri).Distinct(StringComparer.Ordinal).Count();
                acceptance.VerifiedWebSnapshot = acceptance.RequireWebVerification &&
                    AssessWebVerification(workspace, session, summary.RunId, acceptance.ExpectedFiles);
                if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation)
                {
                    acceptance.State = WorkspaceAcceptanceState.Pending;
                    return;
                }
                acceptance.State = summary.Reason == "model_done" && acceptance.MissingFiles.Count == 0 &&
                    acceptance.AcceptedCompleteFileReads >= acceptance.MinimumVerifiedReads &&
                    acceptance.VerifiedFileChanges >= acceptance.MinimumVerifiedWrites &&
                    (!acceptance.RequireWebVerification || acceptance.VerifiedWebSnapshot)
                    ? WorkspaceAcceptanceState.Passed : WorkspaceAcceptanceState.Failed;
            }
            catch (Exception ex) when (ex is WorkspaceFileException || ex is IOException || ex is UnauthorizedAccessException)
            {
                acceptance.State = WorkspaceAcceptanceState.Unknown;
                acceptance.Error = ex.Message;
            }
        }

        private sealed class WebVerificationToolData
        {
            [JsonProperty("status")]
            public string Status { get; set; }
            [JsonProperty("checkedFiles")]
            public List<string> CheckedFiles { get; set; }
        }

        private bool AssessWebVerification(WorkspaceDescriptor workspace, ChatSession session,
            string runId, IReadOnlyList<string> expectedFiles)
        {
            var fact = session.Messages.LastOrDefault(item => item.RunId == runId &&
                item.ProtocolMessage && item.ToolName == "web.verify" &&
                !string.IsNullOrWhiteSpace(item.ToolCallId));
            if (fact == null) return false;
            var wire = ToolResultWire.Read(fact.Content);
            if (!wire.Success || wire.Result.Status != ToolResultStatus.Ok) return false;
            WebVerificationToolData data;
            try { data = JsonConvert.DeserializeObject<WebVerificationToolData>(wire.Result.DataJson); }
            catch (JsonException) { return false; }
            if (data?.Status != "passed" || data.CheckedFiles == null || data.CheckedFiles.Count == 0 ||
                data.CheckedFiles.Count != (fact.ResourceEvidence?.Count ?? 0) ||
                expectedFiles.Any(path => !data.CheckedFiles.Contains(path, StringComparer.Ordinal))) return false;
            foreach (var path in data.CheckedFiles)
            {
                try { _files.ReadText(workspace, path); }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }
            var frozen = _files.CaptureAuthority(fact.ResourceEvidence);
            var reducer = new EvidenceStateReducer();
            return fact.ResourceEvidence.All(evidence => evidence != null && evidence.Complete &&
                reducer.Reduce(evidence, frozen).State == EvidenceState.Current);
        }

        private IDisposable AcquireSessionLease(string sessionId)
        {
            var directory = Path.Combine(_paths.WorkspaceDirectory, "run-leases");
            StorageFileSystem.EnsureRegularDirectory(directory);
            var path = Path.Combine(directory, TextPatternEngine.Sha256(sessionId) + ".lck");
            if (File.Exists(path) && StorageFileSystem.IsReparsePoint(path))
                throw new IOException("Workspace run lease cannot be a link.");
            try { return StorageFileSystem.AcquireWriteLock(path); }
            catch (IOException ex) { throw new IOException("Workspace session is already running or its lease is unavailable.", ex); }
        }

        private void InterruptAbandonedRun(ChatSession session)
        {
            var run = session.LastRun;
            if (run == null || string.IsNullOrWhiteSpace(run.RunId)) return;
            var lifecycle = run.KernelState?.Summary?.Lifecycle;
            if (lifecycle.HasValue ? lifecycle.Value != RunLifecycle.Running :
                !string.IsNullOrWhiteSpace(run.Status) && run.Status != "running" &&
                run.Status != "cancelling") return;
            var active = run.KernelState?.InFlightTool;
            var accepted = session.Messages.Where(item => item != null && item.RunId == run.RunId &&
                item.ProtocolMessage && item.Role == "assistant")
                .SelectMany(item => item.ToolCalls ?? new List<LlmToolCall>())
                .Select(call => call.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
            var completed = new HashSet<string>(session.Messages.Where(item => item != null &&
                item.RunId == run.RunId && item.ProtocolMessage && !string.IsNullOrWhiteSpace(item.ToolCallId))
                .Select(item => item.ToolCallId), StringComparer.OrdinalIgnoreCase);
            var incomplete = accepted.Any(id => !completed.Contains(id));
            if (active != null || incomplete)
                foreach (var item in session.Messages.Where(item => item != null &&
                    item.RunId == run.RunId && item.ProtocolMessage))
                    item.ExcludeFromModelContext = true;
            run.InterruptedToolId = active?.Call.Name;
            if (active != null && active.Call.Name.StartsWith("files.", StringComparison.Ordinal))
            {
                try
                {
                    var args = JObject.Parse(active.Call.ArgumentsJson);
                    run.InterruptedFilePath = (string)args["relativePath"];
                    run.InterruptedTargetPath = (string)args["targetPath"];
                }
                catch (JsonException) { }
            }
            run.InterruptedEffectPossible = active?.Policy.MayHaveSideEffects == true;
            var message = run.InterruptedEffectPossible
                ? "Previous CLI process stopped with a possible file effect; inspect current files and reconcile an unresolved attempt before a new edit. No call was replayed."
                : "Previous CLI process stopped before a terminal run result. No call was replayed; submit explicit new input to continue.";
            if (run.KernelState != null) run.KernelState = run.KernelState.Interrupt(false, message);
            run.Status = "interrupted";
            run.Phase = "interrupted";
            run.CurrentAction = message;
            if (run.WorkspaceAcceptance?.Requested == true)
            {
                run.WorkspaceAcceptance.State = WorkspaceAcceptanceState.Unknown;
                run.WorkspaceAcceptance.Error = "Run interrupted before acceptance could be assessed.";
            }
            session.Messages.Add(new ChatMessage { Role = "assistant", RunId = run.RunId, Content = message });
            _chats.Save(session);
        }

        private static IReadOnlyList<AgentMessage> RestoreAcceptedHistory(ChatSession session)
        {
            var history = new List<AgentMessage>();
            foreach (var item in session.Messages ?? new List<ChatMessage>())
            {
                if (item == null || item.ExcludeFromModelContext) continue;
                if (item.Role == "user" && !item.ProtocolMessage)
                {
                    history.Add(AgentMessage.User(item.Content));
                    continue;
                }
                if (item.Role == "assistant" && item.ProtocolMessage)
                {
                    var parsed = ConversationResponseJson.Read(item.Content);
                    if (!parsed.Success) throw new InvalidOperationException("Stored workspace response is invalid: " + parsed.Error);
                    var saved = item.ToolCalls ?? new List<LlmToolCall>();
                    if (saved.Count != parsed.Response.ToolCalls.Count)
                        throw new InvalidOperationException("Stored workspace accepted-call IDs are incomplete.");
                    var calls = new List<ToolCall>();
                    for (var i = 0; i < saved.Count; i++)
                    {
                        if (saved[i]?.Id == null || saved[i].Name != parsed.Response.ToolCalls[i].Name ||
                            saved[i].ArgumentsJson != parsed.Response.ToolCalls[i].Arguments.ToString(Formatting.None))
                            throw new InvalidOperationException("Stored workspace accepted call differs from response.");
                        calls.Add(new ToolCall(saved[i].Id, saved[i].Name, saved[i].ArgumentsJson));
                    }
                    history.Add(AgentMessage.Assistant(new AgentResponse(parsed.Response.Message, calls, parsed.Response.Action)));
                    continue;
                }
                if (item.ProtocolMessage && !string.IsNullOrWhiteSpace(item.ToolCallId))
                {
                    var parsed = ToolResultWire.Read(item.Content);
                    if (!parsed.Success || parsed.ToolCallId != item.ToolCallId)
                        throw new InvalidOperationException("Stored workspace tool result is invalid.");
                    history.Add(AgentMessage.AcceptedToolResult(item.ToolCallId, parsed.Result.Message,
                        item.Content, item.ExecutionProgress, item.ResourceEvidence, item.ResourceEffect));
                }
            }
            return history;
        }

        private static string EndpointSha256(string baseUrl)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((baseUrl ?? string.Empty).TrimEnd('/')));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private sealed class WorkspacePorts : IModelProtocol, IRunStore
        {
            private readonly ChatStore _chats;
            private readonly WorkspaceFileService _files;
            private readonly WorkspaceDescriptor _workspace;
            private readonly ChatSession _session;
            private readonly AppSettings _settings;
            private readonly IMaterializedModelProtocol _protocol;
            private readonly Action<WorkspaceRunEvent> _progress;
            private readonly WorkspaceRunAcceptance _acceptance;
            private readonly IReadOnlyList<ToolCatalogEntry> _catalog;
            private readonly ResourceProviderRouter<WorkspaceFileResourceProvider> _resources;
            private readonly Dictionary<string, ResourceRef> _observed = new Dictionary<string, ResourceRef>(StringComparer.Ordinal);
            private long _cursor;
            public ToolRuntime Tools { get; private set; }
            public IReadOnlyList<string> ToolIds { get { return _catalog.Select(item => item.Id).ToArray(); } }

            public WorkspacePorts(ChatStore chats, WorkspaceFileService files, WorkspaceDescriptor workspace,
                ChatSession session, AppSettings settings, Func<string> apiKeyProvider,
                Action<WorkspaceRunEvent> progress, WorkspaceRunAcceptance acceptance = null,
                bool restorePendingObservation = true)
            {
                _chats = chats; _files = files; _workspace = workspace; _session = session;
                _settings = settings.Clone(); _progress = progress; _acceptance = acceptance;
                _resources = new ResourceProviderRouter<WorkspaceFileResourceProvider>(new[]
                    { new WorkspaceFileResourceProvider(files, workspace) });
                var client = new LlmClient(apiKeyProvider);
                _protocol = new ModelProtocolClient(client.CompleteAsync);
                var registry = new ToolHandlerRegistry();
                var entries = new List<ToolCatalogEntry>();
                Register(registry, entries, "common.resources_find", "List up to 200 accessible names in one workspace directory; narrow directory or query when truncated.",
                    Schema("directory", false, "query", false), false,
                    new FileHandler(files, workspace, _resources, _observed, "find"));
                Register(registry, entries, "common.resources_read", "Read a complete UTF-8 workspace file by relative path.",
                    Schema("relativePath", true), false,
                    new FileHandler(files, workspace, _resources, _observed, "read"));
                if (WorkspaceWebVerifier.FindBrowserExecutable() != null)
                    Register(registry, entries, "web.verify", "Load an immutable local HTML/CSS/JS snapshot in an isolated browser and report asset and runtime errors.",
                        Schema("entryPath", true), false,
                        new WebVerifierHandler(files, workspace), independentRead: false);
                if (!workspace.ReadOnly)
                {
                    Register(registry, entries, "files.create", "Create a new real UTF-8 file; never overwrite.",
                        Schema("relativePath", true, "text", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "create"));
                    Register(registry, entries, "files.copy", "Copy a previously read complete UTF-8 file to a new path; never overwrite.",
                        Schema("relativePath", true, "targetPath", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "copy"));
                    Register(registry, entries, "files.move", "Move a previously read complete UTF-8 file to a new path; preserve file identity and never overwrite.",
                        Schema("relativePath", true, "targetPath", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "move"), true);
                    Register(registry, entries, "files.patch", "Replace one exact unique text anchor in a previously read file.",
                        Schema("relativePath", true, "oldText", true, "newText", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "patch"));
                    Register(registry, entries, "files.replace", "Replace a previously read whole UTF-8 file.",
                        Schema("relativePath", true, "text", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "replace"));
                    Register(registry, entries, "files.delete", "Move a previously read UTF-8 file into managed workspace trash.",
                        Schema("relativePath", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "delete"), true);
                    Register(registry, entries, "files.restore", "Restore the latest managed deletion to its original path without overwriting.",
                        Schema("relativePath", true), true,
                        new FileHandler(files, workspace, _resources, _observed, "restore"));
                }
                _catalog = entries;
                Tools = new ToolRuntime(registry, "agent", false, true,
                    (context, preparation) =>
                    {
                        if (context.Call.Name == "files.delete" || context.Call.Name == "files.move")
                        {
                            var path = (string)JObject.Parse(context.Call.ArgumentsJson)["relativePath"];
                            if (string.IsNullOrWhiteSpace(path) || !_observed.ContainsKey(path))
                                throw new InvalidOperationException("Read the complete current file before requesting move or deletion confirmation.");
                        }
                        return "pending_" + Guid.NewGuid().ToString("N");
                    });
                if (restorePendingObservation) RestorePendingObservation();
            }

            private void RestorePendingObservation()
            {
                var pending = _session?.LastRun?.KernelState?.Summary?.PendingConfirmation;
                if (pending?.Call.Name != "files.delete" && pending?.Call.Name != "files.move") return;
                var path = (string)JObject.Parse(pending.Call.ArgumentsJson)["relativePath"];
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Pending file mutation has no semantic path.");
                foreach (var fact in _session.Messages.AsEnumerable().Reverse())
                {
                    if (fact.RunId != _session.LastRun.RunId || fact.ToolName != "common.resources_read") continue;
                    var wire = ToolResultWire.Read(fact.Content);
                    if (!wire.Success || wire.Result.Status != ToolResultStatus.Ok) continue;
                    if ((string)JObject.Parse(wire.Result.DataJson)["path"] != path) continue;
                    var evidence = fact.ResourceEvidence?.SingleOrDefault(item => item.Complete && item.View == "text");
                    if (evidence == null) continue;
                    _observed[path] = evidence.Resource.Copy();
                    return;
                }
                throw new InvalidOperationException("Pending file mutation has no accepted complete read evidence.");
            }

            private static void Register(ToolHandlerRegistry registry, List<ToolCatalogEntry> entries,
                string id, string description, string schema, bool mutation, IToolHandler handler,
                bool requiresConfirmation = false, bool independentRead = true)
            {
                var policy = new ToolPolicy(mutation ? ToolEffect.Write : ToolEffect.Read,
                    mutation ? ToolVerification.Tool : ToolVerification.None,
                    requiresConfirmation, !mutation && independentRead, new[] { "agent" }, mutation ? 1 : 0);
                var binding = new ToolBinding(id, scope: "workspace", host: "Common");
                registry.Register(new ToolRegistration(new ToolDescriptor(id, description, schema),
                    policy, binding, "workspace-v1"), handler);
                entries.Add(new ToolCatalogEntry
                { Id = id, Host = "Common", Name = id, Description = description,
                    ArgumentSchemaJson = schema, Policy = policy, Binding = binding, AgentCanRun = true,
                    BuiltIn = true, Enabled = true });
            }

            private static string Schema(string first, bool firstRequired,
                string second = null, bool secondRequired = false, string third = null, bool thirdRequired = false)
            {
                var properties = new JObject { [first] = new JObject { ["type"] = "string", ["description"] = first } };
                var required = new JArray();
                if (firstRequired) required.Add(first);
                if (second != null) { properties[second] = new JObject { ["type"] = "string", ["description"] = second }; if (secondRequired) required.Add(second); }
                if (third != null) { properties[third] = new JObject { ["type"] = "string", ["description"] = third }; if (thirdRequired) required.Add(third); }
                return new JObject { ["type"] = "object", ["properties"] = properties,
                    ["required"] = required, ["additionalProperties"] = false }.ToString(Formatting.None);
            }

            public async Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken cancellationToken)
            {
                // Refresh only files read in this run. Old-turn read bodies are
                // projected as stale below; they never authorize a new write.
                foreach (var path in _observed.Keys.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { _resources.Select("file").Read(path); }
                    catch (System.IO.FileNotFoundException) { /* ReadText marked the old head unavailable. */ }
                }
                var frozen = _resources.Select("file").CaptureAuthority(request.AcceptedMessages.SelectMany(item =>
                    item.ResourceEvidence ?? new ResourceEvidence[0]));
                var messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "developer", Content = Prompt() }
                };
                foreach (var item in request.AcceptedMessages)
                {
                    if (item.Kind == AgentMessageKind.User)
                        messages.Add(new ChatMessage { Role = "user", Content = item.Text });
                    else if (item.Kind == AgentMessageKind.Assistant)
                    {
                        var calls = item.ToolCalls.Select(call => new ConversationToolCall
                        { Name = call.Name, Arguments = JObject.Parse(call.ArgumentsJson) }).ToArray();
                        messages.Add(new ChatMessage { Role = "assistant",
                            Content = new ConversationResponse(item.Text, calls, item.Action).ToJson() });
                    }
                    else if (item.Kind == AgentMessageKind.ToolResult)
                        messages.Add(new ChatMessage { Role = "user", Content = ProjectResult(item, frozen) });
                }
                var options = ModelProtocolWire.CreateRequestOptions(_settings.AgentResponseMode, _catalog);
                options.ReasoningEnabled = _session.ReasoningEnabled;
                options.TraceStepId = request.StepId;
                options.TraceModelAttemptId = "attempt_" + Guid.NewGuid().ToString("N");
                var callContext = new ModelProtocolCallContext(_catalog.Where(tool => tool.Policy.IndependentLocalRead).Select(tool => tool.Id));
                _chats.AppendTrace(_session, SessionEventTypes.LlmRequest,
                    new { request.StepId, Protocol = ConversationResponse.ProtocolVersion, WorkspaceId = _workspace.WorkspaceId },
                    JsonConvert.SerializeObject(messages), "application/json", request.RunId, request.TurnId, request.StepId);
                var result = await _protocol.GetResponseAsync(new ModelProtocolRequest
                {
                    Settings = _settings, AcceptedMessages = messages,
                    CompileRepair = notice => messages.Concat(new[] { notice }).ToArray(),
                    CallableTools = _catalog, RunnableCatalog = _catalog,
                    CallContext = callContext, Options = options
                }, null, cancellationToken).ConfigureAwait(false);
                _chats.AppendTrace(_session, result.Failure == null ? SessionEventTypes.LlmResponse : SessionEventTypes.LlmFailure,
                    new { request.StepId, Accepted = result.Response != null, Failure = result.Failure?.Kind.ToString() },
                    result.Completion?.Content, "application/json", request.RunId, request.TurnId, request.StepId);
                if (result.Failure != null) return AgentModelResult.Failed(result.Failure.Kind, result.Failure.Message);
                if (result.ProviderRefusal != null) return AgentModelResult.Refused(result.ProviderRefusal);
                if (result.Response == null) return AgentModelResult.Failed(ModelProtocolFailureKind.Infrastructure, "Missing accepted response.");
                return AgentModelResult.Accepted(new AgentResponseDraft(result.Response.Message,
                    result.Response.ToolCalls.Select(call => new ToolCallDraft(call.Name,
                        call.Arguments.ToString(Formatting.None))), result.Response.Action));
            }

            private string ProjectResult(AgentMessage item, ResourceAuthoritySnapshotSet frozen)
            {
                var resultJson = item.ResultJson;
                if (string.IsNullOrWhiteSpace(resultJson) && item.Execution != null)
                    resultJson = WireResult(item.Execution);
                if (string.IsNullOrWhiteSpace(resultJson))
                    throw new InvalidOperationException("Accepted tool result has no model-facing body.");
                var wire = ToolResultWire.Read(resultJson);
                if (!wire.Success || wire.Name != "common.resources_read" ||
                    wire.Result.Status != ToolResultStatus.Ok) return resultJson;
                string path = null;
                try { path = (string)JObject.Parse(wire.Result.DataJson)["path"]; }
                catch (JsonException) { }
                ResourceRef accepted;
                var current = !string.IsNullOrWhiteSpace(path) && _observed.TryGetValue(path, out accepted) &&
                    item.ResourceEvidence != null && item.ResourceEvidence.Any(evidence =>
                        evidence.Resource.Uri == accepted.Uri && evidence.Resource.Revision == accepted.Revision &&
                        new EvidenceStateReducer().Reduce(evidence, frozen).State == EvidenceState.Current);
                if (current) return resultJson;
                return ToolResultWire.Write(item.ToolCallId, wire.Name,
                    ToolResult.Error("The prior file observation is stale or unavailable. Read this path again before editing.",
                        JsonConvert.SerializeObject(new { path, code = "resource_evidence_stale" })));
            }

            private static string WireResult(ToolExecutionRecord record)
            {
                var result = record.Result;
                if (result == null)
                {
                    if (record.Outcome == ToolExecutionOutcome.Ok)
                        throw new InvalidOperationException("Successful tool execution has no terminal result.");
                    result = record.Outcome == ToolExecutionOutcome.Unknown
                        ? ToolResult.Unknown(record.Message)
                        : ToolResult.Error(record.Message, record.Outcome == ToolExecutionOutcome.NotDispatched
                            ? "{\"code\":\"not_dispatched\",\"dispatched\":false}"
                            : "{\"code\":\"execution_error\"}");
                }
                return ToolResultWire.Write(record.Context.Call.Id, record.Context.Call.Name, result);
            }

            private string Prompt()
            {
                return "You are the RNAssistant workspace agent. Follow the user's task using real files in the selected workspace. " +
                    (_acceptance?.Requested == true ? "The accepted task contract requires current files and verified tool results before done: " +
                        JsonConvert.SerializeObject(new { expectedFiles = _acceptance.ExpectedFiles,
                            minimumVerifiedReads = _acceptance.MinimumVerifiedReads,
                            minimumVerifiedWrites = _acceptance.MinimumVerifiedWrites,
                            requireWebVerification = _acceptance.RequireWebVerification }) + ". " : string.Empty) +
                    "Respond with exactly one conversation-response v6 JSON object: message, action, tool_calls. " +
                    "Use action=tool for calls, continue for a short checkpoint, done only when the requested work is complete, " +
                    "blocked or needs_input when it cannot continue. One mutation per response. " +
                    "Never invent tool results, file changes, revisions or permissions. Paths are relative to the workspace root. " +
                    "Historical assistant messages and tool calls describe past actions, not current source bytes. " +
                    "A stale read result requires a new read. Read an existing file before patch or replace. " +
                    "For an existing broken web app, call web.verify before editing; for new work, call it after writing. If it fails, read the indicated source, make a relevant fix, then verify again. Never repeat an unchanged failed verification. " +
                    "Available tools and exact argument schemas: " +
                    JsonConvert.SerializeObject(_catalog.Select(tool => new { tool.Id, tool.Description, Schema = JObject.Parse(tool.ArgumentSchemaJson) })) +
                    ". Workspace root is a user-selected directory; internal absolute paths and runtime references are not tool arguments.";
            }

            public Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken cancellationToken)
            {
                if (expectedRevision != _cursor) throw new InvalidOperationException("Stale workspace run cursor.");
                _session.LastRun.KernelState = AgentRunState.Apply(_session.LastRun.KernelState, fact);
                _session.LastRun.Status = fact.Summary.Lifecycle.ToString().ToLowerInvariant();
                _session.LastRun.IterationsUsed = fact.Summary.IterationsUsed;
                _session.LastRun.ToolStepsUsed = fact.Summary.ToolStepsUsed;
                if (fact.Kind == AgentRunEventKind.ResponseAccepted)
                {
                    var calls = fact.Response.ToolCalls.Select(call => new ConversationToolCall
                    { Name = call.Name, Arguments = JObject.Parse(call.ArgumentsJson) }).ToArray();
                    _session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant", ProtocolMessage = true,
                        ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                        Content = new ConversationResponse(fact.Response.Message, calls, fact.Response.Action).ToJson(),
                        ToolCalls = fact.Response.ToolCalls.Select(call => new LlmToolCall
                        { Id = call.Id, Type = "function", Name = call.Name, ArgumentsJson = call.ArgumentsJson }).ToList(),
                        RunId = fact.Summary.RunId
                    });
                }
                if (fact.Kind == AgentRunEventKind.ToolCompleted &&
                    fact.Execution.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
                {
                    var record = fact.Execution;
                    _session.Messages.Add(new ChatMessage
                    {
                        Role = "user", ProtocolMessage = true, ToolCallId = record.Context.Call.Id,
                        ToolName = record.Context.Call.Name, ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                        Content = WireResult(record),
                        ExecutionProgress = ToolExecutionProgress.Capture(record),
                        ResourceEvidence = record.ResourceEvidence.ToList(), ResourceEffect = record.ResourceEffect,
                        RunId = fact.Summary.RunId
                    });
                }
                _chats.Save(_session);
                _cursor++;
                _progress?.Invoke(new WorkspaceRunEvent(fact.Kind.ToString(), fact.Summary,
                    fact.ToolContext?.Call.Name ?? fact.Execution?.Context.Call.Name));
                return Task.FromResult(_cursor);
            }
        }

        private sealed class FileHandler : IReadOnlyToolHandler, IManagedMutationToolHandler
        {
            private readonly WorkspaceFileService _files;
            private readonly WorkspaceDescriptor _workspace;
            private readonly ResourceProviderRouter<WorkspaceFileResourceProvider> _resources;
            private readonly Dictionary<string, ResourceRef> _observed;
            private readonly string _operation;
            public FileHandler(WorkspaceFileService files, WorkspaceDescriptor workspace,
                ResourceProviderRouter<WorkspaceFileResourceProvider> resources,
                Dictionary<string, ResourceRef> observed, string operation)
            { _files = files; _workspace = workspace; _resources = resources;
                _observed = observed; _operation = operation; }

            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_operation == "find")
                    {
                        var directory = Value(context, "directory");
                        var query = Value(context, "query");
                        var page = _resources.Select("file").Find(directory, query);
                        return Return(ToolResult.Ok(page.Truncated
                            ? "Workspace scan was truncated; narrow the directory or filename query."
                            : "Workspace entries listed.",
                            JsonConvert.SerializeObject(new { directory, query, names = page.Names,
                                truncated = page.Truncated, scannedEntries = page.ScannedEntries })), ToolEffectEvidence.None);
                    }
                    var path = Value(context, "relativePath");
                    if (_operation == "read")
                    {
                        var read = _resources.Select("file").Read(path);
                        if (read.Text.Length > 16000)
                            return Return(ToolResult.Error("Whole file exceeds the model read bound; no write observation was accepted.",
                                JsonConvert.SerializeObject(new { path, length = read.Text.Length, code = "read_too_large" })), ToolEffectEvidence.None);
                        _observed[path] = read.Reference;
                        return Return(ToolResult.Ok("Complete file read.",
                            JsonConvert.SerializeObject(new { path, text = read.Text, complete = true })),
                            ToolEffectEvidence.None, read.Evidence);
                    }
                    WorkspaceFileObservation changed;
                    ResourceRef expected = null;
                    if (_operation == "restore")
                    {
                        changed = _files.RestoreDeletedText(_workspace, path, context.MarkDispatchPossible);
                        _observed[path] = changed.Reference;
                        return Return(ToolResult.Ok("Deleted file restored and verified by exact read-back.",
                            JsonConvert.SerializeObject(new { path, restored = true })),
                            ToolEffectEvidence.VerifiedChange, authorityCommit: changed.AuthorityCommit);
                    }
                    if (_operation == "create")
                        changed = _files.CreateText(_workspace, path, Value(context, "text"), context.MarkDispatchPossible);
                    else if (_operation == "copy")
                    {
                        if (!_observed.TryGetValue(path, out expected))
                            return Return(ToolResult.Error("Read the complete current source before copying it.",
                                "{\"code\":\"source_observation_required\"}"), ToolEffectEvidence.None);
                        var target = Value(context, "targetPath");
                        changed = _files.CopyText(_workspace, path, expected, target, context.MarkDispatchPossible);
                        path = target;
                        expected = null; // A copy always creates a distinct target.
                    }
                    else if (_operation == "move")
                    {
                        if (!_observed.TryGetValue(path, out expected))
                            return Return(ToolResult.Error("Read the complete current source before moving it.",
                                "{\"code\":\"source_observation_required\"}"), ToolEffectEvidence.None);
                        var target = Value(context, "targetPath");
                        changed = _files.MoveText(_workspace, path, expected, target, context.MarkDispatchPossible);
                        _observed.Remove(path);
                        _observed[target] = changed.Reference;
                        return Return(ToolResult.Ok("File moved and verified at the new path.",
                            JsonConvert.SerializeObject(new { path, targetPath = target, moved = true })),
                            ToolEffectEvidence.VerifiedChange, authorityCommit: changed.AuthorityCommit);
                    }
                    else
                    {
                        if (!_observed.TryGetValue(path, out expected))
                            return Return(ToolResult.Error("Read the complete current file before editing it.",
                                "{\"code\":\"source_observation_required\"}"), ToolEffectEvidence.None);
                        if (_operation == "delete")
                        {
                            var deleted = _files.DeleteText(_workspace, path, expected, context.MarkDispatchPossible);
                            _observed.Remove(path);
                            return Return(ToolResult.Ok("File moved to managed workspace trash and verified.",
                                JsonConvert.SerializeObject(new { path, deleted = true })),
                                ToolEffectEvidence.VerifiedChange, authorityCommit: deleted.AuthorityCommit);
                        }
                        changed = _operation == "patch"
                            ? _files.PatchExact(_workspace, path, expected, Value(context, "oldText"),
                                Value(context, "newText"), context.MarkDispatchPossible)
                            : _files.ReplaceText(_workspace, path, expected, Value(context, "text"), context.MarkDispatchPossible);
                    }
                    _observed[path] = changed.Reference;
                    var didChange = expected == null || expected.Revision != changed.Reference.Revision;
                    return Return(ToolResult.Ok(didChange ? "File write verified by exact read-back." : "File content already matched; no change.",
                        JsonConvert.SerializeObject(new { path, changed = didChange })),
                        didChange ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange,
                        authorityCommit: changed.AuthorityCommit);
                }
                catch (WorkspaceFileException ex)
                {
                    return Return(ex.Code == "effect_unknown"
                        ? ToolResult.Unknown(ex.Message, JsonConvert.SerializeObject(new { code = ex.Code }))
                        : ToolResult.Error(ex.Message, JsonConvert.SerializeObject(new { code = ex.Code })),
                        ex.Code == "effect_unknown" ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None);
                }
            }

            private static string Value(ToolHandlerContext context, string name)
            {
                object value;
                return context.Arguments.TryGetValue(name, out value) ? Convert.ToString(value) : string.Empty;
            }
            private static Task<ToolHandlerResult> Return(ToolResult result, ToolEffectEvidence effect,
                ResourceEvidence evidence = null, ResourceAuthorityCommit authorityCommit = null)
            { return Task.FromResult(new ToolHandlerResult(result, effect,
                resourceEvidence: evidence == null ? null : new[] { evidence }, authorityCommit: authorityCommit)); }
        }

        private sealed class WebVerifierHandler : IReadOnlyToolHandler
        {
            private readonly WorkspaceWebVerifier _verifier;
            private readonly WorkspaceDescriptor _workspace;
            public WebVerifierHandler(WorkspaceFileService files, WorkspaceDescriptor workspace)
            { _verifier = new WorkspaceWebVerifier(files); _workspace = workspace; }

            public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context,
                CancellationToken cancellationToken)
            {
                object value;
                var entry = context.Arguments.TryGetValue("entryPath", out value)
                    ? Convert.ToString(value) : string.Empty;
                WebVerificationResult result;
                try { result = await _verifier.VerifyAsync(_workspace, entry, cancellationToken).ConfigureAwait(false); }
                catch (WorkspaceFileException ex)
                {
                    return new ToolHandlerResult(ToolResult.Error(ex.Message,
                        JsonConvert.SerializeObject(new { code = ex.Code })), ToolEffectEvidence.None);
                }
                var data = JsonConvert.SerializeObject(new { entryPath = result.EntryPath,
                    status = WorkspaceWebVerifier.StatusCode(result.Status),
                    checkedFiles = result.CheckedFiles, errors = result.Errors, hints = result.Hints });
                var toolResult = result.Status == WebVerificationStatus.Passed
                    ? ToolResult.Ok("Isolated browser smoke passed for this immutable snapshot.", data)
                    : ToolResult.Error(result.Status == WebVerificationStatus.NotRun
                        ? "Browser verification was not run." :
                        "Browser verification failed: " + result.Errors.FirstOrDefault() + " " +
                        result.Hints.FirstOrDefault() + " Read and fix the indicated source before verifying again.", data);
                return new ToolHandlerResult(toolResult, ToolEffectEvidence.None,
                    resourceEvidence: result.Status == WebVerificationStatus.Passed ? result.Evidence : null);
            }
        }
    }

    public sealed class WorkspaceRunEvent
    {
        public string Kind { get; private set; }
        public RunSummary Summary { get; private set; }
        public string ToolId { get; private set; }
        public WorkspaceRunEvent(string kind, RunSummary summary, string toolId)
        { Kind = kind; Summary = summary; ToolId = toolId; }
    }

    public sealed class WorkspaceRunResult
    {
        public string SessionId { get; private set; }
        public RunSummary Summary { get; private set; }
        public WorkspaceRunAcceptance Acceptance { get; private set; }
        public WorkspaceRunResult(string sessionId, RunSummary summary, WorkspaceRunAcceptance acceptance)
        { SessionId = sessionId; Summary = summary; Acceptance = acceptance; }
    }
}
