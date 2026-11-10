using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelSnapshotCopiesPreserveIsolation()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(null), "tools", new SkillCatalogSnapshot(null), null, 1);
            var reference = new ResourceRef("rna://file/snapshot/source", "r1");
            var evidence = new ResourceEvidence("read", new ResourceAuthorityScopeId("workspace", "snapshot"),
                reference, "text", ResourceCoverage.Whole(), true, 1,
                new PayloadRef(new string('a', 64), 12, "text/plain"), immutable: true);
            var diagnostic = new PatchDiagnostic { Code = "conflict", Message = "Keep original", Candidates = new[] {
                new PatchCandidate { Line = 2, Context = "original candidate" } } };
            var message = new ChatMessage { Role = "user", Content = "Exact Ω😀 source", ToolCallId = "call",
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call", Name = "core.read", Type = "function", ArgumentsJson = "{}" } },
                AgentCommunication = new AgentCommunicationContext { MessageId = "peer-input", ReplyTo = "request", Assignment = true,
                    Sender = new AgentChatAddress { Source = "workspace", WorkspaceId = "workspace", ChatId = "sender", Title = "Sender" },
                    Root = new AgentChatAddress { Source = "workspace", WorkspaceId = "workspace", ChatId = "root", Title = "Root" } },
                AttachmentAnalysis = new AttachmentAnalysisContext { Content = "analysis", Models = new List<string> { "model" }, AttachmentIds = new List<string> { "attachment" } },
                CompletedOperation = new CompletedToolOperation { ToolCallId = "call", ToolName = "core.read", Status = ToolResultStatus.Ok,
                    DataJson = "{}", Targets = new List<string> { "source" } },
                ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.UnknownAfterDispatch, targets: new[] {
                    new ResourceTargetEffect { Target = "source", Status = ResourceTargetStatus.Unknown, WriteStarted = true,
                        Effect = new ResourceEffect("nested", "write", ResourceEffectOutcome.UnknownAfterDispatch, targets: new[] {
                            new ResourceTargetEffect { Target = "nested source", Status = ResourceTargetStatus.Unknown } }) } }),
                Activity = new ChatActivity { Title = "Parent", Children = new List<ChatActivity> { new ChatActivity {
                    Title = "Child", ResultSummary = new ToolResultSummary { Kind = "read", Complete = true },
                    ReadSummary = new ResourceReadSummary { Representation = "text", Complete = true } } } },
                RunViewState = new RunViewState("run", "turn", "Narrative", RunViewLifecycles.AwaitingConfirmation, RunViewHealth.Clean,
                    0, 0, 0, 0, 0, 0, new PendingConfirmationViewState("pending", "call", "core.patch", new PatchBatchResult {
                        Phase = "prepare", Diagnostic = diagnostic, Targets = new List<PatchTargetResult> { new PatchTargetResult {
                            Target = "source", Diagnostic = diagnostic, Preview = new List<PatchPreview> { new PatchPreview { Before = "old", After = "new" } } } } }),
                    "confirmation", "patch", DateTime.UtcNow) };
            message.Attachments.Add(new ChatAttachment { FileName = "source.txt", ExtractedText = "original text",
                PageTextLengths = new List<int> { 13 }, VisualSelectionSource = reference });
            message.ResourceRefs.Add(reference); message.ResourceEvidence.Add(evidence);
            message.ContextClaims.Add(new StructuredContextClaim { Kind = "observation", Text = "Claim",
                SourceRoles = new List<string> { "tool" }, SourceMessageIds = new List<string> { "source-message" }, Evidence = new List<ResourceEvidence> { evidence },
                SourceSnapshots = new List<ContextClaimSource> { new ContextClaimSource { MessageId = "source-message", Text = "Original", Role = "tool" } } });
            var receipt = new ContextReceipt { SnapshotId = "snapshot", Arsenal = new AgentArsenal {
                Selected = new List<ArsenalSkill> { new ArsenalSkill { Id = "skill", Tools = new List<string> { "core.read" }, References = new List<string> { "guide.md" } } },
                Available = new List<ArsenalSkill> { new ArsenalSkill { Id = "available" } },
                Tools = new List<ArsenalTool> { new ArsenalTool { Id = "core.read", Skills = new List<string> { "skill" } } } },
                OmittedContext = new Dictionary<string, int> { ["Budget"] = 12 }, ResourceGenerations = new Dictionary<string, long> { ["workspace"] = 1 },
                AtomCounts = new Dictionary<string, int> { ["source"] = 1 }, Messages = new List<ContextMessagePresentation> {
                    new ContextMessagePresentation { SourceMessageId = message.Id, MessageIndex = 0, Presentation = ContextPresentationKind.Full,
                        EvidenceLinks = new List<ContextEvidenceLink> { new ContextEvidenceLink { EvidenceId = "read", Resource = reference, BodyIncludedInMessage = true } },
                        Parts = new List<ContextMessagePresentation> { new ContextMessagePresentation { Kind = "text", Presentation = ContextPresentationKind.Full } } } } };
            var expectedMessage = JsonConvert.SerializeObject(message);
            var expectedReceipt = JsonConvert.SerializeObject(receipt);
            var snapshot = new ModelContextSnapshot(authority, new[] { message }, receipt, new[] { evidence });
            AssertSnapshotMutableObjectsDetached(message, snapshot.Messages.Single());
            AssertSnapshotMutableObjectsDetached(receipt, snapshot.Receipt);
            var copy = snapshot.Messages.Single();
            copy.Content = "Changed"; copy.ToolCalls[0].ArgumentsJson = "changed";
            copy.AgentCommunication.Sender.Title = "Changed sender"; copy.AgentCommunication.Root.ChatId = "other root";
            copy.Attachments[0].PageTextLengths.Clear(); copy.AttachmentAnalysis.Models.Clear();
            copy.ContextClaims[0].SourceSnapshots[0].Text = "Changed claim";
            copy.ResourceEffect.Targets[0].Effect.Targets[0].WriteStarted = true;
            copy.CompletedOperation.Targets.Clear(); copy.Activity.Children[0].ReadSummary.Complete = false;
            copy.RunViewState.PendingConfirmation.PatchPreview.Targets[0].Preview[0].After = "changed patch";
            copy.RunViewState.PendingConfirmation.PatchPreview.Diagnostic.Candidates[0].Context = "changed candidate";
            var copiedReceipt = snapshot.Receipt;
            copiedReceipt.Arsenal.Selected[0].Tools.Clear(); copiedReceipt.Arsenal.Tools[0].Skills.Clear();
            copiedReceipt.OmittedContext["Budget"] = 0; copiedReceipt.Messages[0].EvidenceLinks[0].BodyIncludedInMessage = false;
            copiedReceipt.Messages[0].Parts.Clear();
            AssertEqual(expectedMessage, JsonConvert.SerializeObject(snapshot.Messages.Single()), "getter mutation cannot alter frozen messages");
            AssertEqual(expectedReceipt, JsonConvert.SerializeObject(snapshot.Receipt), "getter mutation cannot alter frozen receipt");
            message.Attachments.Clear(); message.Content = "Input changed"; receipt.Messages.Clear();
            AssertEqual(expectedMessage, JsonConvert.SerializeObject(snapshot.Messages.Single()), "constructor inputs are detached");
            AssertEqual(expectedReceipt, JsonConvert.SerializeObject(snapshot.Receipt), "receipt input is detached");
            var repaired = new ModelContextCompiler().CompileRepair(snapshot, new ChatMessage { Role = "user", Content = "Repair format." });
            AssertEqual(expectedMessage, JsonConvert.SerializeObject(repaired[0]), "repair keeps the exact frozen source");
            repaired[0].Attachments.Clear();
            AssertEqual(expectedMessage, JsonConvert.SerializeObject(snapshot.Messages.Single()), "repair cannot mutate later requests");
            AssertEqual("read", snapshot.DeliveredObservations.Single().EvidenceId, "delivery remains exact and independent of presentation mutation");

            var samples = Enumerable.Range(0, 6).Select(index => new ChatMessage { Role = "user", Content = new string('q', 18000) }).ToArray();
            var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            var measured = new ModelContextSnapshot(authority, samples, snapshot.Receipt, snapshot.DeliveredObservations);
            var characters = 0;
            for (var iteration = 0; iteration < 24; iteration++)
            {
                characters += measured.Messages.Sum(item => item.Content.Length);
                AssertEqual(1, measured.Receipt.Messages.Count, "receipt shape is unchanged");
                AssertEqual(1, measured.DeliveredObservations.Count, "delivery shape is unchanged");
            }
            clock.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            AssertEqual(2592000, characters, "same body content on every access");
            Console.WriteLine("model-snapshot messages=6 reads=24 characters=" + characters + " allocated=" + allocated +
                " wallMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
        }

        private static void AssertSnapshotMutableObjectsDetached(object source, object copy)
        {
            if (source == null || copy == null) { AssertTrue(source == null && copy == null, "null shape retained"); return; }
            var type = source.GetType();
            if (type.IsValueType || source is string) return;
            var sourceList = source as IList; var copyList = copy as IList;
            if (sourceList != null && copyList != null)
            {
                if (!sourceList.IsReadOnly) AssertTrue(!ReferenceEquals(source, copy), "mutable collection is detached");
                AssertEqual(sourceList.Count, copyList.Count, "collection count retained");
                for (var i = 0; i < sourceList.Count; i++) AssertSnapshotMutableObjectsDetached(sourceList[i], copyList[i]);
                return;
            }
            var dictionary = source as IDictionary;
            if (dictionary != null)
            {
                AssertTrue(!ReferenceEquals(source, copy), "mutable dictionary is detached");
                foreach (DictionaryEntry entry in dictionary) AssertSnapshotMutableObjectsDetached(entry.Value, ((IDictionary)copy)[entry.Key]);
                return;
            }
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(item => item.GetIndexParameters().Length == 0).ToArray();
            if (properties.Any(property => property.GetSetMethod() != null))
                AssertTrue(!ReferenceEquals(source, copy), "mutable " + type.Name + " is detached");
            foreach (var property in properties)
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                    AssertSnapshotMutableObjectsDetached(property.GetValue(source), property.GetValue(copy));
            }
        }
    }
}
