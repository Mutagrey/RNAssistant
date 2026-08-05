using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaToolManifestValidatesTypedEntryPoint()
        {
            var tool = BuildVbaPackageToolForTest();
            var parsed = new VbaToolManifestParser().Parse("RNA_Echo", tool.Code);

            AssertTrue(parsed.Success, "typed VBA manifest accepted");
            AssertEqual("excel.echo_vba", parsed.Tool.Id, "manifest id");
            AssertEqual("Echo", parsed.Tool.EntryPoint, "entry point");
            AssertEqual(4, parsed.Tool.ArgumentOrder.Count, "argument order");
            AssertEqual(2, parsed.Tool.Components.Count, "declared components");

            var invalid = tool.Code.Replace("As String\n    Echo =", "As Variant\n    Echo =");
            var invalidResult = new VbaToolManifestParser().Parse("RNA_Echo", invalid);
            AssertEqual("entry_signature", invalidResult.ErrorCode, "String return is mandatory");
        }

        private static void VbaToolStoreRoundTripsPackageSources()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var tool = BuildVbaPackageToolForTest();
                tool.Components[1].FileName = "wrong-extension.bas";
                var saved = store.SaveOne(tool);

                AssertTrue(saved != null, "VBA package saved");
                AssertEqual(2, saved.Components.Count, "component metadata roundtrip");
                AssertContains(saved.Code, "<RNAssistantTool>", "entry source roundtrip");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_Echo.bas")), "entry .bas stored");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls")), "class .cls stored");
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "wrong-extension.bas")), "component filename derived from name and type");

                tool.Components.RemoveAt(1);
                tool.Code = tool.Code.Replace(",\"RNA_EchoService\"", string.Empty);
                store.SaveOne(tool);
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls")), "removed source file cleaned");
            });
        }

        private static void VbaToolPackageRejectsDuplicateSources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                tool.Components.Add(new VbaToolComponent
                {
                    Name = "RNA_EchoService",
                    Type = "ClassModule",
                    Code = "Option Explicit"
                });
                var validation = executor.ValidateToolDefinition(tool);
                AssertEqual("vba_component_duplicate", validation.ErrorCode, "duplicate component rejected before save");
            });
        }

        private static void VbaToolPackageReservesInternalCommandIds()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                tool.Id = "excel.vba_install_package_internal";
                tool.Code = tool.Code.Replace("excel.echo_vba", tool.Id);

                var validation = executor.ValidateToolDefinition(tool);

                AssertEqual("reserved_tool_id", validation.ErrorCode, "internal VBA command id is reserved");
            });
        }

        private static void VbaToolSessionExecutionUsesTypedArgumentsAndCleansUp()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                var command = Command(tool.Id, "text", "hello", "count", 2, "ratio", 1.5);
                var tools = adapter.GetBuiltInTools().Concat(new[] { tool }).ToList();

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "session VBA tool succeeds");
                AssertEqual("fake-vba-result", result.Message, "String output wrapped by runtime");
                var run = adapter.Executed.Last(item => string.Equals(item.ToolId, "excel.run_macro", StringComparison.OrdinalIgnoreCase));
                AssertEqual("[\"hello\",2,1.5,true]", Convert.ToString(run.Arguments["argumentsJson"]), "typed positional arguments and default");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "entry module cleaned");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_EchoService"), "class module cleaned");
            });
        }

        private static void VbaToolPersistentInstallRequiresMacroDocumentAndTracksOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                var blocked = executor.InstallVbaTool(tool, false);
                AssertEqual("vba_macro_enabled_document_required", blocked.ErrorCode, "non-macro document blocked");

                adapter.SetDocumentTitle("Harness.xlsm");
                var installed = executor.InstallVbaTool(tool, false);
                AssertTrue(installed.Success, "macro-enabled install succeeds");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "RNAssistantPackage:", "ownership marker installed");
                AssertEqual("installed", executor.GetVbaInstallationStatus(tool), "installation status");

                var removed = executor.RemoveVbaTool(tool);
                AssertTrue(removed.Success, "owned package uninstalled");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "owned entry removed");

                adapter.SetVbaModule("RNA_Echo", tool.Components[0].Code, "StdModule");
                adapter.SetVbaModule("RNA_EchoService", tool.Components[1].Code, "ClassModule");
                var notOwned = executor.RemoveVbaTool(tool);
                AssertEqual("vba_component_not_owned", notOwned.ErrorCode, "unmarked local source preserved");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "unmarked source remains");
            });
        }

        private static void VbaDocumentToolsAreDiscoveredAndRunnable()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = BuildVbaPackageToolForTest();
                adapter.SetVbaModule("RNA_Echo", tool.Components[0].Code, "StdModule");
                adapter.SetVbaModule("RNA_EchoService", tool.Components[1].Code, "ClassModule");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths), store);
                var catalog = new ToolCatalogService(adapter, executor, store).GetVisibleTools();
                var discovered = catalog.FirstOrDefault(item => string.Equals(item.Id, tool.Id, StringComparison.OrdinalIgnoreCase));

                AssertTrue(discovered != null, "document VBA tool discovered");
                AssertEqual("document", discovered.Scope, "document scope");
                AssertEqual("document", discovered.Scope, "document scope");
                AssertEqual(2, discovered.Components.Count, "document components resolved");

                var result = executor.Execute(
                    Command(discovered.Id, "text", "hello", "count", 2, "ratio", 1.5),
                    catalog,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "document VBA tool runs");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "document source is not removed after run");
            });
        }

        private static void VbaCodeHashIgnoresExportHeadersAndRuntimeMarkers()
        {
            var source = "Option Explicit\nPublic Function Value() As String\n    Value = \"ok\"\nEnd Function";
            var exported =
                "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND\n" +
                "Attribute VB_Name = \"RNA_Class\"\n" +
                "Attribute VB_GlobalNameSpace = False\n" +
                "Attribute VB_Creatable = False\n" +
                "Attribute VB_PredeclaredId = False\n" +
                "Attribute VB_Exposed = False\n" +
                "' RNAssistantSession: id=excel.echo_vba; version=1.0.0\n" + source;
            AssertEqual(VbaToolManifestParser.CodeSha256(source), VbaToolManifestParser.CodeSha256(exported), "normalized export hash");

            var versionedWithoutAttributes = "VERSION 1.0 CLASS\n" + source;
            AssertContains(VbaToolManifestParser.NormalizeCode(versionedWithoutAttributes), "VERSION 1.0 CLASS", "non-export VERSION source is preserved");
        }

        private static ToolDefinition BuildVbaPackageToolForTest()
        {
            var entryCode =
                "Option Explicit\n" +
                "' <RNAssistantTool>\n" +
                "' {\"protocolVersion\":1,\"id\":\"excel.echo_vba\",\"name\":\"Echo VBA\",\"description\":\"Return typed arguments.\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"Echo\",\"components\":[\"RNA_Echo\",\"RNA_EchoService\"],\"argumentOrder\":[\"text\",\"count\",\"ratio\",\"enabled\"],\"parameters\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"},\"ratio\":{\"type\":\"number\"},\"enabled\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                "' </RNAssistantTool>\n" +
                "Public Function Echo(ByVal text As String, ByVal count As Long, ByVal ratio As Double, ByVal enabled As Boolean) As String\n" +
                "    Echo = text & CStr(count) & CStr(ratio) & CStr(enabled)\n" +
                "End Function";
            var classCode = "Option Explicit\nPublic Function Prefix(ByVal value As String) As String\n    Prefix = value\nEnd Function";
            return new ToolDefinition
            {
                Id = "excel.echo_vba",
                Host = "Excel",
                Name = "Echo VBA",
                Description = "Return typed arguments.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"},\"ratio\":{\"type\":\"number\"},\"enabled\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false}",
                Executor = "vba",
                Code = entryCode,
                Enabled = true,
                BuiltIn = false,
                MutatesDocument = true,
                RequiresConfirmation = true,
                AgentCanRun = false,
                RiskLevel = 3,
                PackageVersion = "1.0.0",
                EntryPoint = "Echo",
                ArgumentOrder = new List<string> { "text", "count", "ratio", "enabled" },
                Components = new List<VbaToolComponent>
                {
                    new VbaToolComponent { Name = "RNA_Echo", Type = "StdModule", FileName = "RNA_Echo.bas", Code = entryCode },
                    new VbaToolComponent { Name = "RNA_EchoService", Type = "ClassModule", FileName = "RNA_EchoService.cls", Code = classCode }
                }
            };
        }
    }
}
