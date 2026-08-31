using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using LegacyResult = RNAssistant.Core.Models.ToolResult;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private const string RuntimeEmptySchema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
        private const string RuntimeIsoText = "2026-08-28T12:34:56.000Z";

        private static ToolPolicy RuntimePolicy(ToolEffect effect = ToolEffect.Read,
            ToolVerification verification = ToolVerification.None, bool confirmation = false,
            bool independent = false, IEnumerable<string> modes = null, int risk = 0)
        {
            return new ToolPolicy(effect, verification, confirmation, independent, modes ?? new[] { "agent" }, risk);
        }

        private static ToolRegistration RuntimeRegistration(string id = "fixture.read", ToolPolicy policy = null,
            string schema = RuntimeEmptySchema, string revision = "revision-1", string handlerId = null)
        {
            return new ToolRegistration(new ToolDescriptor(id, "Fixture tool", schema), policy ?? RuntimePolicy(),
                new ToolBinding(handlerId ?? id + ".handler"), revision);
        }

        private static async Task ToolRuntimeUsesExactRegistry()
        {
            var f = new ToolRuntimeFixture();
            AssertTrue(ReferenceEquals(f.Registration, f.Registry.Find("fixture.read")), "exact lookup retains the registration");
            foreach (var id in new[] { "FIXTURE.READ", "fixture_read", "missing" })
            {
                AssertTrue(f.Registry.Find(id) == null && f.Runtime.Describe(new ToolCall("call_unknown", id, "{}")) == null,
                    "no case or provider-alias fallback");
                var failed = await f.Runtime.ExecuteAsync(f.Context(toolId: id), CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Error, failed.Outcome, "unknown id is rejected");
                AssertEqual(ToolDispatchEvidence.NotDispatched, failed.Evidence.Dispatch, "unknown id cannot dispatch");
            }
            AssertEqual(0, f.Handler.Calls, "unknown identities never reach a handler");
            RuntimeThrows<InvalidOperationException>(() => f.Registry.Register(f.Registration, new RuntimeTestHandler()));
            AssertTrue(ReferenceEquals(f.Registration, f.Registry.Find("fixture.read")), "duplicate cannot replace a registration");
            var differentCase = RuntimeRegistration("FIXTURE.READ");
            f.Registry.Register(differentCase, new RuntimeTestHandler());
            AssertTrue(ReferenceEquals(differentCase, f.Registry.Find("FIXTURE.READ")), "explicit distinct exact id remains distinct");
            RuntimeThrows<InvalidOperationException>(() => f.Registry.Register(
                RuntimeRegistration("another", handlerId: f.Registration.Binding.HandlerId), new RuntimeTestHandler()));
        }

        private static void ToolRuntimeRejectsInvalidRegistrations()
        {
            var registry = new ToolHandlerRegistry();
            foreach (var schema in new[]
            {
                "{}", "[]", RuntimeEmptySchema + " {}",
                "{\"type\":\"object\",\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}",
                "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":true}",
                "{\"type\":\"object\",\"properties\":{},\"required\":[\"absent\"],\"additionalProperties\":false}"
            })
            {
                RuntimeThrows<ArgumentException>(() => registry.Register(RuntimeRegistration(schema: schema), new RuntimeTestHandler()));
                AssertTrue(registry.Find("fixture.read") == null, "invalid schema leaves no partial registration");
            }
            RuntimeThrows<ArgumentException>(() => RuntimePolicy(ToolEffect.External, independent: true));
            RuntimeThrows<ArgumentException>(() => RuntimePolicy(confirmation: true, independent: true));
            RuntimeThrows<ArgumentException>(() => RuntimePolicy(modes: new[] { "unknown" }));
            RuntimeThrows<ArgumentException>(() => new ToolDescriptor("not exact", "", RuntimeEmptySchema));
        }

        private static string RuntimeArgumentSchema()
        {
            return new JObject
            {
                ["type"] = "object", ["required"] = new JArray("value"), ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["value"] = new JObject { ["type"] = "string", ["description"] = "Value" },
                    ["optional"] = new JObject { ["type"] = "string", ["description"] = "Optional" },
                    ["stamp"] = new JObject { ["type"] = "string", ["description"] = "Date text", ["default"] = RuntimeIsoText },
                    ["count"] = new JObject { ["type"] = "integer", ["description"] = "Count", ["default"] = 2 },
                    ["nested"] = new JObject
                    {
                        ["type"] = "object", ["description"] = "Nested", ["required"] = new JArray("stamp"),
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject { ["stamp"] = new JObject { ["type"] = "string", ["description"] = "Date text" } },
                        ["default"] = new JObject { ["stamp"] = RuntimeIsoText }
                    }
                }
            }.ToString(Formatting.None);
        }

        private static async Task ToolRuntimePreservesDefaultsAndDateStrings()
        {
            var schema = RuntimeArgumentSchema();
            var f = new ToolRuntimeFixture(RuntimeRegistration(schema: schema));
            var raw = "{\"value\":\"" + RuntimeIsoText + "\",\"optional\":null}";
            f.Handler.Run = (context, token) =>
            {
                AssertTrue(context.Arguments["value"] is string && context.Arguments["stamp"] is string, "ISO values and defaults remain strings");
                AssertEqual(RuntimeIsoText, (string)context.Arguments["value"], "original date string");
                AssertEqual(RuntimeIsoText, (string)context.Arguments["stamp"], "schema date default");
                AssertEqual(2L, context.Arguments["count"], "schema default materialized");
                AssertTrue(!context.Arguments.ContainsKey("optional"), "permitted optional null removed");
                var nested = (JObject)context.Arguments["nested"];
                AssertEqual(JTokenType.String, nested["stamp"].Type, "nested default remains a JSON string");
                AssertEqual(RuntimeIsoText, (string)nested["stamp"], "each execution gets detached defaults");
                nested["stamp"] = "changed by handler";
                context.Arguments["count"] = 99L;
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Read"), ToolEffectEvidence.None));
            };
            for (var index = 0; index < 2; index++)
            {
                var context = f.Context(raw);
                var result = await f.Runtime.ExecuteAsync(context, CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "valid arguments execute");
                AssertEqual(raw, context.Call.ArgumentsJson, "normalization cannot rewrite accepted arguments");
            }
            AssertEqual(schema, f.Registry.Find("fixture.read").Descriptor.ParametersJson, "handler cannot mutate captured schema");
        }

        private static async Task ToolRuntimeValidatesArgumentsBeforeHandler()
        {
            var f = new ToolRuntimeFixture(RuntimeRegistration(schema: RuntimeArgumentSchema()));
            foreach (var args in new[] { "{}", "[]", "null", "{\"value\":1}", "{\"value\":\"x\",\"extra\":true}",
                "{\"value\":\"x\",\"value\":\"y\"}", "{\"value\":\"x\",\"VALUE\":\"y\"}", "{\"value\":\"x\"} {}" })
            {
                var result = await f.Runtime.ExecuteAsync(f.Context(args), CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "invalid arguments reject");
                AssertEqual(ToolDispatchEvidence.NotDispatched, result.Evidence.Dispatch, "validation precedes handler dispatch");
                AssertContains(result.Result.DataJson, "invalid_arguments", "typed error data code");
            }
            AssertEqual(0, f.Handler.Calls, "invalid arguments never invoke a handler");
        }

        private static async Task ToolRuntimeEnforcesModeAndPolicySnapshot()
        {
            var denied = new ToolRuntimeFixture(mode: "chat");
            var modeResult = await denied.Runtime.ExecuteAsync(denied.Context(), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Error, modeResult.Outcome, "mode policy enforced");
            AssertEqual(0, denied.Handler.Calls, "wrong mode cannot execute");
            var f = new ToolRuntimeFixture();
            var stale = new[]
            {
                new ToolPolicySnapshot("fixture.read", "previous", f.Registration.Policy),
                new ToolPolicySnapshot("fixture.read", "revision-1", RuntimePolicy(risk: 1)),
                new ToolPolicySnapshot("fixture.read", "revision-1", false)
            };
            foreach (var snapshot in stale)
            {
                var result = await f.Runtime.ExecuteAsync(f.Context(snapshot: snapshot), CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "all policy fields and typed identity must match");
                AssertContains(result.Result.DataJson, "tool_policy_changed", "stale captured contract");
            }
            AssertEqual(0, f.Handler.Calls, "stale or untyped snapshot cannot execute");
            var confirmation = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write,
                confirmation: true, modes: new[] { "chat" })), "chat", true, false);
            var cannotConfirm = await confirmation.Runtime.ExecuteAsync(confirmation.Context(confirmed: true), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Error, cannotConfirm.Outcome, "mode prohibition survives auto-confirm and a confirmed flag");
            AssertEqual(0, confirmation.Handler.Calls, "confirmation permission cannot be bypassed");
        }

        private static async Task ToolRuntimeGatesAndResumesConfirmation()
        {
            var registrations = 0;
            ToolExecutionContext registered = null;
            var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write, ToolVerification.Tool, true)),
                registrar: context => { registrations++; registered = context; return "pending-runtime"; });
            f.Handler.Run = (context, token) =>
            {
                context.MarkDispatchPossible();
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Changed"), ToolEffectEvidence.VerifiedChange));
            };
            var pendingContext = f.Context();
            var pending = await f.Runtime.ExecuteAsync(pendingContext, CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, "typed pending control");
            AssertEqual("pending-runtime", pending.PendingId, "registered pending id");
            AssertTrue(ReferenceEquals(pendingContext, registered), "registrar sees the captured accepted call");
            AssertTrue(pending.Result == null && !pending.MayHaveDispatched, "pending is not a model result or an effect");
            AssertEqual(0, f.Handler.Calls, "confirmation before handler invocation");
            var completed = await f.Runtime.ExecuteAsync(f.Context(confirmed: true), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Ok, completed.Outcome, "confirmed handler executes");
            AssertEqual(ToolEffectEvidence.VerifiedChange, completed.Evidence.Effect, "actual effect survives confirmation");
            AssertEqual(pending.Context.Call.Id, completed.Context.Call.Id, "same runtime call identity");
            AssertEqual(1, f.Handler.Calls, "one confirmed invocation");
            AssertEqual(1, registrations, "resume does not register another pending action");
        }

        private static async Task ToolRuntimeHandlesUnavailableAndAutomaticConfirmation()
        {
            var registration = RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write, confirmation: true));
            foreach (var registrar in new Func<ToolExecutionContext, string>[] { null, context => "", context => { throw new InvalidOperationException("registration failed"); } })
            {
                var f = new ToolRuntimeFixture(registration, registrar: registrar);
                var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "unavailable registrar rejects");
                AssertTrue(!result.MayHaveDispatched && result.PendingId == null, "failed registration cannot invent pending execution");
                AssertEqual(0, f.Handler.Calls, "failed registration never executes");
            }
            var automatic = new ToolRuntimeFixture(registration, autoConfirm: true);
            automatic.Handler.Run = (context, token) => Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Already equal"), ToolEffectEvidence.VerifiedNoChange));
            var autoResult = await automatic.Runtime.ExecuteAsync(automatic.Context(), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Ok, autoResult.Outcome, "explicit auto-confirm permits handler");
            AssertEqual(1, automatic.Handler.Calls, "automatic confirmation does not retry");
        }

        private static async Task ToolRuntimeNormalizesReadResults()
        {
            foreach (var status in new[] { ToolResultStatus.Ok, ToolResultStatus.Error, ToolResultStatus.Unknown })
            {
                var f = new ToolRuntimeFixture();
                f.Handler.Run = (context, token) => Task.FromResult(new ToolHandlerResult(new RuntimeResult(status, "Read result"), ToolEffectEvidence.None));
                var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
                AssertEqual(status == ToolResultStatus.Ok ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.Error, result.Outcome, "unreliable reads are errors");
                AssertEqual(status == ToolResultStatus.Ok ? ToolResultStatus.Ok : ToolResultStatus.Error, result.Result.Status, "read terminal status");
                AssertEqual(ToolEffectEvidence.None, result.Evidence.Effect, "read result does not certify a write");
                AssertEqual(1, f.Handler.Calls, "no automatic read retry");
            }
            foreach (var evidence in new[] { ToolEffectEvidence.None, ToolEffectEvidence.Unreported, ToolEffectEvidence.VerifiedNoChange })
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(verification: ToolVerification.Tool)));
                f.Handler.Run = (context, token) => Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Read"), evidence));
                var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
                AssertEqual(evidence == ToolEffectEvidence.VerifiedNoChange ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.Error,
                    result.Outcome, "read verification requirements also need actual evidence");
                AssertEqual(evidence, result.Evidence.Effect, "read policy cannot fabricate verification");
            }
        }

        private static async Task ToolRuntimeSeparatesWriteEffects()
        {
            var cases = new[]
            {
                new { Effect = ToolEffectEvidence.VerifiedChange, Status = ToolResultStatus.Ok, Dispatch = true, Expected = ToolExecutionOutcome.Ok },
                new { Effect = ToolEffectEvidence.VerifiedNoChange, Status = ToolResultStatus.Ok, Dispatch = false, Expected = ToolExecutionOutcome.Ok },
                new { Effect = ToolEffectEvidence.VerifiedNoChange, Status = ToolResultStatus.Ok, Dispatch = true, Expected = ToolExecutionOutcome.Ok },
                new { Effect = ToolEffectEvidence.Unreported, Status = ToolResultStatus.Ok, Dispatch = true, Expected = ToolExecutionOutcome.Unknown },
                new { Effect = ToolEffectEvidence.None, Status = ToolResultStatus.Ok, Dispatch = true, Expected = ToolExecutionOutcome.Unknown },
                new { Effect = ToolEffectEvidence.Unknown, Status = ToolResultStatus.Unknown, Dispatch = true, Expected = ToolExecutionOutcome.Unknown },
                new { Effect = ToolEffectEvidence.VerifiedChange, Status = ToolResultStatus.Error, Dispatch = true, Expected = ToolExecutionOutcome.Error },
                new { Effect = ToolEffectEvidence.None, Status = ToolResultStatus.Error, Dispatch = true, Expected = ToolExecutionOutcome.Error },
                new { Effect = ToolEffectEvidence.Unreported, Status = ToolResultStatus.Error, Dispatch = true, Expected = ToolExecutionOutcome.Unknown }
            };
            foreach (var effectKind in new[] { ToolEffect.Write, ToolEffect.External, ToolEffect.Unclassified })
            foreach (var item in cases)
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(effectKind, ToolVerification.Tool)));
                f.Handler.Run = (context, token) =>
                {
                    if (item.Dispatch) context.MarkDispatchPossible();
                    return Task.FromResult(new ToolHandlerResult(new RuntimeResult(item.Status, "Domain result", "{\"partial\":true}"), item.Effect));
                };
                var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
                AssertEqual(item.Expected, result.Outcome, "effect evidence owns write classification");
                AssertEqual(item.Expected == ToolExecutionOutcome.Unknown ? ToolEffectEvidence.Unknown : item.Effect, result.Evidence.Effect, "no-op and known partial change remain distinct");
                AssertEqual("{\"partial\":true}", result.Result.DataJson, "classification preserves domain data");
                AssertEqual(1, f.Handler.Calls, "unknown/error effects are never retried");
            }
            var missing = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write, ToolVerification.Tool)));
            var noEvidence = await missing.Runtime.ExecuteAsync(missing.Context(), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Error, noEvidence.Outcome, "verification policy cannot supply actual evidence before dispatch");
            AssertTrue(!noEvidence.MayHaveDispatched, "unexecuted failure is not an unknown effect");
        }

        private static async Task ToolRuntimeClassifiesExceptionsAndMissingResults()
        {
            foreach (var write in new[] { false, true })
            foreach (var dispatch in new[] { false, true })
            foreach (var missing in new[] { false, true })
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(write ? ToolEffect.Write : ToolEffect.Read)));
                f.Handler.Run = (context, token) =>
                {
                    if (dispatch) context.MarkDispatchPossible();
                    if (missing) return Task.FromResult<ToolHandlerResult>(null);
                    throw new InvalidOperationException("handler failed");
                };
                var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
                AssertEqual(write && dispatch ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error, result.Outcome, "exceptions respect the dispatch boundary");
                AssertEqual(dispatch, result.MayHaveDispatched, "precise dispatch evidence");
                AssertEqual(1, f.Handler.Calls, "exceptions and missing results do not retry");
            }
        }

        private static async Task ToolRuntimePreservesCancellationEvidence()
        {
            using (var source = new CancellationTokenSource())
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write, confirmation: true)),
                    registrar: context => { source.Cancel(); throw new OperationCanceledException(source.Token); });
                var result = await f.Runtime.ExecuteAsync(f.Context(), source.Token);
                AssertEqual(ToolExecutionOutcome.NotDispatched, result.Outcome, "cancelled registration does not become an execution error");
                AssertEqual(0, f.Handler.Calls, "cancelled registration cannot reach the handler");
            }
            foreach (var write in new[] { false, true })
            foreach (var dispatch in new[] { false, true })
            using (var source = new CancellationTokenSource())
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(write ? ToolEffect.Write : ToolEffect.Read)));
                f.Handler.Run = (context, token) =>
                {
                    if (dispatch) context.MarkDispatchPossible();
                    source.Cancel();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("unreachable");
                };
                var result = await f.Runtime.ExecuteAsync(f.Context(), source.Token);
                AssertEqual(!dispatch ? ToolExecutionOutcome.NotDispatched : write ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                    result.Outcome, "cancellation cannot hide a possible write");
                AssertEqual(dispatch, result.MayHaveDispatched, "cancellation retains dispatch evidence");
                AssertEqual(1, f.Handler.Calls, "cancellation never retries");
            }
            using (var source = new CancellationTokenSource())
            {
                var f = new ToolRuntimeFixture();
                source.Cancel();
                var result = await f.Runtime.ExecuteAsync(f.Context(), source.Token);
                AssertEqual(ToolExecutionOutcome.NotDispatched, result.Outcome, "pre-cancelled call remains unexecuted");
                AssertEqual(0, f.Handler.Calls, "pre-cancellation prevents invocation");
            }
            using (var source = new CancellationTokenSource())
            {
                var f = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write, ToolVerification.Tool)));
                f.Handler.Run = (context, token) =>
                {
                    context.MarkDispatchPossible();
                    source.Cancel();
                    return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Verified"), ToolEffectEvidence.VerifiedChange));
                };
                var result = await f.Runtime.ExecuteAsync(f.Context(), source.Token);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "late cancellation does not erase a known terminal effect");
                AssertEqual(ToolEffectEvidence.VerifiedChange, result.Evidence.Effect, "verified evidence survives cancellation");
            }
        }

        private static async Task ToolRuntimePreservesResourcesAndAwaitingUser()
        {
            var reference = new ResourceRef("rna://chat/session/artifact/body?revision=7", "7");
            var references = new List<ResourceRef> { reference };
            var typed = RuntimeResult.Ok("Question", "{\"value\":null}", references);
            reference.Uri = "changed";
            references.Clear();
            var exposed = typed.Resources[0];
            exposed.Revision = "changed";
            var f = new ToolRuntimeFixture();
            f.Handler.Run = (context, token) => Task.FromResult(new ToolHandlerResult(typed, ToolEffectEvidence.None, awaitingUser: true));
            var result = await f.Runtime.ExecuteAsync(f.Context(), CancellationToken.None);
            AssertTrue(result.AwaitingUser && result.Outcome == ToolExecutionOutcome.Ok, "awaiting-user is separate typed control");
            AssertEqual("rna://chat/session/artifact/body?revision=7", result.Result.Resources.Single().Uri, "exact resource URI retained");
            AssertEqual("7", result.Result.Resources.Single().Revision, "resource revision snapshot retained");
            AssertEqual("{\"value\":null}", result.Result.DataJson, "terminal data retained");
            RuntimeThrows<ArgumentException>(() => new ToolHandlerResult(RuntimeResult.Error("Failed"), awaitingUser: true));
        }

        private static void ToolRuntimeContractsRoundTrip()
        {
            var modes = new[] { "agent", "plan" };
            var policy = RuntimePolicy(ToolEffect.Write, ToolVerification.Tool, true, modes: modes, risk: 3);
            modes[0] = "chat";
            AssertTrue(policy.AllowedModes.SequenceEqual(new[] { "agent", "plan" }), "policy snapshots caller-owned modes");
            var snapshot = new ToolPolicySnapshot("fixture.write", "revision-1", policy);
            var restored = JsonConvert.DeserializeObject<ToolPolicySnapshot>(JsonConvert.SerializeObject(snapshot));
            AssertTrue(restored.Policy != null && snapshot.Matches(restored), "typed policy survives the explicit JSON constructor");
            AssertEqual(ToolVerification.Tool, restored.Policy.Verification, "verification requirement retained");
            AssertEqual(ToolEffect.Write, restored.Policy.Effect, "typed effect policy retained");
            RuntimeThrows<JsonSerializationException>(() => JsonConvert.DeserializeObject<ToolPolicy>("{\"AllowedModes\":[\"agent\"]}"));
            foreach (var field in new[] { "Effect", "Verification", "RequiresConfirmation", "IndependentLocalRead", "RiskLevel" })
            {
                var incomplete = JObject.FromObject(policy);
                incomplete.Remove(field);
                RuntimeThrows<JsonSerializationException>(() => JsonConvert.DeserializeObject<ToolPolicy>(incomplete.ToString(Formatting.None)));
            }
            var old = new ToolPolicySnapshot("fixture.write", "revision", true, true);
            var oldJson = JObject.FromObject(old);
            oldJson.Remove("Policy");
            var restoredOld = JsonConvert.DeserializeObject<ToolPolicySnapshot>(oldJson.ToString(Formatting.None));
            AssertTrue(restoredOld.Policy == null && old.Matches(restoredOld), "legacy bool snapshot remains readable without fabricated typed policy");
            var context = new ToolExecutionContext(new ToolCall("call_roundtrip", "fixture.write", "{}"), snapshot,
                "run", "turn", "step", DateTime.UtcNow, true, 1);
            var record = new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, context.StartedUtc,
                evidence: new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.VerifiedNoChange),
                result: RuntimeResult.Ok("No change", "{\"large_payload\":true}"));
            var json = JsonConvert.SerializeObject(record);
            AssertTrue(JObject.Parse(json)["Result"] == null, "runtime record does not duplicate terminal model payload");
            var replay = JsonConvert.DeserializeObject<ToolExecutionRecord>(json);
            AssertEqual(ToolEffectEvidence.VerifiedNoChange, replay.Evidence.Effect, "compact no-op evidence round-trips");
            AssertTrue(replay.Context.Policy.Matches(snapshot), "nested execution policy round-trips");
            var legacyRecord = JObject.FromObject(record);
            legacyRecord.Remove("Evidence");
            var legacyReplay = JsonConvert.DeserializeObject<ToolExecutionRecord>(legacyRecord.ToString(Formatting.None));
            AssertEqual(ToolEffectEvidence.Unreported, legacyReplay.Evidence.Effect, "legacy records never acquire fabricated verification");
            foreach (var malformed in new[] { "{}", "{\"Effect\":\"VerifiedNoChange\"}", "{\"Dispatch\":\"NotDispatched\"}" })
                RuntimeThrows<JsonSerializationException>(() => JsonConvert.DeserializeObject<ToolExecutionEvidence>(malformed));
            RuntimeThrows<ArgumentException>(() => new ToolExecutionEvidence(ToolDispatchEvidence.NotDispatched, ToolEffectEvidence.VerifiedChange));
        }

        private static void ToolRuntimeLegacyTerminalWire()
        {
            var policy = new ToolPolicySnapshot("fixture.write", "revision", true);
            var cases = new[]
            {
                new { Source = LegacyResult.Ok("unknown is only prose"), Status = ToolResultStatus.Ok },
                new { Source = LegacyResult.Fail("optimistic prose", errorCode: "write_rejected"), Status = ToolResultStatus.Error },
                new { Source = LegacyResult.PartialFailure("some writes completed"), Status = ToolResultStatus.Unknown },
                new { Source = new LegacyResult { Status = "unknown", Message = "unverified" }, Status = ToolResultStatus.Unknown }
            };
            foreach (var item in cases)
            {
                var before = JsonConvert.SerializeObject(item.Source);
                var outcome = LegacyToolOutcomeAdapter.Map(policy, item.Source);
                var materialized = LegacyToolResultAdapter.Materialize(item.Source, outcome);
                var command = new ToolCommand { ToolCallId = "call_legacy", ToolId = policy.ToolId };
                AssertTrue(ToolResultResourceService.ExternalizeIfNeeded(new ChatSession(), command,
                    materialized, 1024, new AppSettings()) == null,
                    "small terminal data, including JSON null, remains inline");
                var wire = ToolResultWire.Read(AgentJsonProtocol.BuildToolResult(command, materialized));
                AssertTrue(wire.Success, "legacy terminal execution enters the v1 writer");
                AssertEqual(item.Status, wire.Result.Status, "partial failure is unknown rather than a fourth wire state");
                AssertEqual(item.Source.Message, wire.Result.Message, "message does not classify the terminal outcome");
                AssertEqual(before, JsonConvert.SerializeObject(item.Source), "conversion does not rewrite the legacy result");
                if (item.Status != ToolResultStatus.Ok)
                    AssertEqual(item.Source.ErrorCode ?? "tool_effect_uncertain", (string)RuntimeData(wire.Result.DataJson)["code"],
                        "domain error code is retained or receives the unknown fallback");
            }
            var authoritative = LegacyToolResultAdapter.Materialize(LegacyResult.Ok("optimistic"), ToolExecutionOutcome.Unknown);
            AssertEqual(ToolResultStatus.Unknown, authoritative.Result.Status, "runtime outcome overrides a legacy success flag");
        }

        private static void ToolRuntimeLegacyPausesStayRuntimeOnly()
        {
            foreach (var pause in new[] { LegacyResult.WaitingConfirmation("Confirm"), LegacyResult.AwaitingUser("Answer", "{}") })
                RuntimeThrows<InvalidOperationException>(() => LegacyToolResultAdapter.Materialize(pause, ToolExecutionOutcome.Ok));
            RuntimeThrows<InvalidOperationException>(() =>
                LegacyToolResultAdapter.Materialize(LegacyResult.Ok("done"), ToolExecutionOutcome.AwaitingConfirmation));
            var context = new ToolRuntimeFixture().Context();
            var pending = new ToolExecutionRecord(context, ToolExecutionOutcome.AwaitingConfirmation, context.StartedUtc,
                "Confirm", mayHaveDispatched: false, pendingId: "pending");
            var pendingUi = ToolResultUiProjection.Create(pending);
            AssertEqual("waiting_confirmation", pendingUi.Status, "UI retains the confirmation control");
            AssertEqual("pending", pendingUi.PendingId, "pending identity stays in runtime/UI");
            AssertTrue(pending.Result == null, "confirmation does not fabricate a terminal result");
            var awaiting = new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, context.StartedUtc,
                "Answer", awaitingUser: true, result: RuntimeResult.Ok("Question", "{}"));
            var awaitingUi = ToolResultUiProjection.Create(awaiting);
            AssertEqual("awaiting_user", awaitingUi.Status, "user-input control remains separate from terminal status");
            RuntimeThrows<InvalidOperationException>(() => LegacyToolResultAdapter.Materialize(awaitingUi, awaiting.Outcome));
        }

        private static void ToolRuntimeLegacyErrorDataRemainsLiteral()
        {
            var source = new JObject { ["stamp"] = RuntimeIsoText, ["literal"] = "\\n\t\"quoted\"" };
            var unchanged = source.ToString(Formatting.None);
            var plain = LegacyToolResultAdapter.Materialize(
                LegacyResult.Fail("Failed", unchanged, "runtime_code"), ToolExecutionOutcome.Error);
            var body = RuntimeData(plain.Result.DataJson);
            AssertEqual("runtime_code", (string)body["code"], "runtime code is merged into object data");
            AssertEqual(RuntimeIsoText, (string)body["stamp"], "ISO data stays exact text");
            AssertEqual(JTokenType.String, body["stamp"].Type, "ISO data is not a Date token");
            AssertEqual((string)source["literal"], (string)body["literal"], "literal backslashes and escapes survive merging");
            foreach (var code in new JToken[] { new JValue("domain_code"), new JValue(7), JValue.CreateNull() })
            {
                source["code"] = code;
                source["details"] = new JObject { ["stamp"] = RuntimeIsoText };
                var collision = LegacyToolResultAdapter.Materialize(
                    LegacyResult.Fail("Failed", source.ToString(Formatting.None), "runtime_code"), ToolExecutionOutcome.Error);
                var merged = RuntimeData(collision.Result.DataJson);
                AssertEqual("runtime_code", (string)merged["code"], "runtime code remains authoritative");
                AssertTrue(JToken.DeepEquals(source, merged["details"]), "conflicting code and existing details are retained together");
            }
            source["code"] = "runtime_code";
            var matching = LegacyToolResultAdapter.Materialize(
                LegacyResult.Fail("Failed", source.ToString(Formatting.None), "runtime_code"), ToolExecutionOutcome.Error);
            AssertTrue(JToken.DeepEquals(source, RuntimeData(matching.Result.DataJson)), "matching code does not add a details wrapper");
            foreach (var scalar in new JToken[]
            {
                JValue.CreateNull(), new JValue(RuntimeIsoText), new JValue(42), new JValue(false),
                new JArray(RuntimeIsoText, "\\n")
            })
            {
                var materialized = LegacyToolResultAdapter.Materialize(
                    LegacyResult.Fail("Failed", scalar.ToString(Formatting.None)), ToolExecutionOutcome.Error);
                var merged = RuntimeData(materialized.Result.DataJson);
                AssertEqual("tool_failed", (string)merged["code"], "missing error code has one fallback");
                AssertTrue(JToken.DeepEquals(scalar, merged["details"]), "scalar and array payloads are preserved under details");
            }
            var literaltext = RuntimeIsoText + " literalnotjson \\n";
            var literalResult = LegacyToolResultAdapter.Materialize(
                LegacyResult.Fail("Failed", literaltext), ToolExecutionOutcome.Error);
            AssertEqual(literaltext, (string)RuntimeData(literalResult.Result.DataJson)["details"], "legacy plain text remains literal data");
        }

        private static void ToolRuntimeProjectionCannotChangeExecution()
        {
            var cases = new[]
            {
                new { Outcome = ToolExecutionOutcome.Ok, Status = ToolResultStatus.Ok, Effect = ToolEffectEvidence.VerifiedNoChange },
                new { Outcome = ToolExecutionOutcome.Error, Status = ToolResultStatus.Error, Effect = ToolEffectEvidence.VerifiedChange },
                new { Outcome = ToolExecutionOutcome.Unknown, Status = ToolResultStatus.Unknown, Effect = ToolEffectEvidence.Unknown }
            };
            foreach (var item in cases)
            {
                var context = new ToolRuntimeFixture(RuntimeRegistration(policy: RuntimePolicy(ToolEffect.Write))).Context();
                var original = new RuntimeResult(item.Status, "Executed", "{\"stamp\":\"" + RuntimeIsoText + "\"}");
                var evidence = new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, item.Effect);
                var record = new ToolExecutionRecord(context, item.Outcome, context.StartedUtc, "Executed", evidence: evidence, result: original);
                var before = JsonConvert.SerializeObject(record);
                var materialized = new ToolResultMaterialization(record.Result);
                var reference = new ResourceRef("rna://chat/session/artifact/full/revision/1", "1");
                materialized.IncludeResultResource(reference, ChatArtifactKinds.ToolResult);
                materialized.ReplaceResult(new RuntimeResult(item.Status, "Projection message", "{\"loaded\":false}", materialized.Result.Resources));
                var ui = ToolResultUiProjection.Create(record);
                ToolResultUiProjection.IncludeResources(ui, materialized);
                ui.Success = !ui.Success;
                ui.Status = "prepared";
                ui.Message = "UI only";
                ui.DataJson = "UI_ONLY";
                ui.ModelResourceRefs[0].Uri = "rna://chat/ui-only";
                ui.ModelResultResourceRef.Revision = "UI_ONLY";
                var wire = ToolResultWire.Read(AgentJsonProtocol.BuildToolResult(
                    new ToolCommand { ToolCallId = context.Call.Id, ToolId = context.Call.Name }, materialized));
                AssertTrue(wire.Success, "UI mutation cannot invalidate the model resource relation");
                AssertEqual(item.Status, wire.Result.Status, "projection keeps the terminal status");
                AssertEqual("Projection message", wire.Result.Message, "UI prose is not model data");
                AssertEqual(reference.Uri, wire.ResultResource.Uri, "UI references do not alias model references");
                AssertEqual("1", wire.ResultResource.Revision, "UI cannot change the model result revision");
                AssertEqual(before, JsonConvert.SerializeObject(record), "projection does not rewrite counts or execution evidence");
                AssertTrue(ReferenceEquals(original, record.Result) && ReferenceEquals(evidence, record.Evidence),
                    "the immutable executed result and evidence remain the originals");
            }
        }

        private static void ToolRuntimeConversionFailurePreservesKnownOutcome()
        {
            var cases = new[]
            {
                new { Source = LegacyResult.Ok("Write completed"), Status = ToolResultStatus.Ok },
                new { Source = LegacyResult.Fail("Write rejected", errorCode: "rejected"), Status = ToolResultStatus.Error },
                new { Source = LegacyResult.PartialFailure("Write may have changed state"), Status = ToolResultStatus.Unknown }
            };
            foreach (var item in cases)
                WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
                {
                    item.Source.ModelResourceRefs = new ResourceRef[] { null };
                    var outcome = LegacyToolOutcomeAdapter.Map(new ToolPolicySnapshot("excel.add_table", "revision", true), item.Source);
                    RuntimeThrows<ArgumentException>(() => LegacyToolResultAdapter.Materialize(item.Source, outcome));
                    adapter.QueueResult("excel.add_table", item.Source);
                    var responses = new Queue<string>(new[]
                    {
                        LoadToolSchemaResponse("excel.add_table"),
                        "{\"message\":\"Table\",\"tool_calls\":[{\"name\":\"excel.add_table\",\"arguments\":{\"sourceRange\":\"A1:B2\"}}]}"
                    });
                    var modelCalls = 0;
                    LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                    {
                        modelCalls++;
                        return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                    };
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                    var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                        ChatModes.Agent, "Format range", session, NewContext(adapter),
                        new AppSettings { AutoConfirmToolActions = true }, tools, null).GetAwaiter().GetResult();
                    AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.add_table"), "conversion failure never retries execution");
                    AssertEqual(2, modelCalls, "conversion failure never triggers model repair");
                    var view = JObject.FromObject(result)["RunViewState"];
                    AssertEqual(item.Status == ToolResultStatus.Ok ? 1 : 0, (int)view["UnverifiedWrites"], "legacy success is preserved without fabricated verification");
                    AssertEqual(item.Status == ToolResultStatus.Error ? 1 : 0, (int)view["FailedCalls"], "known error count survives conversion failure");
                    AssertEqual(item.Status == ToolResultStatus.Error ? 0 : 1, (int)view["UnknownEffects"], "conversion failure creates no extra unknown effect");
                    var message = session.Messages.Single(entry => entry.ProtocolMessage && entry.Role != "assistant" &&
                        entry.ToolName == "excel.add_table");
                    ToolResultWireReadResult wire;
                    string error;
                    AssertTrue(ToolResultHistoryReader.TryRead(message, out wire, out error), "failed projection still closes the accepted exchange");
                    AssertEqual(item.Status, wire.Result.Status, "fallback wire retains the established execution outcome");
                    AssertEqual("result_materialization_failed", (string)RuntimeData(wire.Result.DataJson)["code"], "fallback identifies projection failure");
                    var activity = session.Messages.Single(entry => entry.Activity != null && entry.Activity.ToolCallId == wire.ToolCallId).Activity;
                    AssertEqual(ToolDispatchEvidence.MayHaveDispatched, activity.ExecutionEvidence.Dispatch, "conversion cannot claim non-dispatch");
                    AssertEqual(ToolEffectEvidence.Unreported, activity.ExecutionEvidence.Effect, "legacy conversion never fabricates verified evidence");
                });
        }

        private static JToken RuntimeData(string json)
        {
            return JsonConvert.DeserializeObject<JToken>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }

        private static TException RuntimeThrows<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException ex) { return ex; }
            throw new InvalidOperationException("Expected " + typeof(TException).Name);
        }

        private sealed class RuntimeTestHandler : IToolHandler
        {
            internal int Calls;
            internal Func<ToolHandlerContext, CancellationToken, Task<ToolHandlerResult>> Run;
            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
            {
                Calls++;
                return Run == null ? Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Read"), ToolEffectEvidence.None)) : Run(context, cancellationToken);
            }
        }

        private sealed class ToolRuntimeFixture
        {
            internal readonly ToolRegistration Registration;
            internal readonly ToolHandlerRegistry Registry = new ToolHandlerRegistry();
            internal readonly RuntimeTestHandler Handler = new RuntimeTestHandler();
            internal readonly ToolRuntime Runtime;

            internal ToolRuntimeFixture(ToolRegistration registration = null, string mode = "agent", bool autoConfirm = false,
                bool allowsConfirmation = true, Func<ToolExecutionContext, string> registrar = null)
            {
                Registration = registration ?? RuntimeRegistration();
                Registry.Register(Registration, Handler);
                Runtime = new ToolRuntime(Registry, mode, autoConfirm, allowsConfirmation, registrar);
            }

            internal ToolExecutionContext Context(string arguments = "{}", bool confirmed = false,
                ToolPolicySnapshot snapshot = null, string toolId = null)
            {
                var call = new ToolCall("call_fixture", toolId ?? Registration.Descriptor.Id, arguments);
                return new ToolExecutionContext(call, snapshot ?? Runtime.Describe(call) ??
                    new ToolPolicySnapshot(call.Name, Registration.Revision, Registration.Policy),
                    "run", "turn", "step", DateTime.UtcNow, confirmed, 5);
            }
        }
    }
}
