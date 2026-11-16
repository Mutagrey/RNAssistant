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
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Application
{
    // Narrow agent bridge to the existing Workspace HTML definition owner.
    // It creates an absent definition or confirms an identical one; it never
    // rewrites a different definition and never opens Browser itself.
    internal sealed class WorkspaceHtmlRegistrationTool : IPreparableToolHandler, IManagedMutationToolHandler
    {
        internal const string ToolId = "html.project.register";
        internal const string SchemaJson =
            "{\"type\":\"object\",\"properties\":{\"entryPath\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024},\"members\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":512,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024}}},\"required\":[\"entryPath\",\"members\"],\"additionalProperties\":false}";
        private readonly WorkspaceDescriptor _workspace;
        private readonly string _chatId;
        private readonly WorkspaceHtmlVersionService _html;
        private readonly Func<string, bool> _runOwns;

        internal WorkspaceHtmlRegistrationTool(WorkspaceDescriptor workspace, string chatId,
            WorkspaceHtmlVersionService html, Func<string, bool> runOwns)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _chatId = chatId;
            _html = html ?? throw new ArgumentNullException(nameof(html));
            _runOwns = runOwns ?? throw new ArgumentNullException(nameof(runOwns));
        }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                RequireRun(context.Execution.RunId);
                var intent = RegistrationIntent.Read(context.Arguments);
                _html.ValidateDefinition(RequestForValidation(intent), token);
                var current = _html.GetDefinition(RequestForRead());
                if (current?.Definition != null && !Same(current.Definition, intent))
                    return Task.FromResult(Failure("project_definition_conflict",
                        "The Workspace already declares a different HTML project. Do not overwrite it from this tool; update the existing definition through its owner.",
                        ConflictData(current.Definition)));
                var prepared = new PreparedRegistration {
                    Version = 1, WorkspaceId = _workspace.WorkspaceId, ChatId = _chatId,
                    RunId = context.Execution.RunId, EntryPath = intent.EntryPath,
                    Members = intent.Members, ExpectedFingerprint = current?.Fingerprint,
                    NoChange = current?.Definition != null
                };
                return Task.FromResult(new ToolPreparationResult(
                    RuntimeResult.Ok(current?.Definition != null
                        ? "The identical HTML project definition is already registered."
                        : "HTML project registration prepared."),
                    JsonConvert.SerializeObject(prepared)));
            }
            catch (Exception error) when (IsOwnerFailure(error))
            {
                return Task.FromResult(Failure(Code(error), error.Message, null));
            }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            PreparedRegistration prepared = null;
            try
            {
                token.ThrowIfCancellationRequested();
                prepared = PreparedRegistration.Read(context.PreparedStateJson);
                if (prepared.WorkspaceId != _workspace.WorkspaceId || prepared.ChatId != _chatId ||
                    prepared.RunId != context.Execution.RunId)
                    throw new InvalidOperationException("The prepared HTML registration belongs to another Workspace, chat or run.");
                RequireRun(context.Execution.RunId);
                if (prepared.NoChange)
                {
                    var current = _html.GetDefinition(RequestForRead());
                    if (current?.Definition == null || !Same(current.Definition, prepared))
                        return Task.FromResult(Stale());
                    return Task.FromResult(Completed(prepared, false));
                }
                var state = _html.SetDefinition(RequestForWrite(prepared),
                    new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = _chatId, RunId = context.Execution.RunId },
                    context.MarkDispatchPossible, token);
                var readBack = _html.GetDefinition(RequestForRead());
                if (readBack?.Definition == null || !Same(readBack.Definition, prepared))
                    return Task.FromResult(Unknown("HTML project registration was written but read-back did not confirm the declared entry and members."));
                return Task.FromResult(Completed(prepared, state.Fingerprint != prepared.ExpectedFingerprint));
            }
            catch (Exception error) when (IsOwnerFailure(error))
            {
                if (context.MayHaveDispatched)
                    return Task.FromResult(Unknown(error.Message));
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(error.Message,
                    JsonConvert.SerializeObject(new RegistrationError { Code = Code(error) })),
                    ToolEffectEvidence.None,
                    recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
            }
        }

        private void RequireRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(_chatId) || !_runOwns(runId))
                throw new InvalidOperationException("This operation requires the exact current Workspace run.");
            _html.RequireDefinitionRun(Owner(), runId);
        }

        private HtmlProjectDefinitionRequest RequestForRead()
        {
            return new HtmlProjectDefinitionRequest { OwnerScope = Owner(), WorkspaceId = _workspace.WorkspaceId,
                ExpectedFingerprint = null };
        }

        private HtmlProjectDefinitionRequest RequestForWrite(PreparedRegistration prepared)
        {
            return new HtmlProjectDefinitionRequest { OwnerScope = Owner(), WorkspaceId = _workspace.WorkspaceId,
                ExpectedFingerprint = prepared.ExpectedFingerprint,
                Definition = new HtmlProjectDefinition { EntryPath = prepared.EntryPath, Members = prepared.Members } };
        }

        private HtmlProjectDefinitionRequest RequestForValidation(RegistrationIntent intent)
        {
            return new HtmlProjectDefinitionRequest { OwnerScope = Owner(), WorkspaceId = _workspace.WorkspaceId,
                Definition = new HtmlProjectDefinition { EntryPath = intent.EntryPath, Members = intent.Members } };
        }

        private BrowserOwnerScope Owner()
        {
            return new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = _workspace.WorkspaceId, ChatId = _chatId };
        }

        private static bool Same(HtmlProjectDefinition defined, RegistrationIntent intent)
        {
            if (defined == null || intent == null || defined.EntryPath != intent.EntryPath) return false;
            var members = defined.Members == null ? new string[0] : defined.Members.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            return members.Length == intent.Members.Length && members.SequenceEqual(intent.Members, StringComparer.Ordinal);
        }

        private static bool Same(HtmlProjectDefinition defined, PreparedRegistration prepared)
        {
            if (defined == null || prepared == null || defined.EntryPath != prepared.EntryPath) return false;
            var members = defined.Members == null ? new string[0] : defined.Members.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            return members.Length == prepared.Members.Length && members.SequenceEqual(prepared.Members, StringComparer.Ordinal);
        }

        private static ToolHandlerResult Completed(PreparedRegistration prepared, bool changed)
        {
            return new ToolHandlerResult(RuntimeResult.Ok(changed
                ? "HTML project registered. Discover the exact Browser source in this run before opening it."
                : "The identical HTML project definition is already registered; no write was needed.",
                ResultJson(prepared.EntryPath, prepared.Members, changed)),
                changed ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange);
        }

        private static ToolHandlerResult Unknown(string message)
        {
            return new ToolHandlerResult(RuntimeResult.Unknown(message,
                JsonConvert.SerializeObject(new RegistrationError { Code = "project_definition_unknown" })),
                ToolEffectEvidence.Unknown);
        }

        private static ToolHandlerResult Stale()
        {
            return new ToolHandlerResult(RuntimeResult.Error(
                "The HTML project definition changed after preparation. Read its current entry and members before retrying.",
                JsonConvert.SerializeObject(new RegistrationError { Code = "project_definition_changed" })),
                ToolEffectEvidence.None,
                recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
        }

        private static ToolPreparationResult Failure(string code, string message, JObject data)
        {
            return new ToolPreparationResult(RuntimeResult.Error(message,
                data == null ? JsonConvert.SerializeObject(new RegistrationError { Code = code }) : data.ToString(Formatting.None)),
                recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
        }

        private static JObject ConflictData(HtmlProjectDefinition definition)
        {
            return new JObject {
                ["code"] = "project_definition_conflict",
                ["entryPath"] = definition.EntryPath,
                ["members"] = new JArray((definition.Members ?? new string[0]).Take(64))
            };
        }

        private static string ResultJson(string entryPath, string[] members, bool changed)
        {
            return new JObject {
                ["status"] = "registered", ["changed"] = changed,
                ["entryPath"] = entryPath, ["members"] = new JArray(members),
                ["nextAction"] = new JObject {
                    ["tool"] = "core.find",
                    ["arguments"] = new JObject { ["type"] = "browser", ["query"] = entryPath }
                }
            }.ToString(Formatting.None);
        }

        private static string Code(Exception error)
        {
            var workspace = error as WorkspaceFileException;
            return workspace == null ? "project_definition_invalid" : workspace.Code;
        }

        private static bool IsOwnerFailure(Exception error)
        {
            return error is WorkspaceFileException || error is IOException ||
                error is InvalidOperationException || error is ArgumentException;
        }

        private sealed class RegistrationIntent
        {
            internal string EntryPath { get; private set; }
            internal string[] Members { get; private set; }

            internal static RegistrationIntent Read(IDictionary<string, object> arguments)
            {
                object rawEntry, rawMembers;
                if (arguments == null || !arguments.TryGetValue("entryPath", out rawEntry) ||
                    !arguments.TryGetValue("members", out rawMembers))
                    throw new ArgumentException("entryPath and members are required.");
                var entry = Convert.ToString(rawEntry);
                JArray array;
                try { array = rawMembers as JArray ?? JArray.FromObject(rawMembers); }
                catch (Exception) { throw new ArgumentException("members must be an array of workspace-relative paths."); }
                var members = array.Select(item => Convert.ToString(item)).ToArray();
                if (string.IsNullOrWhiteSpace(entry) || entry.Length > 1024 || members.Length == 0 || members.Length > 512 ||
                    members.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 1024 || item.Contains("\\")) ||
                    !members.Contains(entry, StringComparer.Ordinal) ||
                    members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
                    throw new ArgumentException("Declare a canonical HTML entry and 1-512 unique members from this Workspace.");
                return new RegistrationIntent { EntryPath = entry,
                    Members = members.OrderBy(item => item, StringComparer.Ordinal).ToArray() };
            }
        }

        private sealed class PreparedRegistration
        {
            public int Version { get; set; }
            public string WorkspaceId { get; set; }
            public string ChatId { get; set; }
            public string RunId { get; set; }
            public string EntryPath { get; set; }
            public string[] Members { get; set; }
            public string ExpectedFingerprint { get; set; }
            public bool NoChange { get; set; }

            internal static PreparedRegistration Read(string json)
            {
                var value = JsonConvert.DeserializeObject<PreparedRegistration>(json ?? "null");
                if (value?.Version != 1 || string.IsNullOrWhiteSpace(value.WorkspaceId) ||
                    string.IsNullOrWhiteSpace(value.ChatId) || string.IsNullOrWhiteSpace(value.RunId) ||
                    string.IsNullOrWhiteSpace(value.EntryPath) || value.Members == null || value.Members.Length == 0)
                    throw new InvalidOperationException("Missing or incompatible HTML registration preparation.");
                return value;
            }
        }

        private sealed class RegistrationError
        {
            [JsonProperty("code")] public string Code { get; set; }
        }
    }
}
