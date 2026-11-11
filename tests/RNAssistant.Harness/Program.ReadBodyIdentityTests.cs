using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ContextReadBodyInvalidatesChangedInputs()
        {
            var scope = new ResourceAuthorityScopeId("document", "body-identity");
            var resource = new ResourceRef("rna://file/body-identity/source", "r1");
            var evidence = new ResourceEvidence("read", scope, resource, "text", ResourceCoverage.Whole(), true, 1);
            var message = ContinuityResult("read", "core.read", ToolResult.Ok("Read.", "{\"text\":\"Original Ω😀\"}"));
            var body = new ContextReadBody();
            var key = body.Key(evidence, message);
            for (var i = 0; i < 24; i++)
            {
                AssertTrue(body.HasVisibleBody(message), "unchanged content preserves body presence");
                AssertTrue(ReferenceEquals(key, body.Key(evidence, message)), "unchanged identity reuses the derived key");
            }
            var changedEvidence = new ResourceEvidence("other", scope, resource, "text",
                new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 1), false, 1);
            AssertTrue(key != body.Key(changedEvidence, message), "coverage and completeness remain part of identity");
            AssertEqual(key, body.Key(evidence, message), "returning to exact evidence preserves identity");
            var content = message.Content;
            message.Content = ToolResultWire.Write("read", "core.read", ToolResult.Ok("Read.", "{\"text\":\"Changed Ω😀\"}"));
            AssertTrue(key != body.Key(evidence, message), "changed actual bytes invalidate the body key");
            AssertTrue(body.HasVisibleBody(message), "changed bytes still expose an actual body");
            message.Content = content;
            AssertEqual(key, body.Key(evidence, message), "restoring original bytes restores their identity");
            Action<Action, Action, string> reject = (change, restore, label) => {
                change();
                AssertTrue(!body.HasVisibleBody(message), label + " cannot inherit cached body presence");
                AssertEqual(null, body.Key(evidence, message), label + " cannot inherit cached identity");
                AssertTrue(!body.IsSuccessfulResourceRead(message), label + " cannot grant read delivery");
                restore();
                AssertEqual(key, body.Key(evidence, message), label + " restoration keeps exact body semantics");
            };
            reject(() => message.ToolCallId = "other", () => message.ToolCallId = "read", "changed call binding");
            reject(() => message.ToolName = "core.tools.read", () => message.ToolName = "core.read", "changed tool binding");
            reject(() => message.Role = "assistant", () => message.Role = "user", "changed transport role");
            reject(() => message.ToolResultRole = "tool", () => message.ToolResultRole = "user", "changed result role");
            reject(() => message.ProtocolMessage = false, () => message.ProtocolMessage = true, "missing protocol marker");
            reject(() => message.ToolResultProtocolVersion++, () => message.ToolResultProtocolVersion--, "changed protocol version");
            reject(() => message.ResponseProtocolVersion = 1, () => message.ResponseProtocolVersion = 0, "response envelope");
            reject(() => message.Activity = new ChatActivity(), () => message.Activity = null, "activity projection");
            reject(() => message.ToolCalls.Add(new LlmToolCall()), () => message.ToolCalls.Clear(), "call batch");
            reject(() => message.CompletedOperation = new CompletedToolOperation(), () => message.CompletedOperation = null, "operation receipt");
            reject(() => message.Content = "invalid JSON", () => message.Content = content, "invalid content");
            message.Content = ToolResultWire.Write("read", "core.read", ToolResult.Error("Read failed."));
            AssertTrue(!body.IsSuccessfulResourceRead(message) && body.Key(evidence, message) == null,
                "failed result cannot reuse a successful body");
            message.Content = ToolResultWire.Write("read", "core.read", ToolResult.Ok("Omitted.",
                "{\"bodyIncluded\":false,\"text\":\"Original Ω😀\"}"));
            AssertEqual(null, body.Key(evidence, message), "omission invalidates source identity");
            message.Content = ToolResultWire.Write("read", "core.read", ToolResult.Ok("Read empty.", "{\"text\":\"\"}"));
            AssertTrue(body.HasVisibleBody(message), "empty text is an actual body");
            AssertTrue(body.Key(evidence, message) != null, "empty body retains an identity");
            message.SyntheticResourceObservation = true;
            message.ResultPayload = new PayloadRef(new string('a', 64), 1);
            var payloadKey = body.Key(evidence, message);
            message.ResultPayload = new PayloadRef(new string('b', 64), 1);
            AssertTrue(payloadKey != body.Key(evidence, message), "replaced CAS payload invalidates its identity");
            message.SyntheticResourceObservation = false; message.ResultPayload = null; message.Content = content;
            AssertEqual(key, body.Key(evidence, message), "inline and payload identities cannot contaminate each other");
        }

        private static void ContextReadBodyPreparationCost()
        {
            var scope = new ResourceAuthorityScopeId("document", "body-identity");
            var heads = new List<ResourceHeadState>();
            var facts = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "Compare these sources." } };
            for (var index = 0; index < 6; index++)
            {
                var resource = new ResourceRef("rna://file/body-identity/" + (index / 2), "r1");
                if (index % 2 == 0) heads.Add(ResourceHeadState.Known(resource, 1));
                var result = ContinuityResult("read-" + index, "core.read", ToolResult.Ok("Read.", new JObject {
                    ["target"] = "source " + (index / 2), ["representation"] = "text", ["rawContentIncluded"] = true,
                    ["complete"] = true, ["text"] = new string('Ж', 18000) + index / 2 }.ToString(Formatting.None)));
                result.ResourceEvidence.Add(new ResourceEvidence("read-" + index, scope, resource, "text",
                    ResourceCoverage.Whole(), true, 1));
                facts.Add(ContinuityCall(result)); facts.Add(result);
            }
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 1, null, 0, heads) }), "tools", new SkillCatalogSnapshot(null), null, 1);
            var compiler = new ModelContextCompiler();
            var settings = new AppSettings();
            Func<ModelContextSnapshot> compile = () => compiler.Compile(authority, null, facts, null, null, settings, 100000);
            var expected = compile();
            var wire = JsonConvert.SerializeObject(new LlmMessageBuilder().Build(expected.Messages, settings).Messages);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            ModelContextSnapshot snapshot = null;
            for (var iteration = 0; iteration < 12; iteration++) snapshot = compile();
            clock.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            AssertEqual(wire, JsonConvert.SerializeObject(new LlmMessageBuilder().Build(snapshot.Messages, settings).Messages),
                "repeated preparation preserves the exact model request");
            AssertEqual(3, snapshot.DeliveredObservations.Count, "three distinct bodies remain delivered after duplicate folding");
            AssertEqual(3, snapshot.Receipt.Deduplicated, "each duplicate pair keeps one actual body");
            using (var sha = System.Security.Cryptography.SHA256.Create())
                Console.WriteLine("context-body reads=6 characters=108006 preparations=12 allocated=" + allocated +
                    " wallMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) +
                    " wireSha=" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(wire))).Replace("-", ""));
        }
    }
}
