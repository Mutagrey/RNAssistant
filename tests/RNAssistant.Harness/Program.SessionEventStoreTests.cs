using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JsonlByteOffsetsAreExact()
        {
            WithTempPaths(paths =>
            {
                var path = Path.Combine(paths.Root, "offset-reader.jsonl");
                var firstText = "{\"value\":\"" + new string('x', 8181) + "я\"}";
                var thirdText = "{\"value\":3}";
                var firstBytes = Encoding.UTF8.GetByteCount(firstText);
                var thirdOffset = firstBytes + 3L;
                var content = firstText + "\r\n\n" + thirdText;
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

                using (var reader = new JsonlByteReader(path))
                {
                    var first = reader.ReadLine();
                    AssertEqual(0L, first.Offset, "first record starts at byte zero");
                    AssertEqual(firstBytes + 2L, first.NextOffset, "CRLF counts as two bytes");
                    AssertTrue(first.Terminated, "CRLF record is terminated");
                    AssertEqual(firstText, first.Text, "UTF-8 split across buffers decodes exactly");

                    var blank = reader.ReadLine();
                    AssertEqual(first.NextOffset, blank.Offset, "blank record keeps its byte offset");
                    AssertEqual(thirdOffset, blank.NextOffset, "blank LF advances one byte");
                    AssertTrue(blank.Terminated, "blank LF record is terminated");

                    var third = reader.ReadLine();
                    AssertEqual(thirdOffset, third.Offset, "final record offset is exact");
                    AssertEqual(Encoding.UTF8.GetByteCount(content), third.NextOffset, "final byte position is exact");
                    AssertTrue(!third.Terminated, "unterminated final record is explicit");
                    AssertEqual(thirdText, third.Text, "unterminated content is preserved");
                    AssertTrue(reader.ReadLine() == null, "reader stops at the captured file length");
                }

                using (var reader = new JsonlByteReader(path, thirdOffset))
                {
                    var third = reader.ReadLine();
                    AssertEqual(thirdText, third.Text, "reader seeks directly to a known record offset");
                    AssertEqual(thirdOffset, third.Offset, "seek preserves absolute offsets");
                }
            });
        }

        private static void BoundedCacheHonorsLruAndWeights()
        {
            var cache = new BoundedLruCache<string>(
                2,
                3,
                4,
                value => value == null ? 0 : value.Length,
                StringComparer.OrdinalIgnoreCase);
            string cachedValue;

            cache.Set("first", "aa");
            cache.Set("second", "bb");
            AssertTrue(cache.TryGet("FIRST", out cachedValue) && cachedValue == "aa", "cache comparer and lookup work");

            cache.Set("third", "c");
            AssertTrue(!cache.TryGet("second", out cachedValue), "least recently used entry is evicted");
            AssertTrue(cache.TryGet("first", out cachedValue) && cachedValue == "aa", "recent entry survives eviction");

            cache.Set("first", "oversized");
            AssertTrue(!cache.TryGet("first", out cachedValue), "oversized replacement removes the stale value");
            AssertTrue(cache.TryGet("third", out cachedValue) && cachedValue == "c", "oversized replacement preserves other entries");

            cache.Move("third", "THIRD");
            AssertTrue(cache.TryGet("third", out cachedValue) && cachedValue == "c", "case-equivalent move preserves cache weight");
            cache.Set("target", "tt");
            cache.Move("third", "target");
            AssertTrue(!cache.TryGet("third", out cachedValue), "move removes the old key");
            AssertTrue(cache.TryGet("target", out cachedValue) && cachedValue == "c", "move replaces the destination atomically");
        }

        private static void DisposablePairReleasesBothResources()
        {
            var firstDisposed = false;
            var secondDisposed = false;
            var pair = new DisposablePair(
                new CallbackDisposable(() => firstDisposed = true),
                new CallbackDisposable(() =>
                {
                    secondDisposed = true;
                    throw new InvalidOperationException("second dispose failed");
                }));
            var failed = false;
            try
            {
                pair.Dispose();
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            AssertTrue(failed, "dispose preserves the original failure");
            AssertTrue(secondDisposed, "second resource is released first");
            AssertTrue(firstDisposed, "first resource is released even if the second release fails");

            var acquiredFirstDisposed = false;
            failed = false;
            try
            {
                DisposablePair.Acquire(
                    () => new CallbackDisposable(() => acquiredFirstDisposed = true),
                    () => { throw new InvalidOperationException("second acquisition failed"); });
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            AssertTrue(failed, "second acquisition failure is preserved");
            AssertTrue(acquiredFirstDisposed, "first resource is released when second acquisition fails");
        }

        private static void ProjectionCacheTrustsOnlyOwnedAppends()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var created = writer.Create("Word", "projection-cache", "Cache.docx", "Initial");
                created.Messages.Add(new ChatMessage { Role = "user", Content = "seed" });
                writer.Save(created);

                var reader = new ChatStore(paths);
                var loaded = reader.Load(created.Host, created.DocumentKey, created.Id);
                AssertEqual(1L, reader.ProjectionFullReplayCount, "first load validates the complete stream");
                AssertEqual("seed", loaded.Messages.Single().Content, "full replay seeds the cached projection");

                loaded.Title = "Reader save";
                reader.Save(loaded);
                AssertEqual(1L, reader.ProjectionFullReplayCount,
                    "save uses the verified baseline instead of replaying the complete stream");
                AssertEqual("Reader save", reader.Load(loaded.Id).Title, "same-head cache hit projects current state");
                AssertEqual(1L, reader.ProjectionFullReplayCount, "same-head cache hit avoids a full replay");

                var external = new ChatStore(paths);
                var externalSession = external.Load(created.Id);
                externalSession.Title = "External append";
                external.Save(externalSession);

                var refreshed = reader.Load(created.Id);
                AssertEqual("External append", refreshed.Title, "new commit replays from the cached byte boundary");
                AssertEqual(2L, reader.ProjectionFullReplayCount,
                    "growth from another store fully validates the durable prefix");
                AssertEqual(0L, reader.ProjectionIncrementalReplayCount,
                    "unowned growth is never accepted as a trusted suffix");

                reader.AppendTrace(refreshed, SessionEventTypes.AssistantChunk,
                    new { chunkCount = 1 }, null, null, "run-cache", "turn-cache", "step-cache");
                refreshed = reader.Load(created.Id);
                AssertEqual("External append", refreshed.Title, "owned trace append leaves canonical state unchanged");
                AssertEqual(1L, reader.ProjectionIncrementalReplayCount,
                    "owned trace append advances the verified cache directly");
                AssertEqual(2L, reader.ProjectionFullReplayCount,
                    "owned append does not rescan the prefix");

                refreshed.Title = "Saved after trace";
                reader.Save(refreshed);
                AssertEqual(2L, reader.ProjectionFullReplayCount,
                    "save after incremental trace replay still uses the verified baseline");

                var path = SessionEventFile(paths, refreshed);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
                AssertEqual("Saved after trace", reader.Load(created.Id).Title,
                    "metadata rewrite falls back to a complete verified replay");
                AssertEqual(3L, reader.ProjectionFullReplayCount, "non-append change invalidates the cache");
            });
        }

        private static void CacheRejectsTamperedPrefixBeforeSuffix()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "tampered-suffix", "Tampered.docx", "Original");
                session.Title = "Updated!";
                writer.Save(session);

                var projectionReader = new ChatStore(paths);
                var cachedSession = projectionReader.Load(session.Id);
                var headerReader = new ChatStore(paths);
                AssertEqual(1, headerReader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "header cache is warm before tampering");

                writer.AppendTrace(session, SessionEventTypes.AssistantChunk,
                    new { chunkCount = 1 }, null, null, "run-tamper", "turn-tamper", "step-tamper");
                var path = SessionEventFile(paths, session);
                ReplaceFileBytes(path, "Original", "Corrupt!");

                AssertTrue(projectionReader.Load(session.Id) == null,
                    "projection cache does not mask a corrupt prefix behind a valid suffix");
                AssertEqual(0, headerReader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "header cache does not mask a corrupt prefix behind a valid suffix");

                var saveRejected = false;
                try
                {
                    cachedSession.Title = "Must not append";
                    projectionReader.Save(cachedSession);
                }
                catch (ChatConcurrencyException)
                {
                    saveRejected = true;
                }
                AssertTrue(saveRejected, "a warm projection cannot append to the corrupt durable stream");
            });
        }

        private static void StreamingTraceQueueIsOrdered()
        {
            var queue = new SessionTraceWriteQueue(4);
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var order = new List<string>();
            var orderSync = new object();

            queue.Enqueue("session-a", () =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
                lock (orderSync) order.Add("chunk-1");
            });
            AssertTrue(firstStarted.Wait(TimeSpan.FromSeconds(2)), "first queued write starts on a worker");
            queue.Enqueue("session-a", () =>
            {
                lock (orderSync) order.Add("chunk-2");
            });
            AssertEqual(2, queue.PendingCount("session-a"), "SSE callback can enqueue behind a blocked fsync");

            queue.EnqueueAndDrain("session-b", () =>
            {
                lock (orderSync) order.Add("other-session");
            });
            var terminal = Task.Run(() => queue.EnqueueAndDrain("session-a", () =>
            {
                lock (orderSync) order.Add("response");
            }));
            AssertTrue(!terminal.Wait(100), "terminal waits for earlier writes in its session");
            releaseFirst.Set();
            AssertTrue(terminal.Wait(TimeSpan.FromSeconds(2)), "terminal drain completes after the blocked write");
            terminal.GetAwaiter().GetResult();

            lock (orderSync)
            {
                AssertTrue(order.IndexOf("other-session") < order.IndexOf("chunk-1"),
                    "another session is not blocked by this session queue");
                AssertTrue(order.Where(value => value != "other-session")
                    .SequenceEqual(new[] { "chunk-1", "chunk-2", "response" }),
                    "writes retain enqueue order through the terminal barrier");
            }

            var terminalRan = false;
            queue.Enqueue("session-failure", () => { throw new InvalidOperationException("queued-write-failed"); });
            try
            {
                queue.EnqueueAndDrain("session-failure", () => terminalRan = true);
                throw new InvalidOperationException("queue failure was not propagated");
            }
            catch (InvalidOperationException ex)
            {
                AssertEqual("queued-write-failed", ex.Message, "first queued failure reaches the terminal barrier");
            }
            AssertTrue(!terminalRan, "terminal write is skipped after an earlier persistence failure");
            queue.EnqueueAndDrain("session-failure", () => terminalRan = true);
            AssertTrue(terminalRan, "an idle failed queue can be retried by a later request");
            AssertEqual(0, queue.QueueCount, "drained per-session queues are evicted");
        }

        private static void CausalTraceScopesAreIsolated()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var sessions = Enumerable.Range(1, 2).Select(index =>
                {
                    var session = store.Create("Excel", "trace-doc-" + index, "Trace", "Trace");
                    session.LastRun = new ChatRunRecord
                    {
                        RunId = "run-" + index, TurnId = "turn-" + index, Status = "completed",
                        DocumentRuntimeKey = "runtime-" + index, StartedUtc = DateTime.UtcNow
                    };
                    store.Save(session);
                    return session;
                }).ToList();
                Task.WhenAll(sessions.Select(session => Task.Run(async () =>
                {
                    using (RunCausalTrace.Begin(store, session))
                    {
                        RunCausalTrace.Record(new CausalTraceRecord { Stage = "run.started" });
                        await Task.Yield();
                        RunCausalTrace.Summary(session);
                        RunCausalTrace.Projected("test_projection");
                    }
                    RunCausalTrace.Projected("outside_scope");
                }))).GetAwaiter().GetResult();
                foreach (var session in sessions)
                {
                    var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id)
                        .Where(item => item.Type == "run.started" || item.Type == "run.summary.created" || item.Type == "ui.projected").ToList();
                    AssertEqual(3, events.Count, "scope ends without leaking into unrelated work");
                    AssertTrue(events.All(item => item.RunId == session.LastRun.RunId && item.TurnId == session.LastRun.TurnId),
                        "async concurrent sessions retain their own run and turn");
                    AssertEqual("completed", (string)events[1].Data["Status"], "summary only observes the existing run record");
                    AssertEqual("test_projection", (string)events[2].Data["Boundary"], "projection boundary is explicit");
                }
                var gate = new TaskCompletionSource<bool>();
                Task late;
                using (RunCausalTrace.Begin(store, sessions[0]))
                {
                    late = Task.Run(async () => { await gate.Task; RunCausalTrace.Projected("late_child"); });
                }
                var before = sessions[0].Revision;
                gate.SetResult(true);
                late.GetAwaiter().GetResult();
                AssertEqual(before, store.Load(sessions[0].Id).Revision, "disposed scope cannot write from inherited background context");
            });
        }

        private static void CausalTraceConfirmationKeepsTurnAndJournalOrigin()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var store = new ChatStore(paths);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord
                {
                    RunId = "before-confirm", TurnId = "logical-turn", Status = "running",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow
                };
                store.Save(session);
                var command = new ToolCommand
                {
                    ToolId = "common.vba_write_module", ToolCallId = "confirmed", RuntimeStepId = "original-step",
                    Arguments = new Dictionary<string, object>
                    {
                        { "moduleName", "Module1" }, { "code", "Sub Main()\nDebug.Print 42\nEnd Sub" }
                    }
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = false };
                using (RunCausalTrace.Begin(store, session))
                {
                    AssertEqual("waiting_confirmation", executor.Execute(command, tools, settings, false, false, session).Status,
                        "first run pauses before mutation");
                }
                AssertEqual(0, journal.ListMutations(adapter.HostName, adapter.DocumentKey).Count, "confirmation pause has no mutation");
                session.LastRun.RunId = "after-confirm";
                store.Save(session);
                using (RunCausalTrace.Begin(store, session))
                {
                    AssertTrue(executor.Execute(command, tools, settings, false, true, session).Success, "confirmed call executes");
                }
                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id)
                    .Where(item => item.Type.StartsWith("tool.execution.", StringComparison.Ordinal) || item.Type.StartsWith("domain.effect.", StringComparison.Ordinal)).ToList();
                var starts = events.Where(item => item.Type == "tool.execution.started").ToList();
                AssertEqual("before-confirm,after-confirm", string.Join(",", starts.Select(item => item.RunId)), "confirmation creates a distinct execution run");
                AssertTrue(events.All(item => item.TurnId == "logical-turn" && item.StepId == "original-step" &&
                    (string)item.Data["ToolCallId"] == "confirmed"), "turn, originating step and call survive confirmation");
                var mutation = journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                var effects = events.Where(item => item.Type.StartsWith("domain.effect.", StringComparison.Ordinal)).ToList();
                AssertEqual(3, effects.Count, "only confirmed run reaches domain boundaries");
                AssertTrue(effects.All(item => item.RunId == "after-confirm" &&
                    (string)item.Data["JournalRunId"] == mutation.Prepared.RunId &&
                    (string)item.Data["MutationId"] == mutation.Prepared.MutationId), "actual execution run and journal guard origin are not confused");
                AssertEqual(1, adapter.Executed.Count(item => item.ToolId == "excel.vba_replace_module"), "confirmation writes once");
            });
        }

        private static void CausalTraceFailureDoesNotChangeExecution()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var session = NewSession(adapter); // Deliberately not persisted: optional appends must fail harmlessly.
                session.LastRun = new ChatRunRecord { RunId = "unsaved-run", TurnId = "unsaved-turn" };
                using (RunCausalTrace.Begin(store, session))
                {
                    var result = executor.Execute(new ToolCommand
                    {
                        ToolId = "excel.add_sheet", ToolCallId = "one-write",
                        Arguments = new Dictionary<string, object> { { "name", "TraceFailure" } }
                    }, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                    AssertTrue(result.Success && adapter.HasSheet("TraceFailure"), "trace failure does not block or reinterpret a tool");
                }
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    options.TraceSink = record => { throw new InvalidOperationException("optional acceptance trace failed"); };
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"status\":\"completed\",\"message\":\"Answer.\",\"tool_calls\":[]}"
                    });
                };
                var final = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Answer.", session, NewContext(adapter), new AppSettings(),
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();
                AssertEqual("Answer.", final.AssistantText, "optional accepted trace failure preserves response");
                AssertEqual("completed", final.RunStatus, "optional accepted trace failure preserves outcome");
                AssertEqual(1, adapter.Executed.Count(item => item.ToolId == "excel.add_sheet"), "trace failure never retries a write");
            });
        }

        private static void StreamingTraceQueueDrainsBeforeTerminal()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "trace-queue", "Trace.docx", "Trace queue");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-queue",
                    TurnId = "turn-queue",
                    Status = "running",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);

                var service = new ModelTracePersistenceService(store, new SessionTraceWriteQueue(4));
                var options = new LlmRequestOptions { TraceSession = session, TracePurpose = "agent" };
                service.Configure(options);
                var requestRecord = new LlmTraceRecord
                {
                    Type = "request", RequestId = "request-queue", Purpose = "agent",
                    PayloadUtf8Bytes = Encoding.UTF8.GetBytes("{\"request\":\"точно\"}"),
                    PayloadContentType = "application/json"
                };
                options.TraceSink(requestRecord);
                AssertTrue(!requestRecord.PayloadTextMaterialized,
                    "byte-backed request persists without materializing another UTF-16 string");
                options.TraceSink(new LlmTraceRecord
                {
                    Type = "chunk", RequestId = "request-queue", Purpose = "agent",
                    ChunkIndex = 0, ChunkCount = 2, Completed = true,
                    PayloadJson = "[\"one\",\"two\"]", PayloadContentType = "application/json"
                });
                options.TraceSink(new LlmTraceRecord
                {
                    Type = "response", RequestId = "request-queue", Purpose = "agent", StatusCode = 200,
                    PayloadJson = "{\"response\":true}", PayloadContentType = "application/json"
                });

                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                var request = events.Single(item => item.Type == SessionEventTypes.LlmRequest);
                var chunk = events.Single(item => item.Type == SessionEventTypes.AssistantChunk);
                var response = events.Single(item => item.Type == SessionEventTypes.LlmResponse);
                AssertEqual("request-queue", (string)request.Data["ModelAttemptId"], "non-loop request uses its existing transport id as attempt");
                AssertEqual("request-queue", (string)request.Data["StepId"], "non-loop request uses its existing transport step");
                AssertEqual("{\"request\":\"точно\"}", store.ReadEventPayload(request),
                    "request CAS preserves the exact HTTP UTF-8 payload");
                AssertTrue(chunk.Sequence < response.Sequence, "terminal response is durable after queued chunks");
                AssertEqual("[\"one\",\"two\"]", store.ReadEventPayload(chunk), "queued chunk payload remains exact");
                AssertEqual(SessionEventTypes.StepEnded, events.Last().Type,
                    "response and step terminal boundary remain one durable append batch");
            });
        }

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

        private static void NaturalListChangesOmitReorder()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "natural-order", "Order.docx", "Order");
                var first = new ChatMessage { Role = "user", Content = "first" };
                var second = new ChatMessage { Role = "assistant", Content = "second" };
                session.Messages.Add(first);
                session.Messages.Add(second);
                store.Save(session);

                var commit = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last();
                AssertTrue(!commit.Data["Operations"].Any(operation =>
                    string.Equals((string)operation["Type"], SessionOperationTypes.MessagesReorder, StringComparison.Ordinal)),
                    "pure appends do not persist a full order vector");

                session.Messages.Reverse();
                store.Save(session);
                commit = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last();
                AssertTrue(commit.Data["Operations"].Any(operation =>
                    string.Equals((string)operation["Type"], SessionOperationTypes.MessagesReorder, StringComparison.Ordinal)),
                    "an explicit reorder remains canonical");
                AssertTrue(store.Load(session.Id).Messages.Select(item => item.Id)
                    .SequenceEqual(new[] { second.Id, first.Id }), "reordered messages replay exactly");

                session.Messages.RemoveAt(0);
                store.Save(session);
                commit = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last();
                AssertTrue(!commit.Data["Operations"].Any(operation =>
                    string.Equals((string)operation["Type"], SessionOperationTypes.MessagesReorder, StringComparison.Ordinal)),
                    "removal preserves replay order without a full order vector");
            });
        }

        private static void ChatHeadersUseArtifactMetadata()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "header-metadata", "Header.docx", "Header");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>header</h1>", true);
                HtmlArtifactToolExecutor.UpsertDataSource(session, "rows", "{\"rows\":[1]}");
                store.Save(session);

                var active = session.Artifacts.Single(item => item.Id == session.ActiveHtmlArtifactId);
                var blob = Directory.GetFiles(paths.ChatBlobDirectory, active.ContentSha256 + ".blob", SearchOption.AllDirectories).Single();
                File.Delete(blob);
                var header = store.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(1, header.HtmlFileCount, "header reads file count without hydrating CAS");
                AssertEqual(1, header.HtmlDataSourceCount, "header reads data source count without hydrating CAS");
            });
        }

        private static void HeaderCacheTrustsOnlyOwnedAppends()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "header-reducer", "Header.docx", "Initial");
                session.Messages.Add(new ChatMessage { Role = "user", Content = "visible" });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "protocol",
                    ProtocolMessage = true
                });
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-header",
                    RuntimeId = "runtime-header",
                    Status = "completed",
                    Phase = "final",
                    StartedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "<h1>header</h1>", true);
                writer.Save(session);
                session.Artifacts.Single(item => item.Id == session.ActiveHtmlArtifactId).MetadataJson = "{}";
                writer.Save(session);

                var reader = new ChatStore(paths);
                var header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual("Initial", header.Title, "minimal reducer retains session metadata");
                AssertEqual(1, header.MessageCount, "minimal reducer excludes protocol messages");
                AssertEqual("runtime-header", header.RunRuntimeId, "minimal reducer retains run header fields");
                AssertEqual(0, header.HtmlFileCount,
                    "invalid legacy metadata does not hydrate the active CAS body during header replay");
                AssertEqual(1L, reader.HeaderFullReplayCount, "cold header read validates the complete stream once");
                AssertEqual(0L, reader.ProjectionFullReplayCount, "header read does not build a full projection");

                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(1L, reader.HeaderFullReplayCount, "unchanged header uses its byte-offset cache");

                session.Title = "Appended";
                session.Messages.RemoveAt(0);
                session.Messages.Single().ProtocolMessage = false;
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "second visible" });
                session.Messages.Reverse();
                HtmlArtifactToolExecutor.UpsertDataSource(session, "rows", "{\"rows\":[1]}");
                writer.Save(session);

                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual("Appended", header.Title, "full replay applies externally appended metadata operations");
                AssertEqual(2, header.MessageCount, "full replay applies message upsert/remove/reorder operations");
                AssertEqual(1, header.HtmlDataSourceCount, "full replay follows a new active HTML artifact");
                AssertEqual(session.Revision, header.Revision, "header revision follows the validated stream tail");
                AssertEqual(2L, reader.HeaderFullReplayCount,
                    "growth from another store fully validates the durable prefix");
                AssertEqual(0L, reader.HeaderIncrementalReplayCount,
                    "unowned growth is never accepted as a trusted suffix");
                AssertEqual(0L, reader.ProjectionFullReplayCount, "header replay remains projection-free");

                var owned = reader.Load(session.Id);
                reader.AppendTrace(owned, SessionEventTypes.AssistantChunk,
                    new { chunkCount = 1 }, null, null, "run-header", "turn-header", "step-header");
                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(owned.Revision, header.Revision, "owned trace append advances the header revision");
                AssertEqual(1L, reader.HeaderIncrementalReplayCount,
                    "owned trace append advances the header reducer from its byte boundary");

                owned.Title = "Owned save";
                reader.Save(owned);
                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual("Owned save", header.Title, "owned commit advances the cached header reducer");
                AssertEqual(2L, reader.HeaderIncrementalReplayCount,
                    "owned projection append also advances the header reducer");
                AssertEqual(2L, reader.HeaderFullReplayCount,
                    "owned header advances do not rescan the prefix");
            });
        }

        private static void ChatHeadersReportStorageUsage()
        {
            WithTempPaths(paths =>
            {
                var body = "{\"value\":\"" + new string('ж', 32 * 1024) + "\"}";
                var logicalByteLength = Encoding.UTF8.GetByteCount(body);
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "storage-usage", "Storage.docx", "Storage usage");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.PlanDocument,
                    MimeType = "application/json",
                    InlineText = body
                };
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
                writer.Save(session);
                writer.AppendTrace(session, SessionEventTypes.AssistantChunk,
                    new { chunkCount = 1 }, body, "application/json",
                    "storage-run", "storage-turn", "storage-step");

                var blobPath = Path.Combine(paths.ChatBlobDirectory,
                    artifact.ContentSha256.Substring(0, 2), artifact.ContentSha256 + ".blob");
                var reader = new ChatStore(paths);
                var header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(new FileInfo(SessionEventFile(paths, session)).Length, header.JsonlByteLength,
                    "header reports the exact JSONL file length");
                AssertEqual(1, header.CasBlobCount, "equal artifact and trace payload bytes count once");
                AssertEqual((long)logicalByteLength, header.CasLogicalByteLength,
                    "header reports unique plaintext CAS bytes");
                AssertEqual(new FileInfo(blobPath).Length, header.CasStoredByteLength,
                    "header reports the actual compressed CAS file length");
                AssertTrue(header.CasStoredByteLength < header.CasLogicalByteLength,
                    "stored usage reflects compression");
                AssertEqual(0, header.CasMissingBlobCount, "present CAS is not reported missing");
                AssertEqual(0, header.CasReferenceIssueCount, "valid CAS references have no issues");
                AssertEqual(ChatStorageWarningLevels.None, header.StorageWarningLevel,
                    "small healthy history has no warning");

                AssertEqual(ChatStorageWarningLevels.Warning,
                    ChatStorageUsagePolicy.GetWarningLevel(
                        ChatStorageUsagePolicy.WarningJsonlByteLength, 0, 0, 0, 0),
                    "JSONL warning threshold is inclusive");
                AssertEqual(ChatStorageWarningLevels.Critical,
                    ChatStorageUsagePolicy.GetWarningLevel(
                        0, 0, ChatStorageUsagePolicy.CriticalStoredFootprintByteLength, 0, 0),
                    "stored footprint critical threshold is inclusive");

                var storedBlob = File.ReadAllBytes(blobPath);
                File.Delete(blobPath);
                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(1, header.CasMissingBlobCount,
                    "warm header cache refreshes missing referenced CAS state");
                AssertEqual(0L, header.CasStoredByteLength, "missing CAS contributes no stored bytes");
                AssertEqual(ChatStorageWarningLevels.Critical, header.StorageWarningLevel,
                    "missing CAS raises a critical warning");

                Directory.CreateDirectory(Path.GetDirectoryName(blobPath));
                File.WriteAllBytes(blobPath, storedBlob);
                header = reader.ListHeaders(session.Host, session.DocumentKey, session.DocumentTitle).Single();
                AssertEqual(0, header.CasMissingBlobCount,
                    "warm header cache notices a restored referenced CAS blob");
                AssertEqual((long)storedBlob.Length, header.CasStoredByteLength,
                    "restored CAS contributes its current stored bytes");
                AssertEqual(ChatStorageWarningLevels.None, header.StorageWarningLevel,
                    "restored CAS clears the storage warning without a JSONL append");
            });
        }

        private static void TrajectoryQueryPaginatesAndFilters()
        {
            var resourceUri = ResourceUri.Create("chat", "session-1", "artifact", "artifact-1", "revision", "1");
            var events = new List<SessionEvent>
            {
                new SessionEvent { Sequence = 1, EventId = "event-1", Type = SessionEventTypes.SessionCreated, Data = new JObject { ["Title"] = "Root" } },
                new SessionEvent
                {
                    Sequence = 2,
                    EventId = "event-2",
                    Type = SessionEventTypes.SessionCommit,
                    Data = new JObject
                    {
                        ["Operations"] = new JArray(new JObject
                        {
                            ["Type"] = SessionOperationTypes.MessageUpdated,
                            ["Data"] = new JObject
                            {
                                ["Value"] = new JObject
                                {
                                    ["Id"] = "message-1",
                                    ["ResponseStatus"] = AgentResponseStatuses.AwaitingUser,
                                    ["ResourceRefs"] = new JArray(new JObject { ["uri"] = resourceUri, ["revision"] = "1" }),
                                    ["Activity"] = new JObject { ["ToolCallId"] = "tool-call-old", ["Status"] = "running" }
                                }
                            }
                        })
                    }
                },
                new SessionEvent
                {
                    Sequence = 3,
                    EventId = "event-3",
                    Type = SessionEventTypes.SessionCommit,
                    Data = new JObject
                    {
                        ["Operations"] = new JArray(new JObject
                        {
                            ["Type"] = SessionOperationTypes.MessageRemove,
                            ["Data"] = new JObject { ["Id"] = "message-1" }
                        })
                    }
                },
                new SessionEvent
                {
                    Sequence = 4,
                    EventId = "event-4",
                    Type = SessionEventTypes.SessionCommit,
                    Data = new JObject
                    {
                        ["Operations"] = new JArray(new JObject
                        {
                            ["Type"] = SessionOperationTypes.ArtifactRevisionCreated,
                            ["Data"] = new JObject { ["Value"] = new JObject { ["Id"] = "artifact-1", ["Title"] = "Plan" } }
                        })
                    }
                },
                new SessionEvent
                {
                    Sequence = 5,
                    EventId = "event-5",
                    Type = SessionEventTypes.AgentResponseRejected,
                    RunId = "run-failed",
                    TurnId = "turn-failed",
                    StepId = "step-failed",
                    Data = new JObject { ["Status"] = "failed", ["Reason"] = "unique repair marker" }
                }
            };
            var query = new EventStreamTrajectoryQuery();
            var first = query.Query(events, new TrajectoryQueryRequest { PageSize = 2 });
            AssertEqual(5, first.TotalEvents, "query reports canonical event count");
            AssertEqual(5, first.TotalMatches, "unfiltered query matches all events");
            AssertEqual(5L, first.Records[0].Event.Sequence, "query is newest first");
            AssertEqual(4L, first.Records[1].Event.Sequence, "first page is bounded");
            AssertEqual("seq:4", first.NextCursor, "cursor is sequence based");
            AssertTrue(first.HasMore, "older events remain");

            var second = query.Query(events, new TrajectoryQueryRequest { PageSize = 2, Cursor = first.NextCursor });
            AssertEqual(3L, second.Records[0].Event.Sequence, "cursor continues without overlap");
            AssertEqual(2L, second.Records[1].Event.Sequence, "second page remains newest first");
            AssertEqual(2L, second.Records[1].SourceEventSeqs.Single(), "projection keeps source sequence");
            AssertEqual("event-2", second.Records[1].SourceEventIds.Single(), "projection keeps source event id");

            AssertEqual(2L, query.Query(events, new TrajectoryQueryRequest { Visibility = TrajectoryVisibility.Shadowed })
                .Records.Single().Event.Sequence, "superseded projection mutation is shadowed");
            AssertEqual(5L, query.Query(events, new TrajectoryQueryRequest { Visibility = TrajectoryVisibility.LogOnly })
                .Records.Single().Event.Sequence, "trace event is log-only");
            AssertEqual(3, query.Query(events, new TrajectoryQueryRequest { Visibility = TrajectoryVisibility.Current }).TotalMatches,
                "seed and live projection mutations are current");
            AssertEqual(2L, query.Query(events, new TrajectoryQueryRequest { ToolCallId = "tool-call-old" }).Records.Single().Event.Sequence,
                "tool-call correlation filter searches event data");
            AssertEqual(2L, query.Query(events, new TrajectoryQueryRequest
                { Status = AgentResponseStatuses.AwaitingUser }).Records.Single().Event.Sequence,
                "response status participates in trajectory status filters");
            var resourceRecord = query.Query(events, new TrajectoryQueryRequest { ResourceUri = resourceUri }).Records.Single();
            AssertEqual(2L, resourceRecord.Event.Sequence, "resource filter resolves canonical message references");
            AssertEqual("1", resourceRecord.ResourceRefs.Single().Revision, "resource projection preserves revision evidence");
            AssertEqual(4L, query.Query(events, new TrajectoryQueryRequest { ArtifactId = "artifact-1" }).Records.Single().Event.Sequence,
                "artifact filter resolves operation values");
            AssertEqual(5L, query.Query(events, new TrajectoryQueryRequest { Search = "unique marker", Status = "failed", RunId = "run-failed" })
                .Records.Single().Event.Sequence, "full-text/status/run filters compose");
            AssertEqual(2, query.Query(events, new TrajectoryQueryRequest
            {
                EventTypes = new List<string> { SessionEventTypes.SessionCommit },
                MinSequence = 3,
                MaxSequence = 4
            }).TotalMatches, "event type and sequence filters compose");
        }

        private static void TrajectoryDerivedViewsRetainSourcesAndUsage()
        {
            var started = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc);
            var events = new List<SessionEvent>
            {
                TrajectoryEvent(1, SessionEventTypes.TurnStarted, started, "run-1", "turn-1", null, new JObject { ["Status"] = "running" }),
                TrajectoryEvent(2, SessionEventTypes.StepStarted, started.AddSeconds(1), "run-1", "turn-1", "step-1", new JObject { ["Purpose"] = "agent", ["Status"] = "running" }),
                TrajectoryEvent(3, SessionEventTypes.LlmRequest, started.AddSeconds(2), "run-1", "turn-1", "step-1", new JObject { ["Model"] = "model-a", ["EstimatedPromptTokens"] = 12 }),
                TrajectoryEvent(4, SessionEventTypes.LlmResponse, started.AddSeconds(3), "run-1", "turn-1", "step-1", new JObject
                {
                    ["PromptTokens"] = 10,
                    ["CompletionTokens"] = 4,
                    ["TotalTokens"] = 14,
                    ["UsageJson"] = "{\"cost_usd\":0.002}"
                }),
                TrajectoryEvent(5, SessionEventTypes.StepEnded, started.AddSeconds(4), "run-1", "turn-1", "step-1", new JObject { ["Status"] = "completed" }),
                TrajectoryEvent(6, SessionEventTypes.SessionCommit, started.AddSeconds(5), "run-1", "turn-1", null, new JObject
                {
                    ["Operations"] = new JArray(
                        new JObject
                        {
                            ["Type"] = SessionOperationTypes.ToolCallRecorded,
                            ["Data"] = new JObject { ["Value"] = new JObject { ["ToolCalls"] = new JArray(new JObject { ["Id"] = "call-1", ["Name"] = "excel.inspect" }) } }
                        },
                        new JObject
                        {
                            ["Type"] = SessionOperationTypes.ToolExecutionFinished,
                            ["Data"] = new JObject { ["Value"] = new JObject { ["Activity"] = new JObject { ["ToolCallId"] = "call-1", ["ToolId"] = "excel.inspect", ["Status"] = "waiting", ["ExecutionStatus"] = "waiting_confirmation" } } }
                        },
                        new JObject
                        {
                            ["Type"] = SessionOperationTypes.ArtifactRevisionCreated,
                            ["Data"] = new JObject { ["Value"] = new JObject { ["Id"] = "artifact-1", ["ParentArtifactId"] = "artifact-root", ["Kind"] = "html_workspace", ["Title"] = "Dashboard", ["Revision"] = 2 } }
                        })
                }),
                TrajectoryEvent(7, SessionEventTypes.SessionCommit, started.AddSeconds(7), "run-1", "turn-1", null, new JObject
                {
                    ["Operations"] = new JArray(new JObject
                    {
                        ["Type"] = SessionOperationTypes.ToolExecutionFinished,
                        ["Data"] = new JObject { ["Value"] = new JObject { ["Activity"] = new JObject { ["ToolCallId"] = "call-1", ["ToolId"] = "excel.inspect", ["Status"] = "completed", ["ExecutionStatus"] = "completed" } } }
                    })
                }),
                TrajectoryEvent(8, SessionEventTypes.AgentResponseRejected, started.AddSeconds(8), "run-1", "turn-1", "step-1", new JObject { ["Attempt"] = 2, ["FailureKind"] = "invalid_agent_response" }),
                TrajectoryEvent(9, SessionEventTypes.TurnEnded, started.AddSeconds(10), "run-1", "turn-1", null, new JObject { ["Status"] = "completed" })
            };

            var query = new EventStreamTrajectoryQuery();
            var model = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.ModelReplay }).Rows.Single();
            AssertEqual("rejected", model.Status, "model view exposes format repair");
            AssertEqual(14, model.TotalTokens, "model usage is correlated to the step");
            AssertEqual(0.002m, model.CostUsd, "provider-reported cost is preserved");
            AssertTrue(model.SourceEventSeqs.SequenceEqual(new long[] { 2, 3, 4, 5, 8 }), "model row retains every contributing event sequence");
            AssertEqual(model.SourceEventSeqs.Count, model.SourceEventIds.Count, "model source ids remain correlated");

            var tool = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.ToolExecution, ToolCallId = "call-1" }).Rows.Single();
            AssertEqual("completed", tool.Status, "tool view follows execution through confirmation");
            AssertTrue(tool.SourceEventSeqs.SequenceEqual(new long[] { 6, 7 }), "tool row deduplicates commit sources");
            var confirmation = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.ConfirmationPauses }).Rows.Single();
            AssertEqual("resolved", confirmation.Status, "confirmation pause has terminal outcome");
            AssertEqual(2000L, confirmation.DurationMs, "confirmation duration uses waiting and terminal events");

            var artifact = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.ArtifactLineage, ArtifactId = "artifact-root" }).Rows.Single();
            AssertEqual("artifact-1", artifact.ArtifactId, "artifact lineage filters by parent id");
            AssertEqual(6L, artifact.SourceEventSeqs.Single(), "artifact row retains revision source");
            AssertEqual("rna://chat/trajectory-chat/artifact/artifact-1/revision/2", artifact.ResourceRefs.Single().Uri,
                "artifact diagnostics expose the canonical revision-pinned resource URI");

            var failure = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.FailureRetries }).Rows.Single();
            AssertEqual("model-failure", failure.Kind, "failure view records rejected model attempt");
            AssertEqual(1, (int)failure.Data["retryCount"], "failure view exposes retry count");

            var turn = query.QueryView(events, new TrajectoryViewQueryRequest { View = TrajectoryViews.TurnUsage }).Rows.Single();
            AssertEqual(10000L, turn.DurationMs, "turn timing uses lifecycle boundaries");
            AssertEqual(14, turn.TotalTokens, "turn usage aggregates model steps");
            AssertEqual(0.002m, turn.CostUsd, "turn cost aggregates provider usage");
            AssertTrue(turn.SourceEventSeqs.SequenceEqual(Enumerable.Range(1, 9).Select(value => (long)value)), "turn row retains complete event lineage");

            var declaredOutcomes = new List<SessionEvent>
            {
                TrajectoryEvent(1, SessionEventTypes.TurnStarted, started, "run-blocked", "turn-blocked", null,
                    new JObject { ["Status"] = "running" }),
                TrajectoryEvent(2, SessionEventTypes.TurnEnded, started.AddSeconds(1), "run-blocked", "turn-blocked", null,
                    new JObject { ["Status"] = AgentResponseStatuses.Blocked }),
                TrajectoryEvent(3, SessionEventTypes.TurnStarted, started.AddSeconds(2), "run-awaiting", "turn-awaiting", null,
                    new JObject { ["Status"] = "running" }),
                TrajectoryEvent(4, SessionEventTypes.TurnEnded, started.AddSeconds(3), "run-awaiting", "turn-awaiting", null,
                    new JObject { ["Status"] = AgentResponseStatuses.AwaitingUser })
            };
            var declaredFailures = query.QueryView(declaredOutcomes,
                new TrajectoryViewQueryRequest { View = TrajectoryViews.FailureRetries }).Rows;
            AssertEqual(1, declaredFailures.Count,
                "blocked outcome is diagnosable while awaiting_user is not mislabeled as failure");
            AssertEqual(AgentResponseStatuses.Blocked, declaredFailures.Single().Status,
                "declared blocked outcome is preserved in failure diagnostics");
        }

        private static void TrajectoryExportRedactsAndVerifiesBundle()
        {
            WithTempPaths(paths =>
            {
                const string credential = "TRAJECTORY_SECRET_91c8";
                const string visible = "TRAJECTORY_VISIBLE_5b42";
                const string payload = "TRAJECTORY_PAYLOAD_a0e7";
                var protector = new StorageProtector(
                    HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "trajectory export key",
                    Enumerable.Range(61, 32).Select(value => (byte)value).ToArray());
                Func<StorageProtector> protection = () => protector;
                var store = new ChatStore(paths, protection);
                var session = store.Create("Word", "trajectory-export", "Export.docx", "Export");
                store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new { ApiKey = credential, SafeValue = visible },
                    payload, "application/json", "run-export", "turn-export", "step-export");
                var events = store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id);
                var exporter = new TrajectoryExportService(paths, protection, new EventStreamTrajectoryQuery());

                var metadata = exporter.Export(session.Host, session.DocumentKey, session.Id, events,
                    new TrajectoryExportRequest
                    {
                        EventTypes = new List<string> { SessionEventTypes.LlmRequest },
                        RedactionMode = TrajectoryExportRedactionModes.Metadata
                    });
                var metadataEvents = ZipEntryText(metadata.BundleBytes, "events.jsonl");
                AssertTrue(metadataEvents.IndexOf(credential, StringComparison.Ordinal) < 0, "metadata export removes credential");
                AssertTrue(metadataEvents.IndexOf(visible, StringComparison.Ordinal) < 0, "metadata export removes event data");
                AssertContains(metadataEvents, "\"redacted\":true", "metadata export marks redaction");
                AssertContains(ZipEntryText(metadata.BundleBytes, "checksums.sha256"), "manifest.json", "export checksums cover manifest");
                AssertTrue(!ZipEntryNames(metadata.BundleBytes).Any(name => name.StartsWith("cas/", StringComparison.Ordinal)),
                    "metadata export excludes CAS bodies");

                var secrets = exporter.Export(session.Host, session.DocumentKey, session.Id, events,
                    new TrajectoryExportRequest
                    {
                        EventTypes = new List<string> { SessionEventTypes.LlmRequest },
                        RedactionMode = TrajectoryExportRedactionModes.Secrets
                    });
                var secretEvents = ZipEntryText(secrets.BundleBytes, "events.jsonl");
                AssertTrue(secretEvents.IndexOf(credential, StringComparison.Ordinal) < 0, "credential field is redacted");
                AssertContains(secretEvents, visible, "non-credential event data remains in secrets mode");

                var full = exporter.Export(session.Host, session.DocumentKey, session.Id, events,
                    new TrajectoryExportRequest
                    {
                        EventTypes = new List<string> { SessionEventTypes.LlmRequest },
                        RedactionMode = TrajectoryExportRedactionModes.None,
                        IncludeCasPayloads = true
                    });
                var manifest = JObject.Parse(ZipEntryText(full.BundleBytes, "manifest.json"));
                var exportedPath = (string)manifest["references"][0]["exportPath"];
                AssertEqual(payload, ZipEntryText(full.BundleBytes, exportedPath), "full export decrypts and verifies CAS body");
                AssertContains(ZipEntryText(full.BundleBytes, "events.jsonl"), credential, "full export preserves event data");
                AssertEqual(64, full.BundleSha256.Length, "bundle has SHA-256");

                File.AppendAllText(SessionEventFile(paths, session), "{\"incomplete\"");
                var incompleteRejected = false;
                try
                {
                    store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id);
                }
                catch (ChatConcurrencyException)
                {
                    incompleteRejected = true;
                }
                AssertTrue(incompleteRejected, "trajectory export source rejects incomplete tail");
            });
        }

        private static SessionEvent TrajectoryEvent(
            long sequence,
            string type,
            DateTime createdUtc,
            string runId,
            string turnId,
            string stepId,
            JToken data)
        {
            return new SessionEvent
            {
                SessionId = "trajectory-chat",
                Sequence = sequence,
                EventId = "derived-event-" + sequence.ToString(CultureInfo.InvariantCulture),
                Type = type,
                CreatedUtc = createdUtc,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId,
                Data = data
            };
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
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

                var appendRejected = false;
                try
                {
                    store.AppendTrace(session, SessionEventTypes.AssistantChunk,
                        new { requestId = "tampered-prefix", chunkCount = 1 },
                        null, null, "run-tampered", "turn-tampered", "tampered-prefix");
                }
                catch (ChatConcurrencyException)
                {
                    appendRejected = true;
                }
                AssertTrue(appendRejected, "fast append falls back to full validation after an external file change");

                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "hash mismatch rejects projection");
                AssertEqual(0, store.List(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "corrupt stream is excluded from listing");
            });

            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "shape-doc", "Shape.xlsx", "Shape");
                var path = SessionEventFile(paths, session);
                var lines = File.ReadAllLines(path);
                var first = JObject.Parse(lines[0]);
                first["UnhashedExtension"] = "must-not-be-ignored";
                lines[0] = first.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllLines(path, lines);

                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "unknown unhashed event fields reject the stream");
            });

            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "blank-doc", "Blank.xlsx", "Blank");
                var path = SessionEventFile(paths, session);
                File.AppendAllText(path, "\n");
                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "blank canonical JSONL records reject the stream");
            });
        }

        private static void PreCutoverSessionFormatsAreResetOnly()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "old-schema-doc", "Old.xlsx", "Old schema");
                var path = SessionEventFile(paths, session);
                var lines = File.ReadAllLines(path);
                var seed = JObject.Parse(lines[0]);
                seed["SchemaVersion"] = SessionEvent.CurrentSchemaVersion - 1;
                seed["Hash"] = ComputeUnkeyedEventHash(seed);
                lines[0] = seed.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllLines(path, lines);

                var reader = new ChatStore(paths);
                AssertTrue(reader.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "pre-cutover schema is refused instead of migrated");
                AssertEqual(0, reader.List(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "pre-cutover stream is skipped until Chats/Data is cleared");
            });

            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "old-format-doc", "OldFormat.xlsx", "Old format");
                var path = SessionEventFile(paths, session);
                var lines = File.ReadAllLines(path);
                var seed = JObject.Parse(lines[0]);
                seed["Data"]["FormatVersion"] = ChatSession.CurrentFormatVersion - 1;
                seed["Hash"] = ComputeUnkeyedEventHash(seed);
                lines[0] = seed.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllLines(path, lines);

                var reader = new ChatStore(paths);
                AssertTrue(reader.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "pre-cutover projection format is refused instead of normalized");
                AssertEqual(0, reader.List(session.Host, session.DocumentKey, session.DocumentTitle).Count,
                    "pre-cutover projection format is excluded from headers");
            });
        }

        private static void SessionEventHmacRequiresMatchingKey()
        {
            WithTempPaths(paths =>
            {
                var salt = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
                var protector = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.None,
                    "correct portable secret",
                    salt);
                var store = new ChatStore(paths, () => protector);
                var session = store.Create("Word", "hmac-doc", "Hmac.docx", "Signed history");
                session.Messages.Add(new ChatMessage { Role = "user", Content = "readable but signed" });
                store.Save(session);

                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertTrue(events.All(item => item.HashAlgorithm == HistoryIntegrityModes.HmacSha256),
                    "all events use HMAC");
                AssertTrue(events.All(item => item.ProtectionKeyId == protector.KeyId),
                    "all events identify the HMAC key");
                AssertContains(File.ReadAllText(SessionEventFile(paths, session)), "readable but signed",
                    "HMAC does not encrypt history");

                var wrong = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.None,
                    "different portable secret",
                    salt);
                var wrongStore = new ChatStore(paths, () => wrong);
                AssertTrue(wrongStore.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "another HMAC key cannot validate history");
            });
        }

        private static void EncryptedHistoryProtectsEventsAndCas()
        {
            WithTempPaths(paths =>
            {
                const string titleMarker = "PRIVATE_TITLE_7f29";
                const string messageMarker = "PRIVATE_MESSAGE_58c1";
                const string artifactMarker = "PRIVATE_ARTIFACT_f04a";
                const string payloadMarker = "PRIVATE_MODEL_PAYLOAD_91de";
                var salt = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
                var protector = new StorageProtector(
                    HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "portable encrypted secret",
                    salt);
                var store = new ChatStore(paths, () => protector);
                var session = store.Create("Excel", "encrypted-doc", "Encrypted.xlsx", titleMarker);
                session.Messages.Add(new ChatMessage { Role = "user", Content = messageMarker });
                session.Artifacts.Add(new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    MimeType = "text/markdown",
                    InlineText = artifactMarker
                });
                store.Save(session);
                store.AppendTrace(
                    session,
                    SessionEventTypes.LlmRequest,
                    new { requestId = "encrypted-request", purpose = "agent" },
                    payloadMarker,
                    "application/json",
                    "encrypted-run",
                    "encrypted-turn",
                    "encrypted-request");

                var eventText = File.ReadAllText(SessionEventFile(paths, session));
                AssertContains(eventText, "EncryptedData", "encrypted event envelope");
                AssertTrue(eventText.IndexOf(titleMarker, StringComparison.Ordinal) < 0, "title is not plaintext");
                AssertTrue(eventText.IndexOf(messageMarker, StringComparison.Ordinal) < 0, "message is not plaintext");
                AssertTrue(eventText.IndexOf(artifactMarker, StringComparison.Ordinal) < 0, "artifact is not in event plaintext");
                AssertTrue(eventText.IndexOf(payloadMarker, StringComparison.Ordinal) < 0, "model payload is not in event plaintext");
                foreach (var path in Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories))
                {
                    var raw = File.ReadAllBytes(path);
                    AssertTrue(StorageProtector.IsProtectedPayload(raw), "CAS blob uses protected envelope");
                    AssertTrue(!ContainsBytes(raw, Encoding.UTF8.GetBytes(artifactMarker)), "artifact CAS body is encrypted");
                    AssertTrue(!ContainsBytes(raw, Encoding.UTF8.GetBytes(payloadMarker)), "model CAS payload is encrypted");
                }

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(titleMarker, loaded.Title, "encrypted title replays");
                AssertEqual(messageMarker, loaded.Messages.Single().Content, "encrypted message replays");
                AssertEqual(artifactMarker, loaded.Artifacts.Single().InlineText, "encrypted artifact hydrates");
                var trace = store.ReadEvents(session.Host, session.DocumentKey, session.Id)
                    .Single(item => item.Type == SessionEventTypes.LlmRequest);
                AssertEqual(payloadMarker, store.ReadEventPayload(trace), "encrypted model payload roundtrip");

                File.AppendAllText(SessionEventFile(paths, session), "{\"SchemaVersion\":");
                session.Title = titleMarker + "_UPDATED";
                store.Save(session);
                AssertTrue(File.ReadAllText(SessionEventFile(paths, session)).IndexOf(titleMarker, StringComparison.Ordinal) < 0,
                    "encrypted tail recovery does not leak hydrated data");
                AssertEqual(titleMarker + "_UPDATED", store.Load(session.Host, session.DocumentKey, session.Id).Title,
                    "encrypted tail recovery replays");

                var wrong = new StorageProtector(
                    HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "wrong encrypted secret",
                    salt);
                AssertTrue(new ChatStore(paths, () => wrong).Load(session.Host, session.DocumentKey, session.Id) == null,
                    "wrong encryption key cannot project history");

                var lines = File.ReadAllLines(SessionEventFile(paths, session));
                var rewritten = JObject.Parse(lines[0]);
                rewritten["Type"] = SessionEventTypes.SessionForked;
                rewritten["Hash"] = ComputeUnkeyedEventHash(rewritten);
                lines[0] = rewritten.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllLines(SessionEventFile(paths, session), lines);
                AssertTrue(store.Load(session.Host, session.DocumentKey, session.Id) == null,
                    "encryption authenticates event metadata even after SHA is recomputed");
            });
        }

        private static void StorageProtectionHandlesBlockBoundaries()
        {
            var protector = new StorageProtector(
                HistoryIntegrityModes.Sha256,
                HistoryEncryptionModes.Aes256CbcHmacSha256,
                "low-copy protection secret",
                Enumerable.Range(121, 32).Select(value => (byte)value).ToArray());
            foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 1024 * 1024 + 3 })
            {
                var plaintext = Enumerable.Range(0, length).Select(value => (byte)(value % 251)).ToArray();
                var stored = protector.Protect(plaintext, "block-boundary");
                AssertEqual(protector.StoredByteLength(plaintext.LongLength), stored.LongLength,
                    "protected envelope length is exact at plaintext size " + length);
                AssertTrue(plaintext.SequenceEqual(protector.Unprotect(stored, "block-boundary")),
                    "protected payload roundtrips at plaintext size " + length);

                byte[] streamed;
                using (var input = new MemoryStream(plaintext, false))
                using (var output = new MemoryStream())
                {
                    protector.ProtectStream(input, output, "block-boundary");
                    streamed = output.ToArray();
                }
                AssertEqual(protector.StoredByteLength(plaintext.LongLength), streamed.LongLength,
                    "streamed protected envelope length is exact at plaintext size " + length);
                AssertTrue(plaintext.SequenceEqual(protector.Unprotect(streamed, "block-boundary")),
                    "byte-array reader accepts streamed ciphertext at plaintext size " + length);
                using (var input = new MemoryStream(stored, false))
                using (var output = new MemoryStream())
                {
                    protector.UnprotectToStream(input, output, "block-boundary");
                    AssertTrue(plaintext.SequenceEqual(output.ToArray()),
                        "stream reader accepts byte-array ciphertext at plaintext size " + length);
                }
            }

            var tampered = protector.Protect(Encoding.UTF8.GetBytes("authenticated"), "tamper-test");
            tampered[tampered.Length - 1] ^= 0x01;
            try
            {
                protector.Unprotect(tampered, "tamper-test");
                throw new InvalidOperationException("tampered protected payload was accepted");
            }
            catch (CryptographicException)
            {
            }
        }

        private static string ComputeUnkeyedEventHash(JObject record)
        {
            var canonical = new JObject
            {
                ["SchemaVersion"] = record["SchemaVersion"],
                ["SessionId"] = record["SessionId"],
                ["Sequence"] = record["Sequence"],
                ["EventId"] = record["EventId"],
                ["CreatedUtc"] = record["CreatedUtc"].Value<DateTime>().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = record["Type"],
                ["RunId"] = record["RunId"] == null ? JValue.CreateNull() : record["RunId"].DeepClone(),
                ["TurnId"] = record["TurnId"] == null ? JValue.CreateNull() : record["TurnId"].DeepClone(),
                ["StepId"] = record["StepId"] == null ? JValue.CreateNull() : record["StepId"].DeepClone(),
                ["PreviousHash"] = record["PreviousHash"] == null ? JValue.CreateNull() : record["PreviousHash"].DeepClone(),
                ["HashAlgorithm"] = record["HashAlgorithm"],
                ["ProtectionKeyId"] = record["ProtectionKeyId"] == null ? JValue.CreateNull() : record["ProtectionKeyId"].DeepClone(),
                ["Data"] = record["Data"] == null ? JValue.CreateNull() : record["Data"].DeepClone(),
                ["EncryptedData"] = record["EncryptedData"] == null ? JValue.CreateNull() : record["EncryptedData"].DeepClone(),
                ["Payload"] = record["Payload"] == null ? JValue.CreateNull() : record["Payload"].DeepClone()
            };
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(canonical.ToString(Newtonsoft.Json.Formatting.None));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool ContainsBytes(byte[] source, byte[] value)
        {
            if (source == null || value == null || value.Length == 0 || source.Length < value.Length) return false;
            for (var offset = 0; offset <= source.Length - value.Length; offset++)
            {
                var matches = true;
                for (var index = 0; index < value.Length; index++)
                {
                    if (source[offset + index] == value[index]) continue;
                    matches = false;
                    break;
                }
                if (matches) return true;
            }
            return false;
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

        private static void UnchangedArtifactsSkipCasExternalization()
        {
            WithTempPaths(paths =>
            {
                const string original = "{\"goal\":\"reuse known artifact body\"}";
                const string changed = "{\"goal\":\"changed artifact body\"}";
                var store = new ChatStore(paths);
                var session = store.Create("Word", "cas-fast-skip", "CAS.docx", "CAS skip");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.PlanDocument,
                    MimeType = "application/json",
                    InlineText = original
                };
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
                store.Save(session);
                AssertEqual(1L, store.ArtifactCasExternalizationCount, "new body enters CAS once");

                session.Title = "Metadata only";
                store.Save(session);
                AssertEqual(1L, store.ArtifactCasExternalizationCount,
                    "metadata-only save skips unchanged trusted artifact text");

                artifact.InlineText = new string(original.ToCharArray());
                store.Save(session);
                AssertEqual(1L, store.ArtifactCasExternalizationCount,
                    "equal text remains reusable even when its string instance changes");

                artifact.InlineText = changed;
                store.Save(session);
                AssertEqual(2L, store.ArtifactCasExternalizationCount, "changed text is externalized");
                var blobPath = Path.Combine(paths.ChatBlobDirectory,
                    artifact.ContentSha256.Substring(0, 2), artifact.ContentSha256 + ".blob");
                File.Delete(blobPath);
                store.Save(session);
                AssertEqual(3L, store.ArtifactCasExternalizationCount, "missing known blob falls back to StoreText");
                AssertTrue(File.Exists(blobPath), "fallback restores the missing CAS body");

                var reloadedStore = new ChatStore(paths);
                var loaded = reloadedStore.Load(session.Id);
                AssertEqual(changed, loaded.Artifacts.Single(item => item.Id == artifact.Id).InlineText,
                    "load verifies and remembers the trusted body");
                loaded.Title = "Loaded metadata only";
                reloadedStore.Save(loaded);
                AssertEqual(0L, reloadedStore.ArtifactCasExternalizationCount,
                    "verified loaded body also takes the fast skip path");
            });
        }

        private static void CasCompressionIsTransparent()
        {
            var body = "{\"value\":\"" + new string('ж', 32 * 1024) + "\"}";
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "cas-compression", "Compression.docx", "Compression");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    MimeType = "application/json",
                    InlineText = body
                };
                session.Artifacts.Add(artifact);
                store.Save(session);

                var blobPath = Path.Combine(paths.ChatBlobDirectory,
                    artifact.ContentSha256.Substring(0, 2), artifact.ContentSha256 + ".blob");
                var stored = File.ReadAllBytes(blobPath);
                AssertEqual("RNACAS01", Encoding.ASCII.GetString(stored, 0, 8),
                    "compressible plaintext CAS uses the versioned envelope");
                AssertTrue(stored.LongLength < artifact.ContentByteLength.Value / 4,
                    "repetitive JSON consumes substantially fewer stored bytes");
                AssertEqual(body, store.Load(session.Id).Artifacts.Single().InlineText,
                    "compressed plaintext artifact roundtrips exactly");

                session.Title = "Metadata only";
                store.Save(session);
                AssertEqual(1L, store.ArtifactCasExternalizationCount,
                    "compressed blob header preserves the unchanged-artifact fast skip");
                var health = CasService(paths, store, new VbaJournalStore(paths), () => StorageProtector.None).Audit();
                AssertEqual(0, health.CorruptBlobCount, "CAS audit verifies compressed plaintext");

                stored[stored.Length - 1] ^= 0x01;
                File.WriteAllBytes(blobPath, stored);
                AssertTrue(store.Load(session.Id).Artifacts.Single().InlineText == null,
                    "corrupt compressed content is never hydrated");
                health = CasService(paths, store, new VbaJournalStore(paths), () => StorageProtector.None).Audit();
                AssertEqual(1, health.CorruptBlobCount, "CAS audit detects compressed payload corruption");
            });

            WithTempPaths(paths =>
            {
                var protector = new StorageProtector(
                    HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "compressed CAS secret",
                    Enumerable.Range(91, 32).Select(value => (byte)value).ToArray());
                Func<StorageProtector> protection = () => protector;
                var store = new ChatStore(paths, protection);
                var session = store.Create("Excel", "cas-compression-protected", "Compression.xlsx", "Compression");
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    MimeType = "application/json",
                    InlineText = body
                };
                session.Artifacts.Add(artifact);
                store.Save(session);

                var blobPath = Path.Combine(paths.ChatBlobDirectory,
                    artifact.ContentSha256.Substring(0, 2), artifact.ContentSha256 + ".blob");
                var stored = File.ReadAllBytes(blobPath);
                AssertTrue(StorageProtector.IsProtectedPayload(stored),
                    "compression envelope is encrypted inside the authenticated CAS payload");
                AssertTrue(stored.LongLength < artifact.ContentByteLength.Value / 4,
                    "compression remains effective with history encryption enabled");
                AssertEqual(body, store.Load(session.Id).Artifacts.Single().InlineText,
                    "compressed encrypted artifact roundtrips exactly");

                session.Title = "Protected metadata only";
                store.Save(session);
                AssertEqual(1L, store.ArtifactCasExternalizationCount,
                    "encrypted compressed blob keeps the trusted-reference fast skip");
                var health = CasService(
                    paths,
                    store,
                    new VbaJournalStore(paths, protection),
                    protection).Audit();
                AssertEqual(0, health.CorruptBlobCount, "CAS audit verifies compressed ciphertext");
            });
        }

        private static void FileCasStreamsHashCompressionAndEncryption()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"rows\":\"" + new string('ж', 256 * 1024) + "\"}");
            WithTempPaths(paths =>
            {
                var sourcePath = Path.Combine(paths.Root, "stream-source.json");
                File.WriteAllBytes(sourcePath, bytes);
                var blobs = new ChatBlobStore(paths);
                var reference = blobs.StoreFile(sourcePath, "application/json");
                var blobPath = blobs.PathFor(reference.Sha256);

                AssertEqual((long)bytes.Length, reference.ByteLength, "streamed file reference keeps plaintext length");
                AssertTrue(blobs.HasVerifiedReference(reference), "streaming verifier accepts streamed plaintext CAS");
                AssertTrue(bytes.SequenceEqual(blobs.ReadBytes(reference)),
                    "byte-array reader accepts streamed compressed CAS");
                AssertEqual("RNACAS01", Encoding.ASCII.GetString(File.ReadAllBytes(blobPath), 0, 8),
                    "streamed compressible file uses the existing CAS envelope");
                var arrayReference = blobs.StoreBytes(bytes, "application/json");
                AssertEqual(reference.Sha256, arrayReference.Sha256,
                    "streamed and byte-array writers retain identical CAS identity");
                AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length,
                    "streaming plaintext write leaves no temporary files");
            });

            WithTempPaths(paths =>
            {
                var protector = new StorageProtector(
                    HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "streaming CAS secret",
                    Enumerable.Range(121, 32).Select(value => (byte)value).ToArray());
                Func<StorageProtector> protection = () => protector;
                var sourcePath = Path.Combine(paths.Root, "stream-protected.json");
                File.WriteAllBytes(sourcePath, bytes);
                var blobs = new ChatBlobStore(paths, protection);
                var reference = blobs.StoreFile(sourcePath, "application/json");
                var blobPath = blobs.PathFor(reference.Sha256);
                var stored = File.ReadAllBytes(blobPath);

                AssertTrue(StorageProtector.IsProtectedPayload(stored),
                    "streamed file uses the existing authenticated encryption envelope");
                AssertTrue(blobs.HasVerifiedReference(reference),
                    "streaming verifier authenticates and hashes streamed ciphertext");
                AssertTrue(bytes.SequenceEqual(blobs.ReadBytes(reference)),
                    "byte-array reader decrypts streamed file ciphertext");

                var arrayBytes = Encoding.UTF8.GetBytes("array-to-stream-verifier");
                var arrayReference = blobs.StoreBytes(arrayBytes, "text/plain");
                AssertTrue(blobs.HasVerifiedReference(arrayReference),
                    "streaming verifier accepts byte-array writer ciphertext");

                stored[stored.Length - 1] ^= 0x01;
                File.WriteAllBytes(blobPath, stored);
                AssertTrue(!blobs.HasVerifiedReference(reference),
                    "streaming verifier rejects an invalid authentication tag");
                AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length,
                    "streaming encrypted verification leaves no plaintext temporary files");
            });
        }

        private static void PlaintextCasAcceptsEnvelopePrefix()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "cas-magic", "Magic.docx", "Magic");
                session.Artifacts.Add(new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    MimeType = "text/markdown",
                    InlineText = "RNAENC01-plain-cas-body"
                });
                session.Artifacts.Add(new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Markdown,
                    MimeType = "text/markdown",
                    InlineText = "RNACAS01-plain-cas-body"
                });

                store.Save(session);

                var bodies = store.Load(session.Id).Artifacts.Select(item => item.InlineText).ToList();
                AssertTrue(bodies.Contains("RNAENC01-plain-cas-body"),
                    "plaintext CAS body is not mistaken for an encrypted envelope");
                AssertTrue(bodies.Contains("RNACAS01-plain-cas-body"),
                    "plaintext CAS body is not mistaken for a compression envelope");
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

                AssertEqual(3L, trace.Sequence, "request follows first-class step start");
                AssertEqual(requestBody, store.ReadEventPayload(trace), "exact request payload roundtrip");
                var responseBody = "{\"choices\":[{\"message\":{\"content\":\"done\"}}]}";
                var response = store.AppendTrace(session, SessionEventTypes.LlmResponse,
                    new { requestId = "request-1", purpose = "agent", model = "test", statusCode = 200 },
                    responseBody, "application/json", "run-1", "run-1", "request-1");
                AssertEqual(4L, response.Sequence, "response precedes first-class step end");
                session.Title = "Saved after trace";
                store.Save(session);
                AssertEqual("Saved after trace", store.Load(session.Id).Title, "state commit follows trace revision");
                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(SessionEventTypes.StepStarted, events[1].Type, "step start shares session stream");
                AssertEqual(SessionEventTypes.LlmRequest, events[2].Type, "request shares session stream");
                AssertEqual(SessionEventTypes.LlmResponse, events[3].Type, "response shares session stream");
                AssertEqual(SessionEventTypes.StepEnded, events[4].Type, "step end shares session stream");
                AssertEqual("request-1", events[4].StepId, "step correlation survives terminal event");

                var raw = File.ReadAllBytes(SessionEventFile(paths, session));
                long expectedOffset = 0;
                foreach (var sessionEvent in events)
                {
                    AssertEqual(expectedOffset, sessionEvent.StorageByteOffset,
                        "replayed event exposes its exact byte offset");
                    var lineEnd = Array.IndexOf(raw, (byte)'\n', checked((int)expectedOffset));
                    AssertTrue(lineEnd >= 0, "every durable event has a terminator");
                    expectedOffset = lineEnd + 1L;
                }
                AssertEqual(raw.LongLength, expectedOffset, "event offsets cover the complete stream");
                AssertEqual(events.Last().StorageByteOffset, session.StorageTailByteOffset,
                    "session caches the exact tail offset");
            });
        }

        private static void TurnLifecycleIsFirstClass()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.CreateTransient("Word", "turn-doc", "Turn.docx", "Turn");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-1",
                    TurnId = "turn-1",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    Status = "running",
                    Phase = "starting",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);

                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(SessionEventTypes.SessionCreated, events[0].Type, "session seed precedes turn");
                AssertEqual(SessionEventTypes.TurnStarted, events[1].Type, "turn start is first-class");
                AssertEqual("turn-1", events[1].TurnId, "turn id is independent from run id");
                AssertEqual(AgentResponseProtocol.CurrentVersion,
                    (int)events[1].Data["ResponseProtocolVersion"],
                    "turn start records the response protocol version");

                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-2",
                    TurnId = "turn-1",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    Status = "running",
                    Phase = "executing",
                    StartedUtc = session.LastRun.StartedUtc
                };
                store.Save(session);
                events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(1, events.Count(item => item.Type == SessionEventTypes.TurnStarted),
                    "confirmation continuation does not open another logical turn");
                AssertEqual(0, events.Count(item => item.Type == SessionEventTypes.TurnEnded),
                    "confirmation continuation keeps logical turn open");

                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Нужны дополнительные данные.",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    ResponseStatus = AgentResponseStatuses.AwaitingUser,
                    RunId = "run-2"
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Подготавливаю старый ответ без метаданных.",
                    RunId = "legacy-run"
                });
                session.LastRun.Status = AgentResponseStatuses.AwaitingUser;
                session.LastRun.Phase = AgentResponseStatuses.AwaitingUser;
                store.Save(session);
                events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(SessionEventTypes.TurnEnded, events.Last().Type, "terminal run closes turn");
                AssertEqual(AgentResponseStatuses.AwaitingUser, (string)events.Last().Data["Status"],
                    "declared terminal status is recorded without inspecting message text");
                AssertEqual(AgentResponseProtocol.CurrentVersion,
                    (int)events.Last().Data["ResponseProtocolVersion"],
                    "turn end records the response protocol version");
                AssertEqual("run-2", events.Last().RunId, "turn end records the final continuation run");
                AssertEqual(events.Last().Sequence, session.Revision, "turn event advances canonical revision");

                var loaded = store.Load(session.Id);
                var current = loaded.Messages.Single(item => item.RunId == "run-2");
                AssertEqual(AgentResponseProtocol.CurrentVersion, current.ResponseProtocolVersion,
                    "accepted response protocol version replays with the assistant message");
                AssertEqual(AgentResponseStatuses.AwaitingUser, current.ResponseStatus,
                    "accepted response status replays with the assistant message");
                var legacy = loaded.Messages.Single(item => item.RunId == "legacy-run");
                AssertEqual(0, legacy.ResponseProtocolVersion,
                    "legacy messages remain explicitly unversioned");
                AssertTrue(string.IsNullOrWhiteSpace(legacy.ResponseStatus),
                    "legacy prose is not backfilled with an inferred status");
                AssertEqual(AgentResponseProtocol.CurrentVersion, loaded.LastRun.ResponseProtocolVersion,
                    "run response protocol version replays");
                AssertEqual(AgentResponseStatuses.AwaitingUser, loaded.LastRun.Status,
                    "declared terminal run status replays");

                loaded.LastRun = new ChatRunRecord
                {
                    RunId = "run-3",
                    TurnId = "turn-3",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    Status = "running",
                    Phase = "executing",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(loaded);
                loaded.LastRun.Status = "failed";
                loaded.LastRun.Phase = "failed";
                store.Save(loaded);
                var failedTurn = store.ReadEvents(loaded.Host, loaded.DocumentKey, loaded.Id).Last();
                AssertEqual(SessionEventTypes.TurnEnded, failedTurn.Type,
                    "runtime-owned failure still closes a later turn");
                AssertEqual("failed", (string)failedTurn.Data["Status"],
                    "runtime-owned terminal status remains durable");
            });
        }

        private static void InterruptedStepGetsSyntheticEnd()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.CreateTransient("Excel", "open-step", "Open.xlsx", "Open step");
                session.LastRun = new ChatRunRecord
                {
                    RunId = "run-open",
                    TurnId = "turn-open",
                    Status = "running",
                    StartedUtc = DateTime.UtcNow
                };
                store.Save(session);
                store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new { requestId = "request-open", purpose = "agent" },
                    "{\"model\":\"test\"}", "application/json",
                    "run-open", "turn-open", "request-open");

                AssertEqual(1, store.CloseOpenSteps(session, "run-open", "interrupted", "runtime stopped"),
                    "one open model step is closed");
                AssertEqual(0, store.CloseOpenSteps(session, "run-open", "interrupted", "runtime stopped"),
                    "synthetic close is idempotent");
                var ended = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last();
                AssertEqual(SessionEventTypes.StepEnded, ended.Type, "synthetic step end is durable");
                AssertTrue((bool)ended.Data["Synthetic"], "synthetic marker is explicit");
                AssertEqual("request-open", ended.StepId, "open step correlation preserved");
            });
        }

        private static void StreamingFramesAreBufferedAsExactChunks()
        {
            var records = new List<LlmTraceRecord>();
            var options = new LlmRequestOptions
            {
                TracePurpose = "harness",
                TraceSink = records.Add
            };
            var buffer = new LlmStreamTraceBuffer(
                options, "request-stream", "https://example.invalid/v1/chat/completions",
                "test-model", LlmResponseFormats.Text, 2, 42, 1024 * 1024);
            var first = "{\"choices\":[{\"delta\":{\"content\":\"one\"}}]}";
            var second = "{\"choices\":[{\"delta\":{\"content\":\"two\"}}]}";
            buffer.Add(first);
            buffer.Add(second);
            buffer.Flush(true);

            AssertEqual(1, records.Count, "small stream frames flush as one bounded event");
            AssertEqual("chunk", records[0].Type, "stream trace type");
            AssertEqual(0, records[0].ChunkIndex.Value, "chunk starts at first provider frame");
            AssertEqual(2, records[0].ChunkCount.Value, "chunk records provider frame count");
            AssertTrue(records[0].Completed.Value, "final chunk marks completed buffer");
            var payload = JArray.Parse(records[0].PayloadJson);
            AssertEqual(first, (string)payload[0], "first provider frame remains exact");
            AssertEqual(second, (string)payload[1], "second provider frame remains exact");
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
            AssertTrue(trace.PayloadUtf8Bytes != null && trace.PayloadUtf8Bytes.Length > 0,
                "final request is materialized as reusable UTF-8 bytes");
            AssertTrue(!trace.PayloadTextMaterialized,
                "request trace does not eagerly allocate a duplicate UTF-16 payload");
            var payload = JObject.Parse(trace.PayloadJson);
            AssertTrue(trace.PayloadUtf8Bytes.SequenceEqual(Encoding.UTF8.GetBytes(trace.PayloadJson)),
                "trace bytes are exactly the JSON payload exposed to observers");
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

        private static void UnterminatedValidEventTailRecovers()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "tail-valid", "Tail.docx", "Before valid tail");
                var path = SessionEventFile(paths, session);
                File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\r', '\n'), new UTF8Encoding(false));

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual("Before valid tail", loaded.Title, "unterminated valid record remains readable");
                loaded.Title = "After valid tail";
                store.Save(loaded);

                var text = File.ReadAllText(path);
                AssertTrue(text.EndsWith("\n", StringComparison.Ordinal), "recovered stream ends with a line terminator");
                AssertEqual(2, File.ReadAllLines(path).Length, "next commit does not concatenate JSON objects");
                AssertEqual("After valid tail", store.Load(loaded.Id).Title, "recovered valid tail replays");

                var journal = new VbaJournalStore(paths);
                journal.Save("Word", "tail-valid-vba", "Tail.docx", "Module1", "StdModule", "Sub One()\nEnd Sub");
                var journalPath = Path.Combine(
                    paths.VbaJournalDirectory,
                    AppDataPaths.SafeFileName("Word|tail-valid-vba"),
                    "mutations.events.jsonl");
                File.WriteAllText(journalPath, File.ReadAllText(journalPath).TrimEnd('\r', '\n'), new UTF8Encoding(false));
                AssertEqual(1, journal.List("Word", "tail-valid-vba").Count,
                    "unterminated valid VBA record remains readable");
                journal.Save("Word", "tail-valid-vba", "Tail.docx", "Module2", "StdModule", "Sub Two()\nEnd Sub");
                AssertEqual(2, File.ReadAllLines(journalPath).Length,
                    "next VBA append does not concatenate JSON objects");
                AssertEqual(2, journal.List("Word", "tail-valid-vba").Count,
                    "recovered VBA tail replays");
            });
        }

        private static void TerminatedCorruptTailsAreRejected()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "tail-corrupt", "Tail.docx", "Before corruption");
                File.AppendAllText(SessionEventFile(paths, session), "{not-json}\n");
                AssertTrue(store.Load(session.Id) == null, "terminated corrupt chat stream is not projected");
                var chatRejected = false;
                try
                {
                    session.Title = "Must not overwrite corruption";
                    store.Save(session);
                }
                catch (ChatConcurrencyException)
                {
                    chatRejected = true;
                }
                AssertTrue(chatRejected, "terminated corrupt chat record is rejected");

                var journal = new VbaJournalStore(paths);
                journal.Save("Word", "tail-corrupt", "Tail.docx", "Module1", "StdModule", "Sub Before()\nEnd Sub");
                var journalPath = Path.Combine(
                    paths.VbaJournalDirectory,
                    AppDataPaths.SafeFileName("Word|tail-corrupt"),
                    "mutations.events.jsonl");
                File.AppendAllText(journalPath, "{not-json}\n");
                var journalRejected = false;
                try
                {
                    journal.List("Word", "tail-corrupt");
                }
                catch (VbaJournalException)
                {
                    journalRejected = true;
                }
                AssertTrue(journalRejected, "terminated corrupt VBA journal record is rejected");
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
                    Kind = ChatArtifactKinds.PlanDocument,
                    MimeType = "application/json",
                    InlineText = "{\"goal\":\"safe\"}"
                };
                session.Artifacts.Add(artifact);
                session.ActivePlanDocumentArtifactId = artifact.Id;
                store.Save(session);
                var blob = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Single();
                File.WriteAllText(blob, "corrupt");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertTrue(loaded != null, "metadata projection remains available");
                AssertTrue(string.IsNullOrWhiteSpace(loaded.Artifacts.Single().InlineText),
                    "hash mismatch does not hydrate corrupted content");

                var repair = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.PlanDocument,
                    MimeType = "application/json",
                    InlineText = "{\"goal\":\"safe\"}"
                };
                loaded.Artifacts.Add(repair);
                loaded.ActivePlanDocumentArtifactId = repair.Id;
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
                AssertEqual(secondId, loaded.HtmlWorkspace.RedoBranches.Single().Id, "redo points to direct child artifact");
                store.Save(loaded);

                loaded = store.Load(loaded.Id);
                AssertEqual("version one", loaded.HtmlWorkspace.Files.Single().Content, "undo survives replay");
                AssertTrue(store.LoadArtifactBody(loaded, secondId), "redo artifact body loads lazily");
                HtmlArtifactToolExecutor.RedoSnapshot(loaded, secondId);
                AssertEqual("version two", loaded.HtmlWorkspace.Files.Single().Content, "redo activates child artifact");
                AssertEqual(2, loaded.Artifacts.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace),
                    "undo and redo do not duplicate revisions");
            });
        }

        private static void HtmlRecoveryBlocksMutationAndSelectsHealthyRevision()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "html-recovery", "Recovery.docx", "Recovery");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "healthy root", true);
                store.Save(session);
                var rootId = session.ActiveHtmlArtifactId;
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "broken active", true);
                store.Save(session);
                var brokenId = session.ActiveHtmlArtifactId;
                var brokenArtifact = session.Artifacts.Single(item => item.Id == brokenId);
                var brokenBlob = Path.Combine(
                    paths.ChatBlobDirectory,
                    brokenArtifact.ContentSha256.Substring(0, 2),
                    brokenArtifact.ContentSha256 + ".blob");
                File.WriteAllText(brokenBlob, "corrupt");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(HtmlWorkspaceRecoveryStatuses.Degraded, loaded.HtmlWorkspaceRecovery.Status,
                    "corrupt active revision enters recovery");
                AssertTrue(!loaded.HtmlWorkspaceRecovery.CanMutate, "corrupt active revision blocks mutation");
                AssertEqual(HtmlWorkspaceRecoveryIssues.ActiveBodyUnavailable, loaded.HtmlWorkspaceRecovery.Issue,
                    "recovery identifies unavailable active body");
                AssertEqual(0, loaded.HtmlWorkspace.Files.Count, "corrupt active body is not projected as editable content");
                AssertTrue(loaded.HtmlWorkspaceRecovery.Candidates.Any(item => item.Id == rootId),
                    "healthy ancestor is offered without eagerly loading its body");

                var blocked = false;
                try
                {
                    HtmlArtifactToolExecutor.UpsertFile(loaded, "index.html", "html", "must not write", true);
                }
                catch (InvalidOperationException)
                {
                    blocked = true;
                }
                AssertTrue(blocked, "direct HTML mutation fails closed during recovery");

                var artifactCount = loaded.Artifacts.Count;
                loaded.Messages.Add(new ChatMessage { Role = "user", Content = "chat still works" });
                store.Save(loaded);
                loaded = store.Load(loaded.Id);
                AssertEqual(brokenId, loaded.ActiveHtmlArtifactId, "unrelated save preserves broken active pointer");
                AssertEqual(artifactCount, loaded.Artifacts.Count, "unrelated save does not create an empty HTML branch");
                AssertTrue(!loaded.HtmlWorkspaceRecovery.CanMutate, "recovery survives unrelated session commits");

                string error;
                AssertTrue(!store.TryActivateHtmlWorkspaceRevision(loaded, brokenId, out error),
                    "corrupt recovery candidate is rejected");
                AssertEqual(brokenId, loaded.ActiveHtmlArtifactId, "failed selection does not move the active pointer");
                AssertTrue(store.TryActivateHtmlWorkspaceRevision(loaded, rootId, out error),
                    "explicit healthy revision activates");
                AssertTrue(string.IsNullOrWhiteSpace(error), "successful recovery has no error");
                AssertEqual("healthy root", loaded.HtmlWorkspace.Files.Single().Content, "healthy revision body is restored");
                AssertEqual(HtmlWorkspaceRecoveryStatuses.Healthy, loaded.HtmlWorkspaceRecovery.Status,
                    "successful selection clears recovery block");
                store.Save(loaded);

                loaded = store.Load(loaded.Id);
                AssertEqual(rootId, loaded.ActiveHtmlArtifactId, "recovered active revision survives replay");
                AssertEqual("healthy root", loaded.HtmlWorkspace.Files.Single().Content, "recovered body survives replay");
            });
        }

        private static void HtmlRecoveryKeepsReadableActiveWithBrokenParent()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "html-parent-recovery", "Parent.docx", "Parent recovery");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "root", true);
                store.Save(session);
                var rootId = session.ActiveHtmlArtifactId;
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "readable child", true);
                store.Save(session);
                session.Artifacts.RemoveAll(item => item != null && item.Id == rootId);
                store.Save(session);

                var loaded = store.Load(session.Id);
                AssertEqual("readable child", loaded.HtmlWorkspace.Files.Single().Content,
                    "readable active revision remains available");
                AssertEqual(HtmlWorkspaceRecoveryStatuses.Degraded, loaded.HtmlWorkspaceRecovery.Status,
                    "broken parent degrades navigation");
                AssertEqual(HtmlWorkspaceRecoveryIssues.ParentArtifactMissing, loaded.HtmlWorkspaceRecovery.Issue,
                    "broken parent is classified");
                AssertTrue(loaded.HtmlWorkspaceRecovery.CanMutate,
                    "readable active revision remains mutable despite truncated ancestry");
                AssertEqual(0, loaded.HtmlWorkspace.History.Count, "undo stops before missing parent");

                HtmlArtifactToolExecutor.UpsertFile(loaded, "index.html", "html", "new child", true);
                store.Save(loaded);
                AssertEqual("new child", store.Load(loaded.Id).HtmlWorkspace.Files.Single().Content,
                    "new revision can extend a readable degraded branch");
            });
        }

        private static void HtmlRedoBranchesAreExplicitAndLazy()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "html-branches", "Branches.docx", "Branches");
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "root", true);
                store.Save(session);
                var rootId = session.ActiveHtmlArtifactId;

                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch A", true);
                store.Save(session);
                var branchAId = session.ActiveHtmlArtifactId;
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch A child", true);
                store.Save(session);
                var descendantId = session.ActiveHtmlArtifactId;

                HtmlArtifactToolExecutor.RestoreSnapshot(session, rootId);
                store.Save(session);
                HtmlArtifactToolExecutor.UpsertFile(session, "index.html", "html", "branch B", true);
                store.Save(session);
                var branchBId = session.ActiveHtmlArtifactId;
                HtmlArtifactToolExecutor.RestoreSnapshot(session, rootId);
                store.Save(session);

                File.AppendAllText(SessionEventFile(paths, session), "{\"SchemaVersion\":");

                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(rootId, loaded.ActiveHtmlArtifactId, "branch point survives replay");
                AssertEqual(2, loaded.HtmlWorkspace.RedoBranches.Count, "both direct redo branches are exposed");
                AssertTrue(loaded.HtmlWorkspace.RedoBranches.All(branch => branch.ParentArtifactId == rootId),
                    "redo choices are direct children");
                AssertTrue(loaded.HtmlWorkspace.RedoBranches.Any(branch => branch.Id == branchAId) &&
                    loaded.HtmlWorkspace.RedoBranches.Any(branch => branch.Id == branchBId),
                    "redo choices preserve both branches");
                AssertTrue(loaded.Artifacts.Where(artifact => artifact.ParentArtifactId == rootId)
                    .All(artifact => artifact.InlineText == null),
                    "redo branch bodies remain lazy after replay");

                var transport = HtmlWorkspaceDto.From(loaded.HtmlWorkspace, loaded.HtmlWorkspaceRecovery);
                AssertEqual(2, transport.RedoBranches.Count, "bridge exposes branch metadata");
                AssertTrue(transport.RedoBranches.All(branch => branch.Revision > 1),
                    "bridge exposes revision numbers");
                AssertEqual(HtmlWorkspaceRecoveryStatuses.Healthy, transport.Recovery.Status,
                    "bridge exposes derived recovery state");

                var ambiguousRejected = false;
                try
                {
                    HtmlArtifactToolExecutor.RedoSnapshot(loaded, null);
                }
                catch (InvalidOperationException ex)
                {
                    ambiguousRejected = ex.Message.IndexOf("multiple branches", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                AssertTrue(ambiguousRejected, "redo without id rejects an ambiguous branch point");

                var descendantRejected = false;
                try
                {
                    HtmlArtifactToolExecutor.RedoSnapshot(loaded, descendantId);
                }
                catch (InvalidOperationException)
                {
                    descendantRejected = true;
                }
                AssertTrue(descendantRejected, "redo cannot jump over a direct child");

                AssertTrue(store.LoadArtifactBody(loaded, branchAId), "selected branch body loads on demand");
                HtmlArtifactToolExecutor.RedoSnapshot(loaded, branchAId);
                AssertEqual("branch A", loaded.HtmlWorkspace.Files.Single().Content, "explicit branch redo succeeds");
                AssertEqual(descendantId, loaded.HtmlWorkspace.RedoBranches.Single().Id,
                    "next direct child becomes the only redo choice");
                AssertTrue(store.LoadArtifactBody(loaded, descendantId), "next branch body loads on demand");
                HtmlArtifactToolExecutor.RedoSnapshot(loaded, null);
                AssertEqual("branch A child", loaded.HtmlWorkspace.Files.Single().Content,
                    "redo without id succeeds for exactly one child");
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
                    ResourceRefs = new System.Collections.Generic.List<ResourceRef> { ArtifactReference(session, artifact) }
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

        private static void ReplaceFileBytes(string path, string expected, string replacement)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
            var replacementBytes = Encoding.UTF8.GetBytes(replacement ?? string.Empty);
            if (expectedBytes.Length == 0 || expectedBytes.Length != replacementBytes.Length)
            {
                throw new InvalidOperationException("Byte-preserving replacement requires equal non-empty UTF-8 lengths.");
            }
            var bytes = File.ReadAllBytes(path);
            var offset = -1;
            for (var index = 0; index <= bytes.Length - expectedBytes.Length; index++)
            {
                var matches = true;
                for (var inner = 0; inner < expectedBytes.Length; inner++)
                {
                    if (bytes[index + inner] == expectedBytes[inner]) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                offset = index;
                break;
            }
            if (offset < 0) throw new InvalidOperationException("Expected byte sequence was not found.");
            Buffer.BlockCopy(replacementBytes, 0, bytes, offset, replacementBytes.Length);
            File.WriteAllBytes(path, bytes);
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

        private static string ZipEntryText(byte[] bytes, string path)
        {
            using (var stream = new MemoryStream(bytes))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(path);
                if (entry == null) throw new InvalidOperationException("ZIP entry was not found: " + path);
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        private static List<string> ZipEntryNames(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                return archive.Entries.Select(entry => entry.FullName).ToList();
            }
        }
    }
}
