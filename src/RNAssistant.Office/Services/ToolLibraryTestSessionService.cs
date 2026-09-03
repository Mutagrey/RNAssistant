using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    // One bounded, in-memory continuation for the Library's semantic reference
    // reader. It never mutates or persists the active chat and never exposes an
    // opaque cursor to the WebView.
    internal sealed class ToolLibraryTestSessionService
    {
        private const int MaximumChunks = 128;
        private readonly object _sync = new object();
        private Continuation _continuation;

        internal ToolRunResult Execute(
            ChatSession source,
            ToolInvocation command,
            Func<ChatSession, ToolRunResult> execute)
        {
            if (command == null || execute == null)
                throw new ArgumentNullException(command == null
                    ? nameof(command) : nameof(execute));
            if (!string.Equals(command.ToolId,
                    CapabilityToolCatalog.ReadToolId,
                    StringComparison.Ordinal))
            {
                return execute(CreateIsolated(source));
            }

            lock (_sync)
            {
                var request = Request.TryCreate(command.Arguments);
                if (request == null)
                {
                    _continuation = null;
                    return execute(CreateIsolated(source));
                }

                ChatSession session;
                if (request.Next)
                {
                    if (_continuation == null ||
                        _continuation.Chunks >= MaximumChunks ||
                        !string.Equals(_continuation.SourceIdentity,
                            SourceIdentity(source), StringComparison.Ordinal) ||
                        !string.Equals(_continuation.CapabilityId,
                            request.CapabilityId, StringComparison.Ordinal) ||
                        !string.Equals(_continuation.ReferencePath,
                            request.ReferencePath, StringComparison.Ordinal))
                    {
                        _continuation = null;
                        return ToolRunResult.Error(
                            "No matching incomplete Library read exists. Start this exact skill reference with action=read.",
                            null, "capability_continuation_missing", false);
                    }
                    session = _continuation.Session;
                }
                else
                {
                    session = CreateIsolated(source);
                    _continuation = new Continuation
                    {
                        SourceIdentity = SourceIdentity(source),
                        CapabilityId = request.CapabilityId,
                        ReferencePath = request.ReferencePath,
                        Session = session
                    };
                }

                ToolRunResult result;
                try
                {
                    result = execute(session);
                }
                catch
                {
                    _continuation = null;
                    throw;
                }
                JObject data;
                if (!TryIncompleteReference(result, request, out data))
                {
                    _continuation = null;
                    return result;
                }
                AppendResult(session, command.ToolId, result);
                _continuation.Chunks += 1;
                return result;
            }
        }

        private static bool TryIncompleteReference(ToolRunResult result,
            Request request, out JObject data)
        {
            data = null;
            if (result == null || !string.Equals(result.Status, "ok",
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(result.DataJson)) return false;
            try
            {
                data = JObject.Parse(result.DataJson);
            }
            catch (JsonException)
            {
                return false;
            }
            return string.Equals((string)data["kind"], "reference",
                       StringComparison.Ordinal) &&
                string.Equals((string)data["id"], request.CapabilityId,
                    StringComparison.Ordinal) &&
                string.Equals((string)data["path"], request.ReferencePath,
                    StringComparison.Ordinal) &&
                (bool?)data["hasMore"] == true &&
                (bool?)data["complete"] != true;
        }

        private static void AppendResult(ChatSession session, string toolId,
            ToolRunResult result)
        {
            if (session == null) return;
            if (session.Messages == null)
                session.Messages = new List<ChatMessage>();
            var callId = "manual_" + Guid.NewGuid().ToString("N");
            var terminal = TerminalResult.Ok(result.Message,
                result.DataJson, result.ModelResourceRefs ??
                    new ResourceRef[0]);
            session.Messages.Add(new ChatMessage
            {
                Role = ToolResultRoles.User,
                ToolCallId = callId,
                ToolName = toolId,
                ToolResultRole = ToolResultRoles.User,
                ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                Content = "TOOL_RESULT:" + ToolResultWire.Write(
                    callId, toolId, terminal),
                ProtocolMessage = true
            });
        }

        private static ChatSession CreateIsolated(ChatSession source)
        {
            var session = ChatCloneService.CloneSessionSnapshot(source);
            if (session == null)
                session = new ChatSession();
            session.Id = "manual_" + Guid.NewGuid().ToString("N");
            return session;
        }

        private static string SourceIdentity(ChatSession source)
        {
            return string.Join("\n", new[]
            {
                source == null ? string.Empty : source.Id ?? string.Empty,
                source == null ? string.Empty : source.Host ?? string.Empty,
                source == null ? string.Empty : source.DocumentKey ?? string.Empty,
                source == null || source.LastRun == null
                    ? string.Empty
                    : source.LastRun.DocumentRuntimeKey ?? string.Empty
            });
        }

        private sealed class Continuation
        {
            public string SourceIdentity { get; set; }
            public string CapabilityId { get; set; }
            public string ReferencePath { get; set; }
            public ChatSession Session { get; set; }
            public int Chunks { get; set; }
        }

        private sealed class Request
        {
            public string CapabilityId { get; private set; }
            public string ReferencePath { get; private set; }
            public bool Next { get; private set; }

            public static Request TryCreate(
                IDictionary<string, object> arguments)
            {
                if (arguments == null ||
                    !arguments.ContainsKey("referencePath")) return null;
                var id = Value(arguments, "id").Trim();
                var path = Value(arguments, "referencePath").Trim();
                var action = Value(arguments, "action").Trim();
                if (id.Length == 0 || path.Length == 0 ||
                    action.Length > 0 && action != "read" && action != "next")
                    return null;
                return new Request
                {
                    CapabilityId = id,
                    ReferencePath = path,
                    Next = action == "next"
                };
            }

            private static string Value(
                IDictionary<string, object> arguments, string name)
            {
                object value;
                return arguments.TryGetValue(name, out value) && value != null
                    ? Convert.ToString(value,
                        System.Globalization.CultureInfo.InvariantCulture) ??
                      string.Empty
                    : string.Empty;
            }
        }
    }
}
