using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SessionEventLogIsCanonical()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "event-doc", "Event.docx", "Event chat");
                session.Messages.Add(new ChatMessage { Role = "user", Content = "hello event log" });
                store.Save(session);

                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(2, events.Count, "created and commit events");
                AssertEqual(SessionEventTypes.SessionCreated, events[0].Type, "first event type");
                AssertEqual(SessionEventTypes.SessionCommit, events[1].Type, "commit event type");
                AssertTrue(events[1].Data["Operations"].Any(operation =>
                    string.Equals((string)operation["Type"], SessionOperationTypes.UserMessageAppended, StringComparison.Ordinal)),
                    "user message has a semantic operation");
                AssertEqual(events.Last().Sequence, session.Revision, "revision is stream sequence");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual("hello event log", loaded.Messages.Single().Content, "projection replay");
                AssertEqual(0, Directory.GetFiles(SessionDirectory(paths, session), "*.json").Length,
                    "mutable session snapshot is absent");
                AssertEqual(1, Directory.GetFiles(SessionDirectory(paths, session), "*.events.jsonl").Length,
                    "one canonical event stream");
            });
        }

        private static void SessionEventIntegrityRejectsTampering()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "integrity-doc", "Integrity.xlsx", "Original");
                var path = SessionEventFile(paths, session);
                var lines = File.ReadAllLines(path);
                var first = JObject.Parse(lines[0]);
                first["Data"]["Title"] = "Tampered";
                lines[0] = first.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllLines(path, lines);

                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "hash mismatch rejects projection");
                AssertEqual(0, store.List(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "corrupt stream is excluded from listing");
            });
        }

        private static void SessionForkLineageIsCanonical()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var parent = store.Create("Word", "fork-doc", "Fork.docx", "Parent");
                parent.Messages.Add(new ChatMessage { Role = "user", Content = "fork boundary" });
                store.Save(parent);
                var fork = store.CreateTransient(parent.Host, parent.DocumentKey, parent.DocumentTitle, "Fork");
                fork.ParentSessionId = parent.Id;
                fork.ParentSessionRevision = parent.Revision;
                fork.ForkedThroughMessageId = parent.Messages.Single().Id;
                store.Save(fork);

                var first = store.ReadEvents(fork.Host, fork.DocumentKey, fork.Id).Single();
                AssertEqual(SessionEventTypes.SessionForked, first.Type, "fork uses a distinct seed event");
                var loaded = store.Load(fork.Id);
                AssertEqual(parent.Id, loaded.ParentSessionId, "parent id replays");
                AssertEqual(parent.Revision, loaded.ParentSessionRevision.Value, "parent revision replays");
                AssertEqual(parent.Messages.Single().Id, loaded.ForkedThroughMessageId, "fork message boundary replays");
            });
        }

        private static void DeletesDocumentEventLogs()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                store.Create("Excel", "delete-doc", "Delete.xlsx", "First");
                store.Create("Excel", "delete-doc", "Delete.xlsx", "Second");
                store.Create("Excel", "keep-doc", "Keep.xlsx", "Keep");

                AssertTrue(store.DeleteDocument("Excel", "delete-doc"), "document event directory deleted");
                AssertEqual(0, store.List("Excel", "delete-doc", "Delete.xlsx").Count, "deleted projections absent");
                AssertEqual(1, store.List("Excel", "keep-doc", "Keep.xlsx").Count, "other stream preserved");
            });
        }

        private static void ArtifactBodiesUseContentAddressing()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var first = store.Create("Word", "cas-1", "One.docx", "One");
                first.HtmlWorkspace.ActiveFileId = "index";
                first.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = "index", Path = "index.html", Kind = "html", Content = "unique-cas-body",
                    CreatedUtc = timestamp, UpdatedUtc = timestamp
                });
                store.Save(first);
                var firstArtifact = first.Artifacts.Single(item => item.Id == first.ActiveHtmlArtifactId);

                var second = store.Create("Word", "cas-2", "Two.docx", "Two");
                second.HtmlWorkspace.ActiveFileId = "index";
                second.HtmlWorkspace.Files.Add(new HtmlWorkspaceFile
                {
                    Id = "index", Path = "index.html", Kind = "html", Content = "unique-cas-body",
                    CreatedUtc = timestamp, UpdatedUtc = timestamp
                });
                store.Save(second);
                var secondArtifact = second.Artifacts.Single(item => item.Id == second.ActiveHtmlArtifactId);

                AssertEqual(firstArtifact.ContentSha256, secondArtifact.ContentSha256, "same content has same hash");
                AssertEqual(1, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                    "content is stored once");
                AssertTrue(!File.ReadAllText(SessionEventFile(paths, first)).Contains("unique-cas-body"),
                    "artifact body is not duplicated into event log");
                AssertEqual("unique-cas-body",
                    store.Load(first.Host, first.DocumentKey, first.Id).HtmlWorkspace.Files.Single().Content,
                    "active workspace projects from CAS artifact");
            });
        }

        private static void ModelTraceSharesSessionStream()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("PowerPoint", "trace-doc", "Trace.pptx", "Trace");
                var requestBody = "{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"exact\"}]}";
                var trace = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new { requestId = "request-1", purpose = "agent", model = "test" },
                    requestBody, "application/json", "run-1", "run-1", null);

                AssertEqual(2L, trace.Sequence, "trace advances canonical sequence");
                AssertEqual(requestBody, store.ReadEventPayload(trace), "exact request payload roundtrip");
                session.Title = "Saved after trace";
                store.Save(session);
                AssertEqual("Saved after trace", store.Load(session.Id).Title, "state commit follows trace revision");
                AssertEqual(SessionEventTypes.LlmRequest,
                    store.ReadEvents(session.Host, session.DocumentKey, session.Id)[1].Type,
                    "trace and state use same stream");
            });
        }

        private static async Task ModelRequestTracePrecedesDispatch()
        {
            var secret = "must-not-enter-trace";
            var client = new LlmClient(() => secret);
            LlmTraceRecord trace = null;
            var stopped = false;
            try
            {
                await client.CompleteAsync(
                    new AppSettings
                    {
                        BaseUrl = "https://example.invalid",
                        Model = "trace-model",
                        StreamResponses = false
                    },
                    new[] { new ChatMessage { Role = "user", Content = "materialized prompt" } },
                    new LlmRequestOptions
                    {
                        ResponseFormat = LlmResponseFormats.JsonObject,
                        TracePurpose = "harness",
                        TraceSink = record =>
                        {
                            trace = record;
                            throw new InvalidOperationException("stop-after-trace");
                        }
                    },
                    null);
            }
            catch (InvalidOperationException ex)
            {
                stopped = string.Equals(ex.Message, "stop-after-trace", StringComparison.Ordinal);
            }

            AssertTrue(stopped, "trace persistence failure aborts before HTTP dispatch");
            AssertTrue(trace != null, "final request trace emitted");
            AssertEqual("request", trace.Type, "request trace type");
            var payload = JObject.Parse(trace.PayloadJson);
            AssertEqual("trace-model", (string)payload["model"], "materialized model recorded");
            AssertEqual("materialized prompt", (string)payload.SelectToken("messages[0].content"),
                "materialized message recorded");
            AssertTrue(trace.PayloadJson.IndexOf(secret, StringComparison.Ordinal) < 0,
                "authorization secret is absent from request payload");
        }

        private static void IncompleteEventTailRecovers()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "tail-doc", "Tail.docx", "Before tail");
                var path = SessionEventFile(paths, session);
                File.AppendAllText(path, "{\"SchemaVersion\":1");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual("Before tail", loaded.Title, "valid prefix remains readable");
                loaded.Title = "After recovery";
                store.Save(loaded);
                AssertEqual("After recovery", store.Load(loaded.Id).Title, "next commit removes incomplete tail");
                AssertEqual(2, File.ReadAllLines(path).Length, "stream contains only valid records");
            });
        }

        private static void CorruptedArtifactBlobIsSafe()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "blob-doc", "Blob.docx", "Blob");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Plan,
                    MimeType = "application/json",
                    InlineText = "{\"goal\":\"safe\"}"
                };
                session.Artifacts.Add(artifact);
                session.ActivePlanArtifactId = artifact.Id;
                store.Save(session);
                var blob = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Single();
                File.WriteAllText(blob, "corrupt");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertTrue(loaded != null, "metadata projection remains available");
                AssertTrue(string.IsNullOrWhiteSpace(loaded.Artifacts.Single().InlineText),
                    "hash mismatch does not hydrate corrupted content");

                var repair = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Plan,
                    MimeType = "application/json",
                    InlineText = "{\"goal\":\"safe\"}"
                };
                loaded.Artifacts.Add(repair);
                loaded.ActivePlanArtifactId = repair.Id;
                store.Save(loaded);
                AssertEqual("{\"goal\":\"safe\"}", store.Load(loaded.Id).Artifacts
                    .Single(item => item.Id == repair.Id).InlineText, "known content repairs its corrupted CAS blob");
            });
        }

        private static void HtmlNavigationProjectsFromArtifacts()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "html-nav", "Navigation.docx", "Navigation");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "version one", true);
                store.Save(session);
                var firstId = session.ActiveHtmlArtifactId;
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "version two", true);
                store.Save(session);
                var secondId = session.ActiveHtmlArtifactId;

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual("version two", loaded.HtmlWorkspace.Files.Single().Content, "active revision projected");
                AssertEqual(firstId, loaded.HtmlWorkspace.History.Single().Id, "undo points to parent artifact");
                HtmlArtifactToolExecutor.RestoreSnapshot(loaded, firstId);
                AssertEqual(firstId, loaded.ActiveHtmlArtifactId, "undo activates prior artifact");
                AssertEqual(secondId, loaded.HtmlWorkspace.RedoHistory.Single().Id, "redo points to child artifact");
                store.Save(loaded);

                loaded = store.Load(loaded.Id);
                AssertEqual("version one", loaded.HtmlWorkspace.Files.Single().Content, "undo survives replay");
                HtmlArtifactToolExecutor.RedoSnapshot(loaded, secondId);
                AssertEqual("version two", loaded.HtmlWorkspace.Files.Single().Content, "redo activates child artifact");
                AssertEqual(2, loaded.Artifacts.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace),
                    "undo and redo do not duplicate revisions");
            });
        }

        private static void ChartActivityProjectsFromArtifact()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "chart-doc", "Chart.xlsx", "Chart");
                var message = new ChatMessage
                {
                    Role = "assistant",
                    Activity = new ChatActivity
                    {
                        Kind = "tool",
                        Title = "Chart",
                        Status = "completed",
                        DataJson = "{\"type\":\"rnassistant.chart\",\"title\":\"Sales\",\"rows\":[{\"value\":\"unique-chart-row\"}]}"
                    }
                };
                session.Messages.Add(message);
                store.Save(session);
                var first = session.Artifacts.Single(item => item.Kind == ChatArtifactKinds.Chart);
                AssertTrue(!File.ReadAllText(SessionEventFile(paths, session)).Contains("unique-chart-row"),
                    "chart body is absent from event records");

                var loaded = store.Load(session.Id);
                AssertContains(loaded.Messages.Single().Activity.DataJson, "unique-chart-row", "chart activity projection");
                loaded.Messages.Single().Activity.DataJson =
                    "{\"type\":\"rnassistant.chart\",\"title\":\"Sales\",\"rows\":[{\"value\":\"updated\"}]}";
                store.Save(loaded);
                var second = loaded.Artifacts.Single(item => item.Kind == ChatArtifactKinds.Chart && item.Id != first.Id);
                AssertEqual(first.Id, second.ParentArtifactId, "chart edit creates linked revision");
                AssertEqual(2, second.Revision, "chart revision increments");
            });
        }

        private static void CompactionProjectsFromArtifact()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "compact-doc", "Compact.docx", "Compact");
                var source = new ChatMessage { Role = "user", Content = "old context" };
                session.Messages.Add(source);
                var checkpoint = new ContextCheckpoint
                {
                    ThroughMessageId = source.Id,
                    SummaryJson = "{\"summary\":\"unique-compaction-summary\"}",
                    SummaryMarkdown = "unique-compaction-summary",
                    Model = "test",
                    SourceMessageCount = 1
                };
                var artifact = new ChatArtifact
                {
                    Id = checkpoint.Id,
                    Kind = ChatArtifactKinds.Compaction,
                    MimeType = "application/json",
                    InlineText = Newtonsoft.Json.JsonConvert.SerializeObject(checkpoint),
                    ModelContextPolicy = "checkpoint",
                    MetadataJson = "{\"sourceMessageCount\":1}"
                };
                var activityMessage = new ChatMessage
                {
                    Role = "assistant",
                    Content = checkpoint.SummaryMarkdown,
                    ExcludeFromModelContext = true,
                    Activity = new ChatActivity
                    {
                        Kind = "compaction",
                        Status = "completed",
                        ResultMessage = checkpoint.SummaryMarkdown,
                        DataJson = artifact.MetadataJson
                    },
                    ArtifactIds = new System.Collections.Generic.List<string> { artifact.Id }
                };
                artifact.SourceMessageId = activityMessage.Id;
                session.Artifacts.Add(artifact);
                session.Messages.Add(activityMessage);
                session.ContextCheckpoints.Add(checkpoint);
                session.ActiveContextCheckpointId = artifact.Id;
                store.Save(session);

                AssertTrue(!File.ReadAllText(SessionEventFile(paths, session)).Contains("unique-compaction-summary"),
                    "compaction body is externalized");
                var loaded = store.Load(session.Id);
                AssertEqual(artifact.Id, loaded.ContextCheckpoints.Single().Id, "checkpoint id is artifact revision id");
                AssertEqual("unique-compaction-summary", loaded.ContextCheckpoints.Single().SummaryMarkdown,
                    "checkpoint projection reads artifact body");
                AssertEqual("unique-compaction-summary", loaded.Messages.Single(item => item.Id == activityMessage.Id).Content,
                    "compaction message projects from artifact body");
            });
        }

        private static string SessionDirectory(AppDataPaths paths, ChatSession session)
        {
            return Path.Combine(paths.ChatDirectory,
                AppDataPaths.SafeFileName((session.Host ?? string.Empty) + "|" + (session.DocumentKey ?? string.Empty)));
        }

        private static string SessionEventFile(AppDataPaths paths, ChatSession session)
        {
            return Path.Combine(SessionDirectory(paths, session), AppDataPaths.SafeFileName(session.Id) + ".events.jsonl");
        }
    }
}
