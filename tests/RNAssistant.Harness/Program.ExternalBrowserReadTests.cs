using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private class HarnessBrowserReadSource : IExternalBrowserReadSource
        {
            internal ExternalBrowserReadTarget Target;
            internal int Captures, ReadThread;
            internal Action DuringRead;
            internal ExternalBrowserSemanticContent Content = new ExternalBrowserSemanticContent {
                ObservationId = Guid.NewGuid().ToString("N"),
                SourceUrl = "https://example.test/form", Title = "Contact form", CapturedUtc = DateTime.UtcNow,
                Nodes = new[] { new ExternalBrowserSemanticNode { Role = "heading", Name = "Local form" },
                    new ExternalBrowserSemanticNode { Role = "button", Name = "Save", Disabled = true } }, Visited = 4 };
            public ExternalBrowserReadTarget TargetFor(string chatId) { return Target?.ChatId == chatId ? Target : null; }
            public Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); Captures++; ReadThread = Thread.CurrentThread.ManagedThreadId;
                AssertTrue(target.Matches(Target), "capture uses the exact runtime binding");
                DuringRead?.Invoke(); Content.ObservationId = Guid.NewGuid().ToString("N"); Content.CapturedUtc = DateTime.UtcNow;
                return Task.FromResult(Content);
            }
        }

        private static void ExternalBrowserSemanticGateway()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var source = new HarnessBrowserReadSource { Target = new ExternalBrowserReadTarget(session.Id, "browser-runtime-1", 1) };
                executor.ExternalBrowserReadSource = source;
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToArray(), new AppSettings(), ChatModes.Agent, false);
                Func<string, object, JObject> execute = (id, arguments) => {
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), id, JsonConvert.SerializeObject(arguments));
                    var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call),
                        "run", "turn", "step", DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, id + ": " + result.Message);
                    AssertTrue(!result.Result.DataJson.Contains("browser-runtime-1") && !result.Result.DataJson.Contains("rna://browser/"), "model projection contains no runtime handles");
                    return JObject.Parse(result.Result.DataJson);
                };
                var found = execute(ResourceToolCatalog.FindToolId, new { scope = "browser" });
                AssertEqual(ExternalBrowserResourceProvider.Target, (string)found["items"].Single()["target"], "one semantic page target");
                AssertEqual(0, source.Captures, "discovery does not capture DOM");
                var thread = Thread.CurrentThread.ManagedThreadId;
                var read = execute(ResourceToolCatalog.ReadToolId, new { target = ExternalBrowserResourceProvider.Target, representation = "text" });
                AssertTrue(((string)read["text"]).Contains("button: Save [disabled]"), "bounded semantic text reaches the model");
                AssertEqual(false, (bool)read["sourceComplete"], "a semantic observation never claims the full page");
                AssertTrue(source.ReadThread != thread, "native asynchronous capture cannot block the calling UI thread");
                AssertEqual(1, source.Captures, "one read performs one capture");
                var uri = ResourceUri.Create("browser", session.Id, source.Target.SessionId);
                var identity = new ResourceIdentity(uri);
                var scope = executor.ResourceAuthority.Scope(session, false);
                var head = executor.ResourceAuthority.Store.GetHead(scope, identity);
                var retained = executor.ResourceAuthority.Revisions.GetView(scope, head.Revision, "text");
                AssertEqual((string)read["text"], executor.Payloads.ReadText(retained.Payload.ToBlobReference()), "existing CAS retains the exact observation");
                source.Content.Nodes[0].Name = "Changed form";
                var next = execute(ResourceToolCatalog.ReadToolId, new { target = ExternalBrowserResourceProvider.Target });
                AssertTrue(((string)next["text"]).Contains("Changed form"), "semantic reads capture again instead of reusing a live-page cache");
                AssertEqual(2, source.Captures, "fresh read reaches the source");
                AssertTrue(head.Revision.Revision != executor.ResourceAuthority.Store.GetHead(scope, identity).Revision.Revision, "changed content changes retained revision");
                var other = NewSession(adapter); other.Id = "other-chat";
                AssertEqual(0, executor.ResourceGateway.Find(other, null, "browser").Items.Count, "another chat cannot discover the page");
            });
        }

        private static void ExternalBrowserReadGuards()
        {
            var session = new ChatSession { Id = "owner" };
            var source = new HarnessBrowserReadSource { Target = new ExternalBrowserReadTarget(session.Id, "page-1", 2) };
            var provider = new ExternalBrowserResourceProvider(() => source);
            Func<ResourceReadRequest> request = () => new ResourceReadRequest { Reference = new ResourceRef(ResourceUri.Create("browser", session.Id, "page-1")), Representation = "text" };
            source.DuringRead = () => source.Target = new ExternalBrowserReadTarget(session.Id, "page-1", 3);
            AssertEqual("browser_document_stale", RuntimeThrows<ResourceRequestException>(() => provider.Read(session, request())).ErrorCode, "navigation during capture discards its body");
            source.DuringRead = () => source.Target = null;
            AssertEqual("browser_document_stale", RuntimeThrows<ResourceRequestException>(() => provider.Read(session, request())).ErrorCode, "close during capture discards its body");
            source.DuringRead = null; source.Target = new ExternalBrowserReadTarget("other", "page-1", 4);
            AssertEqual("browser_page_unavailable", RuntimeThrows<ResourceRequestException>(() => provider.Read(session, request())).ErrorCode, "foreign chat cannot capture");
            source.Target = new ExternalBrowserReadTarget(session.Id, "page-2", 1);
            AssertEqual("browser_page_unavailable", RuntimeThrows<ResourceRequestException>(() => provider.Read(session, request())).ErrorCode, "reopened page cannot satisfy an old binding");
            source.Target = new ExternalBrowserReadTarget(session.Id, "page-1", 5);
            source.Content.Nodes[0].Name = new string('x', ExternalBrowserResourceProvider.MaximumName + 1);
            AssertEqual("browser_capture_invalid", RuntimeThrows<ResourceRequestException>(() => provider.Read(session, request())).ErrorCode, "oversized renderer output is not accepted");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); var before = source.Captures;
                RuntimeThrows<OperationCanceledException>(() => provider.Read(session, request(), cancel.Token));
                AssertEqual(before, source.Captures, "cancelled read never reaches the renderer");
            }
        }

        private static void ExternalBrowserResearchObservations()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var source = new HarnessBrowserReadSource { Target = new ExternalBrowserReadTarget(session.Id, "research", 1) };
                executor.ExternalBrowserReadSource = source;
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToArray(), new AppSettings(), ChatModes.Agent, false);
                Func<string, ToolExecutionRecord> read = target => {
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), ResourceToolCatalog.ReadToolId,
                        JsonConvert.SerializeObject(new { target }));
                    var record = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "research", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, record.Message);
                    return record;
                };
                source.Content.SourceUrl = ExternalBrowserResourceProvider.SourceAddress("https://user:password@example.test/article-a?token=SECRET#PRIVATE");
                AssertEqual("https://example.test/article-a", source.Content.SourceUrl, "source attribution removes credentials, query and fragment");
                source.Content.Title = "Research A"; source.Content.Nodes[0].Name = "Fact from page A";
                var first = read(ExternalBrowserResourceProvider.Target);
                var firstData = JObject.Parse(first.Result.DataJson);
                var savedTarget = (string)firstData["snapshotTarget"];
                AssertTrue(savedTarget.StartsWith(ExternalBrowserResourceProvider.ObservationPrefix), "fresh read returns a semantic retained target");
                AssertTrue(!first.Result.DataJson.Contains("rna://") && !first.Result.DataJson.Contains("SECRET"), "projection excludes handles and URL secrets");
                var evidence = first.ResourceEvidence.Single();
                var scope = executor.ResourceAuthority.Scope(session, false);
                executor.ResourceAuthority.ReportExternalDrift(scope, evidence.Dependencies.Single().Resource.Identity);
                source.Target = new ExternalBrowserReadTarget(session.Id, "research", 2);
                source.Content.SourceUrl = "https://example.test/article-b";
                source.Content.Title = "Research B"; source.Content.Nodes[0].Name = "Fact from page B";
                var second = read(ExternalBrowserResourceProvider.Target);
                AssertTrue(second.ResourceEvidence.Single().Resource.Uri != evidence.Resource.Uri, "each captured observation has an immutable identity");
                var authority = new ModelAuthoritySnapshot(executor.ResourceAuthority.CaptureMany(new[] { scope }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(evidence, authority.Resources).State,
                    "changing the live head preserves the earlier observation");
                foreach (var record in new[] { first, second })
                {
                    var result = ContinuityResult(Guid.NewGuid().ToString("N"), "core.read", record.Result, "user");
                    result.ResourceEvidence = record.ResourceEvidence.ToList();
                    session.Messages.Add(ContinuityCall(result)); session.Messages.Add(result);
                }
                var compiler = new ModelContextCompiler(executor.Payloads, projection: ModelToolResultProjection.Instance);
                Func<System.Collections.Generic.IReadOnlyList<ChatMessage>, ContextWorkingSet, string> compile = (active, working) =>
                    string.Join("\n", compiler.Compile(authority, new ChatMessage[0], active, null,
                        new ToolCatalogEntry[0], new AppSettings(), 10000, workingSet: working).Messages.Select(message => message.Content));
                var model = compile(session.Messages, null);
                AssertContains(model, "Fact from page A", "page A reaches the model after navigation");
                AssertContains(model, "Fact from page B", "page B reaches the same model request");
                AssertContains(model, "article-a", "the original page source reaches the model");
                SetContinuityCheckpoint(session, session.Messages.Last());
                var compacted = compile(new ChatMessage[0], ContextWorkingSet.Restore(session, new ChatMessage[0], 6000));
                AssertContains(compacted, "Fact from page A", "archived research survives context compaction");
                AssertContains(compacted, "Fact from page B", "archived observations retain separate facts");
                source.Target = null;
                AssertEqual((string)firstData["text"], (string)JObject.Parse(read(savedTarget).Result.DataJson)["text"],
                    "closing the Browser leaves exact captured facts readable");
                AssertEqual(2, source.Captures, "historical reading never recaptures the live page");
                var store = new ResourceAuthorityStore(FixturePaths.Value);
                var restartedAuthority = new ResourceAuthorityService(store, store, payloads: executor.Payloads);
                var provider = new ExternalBrowserResourceProvider(() => null, authority: restartedAuthority);
                var restored = provider.ResolveSemanticTarget(new ChatSession { Id = session.Id }, savedTarget);
                AssertEqual(evidence.Resource.Uri, restored.Reference.Uri, "discovery after restart uses existing persisted authority");
                AssertEqual(2, provider.List(session, ExternalBrowserResourceProvider.ObservationKind, null, 10).Total, "bounded discovery exposes both sources after close");
                AssertEqual(1, provider.Search(session, "article-a", ExternalBrowserResourceProvider.ObservationKind, 10, 100).Matches.Count,
                    "source address is searchable without opening the page");
                var foreign = new ChatSession { Id = "foreign" };
                RuntimeThrows<ResourceRequestException>(() => provider.ResolveSemanticTarget(foreign, savedTarget));
                System.IO.File.Delete(executor.Payloads.PathFor(evidence.Payload.Sha256));
                RuntimeThrows<ResourceRequestException>(() => provider.Read(session, new ResourceReadRequest {
                    Reference = restored.Reference, Representation = "text" }));
                AssertEqual(2, source.Captures, "missing captured bytes never fall back to a live page");
            });
        }
    }
}
