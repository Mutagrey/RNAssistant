using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
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
            if (args[0] == "inspect")
            {
                var session = service.GetSession(workspace, Required(options, "session"));
                if (session == null) throw new ArgumentException("Session not found.");
                Output(jsonl, "session", new { session.Id, session.WorkspaceId, session.Title,
                    session.Model, session.Mode, session.Revision, session.LastRun?.Status,
                    acceptance = session.LastRun?.WorkspaceAcceptance,
                    messageCount = session.Messages?.Count ?? 0 });
                return 0;
            }
            if (args[0] == "resume")
            {
                var session = service.GetSession(workspace, Required(options, "session"));
                if (session == null) throw new ArgumentException("Session not found.");
                Output(jsonl, "run.needs_input", new { session.Id,
                    reason = "Explicit new input is required; possible file effects are never replayed automatically.",
                    command = "rna run --workspace <path> --session <id> --message <task>" });
                return 3;
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
            { ExpectedFiles = expectedFiles.ToList(), MinimumVerifiedReads = minReads, MinimumVerifiedWrites = minWrites };
            var settings = new AppSettings
            {
                BaseUrl = Value(options, "base-url", Environment.GetEnvironmentVariable("RNA_BASE_URL")),
                Model = Value(options, "model", Environment.GetEnvironmentVariable("RNA_MODEL")),
                AgentResponseMode = AgentResponseModes.JsonSchema,
                ReasoningRequestMode = ReasoningRequestModes.ReasoningEffort,
                MaxTokens = 4096
            };
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
            {
                Console.Error.WriteLine("Set RNA_BASE_URL and RNA_MODEL, or pass --base-url and --model.");
                return 4;
            }
            Uri endpoint;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
                throw new ArgumentException("The LLM endpoint must use HTTPS or loopback HTTP.");
            var key = Environment.GetEnvironmentVariable("RNA_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("Set RNA_API_KEY or OPENAI_API_KEY for the remote LLM endpoint.");
                return 4;
            }
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
                        }, cancellation.Token, acceptance).GetAwaiter().GetResult();
                    var checkedAcceptance = result.Acceptance;
                    var acceptancePassed = checkedAcceptance.State == WorkspaceAcceptanceState.NotRequested ||
                        checkedAcceptance.State == WorkspaceAcceptanceState.Passed;
                    Output(jsonl, "run.completed", new { result.SessionId, result.Summary.RunId,
                        lifecycle = result.Summary.Lifecycle.ToString(), reason = result.Summary.Reason,
                        action = result.Summary.Reason == "model_done" ? "done" :
                            result.Summary.Reason == "model_blocked" ? "blocked" :
                            result.Summary.Reason == "model_needs_input" ? "needs_input" : "other",
                        health = result.Summary.ExecutionHealth.ToString(),
                        result.Summary.AssistantMessage, result.Summary.ToolCounts,
                        acceptance = checkedAcceptance.State.ToString().ToLowerInvariant(),
                        expectedFiles = checkedAcceptance.ExpectedFiles,
                        missingFiles = checkedAcceptance.MissingFiles,
                        minReads = checkedAcceptance.MinimumVerifiedReads,
                        minWrites = checkedAcceptance.MinimumVerifiedWrites,
                        completeFileReads = checkedAcceptance.AcceptedCompleteFileReads,
                        verifiedFileChanges = checkedAcceptance.VerifiedFileChanges,
                        acceptanceError = checkedAcceptance.Error });
                    if (!acceptancePassed) return 5;
                    return result.Summary.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.Failed ? 5 :
                        result.Summary.Reason == "model_needs_input" ? 3 : 0;
                }
                finally { Console.CancelKeyPress -= interrupt; }
            }
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument: " + args[i]);
                var key = args[i].Substring(2);
                if (key == "jsonl" || key == "read-only" || key == "create") result[key] = "true";
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
            Console.WriteLine("rna run --workspace <path> (--message <text> | --task-file <file>) [--session <id>] [--profile development] [--model <name>] [--base-url <url>] [--expect-files <comma-separated paths>] [--min-reads <n>] [--min-writes <n>] [--jsonl]");
            Console.WriteLine("rna inspect --workspace <path> --session <id> [--jsonl]");
            Console.WriteLine("rna recover --workspace <path> --path <relative-path> [--jsonl]  (inspect an uncertain file effect)");
            Console.WriteLine("rna resume --workspace <path> --session <id> [--jsonl]  (reports explicit input required)");
            Console.WriteLine("LLM settings: RNA_BASE_URL, RNA_MODEL, RNA_API_KEY or OPENAI_API_KEY. Keys are never command arguments.");
            Console.WriteLine("Optional state root: RNA_STATE_ROOT (default: user application data).");
            Console.WriteLine("Exit: 0 command completed, 2 arguments, 3 input needed, 4 dependency, 5 failure, 130 cancelled.");
        }
    }
}
