using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime;

namespace RNAssistant.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return Run(args ?? new string[0]); }
            catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            catch (WorkspaceFileException ex) { Console.Error.WriteLine(ex.Code + ": " + ex.Message); return 5; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 5; }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
            {
                Help(); return 0;
            }
            Dictionary<string, string> options;
            if (args[0] == "workspace" && args.Length >= 3 && args[1] == "open" &&
                !args[2].StartsWith("--", StringComparison.Ordinal))
            {
                options = Parse(args.Skip(3).ToArray());
                options["workspace"] = args[2];
                options["create"] = "true";
            }
            else options = Parse(args.Skip(1).ToArray());
            var jsonl = options.ContainsKey("jsonl");
            var root = Required(options, "workspace");
            var stateRoot = Environment.GetEnvironmentVariable("RNA_STATE_ROOT");
            var paths = string.IsNullOrWhiteSpace(stateRoot)
                ? AppDataPaths.CreateDefault() : AppDataPaths.CreateForRoot(stateRoot);
            var service = new WorkspaceConversationService(paths);
            var workspace = service.OpenWorkspace(root, args[0] == "workspace" && options.ContainsKey("create"),
                !options.ContainsKey("read-only"));
            if (args[0] == "workspace")
            {
                Output(jsonl, "workspace.opened", new { workspace.WorkspaceId, workspace.Name,
                    workspace.RootPath, workspace.ReadOnly });
                return 0;
            }
            if (args[0] == "env")
            {
                Output(jsonl, "environment", new { workspace.WorkspaceId, workspace.RootPath,
                    profile = "development", capabilities = service.AvailableToolIds(workspace) });
                return 0;
            }
            if (args[0] == "sessions")
            {
                Output(jsonl, "sessions", new { workspace.WorkspaceId,
                    sessions = service.ListSessions(workspace).Select(item => new { item.Id, item.Title,
                        item.RunStatus, item.LastActivityUtc }) });
                return 0;
            }
            if (args[0] == "recover")
            {
                var relativePath = Required(options, "path");
                var recovered = service.ReconcileFile(workspace, relativePath);
                Output(jsonl, "resource.reconciled", new { path = relativePath, recovered.Exists,
                    currentSha256 = recovered.Current?.ContentSha256,
                    outcome = recovered.Outcome.ToString(), replayed = false });
                return 0;
            }
            if (args[0] == "verify")
            {
                var result = service.VerifyWebAsync(workspace, Value(options, "entry", "index.html"))
                    .GetAwaiter().GetResult();
                Output(jsonl, "verification.completed", new { entryPath = result.EntryPath,
                    status = WorkspaceWebVerifier.StatusCode(result.Status),
                    checkedFiles = result.CheckedFiles, snapshotSha256 = result.SnapshotSha256,
                    browser = result.Browser, errors = result.Errors, hints = result.Hints });
                return result.Status == WebVerificationStatus.Passed ? 0 :
                    result.Status == WebVerificationStatus.NotRun ? 4 : 5;
            }
            if (args[0] == "inspect")
            {
                var session = service.GetSession(workspace, Required(options, "session"));
                if (session == null) throw new ArgumentException("Session not found.");
                Output(jsonl, "session", new { session.Id, session.WorkspaceId, session.Title,
                    session.Model, session.Mode, session.Revision, session.LastRun?.Status,
                    modelConfiguration = session.LastRun?.ModelConfiguration,
                    contextReceipt = session.LastContextReceipt,
                    acceptance = session.LastRun?.WorkspaceAcceptance,
                    interruptedToolId = session.LastRun?.InterruptedToolId,
                    interruptedPath = session.LastRun?.InterruptedFilePath,
                    interruptedEffectPossible = session.LastRun?.InterruptedEffectPossible,
                    messageCount = session.Messages?.Count ?? 0 });
                return 0;
            }
            if (args[0] == "resume")
            {
                var session = service.PrepareResume(workspace, Required(options, "session"));
                var pending = session.LastRun?.KernelState?.Summary?.PendingConfirmation;
                if (pending != null)
                {
                    var pendingTarget = PendingTarget(pending.Call.Name, pending.Call.ArgumentsJson);
                    Output(jsonl, "run.awaiting_confirmation", new { session.Id,
                        pendingId = pending.PendingId, toolId = pending.Call.Name,
                        path = PendingPath(pending.Call.Name, pending.Call.ArgumentsJson),
                        targetPath = pendingTarget,
                        command = "rna approve|deny --workspace <path> --session <id> --pending <id>" });
                    return 3;
                }
                if (session.LastRun?.Status == "interrupted")
                {
                    var run = session.LastRun;
                    Output(jsonl, "run.interrupted", new { session.Id, run.RunId,
                        toolId = run.InterruptedToolId, path = run.InterruptedFilePath,
                        targetPath = run.InterruptedTargetPath,
                        possibleEffect = run.InterruptedEffectPossible,
                        acceptance = run.WorkspaceAcceptance?.State.ToString().ToLowerInvariant(),
                        reason = run.CurrentAction,
                        command = "rna run --workspace <path> --session <id> --message <task>" });
                    return 3;
                }
                Output(jsonl, "run.needs_input", new { session.Id,
                    reason = "Explicit new input is required; possible file effects are never replayed automatically.",
                    command = "rna run --workspace <path> --session <id> --message <task>" });
                return 3;
            }
            if (args[0] == "approve" || args[0] == "deny")
            {
                var sessionId = Required(options, "session");
                var pendingId = Required(options, "pending");
                var session = service.GetSession(workspace, sessionId);
                if (session == null) throw new ArgumentException("Session not found.");
                var approve = args[0] == "approve";
                AppSettings continuationSettings = null;
                string continuationKey = null;
                string continuationDigest = null;
                if (approve)
                {
                    continuationSettings = Settings(options, session.Model,
                        session.LastRun?.ModelConfiguration?.AgentResponseMode);
                    if (!EndpointReady(continuationSettings, out continuationKey)) return 4;
                    if (!ModelReady(continuationSettings, out continuationDigest)) return 4;
                    var requestedThinking = OptionalThinking(options);
                    if (requestedThinking.HasValue && session.LastRun.ModelConfiguration != null &&
                        requestedThinking.Value != session.LastRun.ModelConfiguration.ReasoningEnabled)
                        throw new ArgumentException("Approval must keep the accepted run's thinking setting.");
                }
                var result = service.ResolvePendingAsync(workspace, sessionId, pendingId, approve,
                    continuationSettings, () => continuationKey, modelDigest: continuationDigest).GetAwaiter().GetResult();
                return ReportRun(jsonl, result);
            }
            if (args[0] != "run") throw new ArgumentException("Unknown command. Use --help.");
            if (options.ContainsKey("profile") && options["profile"] != "development")
                throw new ArgumentException("Only the development profile is available in the CLI.");
            var message = options.ContainsKey("message") ? options["message"] : null;
            if (options.ContainsKey("task-file"))
            {
                if (message != null) throw new ArgumentException("Choose --message or --task-file.");
                message = File.ReadAllText(Path.GetFullPath(options["task-file"]));
            }
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("--message or --task-file is required.");
            var expectedFiles = options.ContainsKey("expect-files")
                ? options["expect-files"].Split(',').Select(path => path.Trim()).ToArray()
                : new string[0];
            if (expectedFiles.Length > 32 || expectedFiles.Any(string.IsNullOrWhiteSpace) ||
                expectedFiles.Distinct(StringComparer.Ordinal).Count() != expectedFiles.Length)
                throw new ArgumentException("--expect-files requires 1–32 distinct relative paths.");
            var minReads = NonnegativeOption(options, "min-reads");
            var minWrites = NonnegativeOption(options, "min-writes");
            var acceptance = new WorkspaceRunAcceptance
            { ExpectedFiles = expectedFiles.ToList(), MinimumVerifiedReads = minReads, MinimumVerifiedWrites = minWrites,
                RequireWebVerification = options.ContainsKey("require-web-verify") };
            if (acceptance.RequireWebVerification && WorkspaceWebVerifier.FindBrowserExecutable() == null)
            {
                Console.Error.WriteLine("Browser verification requires an available Chromium executable (RNA_BROWSER_EXECUTABLE).");
                return 4;
            }
            var settings = Settings(options, Environment.GetEnvironmentVariable("RNA_MODEL"));
            string key;
            if (!EndpointReady(settings, out key)) return 4;
            var thinking = OptionalThinking(options);
            string modelDigest;
            if (!ModelReady(settings, out modelDigest)) return 4;
            using (var cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler interrupt = (sender, eventArgs) =>
                { eventArgs.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += interrupt;
                try
                {
                    var result = service.RunAsync(workspace, Value(options, "session", null), message,
                        settings, () => key, update =>
                        {
                            if (update.Kind == "ToolStarted" || update.Kind == "ToolCompleted")
                                Output(jsonl, update.Kind == "ToolStarted" ? "tool.started" : "tool.completed",
                                    new { update.Summary.RunId, update.ToolId, lifecycle = update.Summary.Lifecycle.ToString(),
                                        health = update.Summary.ExecutionHealth.ToString() });
                        }, cancellation.Token, acceptance, thinking, modelDigest).GetAwaiter().GetResult();
                    return ReportRun(jsonl, result);
                }
                finally { Console.CancelKeyPress -= interrupt; }
            }
        }

        private static AppSettings Settings(Dictionary<string, string> options, string defaultModel,
            string defaultResponseMode = null)
        {
            var responseMode = Value(options, "response-mode",
                Environment.GetEnvironmentVariable("RNA_RESPONSE_MODE") ??
                defaultResponseMode ?? AgentResponseModes.JsonSchema);
            var normalizedResponseMode = AgentResponseModes.Normalize(responseMode);
            if (!string.Equals(responseMode, normalizedResponseMode, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--response-mode/RNA_RESPONSE_MODE must be json_schema or json_object.");
            var reasoningMode = Value(options, "reasoning-mode",
                Environment.GetEnvironmentVariable("RNA_REASONING_MODE") ?? ReasoningRequestModes.ReasoningEffort);
            var normalizedMode = ReasoningRequestModes.Normalize(reasoningMode);
            if (!string.Equals(reasoningMode, normalizedMode, StringComparison.OrdinalIgnoreCase) ||
                normalizedMode == ReasoningRequestModes.CustomJson)
                throw new ArgumentException("--reasoning-mode must be auto, reasoning_effort, enable_thinking, chat_template_kwargs or reasoning_enabled.");
            return new AppSettings
            {
                BaseUrl = Value(options, "base-url", Environment.GetEnvironmentVariable("RNA_BASE_URL")),
                Model = Value(options, "model", defaultModel),
                AgentResponseMode = normalizedResponseMode,
                ReasoningRequestMode = normalizedMode,
                ContextWindowOverrideTokens = ContextTokens(options),
                MaxTokens = 4096,
                MaxAgentIterations = PositiveBoundedOption(options, "max-iterations",
                    AppSettings.DefaultMaxAgentIterations),
                MaxAgentToolSteps = PositiveBoundedOption(options, "max-tool-steps",
                    AppSettings.DefaultMaxAgentToolSteps)
            };
        }

        private static int ContextTokens(Dictionary<string, string> options)
        {
            var raw = Value(options, "context-tokens", Environment.GetEnvironmentVariable("RNA_CONTEXT_TOKENS"));
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            int value;
            if (!int.TryParse(raw, out value) || value < 4096 || value > 262144)
                throw new ArgumentException("--context-tokens/RNA_CONTEXT_TOKENS must be 4096–262144.");
            return value;
        }

        private static bool? OptionalThinking(Dictionary<string, string> options)
        {
            var raw = Value(options, "thinking", Environment.GetEnvironmentVariable("RNA_THINKING"));
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw == "on" || raw == "true") return true;
            if (raw == "off" || raw == "false") return false;
            throw new ArgumentException("--thinking/RNA_THINKING must be on or off.");
        }

        private static bool ModelReady(AppSettings settings, out string digest)
        {
            digest = null;
            Uri endpoint;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out endpoint) ||
                !endpoint.IsLoopback || endpoint.Port != 11434)
            {
                digest = Environment.GetEnvironmentVariable("RNA_MODEL_DIGEST")?.Trim();
                return true;
            }
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    var tags = JObject.Parse(client.GetStringAsync(
                        new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/api/tags"))
                        .GetAwaiter().GetResult());
                    foreach (var item in tags["models"] as JArray ?? new JArray())
                    {
                        var name = (string)item["name"];
                        if (name == settings.Model || name == settings.Model + ":latest")
                        {
                            digest = (string)item["digest"];
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(digest))
                    {
                        using (var body = new StringContent(
                            JsonConvert.SerializeObject(new { model = settings.Model }), Encoding.UTF8, "application/json"))
                        using (var response = client.PostAsync(
                            new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/api/show"), body)
                            .GetAwaiter().GetResult())
                        {
                            response.EnsureSuccessStatusCode();
                            var show = JObject.Parse(response.Content.ReadAsStringAsync()
                                .GetAwaiter().GetResult());
                            var actualContext = OllamaContextTokens((string)show["parameters"]);
                            if (actualContext > 0 &&
                                ModelContextBudget.ContextWindowTokens(settings) > actualContext)
                            {
                                Console.Error.WriteLine("RNAssistant context exceeds the Ollama profile's num_ctx: " +
                                    ModelContextBudget.ContextWindowTokens(settings) + " > " + actualContext);
                                return false;
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Ollama endpoint/model check failed: " + ex.Message);
                return false;
            }
            Console.Error.WriteLine("Ollama model is unavailable or has no digest: " + settings.Model);
            return false;
        }

        private static int OllamaContextTokens(string parameters)
        {
            foreach (var line in (parameters ?? string.Empty).Split('\n'))
            {
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                int value;
                if (parts.Length == 2 && parts[0] == "num_ctx" && int.TryParse(parts[1], out value))
                    return value;
            }
            return 0;
        }

        private static bool EndpointReady(AppSettings settings, out string key)
        {
            key = Environment.GetEnvironmentVariable("RNA_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
            {
                Console.Error.WriteLine("Set RNA_BASE_URL and RNA_MODEL, or pass --base-url and --model.");
                return false;
            }
            Uri endpoint;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
                throw new ArgumentException("The LLM endpoint must use HTTPS or loopback HTTP.");
            if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("Set RNA_API_KEY or OPENAI_API_KEY for the remote LLM endpoint.");
                return false;
            }
            return true;
        }

        private static int ReportRun(bool jsonl, WorkspaceRunResult result)
        {
            var checkedAcceptance = result.Acceptance;
            var summary = result.Summary;
            var pending = summary.PendingConfirmation;
            var acceptancePassed = checkedAcceptance.State == WorkspaceAcceptanceState.NotRequested ||
                checkedAcceptance.State == WorkspaceAcceptanceState.Passed;
            Output(jsonl, pending == null ? "run.completed" : "run.awaiting_confirmation",
                new { result.SessionId, summary.RunId,
                    lifecycle = summary.Lifecycle.ToString(), reason = summary.Reason,
                    action = summary.Reason == "model_done" ? "done" :
                        summary.Reason == "model_blocked" ? "blocked" :
                        summary.Reason == "model_needs_input" ? "needs_input" : "other",
                    health = summary.ExecutionHealth.ToString(), summary.AssistantMessage, summary.ToolCounts,
                    pendingId = pending?.PendingId, pendingTool = pending?.Call.Name,
                    pendingPath = pending == null ? null : PendingPath(pending.Call.Name, pending.Call.ArgumentsJson),
                    pendingTargetPath = pending == null ? null : PendingTarget(pending.Call.Name, pending.Call.ArgumentsJson),
                    acceptance = checkedAcceptance.State.ToString().ToLowerInvariant(),
                    expectedFiles = checkedAcceptance.ExpectedFiles,
                    missingFiles = checkedAcceptance.MissingFiles,
                    minReads = checkedAcceptance.MinimumVerifiedReads,
                    minWrites = checkedAcceptance.MinimumVerifiedWrites,
                    requireWebVerification = checkedAcceptance.RequireWebVerification,
                    completeFileReads = checkedAcceptance.AcceptedCompleteFileReads,
                    verifiedFileChanges = checkedAcceptance.VerifiedFileChanges,
                    webSnapshotVerified = checkedAcceptance.VerifiedWebSnapshot,
                    acceptanceError = checkedAcceptance.Error });
            if (pending != null) return 3;
            if (!acceptancePassed) return 5;
            return summary.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.Failed ? 5 :
                summary.Reason == "model_needs_input" ? 3 : 0;
        }

        private static string PendingPath(string toolId, string argumentsJson)
        {
            if (toolId != "files.delete" && toolId != "files.move") return null;
            try { return (string)JObject.Parse(argumentsJson)["relativePath"]; }
            catch (JsonException) { return null; }
        }

        private static string PendingTarget(string toolId, string argumentsJson)
        {
            if (toolId != "files.move") return null;
            try { return (string)JObject.Parse(argumentsJson)["targetPath"]; }
            catch (JsonException) { return null; }
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument: " + args[i]);
                var key = args[i].Substring(2);
                if (key == "jsonl" || key == "read-only" || key == "create" ||
                    key == "require-web-verify") result[key] = "true";
                else
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Missing value for --" + key);
                    result[key] = args[++i];
                }
            }
            return result;
        }
        private static string Required(Dictionary<string, string> options, string key)
        {
            string value;
            if (!options.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("--" + key + " is required.");
            return value;
        }
        private static string Value(Dictionary<string, string> options, string key, string fallback)
        { string value; return options.TryGetValue(key, out value) ? value : fallback; }
        private static int NonnegativeOption(Dictionary<string, string> options, string key)
        {
            string raw;
            if (!options.TryGetValue(key, out raw)) return 0;
            int value;
            if (!int.TryParse(raw, out value) || value < 0)
                throw new ArgumentException("--" + key + " requires a nonnegative integer.");
            return value;
        }
        private static int PositiveBoundedOption(Dictionary<string, string> options, string key, int maximum)
        {
            string raw;
            if (!options.TryGetValue(key, out raw)) return maximum;
            int value;
            if (!int.TryParse(raw, out value) || value < 1 || value > maximum)
                throw new ArgumentException("--" + key + " requires an integer from 1 to " + maximum + ".");
            return value;
        }
        private static void Output(bool jsonl, string type, object payload)
        {
            if (jsonl) Console.WriteLine(JsonConvert.SerializeObject(new { type, data = payload }));
            else Console.WriteLine(type + ": " + JsonConvert.SerializeObject(payload));
        }
        private static void Help()
        {
            Console.WriteLine("RNAssistant workspace CLI (development profile)");
            Console.WriteLine("rna workspace open <path> [--read-only]");
            Console.WriteLine("rna env --workspace <path>");
            Console.WriteLine("rna sessions --workspace <path> [--jsonl]");
            Console.WriteLine("rna run --workspace <path> (--message <text> | --task-file <file>) [--session <id>] [--profile development] [--model <name>] [--base-url <url>] [--context-tokens <n>] [--response-mode json_schema|json_object] [--reasoning-mode <mode>] [--thinking on|off] [--max-iterations <1..256>] [--max-tool-steps <1..4096>] [--expect-files <comma-separated paths>] [--min-reads <n>] [--min-writes <n>] [--require-web-verify] [--jsonl]");
            Console.WriteLine("rna inspect --workspace <path> --session <id> [--jsonl]");
            Console.WriteLine("rna recover --workspace <path> --path <relative-path> [--jsonl]  (inspect an uncertain file effect)");
            Console.WriteLine("rna verify --workspace <path> [--entry index.html] [--jsonl]  (isolated static web smoke)");
            Console.WriteLine("rna resume --workspace <path> --session <id> [--jsonl]  (close an abandoned run, show pending action or request new input; never replay tools)");
            Console.WriteLine("rna approve --workspace <path> --session <id> --pending <id> [--base-url <url>] [--model <name>] [--context-tokens <n>] [--response-mode json_schema|json_object] [--reasoning-mode <mode>] [--jsonl]");
            Console.WriteLine("rna deny --workspace <path> --session <id> --pending <id> [--jsonl]");
            Console.WriteLine("LLM settings: RNA_BASE_URL, RNA_MODEL, RNA_CONTEXT_TOKENS, RNA_RESPONSE_MODE, RNA_REASONING_MODE, RNA_THINKING, RNA_MODEL_DIGEST, RNA_API_KEY or OPENAI_API_KEY. Keys are never command arguments.");
            Console.WriteLine("Optional state root: RNA_STATE_ROOT (default: user application data).");
            Console.WriteLine("Exit: 0 command completed, 2 arguments, 3 input needed, 4 dependency, 5 failure, 130 cancelled.");
        }
    }
}
