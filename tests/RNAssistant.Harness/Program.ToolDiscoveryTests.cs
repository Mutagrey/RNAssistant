using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using TerminalToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelProjectionPreservesResourceTablePayload()
        {
            var row = JObject.Parse(@"{
                'id': 7, 'customerId': 'customer-1', 'revision': 'edition-2',
                'hash': 'business-hash', 'provider': 'supplier', 'offset': 12,
                'uri': 'https://example.test/item', 'resources': ['stock'],
                'nested': {'id': 'nested-id', 'items': [{'revision': 3, 'hash': null}]}
            }");
            var table = new JObject
            {
                ["columns"] = new JArray(row.Properties().Select(property => new JObject
                {
                    ["key"] = property.Name, ["label"] = property.Name, ["type"] = "mixed"
                })),
                ["rows"] = new JArray(row),
                ["totalRows"] = 1
            };
            var reference = new ResourceRef(
                ResourceUri.Create("chat", "session", "artifact", "table", "revision", "1"), "1");
            foreach (var representation in new[] { "table", "records" })
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var command = Command(ResourceToolCatalog.ReadToolId);
                command.ToolCallId = "table_call";
                var data = new JObject
                {
                    ["kind"] = "resource-read", ["target"] = "data: Customers",
                    ["representation"] = representation, ["table"] = table.DeepClone(),
                    ["complete"] = true, ["uri"] = reference.Uri, ["revision"] = "1",
                    ["nextCursor"] = "runtime-cursor", ["artifactId"] = "runtime-id",
                    ["hydratedForNextModelStep"] = false,
                    ["diagnostics"] = new JObject { ["expectedHash"] = new string('a', 64) }
                };
                var source = AgentJsonProtocol.CreateToolResultMessage(command,
                    TerminalToolResult.Ok("Read.", data.ToString(Newtonsoft.Json.Formatting.None),
                        new[] { reference }), role);
                var original = source.Content;
                var projected = ModelToolResultProjection.Project(source);
                ToolResultWireReadResult wire;
                string error;
                AssertTrue(ToolResultHistoryReader.TryRead(projected, out wire, out error),
                    "table projection preserves the strict result wire for " + role);
                var actual = JObject.Parse(wire.Result.DataJson);
                AssertTrue(JToken.DeepEquals(table, actual["table"]),
                    representation + " preserves all columns, rows and nested user values for " + role);
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, wire.Result.Status,
                    "projection preserves the invocation outcome");
                AssertTrue(actual["uri"] == null && actual["revision"] == null &&
                    actual["nextCursor"] == null && actual["artifactId"] == null &&
                    actual["diagnostics"] == null && actual["hydratedForNextModelStep"] == null &&
                    wire.Result.Resources.Count == 0, "runtime envelope evidence stays hidden");
                AssertEqual(projected.Content, new ModelResultProjection().Project(source).Content,
                    "Office and Core use exactly one resource result projection");
                var request = new LlmMessageBuilder().Build(new[] { projected }, new AppSettings());
                AssertTrue(JArray.FromObject(request.Messages).ToString().IndexOf("expectedHash", StringComparison.Ordinal) < 0,
                    "the actual wire excludes newly added diagnostic fields");
                AssertEqual(original, source.Content, "durable result remains unchanged");
            }
        }

        private static void ModelProjectionPreservesNestedOfficeResults()
        {
            var vba = JToken.Parse(@"{
                'code':'fixture_failure', 'moduleName':'Module1', 'rootMessage':null,
                'failure':{'message':'Failed request-secret', 'requestId':'request-secret'},
                'operations':[{'changed':true,'operationData':{'startLine':7,'ignored':'hidden'}},
                    null,7,['literal',{'newLength':5,'requestId':'nested-secret'}]],
                'details':'Retry request-secret', 'restore':{}, 'diagnostics':{'trace':'hidden'}
            }");
            var visibleVba = JToken.Parse(@"{
                'code':'fixture_failure', 'moduleName':'Module1',
                'failure':{'message':'Failed [runtime value]'},
                'operations':[{'changed':true,'operationData':{'startLine':7}},
                    null,7,['literal',{'newLength':5}]],
                'details':'Retry [runtime value]', 'restore':{}
            }");
            var fixtures = new[]
            {
                new { Tool = ToolAuthoringCatalog.UpsertToolId, Source = JToken.Parse(@"{
                    'id':'fixture.report','parameters':{'type':'object','properties':{
                        'fileName':{'type':'string'},'revision':{'type':'string'},
                        'record':{'type':'object','properties':{'storagePath':{'type':'string'}}}},
                        'required':['fileName','revision','record']},
                    'components':[{'name':'Main','type':'StdModule','fileName':'internal.bas','code':'literal rna://example/data'}],
                    'revision':'runtime-secret','newRuntimeMetadata':{'trace':'hidden'}
                }"), Expected = JToken.Parse(@"{
                    'id':'fixture.report','parameters':{'type':'object','properties':{
                        'fileName':{'type':'string'},'revision':{'type':'string'},
                        'record':{'type':'object','properties':{'storagePath':{'type':'string'}}}},
                        'required':['fileName','revision','record']},
                    'components':[{'name':'Main','type':'StdModule','code':'literal rna://example/data'}]
                }") },
                new { Tool = SkillAuthoringCatalog.UpsertToolId, Source = JToken.Parse(@"{
                    'id':'fixture.skill','operation':'update','changed':true,'referencePath':'references/source.md',
                    'revision':'runtime-secret','expectedRevision':'expected-secret','newRuntimeMetadata':{}}
                "), Expected = JToken.Parse(@"{
                    'id':'fixture.skill','operation':'update','changed':true,'referencePath':'references/source.md'}") },
                new { Tool = PromptToolCatalog.SaveToolId, Source = JToken.Parse(@"{
                    'systemPrompt':'literal rna://example/data and revision','fields':['systemPrompt'],
                    'expectedSha256':'runtime-secret','code':'verification_failed','retryable':false}
                "), Expected = JToken.Parse(@"{
                    'systemPrompt':'literal rna://example/data and revision','fields':['systemPrompt'],
                    'code':'verification_failed','retryable':false}") },
                new { Tool = ExcelReadToolIds.Inspect, Source = JToken.Parse(@"{
                    'sheet':'Data','rows':[{'id':7,'revision':'edition-2','nested':{'hash':null}}],
                    'details':{'code':'fixture_failure','message':'Inspect cells.','scopeSha256':'hidden'},
                    'diagnostics':{'trace':'hidden'}
                }"), Expected = JToken.Parse(@"{
                    'sheet':'Data','rows':[{'id':7,'revision':'edition-2','nested':{'hash':null}}],
                    'details':{'code':'fixture_failure','message':'Inspect cells.'}
                }") },
                new { Tool = VbaToolCatalog.WriteModule, Source = vba, Expected = visibleVba },
                new { Tool = VbaToolCatalog.WriteModule,
                    Source = (JToken)new JArray(vba.DeepClone(), null, "literal"),
                    Expected = (JToken)new JArray(visibleVba.DeepClone(), null, "literal") },
                new { Tool = VbaToolCatalog.WriteModule, Source = JToken.Parse("null"), Expected = JToken.Parse("null") }
            };
            foreach (var fixture in fixtures)
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var command = Command(fixture.Tool); command.ToolCallId = "nested_projection";
                var source = AgentJsonProtocol.CreateToolResultMessage(command,
                    TerminalToolResult.Unknown("Inspect the target.", fixture.Source.ToString(Newtonsoft.Json.Formatting.None)), role);
                var original = source.Content;
                List<ContextMessagePresentation> presentation = null;
                var projected = ModelToolResultProjection.Project(source, recordPresentation: parts => presentation = parts);
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(projected, out wire, out error), "nested Office result remains strict");
                AssertTrue(JToken.DeepEquals(fixture.Expected, ToolResultWire.ParseData(wire.Result.DataJson)),
                    fixture.Tool + " preserves exact nested values, array nulls and diagnostic filtering in " + role);
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, wire.Result.Status,
                    "projection cannot upgrade an unknown effect");
                AssertEqual(original, source.Content, "projection preserves durable evidence");
                AssertEqual(JToken.DeepEquals(fixture.Source, fixture.Expected), presentation.Any(part => part.Kind == "data"),
                    "presentation compares the unchanged parsed source with the projected result");
            }
        }

        private static void ModelProjectionHidesRuntimeEvidence()
        {
            var reference = new ResourceRef(
                ResourceUri.Create("chat", "session", "artifact", "item", "revision", "1"),
                "1");
            var resourceCommand = Command(ResourceToolCatalog.ReadToolId);
            resourceCommand.ToolCallId = "resource_call";
            var resourceMessage = AgentJsonProtocol.CreateToolResultMessage(
                resourceCommand,
                TerminalToolResult.Ok("Read.", new JObject
                {
                    ["kind"] = "resource-read",
                    ["target"] = "note: Example",
                    ["text"] = "body",
                    ["uri"] = reference.Uri,
                    ["revision"] = reference.Revision,
                    ["nextCursor"] = "opaque",
                    ["progressCharacters"] = 8000,
                    ["artifactId"] = "item"
                }.ToString(Newtonsoft.Json.Formatting.None), new[] { reference }));
            resourceMessage.ResourceRefs.Add(reference);

            var projectedResource = ModelToolResultProjection.Project(resourceMessage);
            ToolResultWireReadResult projectedResourceWire;
            string error;
            AssertTrue(ToolResultHistoryReader.TryRead(
                projectedResource, out projectedResourceWire, out error),
                "projected resource result remains strict Tool Result v1");
            AssertEqual(0, projectedResourceWire.Result.Resources.Count,
                "model resource result omits exact references");
            AssertEqual(0, projectedResource.ResourceRefs.Count,
                "model message metadata omits exact references");
            AssertContains(projectedResourceWire.Result.DataJson, "note: Example",
                "semantic resource target remains visible");
            AssertTrue(projectedResource.Content.IndexOf(reference.Uri, StringComparison.Ordinal) < 0 &&
                projectedResource.Content.IndexOf("opaque", StringComparison.Ordinal) < 0 &&
                projectedResource.Content.IndexOf("progressCharacters", StringComparison.Ordinal) < 0 &&
                projectedResource.Content.IndexOf("artifactId", StringComparison.Ordinal) < 0,
                "resource URI, cursor, revision, and internal id stay hidden");
            AssertEqual(1, resourceMessage.ResourceRefs.Count,
                "durable resource message retains exact evidence");

            foreach (var tool in new[] { "excel.upsert_chart", "common.vba_write_module", "files.create" })
            {
                var operation = Command(tool); operation.ToolCallId = "diagnostic_" + tool;
                var input = AgentJsonProtocol.CreateToolResultMessage(operation, TerminalToolResult.Error("Inspect the target.",
                    new JObject { ["code"] = "example_failure", ["moduleName"] = "Module1", ["path"] = "app.js",
                        ["sheet"] = "Data", ["chartName"] = "Chart 1",
                        ["diagnostics"] = new JObject { ["expectedHash"] = new string('c', 64), ["trace"] = new string('x', 8000) } }.ToString()));
                input.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error,
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
                var output = ModelToolResultProjection.Project(input);
                AssertTrue(output.Content.IndexOf("diagnostics", StringComparison.Ordinal) < 0,
                    tool + " has a closed metadata boundary");
                AssertContains(output.Content, "example_failure", "the error code remains actionable");
                AssertContains(output.Content, "Replan", "typed recovery is rendered at the request boundary");
                var beforeWire = JArray.FromObject(new LlmMessageBuilder().Build(new[] { input }, new AppSettings()).Messages).ToString();
                var afterWire = JArray.FromObject(new LlmMessageBuilder().Build(new[] { output }, new AppSettings()).Messages).ToString();
                AssertTrue(afterWire.Length < beforeWire.Length / 2, "fixed diagnostic fixture shrinks the actual request without losing outcome/recovery");
                AssertContains(input.Content, "expectedHash", "full diagnostics remain in the durable result");
            }

            var semanticCall = new AgentToolCall
            {
                Id = "semantic_resource_call",
                Name = ResourceToolCatalog.ReadToolId,
                Arguments = new Dictionary<string, object>
                {
                    ["target"] = "VBA module: MOD_CTX_CodingStandards",
                    ["representation"] = "source"
                }
            };
            var semanticAccepted = AgentJsonProtocol.CreateToolCallMessage(
                semanticCall, "Reading source.", null, ToolResultRoles.Tool,
                FixtureCallOrigin("semantic-resource-step"));
            var semanticResult = AgentJsonProtocol.CreateToolResultMessage(
                new ToolInvocation
                {
                    ToolCallId = semanticCall.Id,
                    ToolId = semanticCall.Name
                },
                TerminalToolResult.Ok("Read.", "{}"),
                ToolResultRoles.Tool);
            var semanticApi = new LlmMessageBuilder().Build(
                new[] { semanticAccepted, semanticResult }, new AppSettings());
            var semanticAssistant = (JObject)semanticApi.Messages[0];
            var semanticFunction = (JObject)semanticAssistant
                .SelectToken("tool_calls[0].function");
            AssertEqual(ResourceToolCatalog.ReadToolId, (string)semanticFunction["name"],
                "native resource replay uses the exact public id without an rna prefix");
            var semanticArguments = JObject.Parse((string)semanticFunction["arguments"]);
            AssertEqual("VBA module: MOD_CTX_CodingStandards", (string)semanticArguments["target"],
                "native resource replay preserves the semantic target");
            AssertTrue(semanticArguments["uri"] == null && semanticArguments["cursor"] == null &&
                semanticArguments["maxChars"] == null,
                "native resource replay cannot expose retired runtime arguments");
            ConversationProtocolContext.EnsureCurrentHistory(new ChatSession
            {
                Messages = new List<ChatMessage> { semanticAccepted, semanticResult }
            });
            AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                    new ToolCall("synthetic_alias", "rna_common_resources_read", "{}"),
                    out error),
                "synthetic rna alias is rejected even when accepted metadata agrees");
            semanticAccepted.ToolCalls[0].Name = "rna_common_resources_read";
            ExpectProtocolPreflightBlock(() => ConversationProtocolContext.EnsureCurrentHistory(
                new ChatSession
                {
                    Messages = new List<ChatMessage> { semanticAccepted, semanticResult }
                }));

            var obsoleteCall = new AgentToolCall
            {
                Id = "obsolete_resource_call",
                Name = ResourceToolCatalog.ReadToolId,
                Arguments = new Dictionary<string, object>
                {
                    ["uri"] = reference.Uri,
                    ["cursor"] = "opaque",
                    ["representation"] = "source"
                }
            };
            var obsoleteAccepted = AgentJsonProtocol.CreateToolCallMessage(
                obsoleteCall, "Reading old state.", null, ToolResultRoles.Tool,
                FixtureCallOrigin("obsolete-resource-step"));
            var obsoleteResult = AgentJsonProtocol.CreateToolResultMessage(
                new ToolInvocation
                {
                    ToolCallId = obsoleteCall.Id,
                    ToolId = obsoleteCall.Name
                },
                TerminalToolResult.Ok("Read.", "{}"),
                ToolResultRoles.Tool);
            ExpectProtocolPreflightBlock(() => ConversationProtocolContext.EnsureCurrentHistory(
                new ChatSession
                {
                    Messages = new List<ChatMessage> { obsoleteAccepted, obsoleteResult }
                }));

            var nativeResource = AgentJsonProtocol.CreateToolResultMessage(
                resourceCommand,
                TerminalToolResult.Ok("Read.", new JObject
                {
                    ["kind"] = "resource-read",
                    ["target"] = "note: Example",
                    ["uri"] = reference.Uri
                }.ToString(Newtonsoft.Json.Formatting.None), new[] { reference }),
                ToolResultRoles.Tool);
            var projectedNative = ModelToolResultProjection.Project(nativeResource);
            ToolResultWireReadResult projectedNativeWire;
            AssertTrue(ToolResultHistoryReader.TryRead(
                projectedNative, out projectedNativeWire, out error),
                "native-role resource result remains strict after projection");
            AssertEqual(0, projectedNativeWire.Result.Resources.Count,
                "native-role resource projection omits exact references");
            AssertTrue(projectedNative.Content.IndexOf(reference.Uri,
                StringComparison.Ordinal) < 0,
                "native-role resource projection hides exact URI data");

            var malformedResource = AgentJsonProtocol.CreateToolResultMessage(
                resourceCommand,
                TerminalToolResult.Ok("Read.", "{}"));
            malformedResource.Content = "TOOL_RESULT:\n{\"uri\":\"" + reference.Uri + "\"";
            var projectedMalformed = ModelToolResultProjection.Project(malformedResource);
            ToolResultWireReadResult projectedMalformedWire;
            AssertTrue(ToolResultHistoryReader.TryRead(
                projectedMalformed, out projectedMalformedWire, out error),
                "malformed switched evidence becomes a strict model error");
            AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error,
                projectedMalformedWire.Result.Status,
                "malformed switched evidence fails closed");
            AssertContains(projectedMalformedWire.Result.DataJson,
                "tool_result_projection_invalid", "projection error has an explicit code");
            AssertTrue(projectedMalformed.Content.IndexOf(reference.Uri,
                StringComparison.Ordinal) < 0,
                "malformed switched evidence cannot leak an exact reference");

            var staleSkill = new SkillDefinition
            {
                Id = "common.fixture_skill",
                Name = "Fixture",
                BodyMarkdown = "old",
                Enabled = true
            };
            var currentSkill = new SkillDefinition
            {
                Id = staleSkill.Id,
                Name = staleSkill.Name,
                BodyMarkdown = "new",
                Enabled = true
            };
            var capabilityCommand = Command(CapabilityToolCatalog.ReadToolId);
            capabilityCommand.ToolCallId = "capability_call";
            var capabilityMessage = AgentJsonProtocol.CreateToolResultMessage(
                capabilityCommand,
                TerminalToolResult.Ok("Skill loaded.", new JObject
                {
                    ["kind"] = "skill",
                    ["id"] = staleSkill.Id,
                    ["revision"] = SkillRevision.Compute(staleSkill),
                    ["loaded"] = true,
                    ["complete"] = true,
                    ["bodyMarkdown"] = staleSkill.BodyMarkdown
                }.ToString(Newtonsoft.Json.Formatting.None)));
            var projectedCapability = ModelToolResultProjection.Project(
                capabilityMessage,
                new ToolCatalogEntry[0],
                new[] { currentSkill });
            ToolResultWireReadResult projectedCapabilityWire;
            AssertTrue(ToolResultHistoryReader.TryRead(
                projectedCapability, out projectedCapabilityWire, out error),
                "projected stale capability remains strict Tool Result v1");
            AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error,
                projectedCapabilityWire.Result.Status,
                "runtime invalidates stale capability evidence");
            AssertContains(projectedCapabilityWire.Result.DataJson,
                "capability_evidence_stale", "stale capability has an explicit code");
            AssertTrue(projectedCapabilityWire.Data["loaded"] == null &&
                (bool?)projectedCapabilityWire.Data["complete"] == false,
                "stale source read reports incomplete evidence without a selection flag");
            AssertTrue(projectedCapability.Content.IndexOf(
                SkillRevision.Compute(staleSkill), StringComparison.Ordinal) < 0,
                "model capability result omits package revision");
        }

        private static void ModelProjectionCompactsOnlyCallableSchema()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var tool = catalog.Single(item => item.Id == "excel.format_range");
                var evidence = ReadSchemaEvidence(executor, catalog, tool.Id, "schema-projection");
                evidence.RunId = "projected-evidence";
                var original = evidence.Content;
                Func<ChatMessage, IEnumerable<ToolCatalogEntry>, ToolResultWireReadResult> project = (message, callable) =>
                {
                    var model = ModelToolResultProjection.Project(message, catalog, new SkillDefinition[0], callableTools: callable);
                    ToolResultWireReadResult wire; string error;
                    AssertTrue(ToolResultHistoryReader.TryRead(model, out wire, out error), "projected schema result keeps its wire contract");
                    return wire;
                };
                foreach (var callable in new[] { null, new ToolCatalogEntry[0] })
                {
                    var pending = project(evidence, callable);
                    AssertTrue(pending.Data["descriptor"] != null && pending.Data["schemaInRequest"] == null,
                        "catalog visibility or a successful read alone does not permit schema compaction");
                    AssertTrue(pending.Data["loaded"] == null && (bool?)pending.Data["complete"] == true,
                        "complete source read does not claim callable admission");
                }
                var compact = project(evidence, new[] { tool });
                AssertTrue(compact.Data["descriptor"] == null && (bool?)compact.Data["schemaInRequest"] == true,
                    "exact callable schema is replaced only in the model view");
                AssertEqual(tool.Id, (string)compact.Data["id"], "receipt retains the public tool identity");
                AssertTrue(compact.Data["loaded"] == null, "schemaInRequest is the exact exposure fact, without an ambiguous loaded flag");
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, compact.Result.Status, "successful evidence stays successful");
                AssertEqual(original, evidence.Content, "full durable descriptor is never rewritten");
                var pack = CallableToolPack.Create(ChatModes.Agent, "Excel", "projected-evidence", catalog);
                AssertTrue(!pack.StageReadResult(ModelToolResultProjection.Project(evidence, catalog,
                    new SkillDefinition[0], callableTools: new[] { tool })), "compact model receipt is never admission authority");
                AssertTrue(pack.StageReadResult(evidence), "original exact evidence still admits the tool");

                ToolResultWireReadResult originalWire; string readError;
                AssertTrue(ToolResultHistoryReader.TryRead(evidence, out originalWire, out readError), "read native schema evidence");
                AssertEqual(true, (bool)originalWire.Data["loaded"], "runtime keeps its original read evidence");
                foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
                {
                    foreach (var change in new Action<JObject>[] {
                        data => data["complete"] = false,
                        data => data["truncated"] = true,
                        data => data["descriptor"]["function"]["parameters"] = JObject.Parse(EmptyFormalToolSchema),
                        data => data["revision"] = "obsolete"
                    })
                    {
                        var data = (JObject)originalWire.Data.DeepClone(); change(data);
                        var command = Command(CapabilityToolCatalog.ReadToolId, "id", tool.Id); command.ToolCallId = "modified-schema";
                        var message = AgentJsonProtocol.CreateToolResultMessage(command,
                            TerminalToolResult.Ok("Schema read.", data.ToString()), role);
                        var visible = project(message, new[] { tool });
                        AssertTrue(visible.Data["schemaInRequest"] == null, "incomplete, mismatched or stale evidence cannot claim current exposure");
                        AssertTrue(visible.Data["loaded"] == null, "every result role omits the source-read flag");
                        if ((string)data["revision"] == "obsolete")
                            AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, visible.Result.Status, "stale evidence keeps its diagnostic");
                    }
                    var accepted = Command(CapabilityToolCatalog.ReadToolId, "id", tool.Id); accepted.ToolCallId = "exact-schema";
                    var full = AgentJsonProtocol.CreateToolResultMessage(accepted, originalWire.Result, role);
                    var receipt = project(full, new[] { tool });
                    AssertTrue(receipt.Data["descriptor"] == null && receipt.Data["loaded"] == null,
                        "all result roles use the same unambiguous compact projection");
                }
                var changed = tool.Clone(); changed.Description += " changed";
                AssertTrue(project(evidence, new[] { changed }).Data["descriptor"] != null,
                    "a different offered descriptor cannot replace the read schema");
            });
        }

        private static void ToolDiscoveryIsCompleteAndLoadsExactSchema()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var catalog = ConversationRunService.PrepareToolsForRun(
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()));
                var skill = new SkillDefinition
                {
                    Id = "excel.review_workbook",
                    Host = "Excel",
                    Name = "Review workbook",
                    Description = "Review workbook structure and formulas.",
                    BodyMarkdown = "# Review\n\nInspect the workbook carefully.",
                    Enabled = true
                };
                new SkillStore(FixturePaths.Value).SaveOne(skill);
                var skills = executor.CaptureSkills().Skills
                    .Where(item => item.Id == skill.Id).ToArray();
                CapabilityCatalogService.BindReadSchema(catalog, skills);

                var compact = CapabilityCatalogService.BuildPromptCatalog(catalog, skills, catalog);
                AssertTrue(((JArray)compact["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.add_sheet" && (string)item["kind"] == "tool"),
                    "compact catalog contains exact tool ids with kind");
                AssertTrue(((JArray)compact["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == ExcelReadToolIds.SummarizeColumns && (string)item["kind"] == "tool"),
                    "the model can discover selected-column analysis without a loaded schema");
                AssertTrue(((JArray)compact["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.review_workbook" && (string)item["kind"] == "skill"),
                    "compact catalog contains exact skill ids with kind");
                AssertEqual(true, (bool)compact["idEnumEnforced"],
                    "bounded catalogs also constrain the reader schema to exact ids");
                var reader = catalog.Single(tool => tool.Id == CapabilityToolCatalog.ReadToolId);
                AssertContains(reader.ArgumentSchemaJson, "excel.add_sheet", "reader enum contains exact tool id");
                AssertContains(reader.ArgumentSchemaJson, ExcelReadToolIds.SummarizeColumns,
                    "reader enum permits loading the column-analysis schema");
                AssertContains(reader.ArgumentSchemaJson, "excel.review_workbook", "reader enum contains exact skill id");
                var capabilityDefinitions = catalog
                    .Where(tool => CapabilityToolCatalog.Owns(tool.Id))
                    .ToArray();
                AssertEqual(2, capabilityDefinitions.Length,
                    "exact capability family is complete");
                foreach (var definition in capabilityDefinitions)
                {
                    AssertTrue(definition.Policy != null,
                        definition.Id + " owns a typed policy");
                    AssertEqual(ToolEffect.Read,
                        definition.Policy.Effect,
                        definition.Id + " is read-only");
                    AssertEqual(ToolVerification.None,
                        definition.Policy.Verification,
                        definition.Id + " does not manufacture verification");
                    AssertTrue(definition.Policy.IndependentLocalRead,
                        definition.Id + " is an independent local read");
                    AssertEqual("agent,plan",
                        string.Join(",", definition.Policy.AllowedModes),
                        definition.Id + " is restricted to Agent and Plan");
                }
                var nativeRuntime = executor.CreateNativeRuntime(
                    NewSession(adapter), capabilityDefinitions,
                    new AppSettings(), ChatModes.Agent, false, null,
                    catalog, skills, false);
                foreach (var definition in capabilityDefinitions)
                {
                    AssertTrue(nativeRuntime.Describe(new ToolCall(
                            "exact_" + definition.Name,
                            definition.Id, "{}")) != null,
                        definition.Id + " has an exact native binding");
                    AssertTrue(nativeRuntime.Describe(new ToolCall(
                            "alias_" + definition.Name,
                            definition.Id.ToUpperInvariant(), "{}")) == null,
                        definition.Id + " has no case alias");
                }
                var nativeSearchCall = new ToolCall(
                    "native_capability_search",
                    CapabilityToolCatalog.SearchToolId,
                    "{\"query\":\"excel.review_workbook\"}");
                var nativeSearchPolicy = nativeRuntime.Describe(nativeSearchCall);
                var nativeSearch = nativeRuntime.ExecuteAsync(
                    new ToolExecutionContext(
                        nativeSearchCall, nativeSearchPolicy,
                        "run", "turn", "step", DateTime.UtcNow,
                        false, 1), CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, nativeSearch.Outcome,
                    "capability search executes through its native handler");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    nativeSearch.Evidence.Dispatch,
                    "local capability search has no dispatch boundary");
                AssertEqual(ToolEffectEvidence.None,
                    nativeSearch.Evidence.Effect,
                    "capability search remains effect-free");
                AssertTrue(!executor.GetControllerTools().Any(tool =>
                    string.Equals(tool.Id, "common.tools_read", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.tools_list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.tools_search", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.skills_read", StringComparison.OrdinalIgnoreCase)),
                    "removed split discovery ids have no aliases");

                var search = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.SearchToolId, "query", "excel.review_workbook"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(search.Success, "capability metadata search succeeds");
                var searchData = JObject.Parse(search.DataJson);
                AssertTrue(((JArray)searchData["items"]).Count <= 20,
                    "search result uses its runtime-owned bound");
                AssertTrue(searchData["cursor"] == null && searchData["nextCursor"] == null,
                    "capability search exposes no caller continuation state");
                AssertContains(search.DataJson, "\"kind\":\"skill\"", "search returns explicit capability kind");
                AssertTrue(search.DataJson.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "search contains no exact schemas");

                var read = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.ReadToolId, "id", "excel.add_sheet"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(read.Success, "exact schema read succeeds");
                var data = JObject.Parse(read.DataJson);
                AssertEqual("tool-schema", (string)data["kind"], "exact result kind");
                AssertEqual(true, (bool)data["loaded"], "exact schema is load evidence");
                AssertEqual(true, (bool)data["complete"], "exact schema is complete");
                AssertEqual(false, (bool)data["truncated"], "exact schema is not truncated");
                AssertEqual("already_callable_or_next_model_step", (string)data["admission"],
                    "schema evidence does not claim callable publication");
                AssertEqual("excel.add_sheet", (string)data.SelectToken("descriptor.function.name"),
                    "exact descriptor names the tool");
                AssertTrue(data.SelectToken("descriptor.function.parameters") is JObject,
                    "exact descriptor includes strict parameters");
                var wrongCaseRead = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.ReadToolId,
                        "id", "EXCEL.ADD_SHEET"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(!wrongCaseRead.Success,
                    "capability ids have no case-insensitive read alias");
                AssertEqual(
                    CapabilityCatalogService.Revision(catalog.Single(tool => tool.Id == "excel.add_sheet")),
                    (string)data["revision"],
                    "schema revision is deterministic");

                var skillRead = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.ReadToolId, "id", "excel.review_workbook"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(skillRead.Success, "same reader loads a skill");
                AssertEqual("skill", (string)JObject.Parse(skillRead.DataJson)["kind"],
                    "skill read returns discriminated kind");

                var collisionDetected = false;
                try
                {
                    CapabilityCatalogService.ThrowOnCollision(catalog, new[]
                    {
                        new SkillDefinition { Id = "excel.add_sheet", Enabled = true }
                    });
                }
                catch (InvalidOperationException)
                {
                    collisionDetected = true;
                }
                AssertTrue(collisionDetected, "tool/skill id collisions fail closed");

                var largeCatalog = catalog.Concat(Enumerable.Range(0, 300).Select(index => new ToolCatalogEntry
                {
                    Id = "excel.synthetic_" + index.ToString("D3"),
                    Host = "Excel",
                    Name = "Synthetic " + index,
                    Description = new string('d', 300) + " " + index,
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true,
                    Policy = OptionalFixturePolicy(),
                    Binding = OptionalFixtureBinding()
                })).ToList();
                CapabilityCatalogService.BindReadSchema(largeCatalog, skills);
                var completeCatalog = CapabilityCatalogService.BuildPromptCatalog(
                    largeCatalog,
                    skills,
                    largeCatalog);
                AssertEqual(true, (bool)completeCatalog["complete"],
                    "large exact-id catalog is explicitly complete");
                AssertEqual(false, (bool)completeCatalog["truncated"],
                    "large exact-id catalog is never silently truncated");
                AssertEqual((int)completeCatalog["total"], ((JArray)completeCatalog["items"]).Count,
                    "every runnable tool and enabled skill is listed");
                AssertTrue(((JArray)completeCatalog["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.synthetic_299"),
                    "complete prompt index contains the tail id");
                var promptTail = ((JArray)completeCatalog["items"]).OfType<JObject>().Single(item =>
                    (string)item["id"] == "excel.synthetic_299");
                AssertTrue(promptTail["summary"] == null && promptTail["name"] == null &&
                    promptTail["mutatesDocument"] == null && (bool)promptTail["schemaLoaded"],
                    "active schema metadata is not duplicated in the compact index");
                var optionalCatalog = CapabilityCatalogService.BuildPromptCatalog(
                    largeCatalog,
                    skills,
                    catalog);
                var optionalTail = ((JArray)optionalCatalog["items"]).OfType<JObject>().Single(item =>
                    (string)item["id"] == "excel.synthetic_299");
                AssertEqual(new string('d', 96) + "...[truncated]", (string)optionalTail["summary"],
                    "unloaded capability keeps a bounded summary for selection");
                AssertTrue(optionalTail["name"] == null && optionalTail["mutatesDocument"] == null &&
                    optionalTail["mutatesLocalState"] == null && optionalTail["requiresConfirmation"] == null,
                    "unloaded prompt entries omit duplicate names and runtime policy metadata");
                var htmlCatalog = CapabilityCatalogService.BuildPromptCatalog(catalog,
                    new RNAssistant.Core.Services.SkillCatalogService(adapter.HostName, executor.CapturePublishedSkills).GetVisibleSkills(), new ToolCatalogEntry[0]);
                var htmlTool = ((JArray)htmlCatalog["items"]).OfType<JObject>().Single(item =>
                    (string)item["id"] == "common.html_workspace_apply_patch");
                AssertTrue(((JArray)htmlTool["loadSkill"]).Values<string>().Contains("common.html_workspace_authoring"),
                    "unexposed specialized tool identifies the skill to load");
                var authoring = ((JArray)htmlCatalog["items"]).OfType<JObject>().Single(item => (string)item["id"] == "common.tools_upsert");
                AssertTrue(((JArray)authoring["loadSkill"]).Values<string>().Contains("common.tool_authoring"),
                    "dynamic authoring schema preserves skill-bound exposure");
                AssertTrue(completeCatalog["items"].ToString(Newtonsoft.Json.Formatting.None)
                        .IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "complete prompt index remains schema-free");
                var tailSearch = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.SearchToolId, "query", "excel.synthetic_299"),
                    largeCatalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(tailSearch.Success, "optional search filters the complete catalog");
                AssertContains(tailSearch.DataJson, "excel.synthetic_299",
                    "search returns the exact tail capability id");
                AssertEqual(new string('d', 160) + "...[truncated]",
                    (string)JObject.Parse(tailSearch.DataJson).SelectToken("items[0].summary"),
                    "search preserves the wider metadata summary");
                AssertTrue(tailSearch.DataJson.IndexOf("schemaLoaded", StringComparison.Ordinal) < 0,
                    "search does not claim working-set state it cannot observe");
                AssertTrue(JObject.Parse(tailSearch.DataJson).SelectToken("items[0].requiresConfirmation") != null,
                    "explicit capability search retains policy inspection metadata");
                var tailRead = executor.ExecuteManual(
                    Command(CapabilityToolCatalog.ReadToolId, "id", "excel.synthetic_299"),
                    largeCatalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(tailRead.Success, "exact reader loads a tail schema from a large catalog");
            });
        }

        private static void OptionalAgentToolRequiresExactAdmission()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string optionalId = "common.html_workspace_write_file";
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Создаю сразу.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.html_workspace_write_file\",\"arguments\":{\"path\":\"progressive.html\",\"content\":\"<main>Ready</main>\"}}]}",
                    LoadSkillResponse("common.html_workspace_authoring"),
                    "{\"message\":\"Создаю после admission.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.html_workspace_write_file\",\"arguments\":{\"path\":\"progressive.html\",\"content\":\"<main>Ready</main>\"}}]}",
                    "{\"message\":\"HTML создан.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var options = new List<LlmRequestOptions>();
                LlmCompletionDelegate completion = (completionSettings, messages, requestOptions, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    options.Add(requestOptions);
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    MaxAgentFormatRetries = 2,
                    ContextWindowOverrideTokens = 65536,
                    AutoConfirmToolActions = true
                };
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteWithApprovalsAsync(
                    ChatModes.Agent,
                    "Создай progressive.html.",
                    session,
                    NewContext(adapter),
                    settings,
                    catalog,
                    null).GetAwaiter().GetResult();

                AssertEqual("HTML создан.", result.AssistantText, "optional tool run completes");
                AssertTrue(session.HtmlWorkspace != null && session.HtmlWorkspace.Files.Any(file =>
                    file != null && string.Equals(file.Path, "progressive.html", StringComparison.OrdinalIgnoreCase)),
                    "admitted optional tool executes once");
                AssertEqual(4, requests.Count, "unloaded repair, schema read, execution, and final requests");
                var initialCallableNames = ResponseSchemaCallNames(options[0].ResponseSchemaJson)
                    .ToList();
                AssertTrue(!initialCallableNames.Contains(optionalId, StringComparer.OrdinalIgnoreCase),
                    "initial strict response schema omits unloaded tool as a callable name");
                var loadedCallableNames = ResponseSchemaCallNames(options[2].ResponseSchemaJson)
                    .ToList();
                AssertTrue(loadedCallableNames.Contains(optionalId, StringComparer.OrdinalIgnoreCase),
                    "strict response schema includes exact loaded tool as a callable name");
                AssertTrue(requests[2][0].Content.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0 &&
                    requests[2][0].Content.Contains("\"safety\""),
                    "strict wire prompt keeps callable metadata while response schema carries arguments");
                AssertContains(FlattenSimple(requests[1]), "Tool schema is not loaded: " + optionalId,
                    "local parser distinguishes an unloaded known tool during repair");
                AssertContains(FlattenSimple(requests[1]), "common.skills_load",
                    "repair directs specialized tools through the skill loader");
                AssertContains(FlattenSimple(requests[1]), "If that load was refused, resolve its reported blocker",
                    "unloaded-tool repair does not override an admission refusal");
                var repair = requests[1].Single(message =>
                    (message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:\n", StringComparison.Ordinal));
                AssertTrue(!repair.Content.Contains("common.capabilities_read"),
                    "generic repair must not contradict the skill-only recovery route");
                AssertContains(FlattenSimple(requests[2]), "SELECTED_SKILL common.html_workspace_authoring",
                    "complete instructions reach the same request as specialized tools");
                AssertTrue(session.LastRun.SelectedSkills.Any(skill => skill.Id == "common.html_workspace_authoring"),
                    "HTML pilot retains the task selection");
            });
            foreach (var mode in new[] { AgentResponseModes.JsonObject, AgentResponseModes.JsonSchema })
                OptionalExcelToolsRequireExactAdmission(mode);
        }

        private static void OptionalExcelToolsRequireExactAdmission(string responseMode)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "excel-admission",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow };
                Func<string, JObject, ConversationToolCall> call = (name, arguments) =>
                    new ConversationToolCall { Name = name, Arguments = arguments };
                var write = ModelProtocolWire.Write("", new[] { call("excel.write_range", new JObject {
                    ["kind"] = "value", ["sheet"] = "Data", ["address"] = "D1", ["value"] = "42" }) });
                var script = new Queue<string>(new[] {
                    write,
                    ModelProtocolWire.Write("", new[] {
                        call(CapabilityToolCatalog.ReadToolId, new JObject { ["id"] = "excel.write_range" }),
                        call(CapabilityToolCatalog.ReadToolId, new JObject { ["id"] = "excel.format_range" }) }),
                    write,
                    ModelProtocolWire.Write("", new[] { call("excel.format_range", new JObject {
                        ["sheet"] = "Data", ["address"] = "D1", ["bold"] = true }) }),
                    ModelProtocolWire.Write("Ячейка записана и отформатирована.", new ConversationToolCall[0])
                });
                var requests = 0;
                LlmCompletionDelegate complete = (config, messages, options, stream, token) =>
                {
                    requests++;
                    AssertTrue(script.Count > 0, "Excel admission needs only the deliberate unloaded-call repair");
                    var callable = session.LastContextReceipt.Arsenal.Tools.Select(tool => tool.Id).ToArray();
                    foreach (var id in new[] { "excel.write_range", "excel.format_range" })
                        AssertEqual(requests >= 3, callable.Contains(id), "direct Excel schema is callable only after admission: " + id);
                    if (requests == 2)
                    {
                        AssertContains(FlattenSimple(messages), "Call common.capabilities_read", "direct tools retain their exact recovery route");
                        AssertEqual(0, adapter.ExcelBackendCalls.Count(operation => operation == FakeOfficeAdapter.ExcelWriteApplyOperation),
                            "guessed tool cannot mutate before admission");
                    }
                    if (requests == 3)
                    {
                        AssertContains(FlattenSimple(messages), "\"admitted\":true", "batched discovery admits both exact schemas together");
                        var receipts = 0;
                        foreach (var message in messages)
                        {
                            ToolResultWireReadResult wire; string error;
                            if (!ToolResultHistoryReader.TryRead(message, out wire, out error) ||
                                wire.Name != CapabilityToolCatalog.ReadToolId) continue;
                            receipts++;
                            AssertTrue(wire.Data["descriptor"] == null && (bool?)wire.Data["schemaInRequest"] == true,
                                "compiler omits duplicate schema history after admission in " + responseMode);
                        }
                        AssertEqual(2, receipts, "both schema reads remain as compact result receipts");
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = script.Dequeue() });
                };
                var settings = new AppSettings { AgentResponseMode = responseMode, AutoConfirmToolActions = true,
                    ContextWindowOverrideTokens = 65536, MaxAgentFormatRetries = 2 };
                var result = CreateConversationRunService(adapter, executor, complete).ExecuteWithApprovalsAsync(
                    ChatModes.Agent, "Запиши 42 в Data!D1 и выдели жирным.", session, NewContext(adapter), settings,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "direct Excel workflow completes: " + result.AssistantText);
                AssertEqual(5, requests, "batched schema reads cost one model step for both tools");
                AssertEqual(0, session.LastRun.SelectedSkills.Count, "standalone Excel tools need no skill");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation => operation == FakeOfficeAdapter.ExcelWriteApplyOperation), "write executes once");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation => operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation), "format executes once");
                AssertEqual("42", adapter.ExcelCellText("Data", "D1"), "exact value reaches the fake document");
                AssertTrue(adapter.HasExcelRangeFormat("Data", "D1"), "format reaches the fake document");
                AssertEqual(2, session.LastRun.KernelState.Summary.ToolCounts.WriteOk, "both changes have verified results");
            });
        }

        private static void AgentRejectsOversizedCapabilityEvidenceExplicitly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string skillId = "common.oversized_evidence";
                const string bodyMarker = "OVERSIZED_SKILL_BODY_MUST_NOT_BE_PARTIALLY_LOADED";
                var skill = new SkillDefinition
                {
                    Id = skillId,
                    Host = "Common",
                    Name = "Oversized evidence",
                    Description = "Budget boundary fixture.",
                    BodyMarkdown = "# Fixture\n\n" + bodyMarker + "\n" + new string('x', 50000),
                    Enabled = true
                };
                new SkillStore(FixturePaths.Value).SaveOne(skill);
                var skills = executor.CaptureSkills().Skills
                    .Where(item => item.Id == skillId).ToArray();
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Where(tool =>
                        tool.Id == CapabilityToolCatalog.ReadToolId));
                CapabilityCatalogService.BindReadSchema(catalog, skills);
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonObject,
                    ContextWindowOverrideTokens = 12000,
                    MaxTokens = 512
                };
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord
                {
                    RunId = "oversized-capability-run",
                    TurnId = "oversized-capability-turn"
                };
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                using (var modelSession = ConversationModelSession.CreateAsync(
                    adapter,
                    null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(store),
                    ChatModes.Agent,
                    "Load the exact skill.",
                    session,
                    NewContext(adapter),
                    settings,
                    catalog,
                    skills,
                    null,
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    const string callId = "read_oversized_capability";
                    modelSession.AppendToolCall(new AgentToolCall
                    {
                        Id = callId,
                        Name = CapabilityToolCatalog.ReadToolId,
                        Arguments = new Dictionary<string, object> { { "id", skillId } }
                    }, string.Empty, null, FixtureCallOrigin("oversized-capability-step"));
                    session.Messages[session.Messages.Count - 1].RunId =
                        session.LastRun.RunId;
                    var command = Command(CapabilityToolCatalog.ReadToolId, "id", skillId);
                    command.ToolCallId = callId;
                    var result = executor.ExecuteManual(
                        command,
                        catalog,
                        settings,
                        false,
                        false,
                        session,
                        AppSettings.DefaultMaxAgentToolSteps,
                        skills);
                    AssertTrue(result.Success,
                        "provider returns complete oversized skill evidence before projection: " +
                        result.ErrorCode + " " + result.Message + " " +
                        result.DataJson);
                    modelSession.AppendToolResult(command, new ConversationModelSession.PreparedToolResult(
                        new ToolResultMaterialization(TerminalToolResult.Ok(
                            result.Message, result.DataJson,
                            result.ModelResourceRefs)), null));

                    store.Save(session);
                    modelSession.EndResponse("after-oversized-capability");
                    var replay = PromptBudgetComposer.ConversationHistory(
                        session, true, false);
                    AssertTrue(replay.Any(item => item.Role == "assistant" &&
                            item.ToolCallId == callId),
                        "oversized capability keeps its accepted call");
                    AssertTrue(replay.Any(item => item.Role != "assistant" &&
                            item.ToolCallId == callId),
                        "oversized capability keeps its terminal result");
                    var request = modelSession.CreateRequest("after-oversized-capability",
                        new ModelProtocolCallContext(new string[0]));
                    var wire = LastToolResult(request.AcceptedMessages, CapabilityToolCatalog.ReadToolId);
                    AssertEqual("error", (string)wire["status"],
                        "oversized capability cannot remain a successful partial result");
                    AssertEqual("capability_evidence_context_too_large", (string)wire.SelectToken("data.code"),
                        "oversized capability reports the exact admission error");
                    AssertTrue(wire.SelectToken("data.loaded") == null && (bool?)wire.SelectToken("data.complete") == false,
                        "oversized capability reports missing content without a selection flag");
                    AssertTrue(FlattenSimple(request.AcceptedMessages).IndexOf(bodyMarker, StringComparison.Ordinal) < 0,
                        "oversized body is not partially copied into model history");
                    var durableMessage = session.Messages.Last(item =>
                        item.ToolCallId == callId && item.Role != "assistant");
                    ToolResultWireReadResult durable;
                    string durableError;
                    AssertTrue(ToolResultHistoryReader.TryRead(
                            durableMessage, out durable, out durableError),
                        "durable oversized result remains readable: " + durableError);
                    AssertEqual(
                        RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok,
                        durable.Result.Status,
                        "request-local admission failure does not rewrite durable evidence");
                    AssertTrue(ModelContextBudget.EstimateAdmittedRequestTokens(
                            request.AcceptedMessages,
                            request.Options,
                            settings,
                            ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings),
                            ModelContextBudget.ContinuationReserveTokens(settings)) <=
                        ModelContextBudget.InputBudgetTokens(settings),
                        "explicit failure still retains all request reserves");
                }
            });
        }

        private static void ToolPackSnapshotPinsCompleteContracts()
        {
            const string schema = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}";
            var descriptor = new ToolDescriptor("fixture.snapshot", "Snapshot fixture", schema);
            var policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true,
                new[] { "agent" }, 1);
            var binding = new ToolBinding("fixture.handler.v1", "Run", "document", "Excel");
            var package = new ToolPackageMetadata("1.2.3", "/fixture/tool", "source-v1",
                "[{\"name\":\"Module1\"}]", "installed");
            var registration = ToolPackSnapshot.Capture(descriptor, policy, binding, package);
            var snapshot = new ToolPackSnapshot("fixture-pack", "agent", "Excel", new[] { registration });

            AssertEqual(1, snapshot.Registrations.Count, "snapshot retains one exact registration");
            AssertEqual(registration.Revision, snapshot.Find("fixture.snapshot").Revision,
                "snapshot retains the captured registration revision");
            AssertTrue(snapshot.Find("FIXTURE.SNAPSHOT") == null,
                "execution lookup remains exact and case-sensitive");
            AssertEqual("document", snapshot.Find("fixture.snapshot").Binding.Scope,
                "execution scope is part of the pinned binding");
            AssertEqual("Excel", snapshot.Find("fixture.snapshot").Binding.Host,
                "execution host is part of the pinned binding");
            AssertTrue(snapshot.Describe("fixture.snapshot").Policy.Matches(policy),
                "snapshot exposes the captured typed policy");

            var reorderedSchema = "{\"required\":[],\"properties\":{\"value\":{\"type\":\"string\"}},\"type\":\"object\",\"additionalProperties\":false}";
            AssertEqual(registration.Revision, ToolPackSnapshot.Capture(
                new ToolDescriptor("fixture.snapshot", "Snapshot fixture", reorderedSchema),
                policy, binding, package).Revision,
                "object property order does not create a false contract revision");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                new ToolDescriptor("fixture.snapshot", "Changed description", schema),
                policy, binding, package).Revision,
                "descriptor text is pinned");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                descriptor,
                new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, false, new[] { "agent" }, 1),
                binding, package).Revision,
                "policy is pinned");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                descriptor, policy, new ToolBinding("fixture.handler.v2", "Run", "document", "Excel"), package).Revision,
                "handler binding is pinned");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                descriptor, policy, new ToolBinding("fixture.handler.v1", "Run", "session", "Excel"), package).Revision,
                "execution scope is pinned");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                descriptor, policy, new ToolBinding("fixture.handler.v1", "Run", "document", "Word"), package).Revision,
                "execution host is pinned");
            AssertTrue(registration.Revision != ToolPackSnapshot.Capture(
                descriptor, policy, binding, new ToolPackageMetadata("1.2.3", "/fixture/tool", "source-v2",
                    "[{\"name\":\"Module1\"}]", "installed")).Revision,
                "package implementation is pinned without exposing its source in the revision");

            var same = new ToolPackSnapshot("fixture-pack", "agent", "Excel", new[] { registration });
            AssertEqual(snapshot.Revision, same.Revision, "pack revision is deterministic");
            RuntimeThrows<InvalidOperationException>(() => new ToolPackSnapshot(
                "fixture-pack", "agent", "Excel",
                new[] { new ToolRegistration(descriptor, policy, binding, "forged", package) }));
            RuntimeThrows<InvalidOperationException>(() => new ToolPackSnapshot(
                "fixture-pack", "agent", "Excel",
                new[]
                {
                    registration,
                    ToolPackSnapshot.Capture(new ToolDescriptor("FIXTURE.SNAPSHOT", "Duplicate", schema),
                        policy, binding, package)
                }));
        }

        private static void ToolPackRuntimeUsesCapturedAuthority()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var catalog = ConversationRunService.PrepareToolsForRun(
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()));
                CapabilityCatalogService.BindReadSchema(catalog, null);
                var snapshot = ToolPackSnapshotFactory.Capture("agent", adapter.HostName, catalog);
                var inspect = snapshot.Find(ExcelReadToolIds.Inspect);
                AssertTrue(inspect != null, "runnable snapshot contains the native Excel read");
                AssertEqual(ExcelReadToolHandler.InspectBinding.HandlerId, inspect.Binding.HandlerId,
                    "snapshot captures the native handler identity");
                AssertEqual(ToolPackSnapshotFactory.ExecutionFingerprint(catalog, ExcelReadToolIds.Inspect),
                    inspect.Revision, "compatibility fingerprint delegates to the snapshot contract");

                var session = NewSession(adapter);
                var runtime = executor.CreateNativeRuntime(session, snapshot, new AppSettings(), "agent", false);
                var described = runtime.Describe(new ToolCall("snapshot_call", ExcelReadToolIds.Inspect,
                    "{\"kind\":\"sheets\"}"));
                AssertTrue(described != null && described.Matches(snapshot.Describe(ExcelReadToolIds.Inspect)),
                    "native runtime registers the exact captured authority");

                var backendCalls = adapter.TotalBackendCallCount;
                foreach (var registered in snapshot.Registrations.Where(item => NativeToolRuntimeAdapter.Owns(item.Descriptor.Id)))
                {
                    foreach (var forgedBinding in new[]
                    {
                        new ToolBinding(registered.Binding.HandlerId + ".forged", registered.Binding.EntryPoint,
                            registered.Binding.Scope, registered.Binding.Host),
                        new ToolBinding(registered.Binding.HandlerId, "forged-entry-point",
                            registered.Binding.Scope, registered.Binding.Host)
                    })
                    {
                        var forged = new ToolPackSnapshot("forged-native", "agent", adapter.HostName,
                            new[] { ToolPackSnapshot.Capture(registered.Descriptor, registered.Policy,
                                forgedBinding, registered.PackageMetadata) });
                        var rejected = RuntimeThrows<InvalidOperationException>(() => executor.CreateNativeRuntime(
                            session, forged, new AppSettings { EnableAgentJavaScript = true }, "agent", false));
                        AssertEqual("Pinned native binding does not match its handler: " + registered.Descriptor.Id,
                            rejected.Message, "native factory rejects an altered handler identity or entry point");
                    }
                }
                AssertEqual(backendCalls, adapter.TotalBackendCallCount,
                    "forged native bindings never reach a backend");

                var originalPackRevision = snapshot.Revision;
                var definition = catalog.Single(tool => tool.Id == ExcelReadToolIds.Inspect);
                definition.Description += " changed after capture";
                AssertEqual(originalPackRevision, snapshot.Revision,
                    "mutating the source catalog cannot rewrite an existing snapshot");
                var replaced = ToolPackSnapshotFactory.Capture("agent", adapter.HostName, catalog);
                AssertTrue(replaced.Revision != originalPackRevision,
                    "a later run observes the replaced descriptor as a new snapshot");
                AssertTrue(replaced.Find(ExcelReadToolIds.Inspect).Revision != inspect.Revision,
                    "same tool id cannot hide a replaced descriptor");

                definition.Description = definition.Description.Replace(" changed after capture", string.Empty);
                definition.UseWhen = "A changed selection hint";
                AssertTrue(ToolPackSnapshotFactory.Capture("agent", adapter.HostName, catalog)
                        .Find(ExcelReadToolIds.Inspect).Revision != inspect.Revision,
                    "the complete model-visible descriptor is pinned");

                var capabilityRead = catalog.Single(tool =>
                    tool.Id == CapabilityToolCatalog.ReadToolId);
                var beforeBinding = ToolPackSnapshotFactory.ExecutionFingerprint(
                    catalog, capabilityRead.Id);
                AssertEqual(CapabilityToolHandler.BindingFor(
                        CapabilityToolCatalog.ReadToolId).HandlerId,
                    snapshot.Find(CapabilityToolCatalog.ReadToolId)
                        .Binding.HandlerId,
                    "capability reader captures its native binding");
                AssertEqual(string.Empty,
                    ToolPackSnapshotFactory.ExecutionFingerprint(
                        catalog.Concat(new[] { capabilityRead.Clone() }),
                        capabilityRead.Id),
                    "a duplicate current registration fails the pre-dispatch fingerprint closed");
                capabilityRead.EntryPoint = "replacement-entry";
                AssertEqual(beforeBinding,
                    ToolPackSnapshotFactory.ExecutionFingerprint(
                        catalog, capabilityRead.Id),
                    "catalog-only projection fields cannot replace the native capability binding");
            });
        }

        private static void CallableToolPackDefinesCoreAndAdmitsAtomically()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var optionalTools = Enumerable.Range(1, 3)
                    .Select(index => new ToolCatalogEntry
                    {
                        Id = "fixture.dynamic_" + index,
                        Host = "Excel",
                        Name = "Dynamic " + index,
                        Description = "Test optional schema " + index,
                        ArgumentSchemaJson = EmptyFormalToolSchema,
                        BuiltIn = true,
                        Enabled = true,
                        AgentCanRun = true,
                        Policy = OptionalFixturePolicy(),
                        Binding = OptionalFixtureBinding()
                    })
                    .ToList();
                var catalog = ConversationRunService.PrepareToolsForRun(
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).Concat(optionalTools));
                CapabilityCatalogService.BindReadSchema(catalog, null);
                const string runId = "run-tool-pack";
                var toolPack = CallableToolPack.Create(
                    ChatModes.Agent,
                    adapter.HostName,
                    runId,
                    catalog);

                foreach (var id in OfficeToolCatalog.ForHost(adapter.HostName).Select(tool => tool.Id))
                {
                    AssertEqual(id == "excel.inspect", toolPack.Tools.Any(tool => tool.Id == id),
                        "only Excel inspection is callable before admission: " + id);
                    AssertTrue(toolPack.Catalog.Any(tool => tool.Id == id),
                        "every Excel tool remains discoverable: " + id);
                }
                foreach (var id in new[]
                {
                    "common.vba_write_module", "common.vba_apply_patch",
                    "common.vba_restore_backup", "common.vba_rename_module",
                    "common.vba_delete_module"
                })
                {
                    AssertTrue(!toolPack.Tools.Any(tool => tool.Id == id),
                        "VBA procedure tool requires a selected skill: " + id);
                    AssertTrue(toolPack.Catalog.Any(tool => tool.Id == id),
                        "VBA intent remains in the exact catalog: " + id);
                }
                AssertTrue(!toolPack.Tools.Any(tool => tool.Id == VbaToolCatalog.RunMacro) &&
                    toolPack.Catalog.Any(tool => tool.Id == VbaToolCatalog.RunMacro),
                    "macro execution remains an optional direct tool");
                AssertTrue(!toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_1"),
                    "optional catalog schema is not injected into the core");
                var capabilityContext = toolPack.CapabilityContext(null);
                AssertTrue(capabilityContext["profile"] == null &&
                    capabilityContext["snapshotRevision"] == null,
                    "model capability context hides runtime profile and snapshot identity");
                AssertTrue(((JArray)capabilityContext["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == ResourceToolCatalog.FindToolId &&
                    (bool?)item["schemaLoaded"] == true),
                    "model capability context exposes semantic callable membership");
                AssertTrue(((JArray)capabilityContext["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "common.vba_rename_module" &&
                    (bool?)item["schemaLoaded"] == false),
                    "compact catalog exposes the exact optional rename without publishing its schema");

                var first = ReadSchemaEvidence(executor, catalog, "fixture.dynamic_1", "read_1");
                var blockedSkill = executor.ExecuteManual(Command(CapabilityToolCatalog.ReadToolId,
                    "id", "common.vba_rename_module"), catalog, new AppSettings(), false, false);
                AssertTrue(blockedSkill.Success && blockedSkill.DataJson.Contains("skill-tool") &&
                    !blockedSkill.DataJson.Contains("\"descriptor\""),
                    "skill-only schema inspection returns guidance without admitting a descriptor");
                var second = ReadSchemaEvidence(executor, catalog, "fixture.dynamic_2", "read_2");
                first.RunId = runId;
                second.RunId = runId;
                AssertTrue(toolPack.StageReadResult(first), "first exact schema is staged");
                AssertTrue(toolPack.StageReadResult(second), "second exact schema is staged in the same batch");
                var originalRevision = toolPack.Revision;
                var admitted = toolPack.PreparePending((tools, state) =>
                {
                    AssertTrue(tools.Any(tool => tool.Id == "fixture.dynamic_1") &&
                        tools.Any(tool => tool.Id == "fixture.dynamic_2"),
                        "admission evaluates the complete candidate batch");
                    AssertContains(state.Content, "\"admitted\":true", "candidate state is evaluated before publication");
                    return true;
                });
                AssertTrue(admitted.Admitted, "candidate batch is admitted atomically");
                AssertEqual(originalRevision, toolPack.Revision,
                    "evaluated admission is not published before its durable barrier");
                toolPack.Publish(admitted);
                AssertTrue(admitted.Revision != originalRevision, "admission creates a new callable snapshot revision");
                AssertEqual(admitted.Revision, toolPack.Revision, "published revision matches the admitted candidate");
                AssertTrue(toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_1") &&
                    toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_2"),
                    "both schemas are published together");
                var admittedRename = toolPack.Tools.Single(tool =>
                    tool.Id == "fixture.dynamic_2");
                var catalogRename = catalog.Single(tool =>
                    tool.Id == "fixture.dynamic_2");
                AssertTrue(admittedRename.Policy.Matches(catalogRename.Policy) &&
                    admittedRename.Binding.Matches(catalogRename.Binding),
                    "optional admission preserves policy and native execution binding");
                AssertContains(admitted.StateMessage.Content, "No schema was evicted", "admission reports no eviction");

                var third = ReadSchemaEvidence(executor, catalog, "fixture.dynamic_3", "read_3");
                third.RunId = runId;
                AssertTrue(toolPack.StageReadResult(third), "third schema is staged");
                var retainedRevision = toolPack.Revision;
                var rejected = toolPack.PreparePending((tools, state) => false);
                AssertTrue(!rejected.Admitted, "overflow rejects the whole extension");
                toolPack.Publish(rejected);
                AssertEqual(retainedRevision, toolPack.Revision, "rejection does not publish a revision");
                AssertTrue(!toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_3"),
                    "rejected schema is never partially published");
                AssertTrue(toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_1") &&
                    toolPack.Tools.Any(tool => tool.Id == "fixture.dynamic_2"),
                    "rejection never removes admitted schemas");
                AssertContains(rejected.StateMessage.Content, "tool_pack_budget_exceeded",
                    "visible state identifies admission failure");

                var recreated = CallableToolPack.Create(
                    ChatModes.Agent,
                    adapter.HostName,
                    runId,
                    catalog);
                AssertTrue(!recreated.Tools.Any(tool => tool.Id == "fixture.dynamic_1") &&
                    !recreated.Tools.Any(tool => tool.Id == "fixture.dynamic_2"),
                    "a recreated pack cannot infer an admission decision from raw read evidence");
                third.RunId = "other-run";
                AssertTrue(!recreated.StageReadResult(third),
                    "live evidence from another run cannot stage an extension");

                var revisedCatalog = catalog.Select(tool => tool.Clone()).ToList();
                revisedCatalog.Single(tool => tool.Id == "fixture.dynamic_2").Description = "Changed revision";
                var revisionMismatch = CallableToolPack.Create(
                    ChatModes.Agent,
                    adapter.HostName,
                    runId,
                    revisedCatalog);
                AssertTrue(!revisionMismatch.StageReadResult(second),
                    "stale schema evidence cannot stage a changed descriptor");

                var planPack = CallableToolPack.Create(ChatModes.Plan, adapter.HostName, runId, catalog);
                AssertTrue(!planPack.Tools.Any(tool => tool.Id == "excel.read_range"),
                    "Plan does not inherit the Agent Excel core");
                AssertTrue(planPack.Tools.Any(tool => tool.Id == CapabilityToolCatalog.ReadToolId),
                    "Plan keeps exact capability discovery in its bootstrap core");
            });
        }

        private static void ConversationModelSessionRejectsToolPackOverflowAtomically()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var enumValues = new JArray(Enumerable.Range(0, 600)
                    .Select(value => "value_" + value + "_" + new string('x', 12)));
                var optionalTools = Enumerable.Range(1, 2).Select(index => new ToolCatalogEntry
                {
                    Id = "fixture.large_" + index,
                    Host = "Excel",
                    Name = "Large " + index,
                    Description = "Optional budget fixture.",
                    ArgumentSchemaJson = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["value"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Bounded value.",
                                ["enum"] = enumValues.DeepClone()
                            }
                        },
                        ["required"] = new JArray(),
                        ["additionalProperties"] = false
                    }.ToString(Newtonsoft.Json.Formatting.None),
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true,
                    Policy = OptionalFixturePolicy(),
                    Binding = OptionalFixtureBinding()
                }).ToArray();
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Where(tool => tool.Id == CapabilityToolCatalog.ReadToolId)
                        .Concat(optionalTools));
                CapabilityCatalogService.BindReadSchema(catalog, null);
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    ContextWindowOverrideTokens = 20000,
                    MaxTokens = 512
                };
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "overflow-run", TurnId = "overflow-turn" };
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                using (var modelSession = ConversationModelSession.CreateAsync(
                    adapter,
                    null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(store),
                    ChatModes.Agent,
                    "Load both optional schemas.",
                    session,
                    NewContext(adapter),
                    settings,
                    catalog,
                    null,
                    null,
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    foreach (var tool in optionalTools)
                    {
                        var callId = "read_" + tool.Id;
                        modelSession.AppendToolCall(new AgentToolCall
                        {
                            Id = callId,
                            Name = CapabilityToolCatalog.ReadToolId,
                            Arguments = new Dictionary<string, object> { { "id", tool.Id } }
                        }, string.Empty, null, FixtureCallOrigin("overflow-step"));
                        var command = Command(CapabilityToolCatalog.ReadToolId, "id", tool.Id);
                        command.ToolCallId = callId;
                        var result = executor.ExecuteManual(command, catalog, settings, false, false);
                        AssertTrue(result.Success, "large schema read succeeds before admission");
                        modelSession.AppendToolResult(command, new ConversationModelSession.PreparedToolResult(
                            new ToolResultMaterialization(TerminalToolResult.Ok(
                                result.Message, result.DataJson,
                                result.ModelResourceRefs)), null));
                    }

                    store.Save(session);
                    modelSession.EndResponse("after-overflow");
                    var request = modelSession.CreateRequest("after-overflow",
                        new ModelProtocolCallContext(new string[0]));
                    AssertTrue(optionalTools.All(candidate => request.RunnableCatalog.Any(tool => tool.Id == candidate.Id)),
                        "overflow keeps the dynamic registry available for discovery");
                    AssertTrue(optionalTools.All(candidate => !request.CallableTools.Any(tool => tool.Id == candidate.Id)),
                        "overflow publishes none of the requested schemas; callable=" +
                        string.Join(",", request.CallableTools.Select(tool => tool.Id).ToArray()));
                    var state = request.AcceptedMessages.Last(message =>
                        (message.Content ?? string.Empty).StartsWith("TOOL_PACK_STATE:", StringComparison.Ordinal));
                    AssertContains(state.Content, "\"admitted\":false", "runtime rejects the candidate before publication");
                    AssertContains(state.Content, "tool_pack_budget_exceeded", "overflow is visible to the model");
                    AssertContains(state.Content, "\"requestedSchemas\":null",
                        "rejection does not repeat an unbounded list of exact ids");
                    AssertContains(state.Content, "\"requestedSchemaCount\":2",
                        "compact rejection retains the requested batch size");
                    var estimated = ModelContextBudget.EstimateAdmittedRequestTokens(
                        request.AcceptedMessages,
                        request.Options,
                        settings,
                        ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings),
                        ModelContextBudget.ContinuationReserveTokens(settings));
                    AssertTrue(estimated <= ModelContextBudget.InputBudgetTokens(settings),
                        "rejected state and retained pack still fit with all request reserves");
                }

                var rejectedEvents = store.ReadEvents(session.Host, session.DocumentKey, session.Id)
                    .Where(item => item.Type == SessionEventTypes.ToolPackExtensionRejected).ToList();
                AssertEqual(1, rejectedEvents.Count, "rejected admission is a durable typed event");
                var rejectedData = rejectedEvents[0].Data.ToObject<ToolPackExtensionEventData>();
                AssertTrue(!rejectedData.Admitted && rejectedData.RequestedSchemas.Count == 2,
                    "rejected event keeps exact bounded diagnostic refs but grants no authority");

                var reconstructionSettings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    ContextWindowOverrideTokens = 131072,
                    MaxTokens = 512
                };
                var reconstructedSession = new ChatStore(FixturePaths.Value).Load(
                    session.Host, session.DocumentKey, session.Id);
                using (var reconstructed = ConversationModelSession.CreateAsync(
                    adapter,
                    null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)),
                    ChatModes.Agent,
                    "Continue after rejected admission.",
                    reconstructedSession,
                    NewContext(adapter),
                    reconstructionSettings,
                    catalog,
                    null,
                    null,
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = reconstructed.CreateRequest("after-reconstruction",
                        new ModelProtocolCallContext(new string[0]));
                    AssertTrue(optionalTools.All(candidate =>
                            !request.CallableTools.Any(tool => tool.Id == candidate.Id)),
                        "raw rejected read evidence cannot become callable after reconstruction");
                }
            });
        }

        private static void CallableToolPackUsesR61ModeHostProfiles()
        {
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost(host), delegate(
                    OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var allTools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    foreach (var mode in new[] { ChatModes.Agent, ChatModes.Plan })
                    {
                        var catalog = ConversationRunService.PrepareToolsForMode(mode, allTools);
                        CapabilityCatalogService.BindReadSchema(catalog, null);
                        var pack = CallableToolPack.Create(
                            mode, adapter.HostName, "r61-profile-run", catalog);
                        var expected = new HashSet<string>(StringComparer.Ordinal);
                        if (string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal))
                        {
                            expected.Add(ResourceToolCatalog.FindToolId);
                            expected.Add(ResourceToolCatalog.ReadToolId);
                        }
                        else
                        {
                            expected.Add(ResourceToolCatalog.FindToolId);
                            expected.Add(ResourceToolCatalog.ReadToolId);
                            expected.Add(CapabilityToolCatalog.SearchToolId);
                            expected.Add(CapabilityToolCatalog.ReadToolId);
                            expected.Add(SkillLoadToolHandler.ToolId);
                            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                                string.Equals(host, "Excel", StringComparison.Ordinal))
                            {
                                expected.Add("excel.inspect");
                            }
                        }

                        var actual = pack.Tools.Select(tool => tool.Id)
                            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                        var expectedOrdered = expected.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                        AssertTrue(actual.SequenceEqual(expectedOrdered, StringComparer.Ordinal),
                            mode + "/" + host + " core is exact; expected=" +
                            string.Join(",", expectedOrdered) + "; actual=" + string.Join(",", actual));
                    }
                });
            }
        }

        private static void ConversationModelSessionRebuildsAuthorityAfterCompaction()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var optional = new ToolCatalogEntry
                {
                    Id = "fixture.compaction_tool",
                    Host = "Excel",
                    Name = "Compaction fixture",
                    Description = "Optional schema requires fresh admission after session reconstruction.",
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true,
                    Policy = OptionalFixturePolicy(),
                    Binding = OptionalFixtureBinding()
                };
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Where(tool => tool.Id == CapabilityToolCatalog.ReadToolId)
                        .Concat(new[] { optional }));
                CapabilityCatalogService.BindReadSchema(catalog, null);
                var settings = new AppSettings { ContextWindowOverrideTokens = 24576, MaxTokens = 1024, AutoCompressContext = true };
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "compaction-run", TurnId = "compaction-turn" };
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Inspect the tool before continuing." });
                session.Messages.Add(AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall
                {
                    Id = "read_before_compaction",
                    Name = CapabilityToolCatalog.ReadToolId,
                    Arguments = new Dictionary<string, object> { { "id", optional.Id } }
                }, string.Empty, null, ToolResultRoles.User, FixtureCallOrigin("before-compaction-step")));
                var evidence = ReadSchemaEvidence(executor, catalog, optional.Id, "read_before_compaction");
                evidence.RunId = session.LastRun.RunId;
                session.Messages.Add(evidence);
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                var loaded = CallableToolPack.Create(ChatModes.Agent, adapter.HostName,
                    session.LastRun.RunId, catalog);
                AssertTrue(loaded.StageReadResult(evidence), "live exact evidence stages before compaction");
                var admission = loaded.PreparePending((tools, state) => true);
                AssertTrue(admission.Admitted,
                    "seed schema is admitted before compaction");
                new ToolPackAdmissionJournal(EventStore(store), session).Append(admission, "after-schema-read");
                loaded.Publish(admission);
                AssertTrue(loaded.Tools.Any(tool => tool.Id == optional.Id), "seed schema is callable before compaction");

                // Many small messages overflow the prompt but leave a complete prefix
                // within the compactor's bounded source budget, including the schema pair.
                var unbounded = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).BuildPreview(
                    ChatModes.Agent, "Continue.", adapter, loaded.Tools, null, NewContext(adapter), settings,
                    session, null, true, 100000, loaded.CapabilityContext(null));
                while (ModelContextBudget.EstimateMessagesTokens(unbounded, settings) <= ModelContextBudget.InputBudgetTokens(settings) + 256)
                {
                    var message = new ChatMessage { Role = "user", Content = string.Concat(Enumerable.Repeat("Earlier context. ", 40)) };
                    session.Messages.Add(message);
                    unbounded.Add(message);
                }
                var originalMessages = session.Messages.ToArray();
                var compactions = 0;
                LlmCompletionDelegate completion = (optionsSettings, messages, options, stream, cancellationToken) =>
                {
                    AssertEqual("context_compaction", options.TracePurpose, "materialization calls only the compactor");
                    compactions++;
                    return Task.FromResult(CompactionReply(messages, "Compaction " + compactions + "."));
                };
                using (var modelSession = ConversationModelSession.CreateAsync(adapter, new ContextCompactionService(completion),
                    new AttachmentAnalysisService(completion), EventStore(store), ChatModes.Agent, "Continue.", session, NewContext(adapter),
                    settings, catalog, null, null, true, null, CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = modelSession.PrepareRequestAsync("after_compaction",
                        new RNAssistant.Core.ModelProtocol.ModelProtocolCallContext(new string[0]),
                        CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(1, compactions, "over-budget preparation compacts once and recomposes");
                    AssertTrue(request.RunnableCatalog.Any(tool => tool.Id == optional.Id), "local execution catalog is preserved");
                    AssertTrue(request.CallableTools.Any(tool => tool.Id == optional.Id),
                        "compaction rematerializes the exact durable optional schema");
                    AssertContains(FlattenSimple(request.AcceptedMessages), "Compaction 1.", "request uses the new checkpoint");
                    AssertTrue(!request.AcceptedMessages.Any(message => message.Id == evidence.Id), "old schema evidence is absent from the request");
                    AssertTrue(ModelContextBudget.EstimateAdmittedRequestTokens(
                            request.AcceptedMessages,
                            request.Options,
                            settings,
                            ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings),
                            ModelContextBudget.ContinuationReserveTokens(settings)) <=
                        ModelContextBudget.InputBudgetTokens(settings),
                        "recomposed request fits the input budget with all reserves");
                    AssertTrue(originalMessages.SequenceEqual(session.Messages.Take(originalMessages.Length)), "compaction keeps the original transcript");

                    var nextPreview = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).BuildPreview(
                        ChatModes.Agent, "Continue.", adapter, loaded.Tools, null, NewContext(adapter), settings,
                        session, null, true, 100000, loaded.CapabilityContext(null));
                    while (ModelContextBudget.EstimateMessagesTokens(nextPreview, settings) <=
                        ModelContextBudget.InputBudgetTokens(settings) + 256)
                    {
                        var message = new ChatMessage { Role = "user", Content = string.Concat(Enumerable.Repeat("Later operation. ", 40)) };
                        session.Messages.Add(message);
                        nextPreview.Add(message);
                    }
                    modelSession.ReleaseRequestMedia();
                    var nextRequest = modelSession.PrepareRequestAsync("later_step",
                        new RNAssistant.Core.ModelProtocol.ModelProtocolCallContext(new string[0]),
                        CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(2, compactions, "a later over-budget model step runs the same automatic preparation");
                    AssertContains(FlattenSimple(nextRequest.AcceptedMessages), "Compaction 2.",
                        "later step uses its incremental checkpoint instead of failing the run");
                }
            });
        }

        private static void ToolPackAdmissionReplaysAcrossChatTurns()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var optional = new ToolCatalogEntry
                {
                    Id = "fixture.durable_tool",
                    Host = "Excel",
                    Name = "Durable fixture",
                    Description = "Optional schema restored only from a typed event.",
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true,
                    Policy = OptionalFixturePolicy(),
                    Binding = OptionalFixtureBinding()
                };
                var secondOptional = new ToolCatalogEntry
                {
                    Id = "fixture.durable_tool_2",
                    Host = "Excel",
                    Name = "Second durable fixture",
                    Description = "Second delta in the durable chain.",
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true,
                    Policy = OptionalFixturePolicy(),
                    Binding = OptionalFixtureBinding()
                };
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Where(tool => tool.Id == CapabilityToolCatalog.ReadToolId)
                        .Concat(new[] { optional, secondOptional }));
                CapabilityCatalogService.BindReadSchema(catalog, null);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "durable-run-1", TurnId = "durable-turn" };
                var evidence = ReadSchemaEvidence(executor, catalog, optional.Id, "durable-read");
                evidence.RunId = session.LastRun.RunId;
                session.Messages.Add(evidence);
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);

                var live = CallableToolPack.Create(ChatModes.Agent, adapter.HostName,
                    session.LastRun.RunId, catalog);
                AssertTrue(live.StageReadResult(evidence), "exact current-run evidence stages the durable fixture");
                var admission = live.PreparePending((tools, state) => true);
                AssertTrue(!live.Tools.Any(tool => tool.Id == optional.Id),
                    "accepted evaluation remains unpublished before the event append");
                var durableEvent = new ToolPackAdmissionJournal(EventStore(store), session)
                    .Append(admission, "durable-next-step");
                live.Publish(admission);
                AssertEqual(SessionEventTypes.ToolPackExtensionAccepted, durableEvent.Type,
                    "accepted extension has a dedicated event type");
                AssertEqual("durable-turn", durableEvent.TurnId, "event is scoped by the stable logical turn");
                var durableData = durableEvent.Data.ToObject<ToolPackExtensionEventData>();
                AssertEqual(ToolPackExtensionEventData.CurrentContractVersion, durableData.ContractVersion,
                    "accepted extension persists the typed event contract version");
                AssertEqual(admission.PreviousRevision, durableData.PreviousSnapshotRevision,
                    "accepted extension pins the exact prior callable revision");
                AssertEqual(admission.Revision, durableData.SnapshotRevision,
                    "accepted extension pins the exact resulting callable revision");
                AssertEqual(optional.Id, durableData.RequestedSchemas.Single().Id,
                    "accepted extension persists only the exact requested delta");
                AssertEqual(CapabilityCatalogService.Revision(optional),
                    durableData.RequestedSchemas.Single().Revision,
                    "accepted extension pins the requested descriptor revision");
                var secondEvidence = ReadSchemaEvidence(executor, catalog, secondOptional.Id, "durable-read-2");
                secondEvidence.RunId = session.LastRun.RunId;
                AssertTrue(live.StageReadResult(secondEvidence), "second exact delta stages independently");
                var secondAdmission = live.PreparePending((tools, state) => true);
                new ToolPackAdmissionJournal(EventStore(store), session)
                    .Append(secondAdmission, "durable-next-step-2");
                live.Publish(secondAdmission);

                var unpersisted = NewSession(adapter);
                unpersisted.LastRun = new ChatRunRecord { RunId = "append-failure-run", TurnId = "append-failure-turn" };
                var unpersistedEvidence = ReadSchemaEvidence(executor, catalog, optional.Id, "append-failure-read");
                unpersistedEvidence.RunId = unpersisted.LastRun.RunId;
                var blocked = CallableToolPack.Create(ChatModes.Agent, adapter.HostName,
                    unpersisted.LastRun.RunId, catalog);
                AssertTrue(blocked.StageReadResult(unpersistedEvidence), "append-failure fixture stages exact evidence");
                var blockedAdmission = blocked.PreparePending((tools, state) => true);
                RuntimeThrows<ChatConcurrencyException>(() =>
                    new ToolPackAdmissionJournal(EventStore(new ChatStore(FixturePaths.Value)), unpersisted)
                        .Append(blockedAdmission, "blocked-step"));
                AssertTrue(!blocked.Tools.Any(tool => tool.Id == optional.Id),
                    "failed event append cannot publish callable authority");

                var settings = new AppSettings { ContextWindowOverrideTokens = 131072, MaxTokens = 512 };
                var reloaded = new ChatStore(FixturePaths.Value).Load(session.Host, session.DocumentKey, session.Id);
                reloaded.LastRun.RunId = "durable-confirmation-run";
                using (var reconstructed = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)), ChatModes.Agent,
                    "Continue after confirmation.", reloaded,
                    NewContext(adapter), settings, catalog, null, null, true, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    AssertTrue(reconstructed.CreateRequest("confirmation-step", new ModelProtocolCallContext(new string[0]))
                            .CallableTools.Count(tool => tool.Id == optional.Id || tool.Id == secondOptional.Id) == 2,
                        "crash/reload and a new runtime run id restore the accepted delta chain by logical turn");
                }

                reloaded.LastRun.RunId = "next-run";
                reloaded.LastRun.TurnId = "next-turn";
                using (var nextTurn = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)), ChatModes.Agent, "Start another turn.", reloaded,
                    NewContext(adapter), settings, catalog, null, null, false, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = nextTurn.CreateRequest("next-step", new ModelProtocolCallContext(new string[0]));
                    AssertEqual(2, request.CallableTools.Count(tool => tool.Id == optional.Id || tool.Id == secondOptional.Id),
                        "unchanged exact admissions remain callable in the next chat turn");
                    AssertContains(FlattenSimple(request.AcceptedMessages), "\"schemaLoaded\":true",
                        "current capability metadata reflects carried callable schemas");
                }
                var inspected = new PromptContextInspectorService(adapter, FixturePaths.Value,
                    executor.ResourceAuthority, executor.Payloads, EventStore(store)).Inspect(reloaded,
                    NewContext(adapter), settings, catalog, new SkillDefinition[0], new ChatAttachment[0],
                    "Start another turn.", false);
                AssertEqual(2, inspected.Sections.Single(section => section.Id == "tools").Items
                    .Count(item => item.Id == optional.Id || item.Id == secondOptional.Id),
                    "context inspector shows the same carried callable schemas");

                var edited = new ChatStore(FixturePaths.Value).Load(session.Host, session.DocumentKey, session.Id);
                edited.Messages.Clear();
                edited.LastRun = new ChatRunRecord { RunId = "edited-run", TurnId = "edited-turn" };
                using (var afterEdit = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)), ChatModes.Agent, "Rewritten history.", edited,
                    NewContext(adapter), settings, catalog, null, null, false, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    AssertTrue(!afterEdit.CreateRequest("edited-step", new ModelProtocolCallContext(new string[0]))
                            .CallableTools.Any(tool => tool.Id == optional.Id || tool.Id == secondOptional.Id),
                        "removed capability reads do not resurrect admissions after history edit");
                }

                reloaded.LastRun.RunId = "changed-run";
                reloaded.LastRun.TurnId = "durable-turn";
                var changedCatalog = catalog.Select(tool => tool.Clone()).ToList();
                changedCatalog.Single(tool => tool.Id == optional.Id).Description = "Changed after admission";
                using (var changed = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)), ChatModes.Agent, "Continue with drift.", reloaded,
                    NewContext(adapter), settings, changedCatalog, null, null, true, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = changed.CreateRequest("changed-step", new ModelProtocolCallContext(new string[0]));
                    AssertTrue(!request.CallableTools.Any(tool => tool.Id == optional.Id),
                        "changed pinned schema fails closed to the deterministic core");
                    AssertContains(FlattenSimple(request.AcceptedMessages), "TOOL_PACK_RESTORE_STATE",
                        "schema drift is visible without blocking a confirmed terminal result");
                }

                store.Save(reloaded);
                var rebase = CallableToolPack.Create(ChatModes.Agent, adapter.HostName,
                    reloaded.LastRun.RunId, changedCatalog,
                    new ToolPackAdmissionJournal(EventStore(store), reloaded).ReadAccepted());
                var rebaseEvidence = ReadSchemaEvidence(executor, changedCatalog, secondOptional.Id, "rebase-read");
                rebaseEvidence.RunId = reloaded.LastRun.RunId;
                AssertTrue(rebase.StageReadResult(rebaseEvidence), "fresh exact evidence can stage after drift");
                var rebaseAdmission = rebase.PreparePending((tools, state) => true);
                AssertEqual(ToolPackSnapshotFactory.Capture(ChatModes.Agent, adapter.HostName, rebase.Tools).Revision,
                    rebaseAdmission.PreviousRevision, "fresh admission rebases from the current deterministic core");
                new ToolPackAdmissionJournal(EventStore(store), reloaded).Append(rebaseAdmission, "rebase-step");
                rebase.Publish(rebaseAdmission);
                using (var rebased = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(new ChatStore(FixturePaths.Value)), ChatModes.Agent, "Continue after rebase.", reloaded,
                    NewContext(adapter), settings, changedCatalog, null, null, true, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = rebased.CreateRequest("rebased-step", new ModelProtocolCallContext(new string[0]));
                    AssertTrue(!request.CallableTools.Any(tool => tool.Id == optional.Id) &&
                        request.CallableTools.Any(tool => tool.Id == secondOptional.Id),
                        "a later accepted core rebase replaces the broken chain without resurrecting drifted schemas");
                    AssertTrue(FlattenSimple(request.AcceptedMessages).IndexOf(
                        "TOOL_PACK_RESTORE_STATE", StringComparison.Ordinal) < 0,
                        "a valid accepted rebase clears the prior reconstruction warning");
                }
            });
        }

        private static void ToolPackCarryoverFallsBackWithinBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var values = new JArray(Enumerable.Range(0, 350)
                    .Select(value => "value_" + value + "_" + new string('x', 12)));
                var optional = new ToolCatalogEntry {
                    Id = "fixture.carried_large", Host = "Excel", Name = "Large carried schema",
                    Description = "Budget fallback fixture.", BuiltIn = true, Enabled = true, AgentCanRun = true,
                    ArgumentSchemaJson = new JObject {
                        ["type"] = "object",
                        ["properties"] = new JObject { ["value"] = new JObject {
                            ["type"] = "string", ["description"] = "Bounded value.",
                            ["enum"] = values.DeepClone() } },
                        ["required"] = new JArray(), ["additionalProperties"] = false
                    }.ToString(Newtonsoft.Json.Formatting.None),
                    Policy = OptionalFixturePolicy(), Binding = OptionalFixtureBinding()
                };
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Where(tool => tool.Id == CapabilityToolCatalog.ReadToolId)
                        .Concat(new[] { optional }));
                AssertTrue(catalog.Any(tool => tool.Id == optional.Id), "large optional descriptor remains runnable");
                CapabilityCatalogService.BindReadSchema(catalog, null);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "large-run", TurnId = "large-turn" };
                var read = ReadSchemaEvidence(executor, catalog, optional.Id, "large-read");
                read.RunId = session.LastRun.RunId;
                read.ExcludeFromModelContext = true;
                session.Messages.Add(read);
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                var pack = CallableToolPack.Create(ChatModes.Agent, adapter.HostName, session.LastRun.RunId, catalog);
                AssertTrue(pack.StageReadResult(read), "exact descriptor stages before carryover");
                var admission = pack.PreparePending((tools, state) => true);
                new ToolPackAdmissionJournal(EventStore(store), session).Append(admission, "large-admission");
                pack.Publish(admission);

                session.LastRun = new ChatRunRecord { RunId = "small-run", TurnId = "small-turn" };
                var settings = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema,
                    ContextWindowOverrideTokens = 12000, MaxTokens = 512, AutoCompressContext = false };
                using (var next = ConversationModelSession.CreateAsync(adapter, null,
                    new AttachmentAnalysisService((s, m, o, u, c) => Task.FromResult(new LlmCompletionResult())),
                    EventStore(store), ChatModes.Agent, "Use the small context.", session,
                    NewContext(adapter), settings, catalog, null, null, false, null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var request = next.PrepareRequestAsync("small-step",
                        new ModelProtocolCallContext(new string[0]), CancellationToken.None).GetAwaiter().GetResult();
                    AssertTrue(!request.CallableTools.Any(tool => tool.Id == optional.Id),
                        "oversized prior optional schema yields to the current request budget");
                    AssertContains(FlattenSimple(request.AcceptedMessages), "tool_pack_carryover_budget",
                        "budget fallback is explicit in the model request");
                    next.RebindAuthority(catalog, new SkillCatalogSnapshot(null), settings, NewContext(adapter), 0);
                    AssertTrue(!next.CreateRequest("small-step-2", new ModelProtocolCallContext(new string[0]))
                            .CallableTools.Any(tool => tool.Id == optional.Id),
                        "catalog rebind does not silently restore a dropped prior schema");
                }
            });
        }

        private static ToolPolicy OptionalFixturePolicy()
        {
            return new ToolPolicy(
                ToolEffect.Read,
                ToolVerification.None,
                false,
                true,
                new[] { ChatModes.Agent, ChatModes.Plan });
        }

        private static ToolBinding OptionalFixtureBinding()
        {
            return new ToolBinding("fixture.optional.handler.v1");
        }

        private static ChatMessage ReadSchemaEvidence(
            OfficeToolExecutor executor,
            IReadOnlyList<ToolCatalogEntry> catalog,
            string toolId,
            string callId)
        {
            var command = Command(CapabilityToolCatalog.ReadToolId, "id", toolId);
            command.ToolCallId = callId;
            var result = executor.ExecuteManual(command, catalog, new AppSettings(), false, false);
            AssertTrue(result.Success, "schema read succeeds for " + toolId + ": " + result.Message);
            return AgentJsonProtocol.CreateToolResultMessage(command,
                new ToolResultMaterialization(TerminalToolResult.Ok(
                    result.Message, result.DataJson,
                    result.ModelResourceRefs)), ToolResultRoles.User);
        }
    }
}
