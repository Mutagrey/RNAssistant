using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaProcedureParserPreservesBoundaries()
        {
            const string selected = "Private _\r\n Function [Получить]$(ByVal x As String)\r\n" +
                "  Debug.Print \"End Function: \"\"Sub Fake()\"\"\", #12:30:00# ' End Sub\r\n" +
                "  Print #1, x: Close #1\r\n" +
                "  Rem End Function\r\n#If Win64 Then\r\n  x = \"😀\"\r\n#Else\r\n  x = \"old\"\r\n#End If\r\nEnd _\r\n Function\r\n";
            const string prefix = "\uFEFF' Sub Fake()\r\nPrivate Declare PtrSafe Function Native Lib \"x\" () As Long\r\n";
            var body = prefix + selected + "Sub NextOne(): Debug.Print \"next\": End Sub";
            var range = VbaProcedureSelector.Select(new StringReader(body), "получить", "function");
            AssertEqual(selected, body.Substring(range.StartChar, range.EndChar - range.StartChar),
                "continuations, bracketed Unicode name, CRLF, comments, strings and dates preserve exact bytes");
            AssertTrue(range.StartChar == prefix.Length && range.StartLine == 3 && !range.ConditionalCompilation,
                "conditional code inside the body does not make its declaration conditional");
            var tail = VbaProcedureSelector.Select(new StringReader(body), "NextOne");
            AssertEqual("Sub NextOne(): Debug.Print \"next\": End Sub", body.Substring(tail.StartChar),
                "colon statements and EOF without newline have exact boundaries");
            const string typed = "Function DoubleValue#()\nDim x#\nx# = 1#\nDoubleValue = x#\nEnd Function";
            AssertEqual(typed, typed.Substring(VbaProcedureSelector.Select(new StringReader(typed), "DoubleValue").StartChar),
                "numeric type suffixes and file channels are not date literal delimiters");
            const string accessors = "Property Get Value() As String\nEnd Property\nProperty Let Value(ByVal v As String)\nEnd Property";
            AssertEqual("resource_procedure_ambiguous", RuntimeThrows<ResourceRequestException>(() =>
                VbaProcedureSelector.Select(new StringReader(accessors), "Value")).ErrorCode,
                "an accessor is not chosen implicitly");
            var setter = VbaProcedureSelector.Select(new StringReader(accessors), "value", "property-let");
            AssertEqual("Property Let Value(ByVal v As String)\nEnd Property", accessors.Substring(setter.StartChar),
                "explicit property kind selects the exact accessor");
            const string branches = "#If Win64 Then\rSub Same()\rEnd Sub\r#Else\rSub Same()\rEnd Sub\r#End If";
            AssertEqual("resource_procedure_ambiguous", RuntimeThrows<ResourceRequestException>(() =>
                VbaProcedureSelector.Select(new StringReader(branches), "Same", "sub")).ErrorCode,
                "conditional alternatives remain ambiguous even with an explicit kind");
            var conditional = VbaProcedureSelector.Select(new StringReader("#If Win64 Then\nSub Only()\nEnd Sub\n#End If"), "Only");
            AssertTrue(conditional.ConditionalCompilation, "a selected conditional declaration is marked without evaluating its branch");
            foreach (var invalid in new[] { "Sub Broken()\nDebug.Print 1", "Sub Broken()\nEnd Function",
                "#If A Then\nSub Broken()\n#Else\nEnd Sub\n#End If", "Sub Broken()\nSub Other()\nEnd Sub",
                "Sub Broken()\nDebug.Print \"unterminated\nEnd Sub" })
                AssertEqual("resource_procedure_source_invalid", RuntimeThrows<ResourceRequestException>(() =>
                    VbaProcedureSelector.Select(new StringReader(invalid), "Broken")).ErrorCode,
                    "incomplete/cross-branch source cannot masquerade as a complete procedure");
            AssertEqual("resource_procedure_not_found", RuntimeThrows<ResourceRequestException>(() =>
                VbaProcedureSelector.Select(new StringReader("' Sub Absent()\nRem Sub Absent()"), "Absent")).ErrorCode,
                "comment declarations do not create procedures");
            RuntimeThrows<OperationCanceledException>(() => VbaProcedureSelector.Select(new StringReader(body), "NextOne",
                cancellationToken: new CancellationToken(true)));
        }

        private static void VbaProcedureReadPinsExactPartialEvidence()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var prefix = "'" + new string('x', 1100000) + "\r\n";
                const string procedure = "Sub Chosen()\r\nDebug.Print \"😀 before\"\r\nEnd Sub";
                var source = prefix + procedure;
                adapter.SetVbaModule("Procedures", source, "StdModule");
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = executor.GetControllerTools().ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), ChatModes.Agent, false);
                Func<string, ToolExecutionRecord> read = name => ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "VBA module: Procedures", ["representation"] = "source", ["procedure"] = name });
                var selected = read("chosen");
                AssertEqual(ToolExecutionOutcome.Ok, selected.Outcome, "procedure read works beyond the former million-character source limit");
                var visible = ModelResultProjection.ProjectResourceData(ResourceToolCatalog.ReadToolId, JObject.Parse(selected.Result.DataJson));
                AssertEqual(procedure, (string)visible["text"], "only the chosen body reaches model projection");
                AssertTrue((bool)visible["selectionComplete"] && !(bool)visible["complete"] && !(bool)visible["sourceComplete"] &&
                    (string)visible["procedure"]["kind"] == "sub" && (int)visible["coverage"]["start"] == prefix.Length &&
                    (int)visible["coverage"]["end"] == source.Length, "procedure coordinates and partial source status survive model projection");
                var evidence = selected.ResourceEvidence.Single();
                AssertEqual(procedure, executor.Payloads.ReadText(evidence.Payload.ToBlobReference()), "observation retains only delivered text");
                AssertTrue(!evidence.Complete && evidence.Resource.IsExact && evidence.ReadMetadata.RequestedProcedure == "chosen" &&
                    evidence.ReadMetadata.SelectionComplete == true && evidence.ReadMetadata.RequestedCoverage == null,
                    "exact partial evidence keeps the accepted semantic selector");
                var restored = JsonConvert.DeserializeObject<ResourceEvidence>(JsonConvert.SerializeObject(evidence));
                AssertEqual("chosen", restored.ReadMetadata.RequestedProcedure, "procedure intent survives durable evidence roundtrip");
                var recovery = new ToolRecoveryContract(ToolFailureKind.ConflictNoEffect, ToolRetryPolicy.RefreshRequired,
                    evidence.Resource.Identity, "source", "VBA module: Procedures");
                AssertTrue(!recovery.IsSatisfiedBy(evidence), "procedure read cannot authorize whole-module replacement");
                var failed = read("Absent");
                AssertContains(failed.Result.DataJson, "resource_procedure_not_found", "missing name has explicit recovery");
                AssertTrue(failed.ResourceEvidence == null || failed.ResourceEvidence.Count == 0, "failed lookup returns no whole-read evidence");
                adapter.SetVbaModule("Procedures", source.Replace("before", "after"), "StdModule");
                var changed = read("Chosen");
                AssertTrue(changed.ResourceEvidence.Single().Resource.Revision != evidence.Resource.Revision,
                    "each semantic read observes the current module revision");
                var frozen = new ResourceAuthoritySnapshotSet(new[] { executor.ResourceAuthority.Store.Capture(evidence.ScopeId) });
                AssertTrue(new EvidenceStateReducer().Reduce(evidence, frozen).State != EvidenceState.Current,
                    "a changed module invalidates the old procedure observation");
                var historical = executor.ResourceGateway.ReadWhole(session, evidence.Resource, "source", CancellationToken.None);
                AssertEqual(source, historical.Result.Text, "procedure retention cannot replace the exact whole snapshot");

                var schema = JObject.Parse(ResourceReadToolHandler.Descriptor.ParametersJson);
                string error;
                var args = new JObject { ["target"] = "VBA module: Procedures", ["representation"] = "source", ["procedure"] = "Chosen" };
                AssertTrue(ToolSchemaSupport.ValidateArguments(args, schema, false, out error), "Office schema admits the semantic procedure selector");
                foreach (var extra in new[] { "partNumber", "startLine", "startChar", "offset" })
                {
                    var mixed = (JObject)args.DeepClone(); mixed[extra] = 1;
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(mixed, schema, false, out error), "Office schema refuses mixed selectors: " + extra);
                }
            });
        }

        private static void VbaProcedureWorkspaceUsesStreamingSnapshot()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                const int commentLength = 16 * 1024 * 1024;
                const string procedure = "Private Property Get Value() As String\r\nValue = \"😀\"\r\nEnd Property\r\n";
                using (var writer = new StreamWriter(Path.Combine(workspace.RootPath, "large.bas"), false, new UTF8Encoding(false, true)))
                {
                    writer.Write("\uFEFF'");
                    var block = new string('x', 8192);
                    for (var i = 0; i < commentLength / block.Length; i++) writer.Write(block);
                    writer.Write("\r\n"); writer.Write(procedure);
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var read = provider.ReadProcedure("large.bas", "value", "property-get", 1000);
                AssertTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 8L * 1024 * 1024,
                    "procedure lookup does not allocate the 16-MiB workspace source body");
                AssertEqual(procedure, read.Result.Text, "streaming lookup preserves BOM-relative UTF-16 offsets and CRLF/Unicode");
                AssertEqual((long)commentLength + 4, read.Evidence.Single().Coverage.Start.Value, "selected source starts after BOM/comment/CRLF");
                var observed = new Dictionary<string, ResourceEvidence>();
                var handler = new WorkspaceResourceHandler(provider, null, () => null, observed, false, null, 1000);
                var execution = new ToolRuntimeFixture().Context(toolId: ResourceToolCatalog.ReadToolId);
                var args = new Dictionary<string, object> { ["target"] = "large.bas", ["procedure"] = "value", ["procedureKind"] = "property-get" };
                var result = handler.ExecuteAsync(new ToolHandlerContext(execution, args), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, result.Result.Status, "workspace handler admits the same selector");
                var metadata = result.ResourceEvidence.Single().ReadMetadata;
                AssertTrue(metadata.RequestedProcedure == "value" && metadata.RequestedProcedureKind == "property-get" &&
                    metadata.RequestedCoverage == null && metadata.SelectionComplete == true && metadata.SourceComplete == false &&
                    !observed["large.bas"].Complete, "workspace metadata and mutation observations remain partial");
                args["startChar"] = 0; args["charCount"] = 1;
                var mixed = handler.ExecuteAsync(new ToolHandlerContext(execution, args), CancellationToken.None).GetAwaiter().GetResult();
                AssertContains(mixed.Result.DataJson, "resource_procedure_invalid", "workspace rejects mixed selectors before reading");
                AssertEqual("resource_procedure_too_large", RuntimeThrows<ResourceRequestException>(() =>
                    provider.ReadProcedure("large.bas", "value", null, 10)).ErrorCode, "only the selected procedure is bounded by the model budget");
                files.CreateText(workspace, "wide.bas", "Sub Wide()\n'" + new string('q', 40000) + "\nEnd Sub");
                var wholeSpan = provider.ReadProcedure("wide.bas", "Wide", null, 50000);
                AssertTrue(wholeSpan.Result.Text.Length > 32000,
                    "procedure reads have no fixed 32000-character ceiling");
                AssertTrue(!wholeSpan.Evidence.Single().Complete && wholeSpan.Result.Offset == 0 &&
                    wholeSpan.Result.Text.Length == wholeSpan.Result.TotalCharacters,
                    "a procedure spanning the whole file still grants only selected-source evidence");

                var ports = typeof(WorkspaceConversationService).GetNestedType("WorkspacePorts", BindingFlags.NonPublic);
                var schema = JObject.Parse((string)ports.GetMethod("ResourceSchema", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { false, true, true, 50000 }));
                string error;
                AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "wide.bas" }, schema, false, out error),
                    "unqualified workspace reads retain their original schema branch");
                var fileArgs = new JObject { ["target"] = "wide.bas", ["procedure"] = "Wide" };
                AssertTrue(ToolSchemaSupport.ValidateArguments(fileArgs, schema, false, out error), "workspace schema admits procedure reads");
                fileArgs["startLine"] = 1;
                AssertTrue(!ToolSchemaSupport.ValidateArguments(fileArgs, schema, false, out error), "workspace schema rejects mixed procedure/line selectors");
                var strictTool = new ToolCatalogEntry { Id = "common.resources_read",
                    ArgumentSchemaJson = schema.ToString(Formatting.None) };
                var strict = JObject.Parse(ConversationResponseSchemaBuilder.Build(new[] { strictTool },
                    new ModelProtocolCallContext(new string[0])));
                var strictCall = new JObject { ["action"] = "execute", ["reason"] = "Read the exact file view.",
                    ["tool_calls"] = new JArray(new JObject { ["name"] = strictTool.Id,
                        ["arguments"] = new JObject { ["target"] = "wide.bas", ["type"] = JValue.CreateNull(),
                            ["procedure"] = "Wide", ["procedureKind"] = JValue.CreateNull() } }), ["message"] = "" };
                AssertTrue(ToolSchemaSupport.ValidateArguments(SchemaTransport(strictCall), strict, false, out error),
                    "v8 strict wire admits the file procedure alternative: " + error);
                ((JObject)strictCall.SelectToken("tool_calls[0].arguments")).Replace(new JObject {
                    ["target"] = "large.json", ["type"] = JValue.CreateNull(), ["representation"] = "tree",
                    ["pointer"] = "", ["depth"] = JValue.CreateNull(), ["offset"] = JValue.CreateNull(),
                    ["limit"] = JValue.CreateNull() });
                AssertTrue(ToolSchemaSupport.ValidateArguments(SchemaTransport(strictCall), strict, false, out error),
                    "v8 strict wire admits the file tree alternative: " + error);
                ((JObject)strictCall.SelectToken("tool_calls[0].arguments")).Replace(new JObject {
                    ["target"] = "pages.pdf", ["type"] = JValue.CreateNull(), ["representation"] = "text",
                    ["pageIndex"] = 0, ["startChar"] = 0, ["charCount"] = 10 });
                AssertTrue(ToolSchemaSupport.ValidateArguments(SchemaTransport(strictCall), strict, false, out error),
                    "v8 strict wire admits the PDF page window alternative: " + error);
                var officeArgs = new JObject { ["type"] = "office", ["target"] = "VBA module: Wide", ["representation"] = "source", ["procedure"] = "Wide" };
                AssertTrue(ToolSchemaSupport.ValidateArguments(officeArgs, schema, false, out error), "workspace Office binding exposes the same procedure selector");
                officeArgs["partNumber"] = 1;
                AssertTrue(!ToolSchemaSupport.ValidateArguments(officeArgs, schema, false, out error), "workspace Office schema also refuses mixed selectors");
            });
        }
    }
}
