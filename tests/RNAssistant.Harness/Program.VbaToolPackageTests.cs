using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
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
            AssertTrue(VbaToolManifestParser.ValidIdentifier(new string('A', 40)), "entry-point identifier limit remains 40");
            AssertTrue(!VbaToolManifestParser.ValidComponentName(new string('A', 32)), "VBE component names stop at 31 characters");
            AssertEqual("invalid_component_name",
                new VbaToolManifestParser().Parse(new string('A', 32), tool.Code).ErrorCode,
                "overlong component rejected before COM");

            var invalid = tool.Code.Replace("As String\n    Echo =", "As Variant\n    Echo =");
            var invalidResult = new VbaToolManifestParser().Parse("RNA_Echo", invalid);
            AssertEqual("entry_signature", invalidResult.ErrorCode, "String return is mandatory");

            var duplicateSafety = tool.Code.Replace(
                "\"requiresConfirmation\":true}",
                "\"requiresConfirmation\":true,\"requiresConfirmation\":false}");
            AssertEqual("manifest_invalid_json",
                new VbaToolManifestParser().Parse("RNA_Echo", duplicateSafety).ErrorCode,
                "duplicate manifest safety fields are rejected");
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
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "pipeline.json")), "VBA package does not persist an unrelated pipeline sidecar");

                var metadataPath = Path.Combine(saved.StoragePath, "tool.json");
                var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                ((JArray)metadata["Components"]).RemoveAt(1);
                File.WriteAllText(metadataPath, metadata.ToString());
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with manifest/component drift is excluded from the catalog");
                store.SaveOne(tool);

                var supportingSource = Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls");
                File.Delete(supportingSource);
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with a missing declared source is excluded from the catalog");
                store.SaveOne(tool);
                var unexpectedSource = Path.Combine(saved.StoragePath, "src", "Unexpected.bas");
                File.WriteAllText(unexpectedSource, "Option Explicit");
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with an undeclared source is excluded from the catalog");
                store.SaveOne(tool);
                var legacyForm = Path.Combine(saved.StoragePath, "src", "Legacy.frm");
                File.WriteAllText(legacyForm, "VERSION 5.00");
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with a forbidden exported form is excluded from the catalog");
                store.SaveOne(tool);
                AssertTrue(!File.Exists(legacyForm), "saving a code-only package removes the forbidden legacy form sidecar");

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

                adapter.QueueResult("excel.vba_read_module", ToolResult.Fail("VBA project is unavailable.", null, "vba_access_error", true));
                var blocked = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!blocked.Success, "unreadable VBA package state blocks execution");
                AssertEqual("vba_package_probe_failed", blocked.ErrorCode, "unreadable VBA package error code");
                AssertEqual(1, adapter.RanMacros.Count, "probe failure does not run macro");
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

        private static void VbaPackageJournalRecordsAtomicTransactions()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tool = BuildVbaPackageToolForTest();
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "run-package", TurnId = "turn-package" };

                var installed = executor.InstallVbaTool(tool, false, session);

                AssertTrue(installed.Success, "journaled package install succeeds");
                var installData = JObject.Parse(installed.DataJson);
                AssertTrue((bool)installData["packageJournaled"], "package install result exposes journal boundary");
                AssertEqual("committed", (string)installData["packageJournalStatus"], "package install journal status");
                var records = journal.ListPackageMutations("Excel", "doc");
                AssertEqual(1, records.Count, "one transaction records the complete install");
                AssertEqual(2, records[0].Prepared.Components.Count, "install transaction contains every component");
                AssertEqual("run-package", records[0].Prepared.RunId, "package transaction keeps run correlation");
                AssertEqual("turn-package", records[0].Prepared.TurnId, "package transaction keeps turn correlation");
                AssertEqual("committed", records[0].Terminal.Status, "install terminal is committed");
                AssertTrue(records[0].Terminal.Components.All(component => component.MatchesIntendedAfter), "every installed component is verified");

                var removed = executor.RemoveVbaTool(tool, session);

                AssertTrue(removed.Success, "journaled package removal succeeds");
                records = journal.ListPackageMutations("Excel", "doc");
                AssertEqual(2, records.Count, "install and removal are separate atomic transactions");
                AssertEqual("package_remove", records[1].Prepared.Operation, "second transaction is removal");
                AssertTrue(records[1].Prepared.RetainBackups, "persistent package removal retains rollback sources");
                AssertTrue(records[1].Prepared.Components.All(component => component.BeforeCodeReference != null), "removal snapshots every component in CAS");
                AssertTrue(records[1].Terminal.Components.All(component => component.MatchesIntendedAfter), "every removed component is verified absent");
                AssertEqual(2, journal.List("Excel", "doc").Count, "package removal exposes one backup per prior component");
                var casHealth = CasService(paths, new ChatStore(paths), journal, () => StorageProtector.None).Audit();
                AssertTrue(casHealth.ReachabilityComplete, "CAS scanner validates package transaction records");
                AssertEqual(0, casHealth.OrphanBlobCount, "package before/intended sources remain reachable from the journal");

                var journalPath = Directory.GetFiles(paths.VbaJournalDirectory, "mutations.events.jsonl", SearchOption.AllDirectories).Single();
                var journalText = File.ReadAllText(journalPath);
                AssertTrue(!journalText.Contains("Public Function Echo"), "package source bodies are referenced through CAS, not embedded in JSONL");
                AssertEqual(2, journal.ReadEvents("Excel", "doc").Count(item =>
                    string.Equals(item.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal)),
                    "one preparation event per package operation");
            });
        }

        private static void VbaPackageJournalReconcilesInterruptedTransaction()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var journal = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                var prepared = journal.PreparePackageMutation(new VbaPackageMutationPreparation
                {
                    Operation = "package_install",
                    PackageId = tool.Id,
                    PackageVersion = tool.PackageVersion,
                    Host = "Excel",
                    DocumentKey = "doc",
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Components = tool.Components.Select(component => new VbaPackageMutationComponent
                    {
                        ModuleName = component.Name,
                        BeforeExists = false,
                        IntendedAfterExists = true,
                        IntendedAfterComponentType = component.Type,
                        IntendedAfterCode = component.Code
                    }).ToList()
                });
                adapter.SetVbaModule(
                    tool.Components[0].Name,
                    "' RNAssistantPackage: id=" + tool.Id + ";\n" + tool.Components[0].Code,
                    tool.Components[0].Type);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));

                var read = executor.Execute(
                    Command("common.vba_read_module", "moduleName", tool.Components[0].Name),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);

                AssertTrue(read.Success, "safe VBA access continues after package reconciliation");
                var record = journal.ListPackageMutations("Excel", "doc").Single(item =>
                    string.Equals(item.Prepared.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase));
                AssertEqual("unknown", record.Terminal.Status, "mixed interrupted package state is never auto-replayed");
                AssertTrue(record.Terminal.Components.Single(item => item.ModuleName == tool.Components[0].Name).MatchesIntendedAfter,
                    "written component is recognized as intended");
                AssertTrue(record.Terminal.Components.Single(item => item.ModuleName == tool.Components[1].Name).MatchesBefore,
                    "missing component is recognized as before state");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode(tool.Components[1].Name), "reconciliation does not create the missing component");
            });
        }

        private static void VbaCodeOnlyUserFormPackageRoundTrips()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var tool = BuildVbaUserFormPackageForTest();
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths), new ToolStore(paths));

                AssertTrue(executor.ValidateToolDefinition(tool).Success, "code-only MSForm package validates");
                var saved = new ToolStore(paths).SaveOne(tool);
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormTool.bas")), "UserForm package entry uses .bas");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormToolForm.form.vba")), "code-only form uses explicit .form.vba source");
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormToolForm.frm")), "Designer .frm is never persisted");
                AssertEqual("MSForm", saved.Components.Single(component => component.Name == "RNA_FormToolForm").Type, "MSForm type roundtrips");

                var installed = executor.InstallVbaTool(saved, false);
                AssertTrue(installed.Success, "code-only UserForm package installs");
                AssertEqual("installed", executor.GetVbaInstallationStatus(saved), "code-only package read-back includes type/profile");
                AssertContains(adapter.GetVbaModuleCode("RNA_FormToolForm"), "Controls.Add", "installed form keeps code-only builder");
                AssertEqual("MSForm", journal.ListPackageMutations("Excel", "doc").Single().Prepared.Components
                    .Single(component => component.ModuleName == "RNA_FormToolForm").IntendedAfterComponentType,
                    "package journal records MSForm intent");
                AssertTrue(executor.RemoveVbaTool(saved).Success, "owned code-only UserForm package uninstalls");

                var designerExport = BuildVbaUserFormPackageForTest();
                designerExport.Components[1].Code =
                    "VERSION 5.00\nBegin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} RNA_FormToolForm\n" +
                    "   OleObjectBlob = \"RNA_FormToolForm.frx\":0000\nEnd";
                var rejected = executor.ValidateToolDefinition(designerExport);
                AssertEqual("vba_userform_designer_unsupported", rejected.ErrorCode, "exported Designer source is rejected before save/install");
            });
        }

        private static void VbaCodeOnlyUserFormPackageComLifecycle()
        {
            var document = new FakeVbaDocumentObject();
            var formCode =
                "Option Explicit\nPrivate WithEvents btnOK As MSForms.CommandButton\n" +
                "Private Sub UserForm_Initialize()\nSet btnOK = Me.Controls.Add(\"Forms.CommandButton.1\", \"btnOK\", True)\nEnd Sub";
            var componentsJson = new JArray(new JObject
            {
                ["name"] = "RNA_FormToolForm",
                ["type"] = "MSForm",
                ["code"] = formCode
            }).ToString();
            var marker = "RNAssistantPackage: id=excel.form_tool; version=1.0.0; hash=test";

            var installed = VbaProjectSupport.InstallPackage(document, componentsJson, marker);

            AssertTrue(installed.Success, "COM package creates blank MSForm without .frm import");
            var form = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single(component => component.Name == "RNA_FormToolForm");
            AssertEqual(3, form.Type, "COM package component type is MSForm");
            AssertEqual(0, form.Designer.Controls.Count, "created package form has blank Designer");
            AssertContains(form.CodeModule.Code, "RNAssistantPackage: id=excel.form_tool;", "created form has ownership marker");
            AssertContains(form.CodeModule.Code, "Controls.Add", "created form has runtime control source");

            var updatedCode = formCode.Replace("btnOK", "btnApply");
            var updatedJson = new JArray(new JObject
            {
                ["name"] = "RNA_FormToolForm",
                ["type"] = "MSForm",
                ["code"] = updatedCode
            }).ToString();
            AssertTrue(VbaProjectSupport.InstallPackage(document, updatedJson, marker).Success, "owned blank MSForm updates in place");
            AssertContains(form.CodeModule.Code, "btnApply", "MSForm code-behind update applied");

            form.Designer.Controls.Count = 1;
            var blocked = VbaProjectSupport.InstallPackage(document, componentsJson, marker);
            AssertEqual("vba_userform_designer_unsupported", blocked.ErrorCode, "Designer controls block package overwrite");
            AssertContains(form.CodeModule.Code, "btnApply", "blocked overwrite preserves live form source");
            form.Designer.Controls.Count = 0;
            form.Designer.Picture = new object();
            AssertEqual(
                "vba_userform_designer_unsupported",
                VbaProjectSupport.InstallPackage(document, componentsJson, marker).ErrorCode,
                "Designer binary assets block package overwrite");
            form.Designer.Picture = null;

            var expected = new JObject
            {
                ["RNA_FormToolForm"] = VbaToolManifestParser.CodeSha256(updatedCode)
            }.ToString();
            var removed = VbaProjectSupport.RemovePackage(document, expected, "RNAssistantPackage: id=excel.form_tool;");
            AssertTrue(removed.Success, "owned blank MSForm can be removed internally by package lifecycle");
            AssertTrue(!document.VBProject.VBComponents.Cast<FakeVbaComponent>().Any(component => component.Name == "RNA_FormToolForm"),
                "package form is absent after verified removal");
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
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var catalogService = new ToolCatalogService(adapter, executor, store);
                var catalog = catalogService.GetVisibleTools();
                var discovered = catalog.FirstOrDefault(item => string.Equals(item.Id, tool.Id, StringComparison.OrdinalIgnoreCase));

                AssertTrue(discovered != null, "document VBA tool discovered");
                AssertEqual("document", discovered.Scope, "document scope");
                AssertEqual(2, discovered.Components.Count, "document components resolved");
                AssertTrue(!adapter.Executed.Any(item =>
                    string.Equals(item.ToolId, "excel.vba_read_module", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(item.Arguments["moduleName"]), "Module1", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery skips standard modules without a manifest");
                var discoveryCalls = adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase));
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery uses short cache");
                adapter.RuntimeDocumentKeyValue = "runtime-reopened-document";
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls + 1, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery cache is scoped to the runtime document");
                catalogService.InvalidateDocumentVbaTools();
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls + 2, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery cache invalidates");

                var result = executor.Execute(
                    Command(discovered.Id, "text", "hello", "count", 2, "ratio", 1.5),
                    catalog,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "document VBA tool runs");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "document source is not removed after run");

                store.SaveOne(tool);
                var duplicateCode = tool.Components[0].Code.Replace(
                    "\"components\":[\"RNA_Echo\",",
                    "\"components\":[\"RNA_EchoDuplicate\",");
                adapter.SetVbaModule("RNA_EchoDuplicate", duplicateCode, "StdModule");
                catalogService.InvalidateDocumentVbaTools();
                var duplicateCatalog = catalogService.GetVisibleTools();
                AssertTrue(duplicateCatalog.Any(item =>
                    item.Id.StartsWith(tool.Id + "#document", StringComparison.OrdinalIgnoreCase) &&
                    !item.Enabled && string.Equals(item.CapabilityStatus, "id_collision", StringComparison.OrdinalIgnoreCase)),
                    "second document manifest cannot hide behind matched global package");
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
                "' {\"protocolVersion\":1,\"id\":\"excel.echo_vba\",\"name\":\"Echo VBA\",\"description\":\"Return typed arguments.\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"Echo\",\"components\":[\"RNA_Echo\",\"RNA_EchoService\"],\"argumentOrder\":[\"text\",\"count\",\"ratio\",\"enabled\"],\"parameters\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Text value.\"},\"count\":{\"type\":\"integer\",\"description\":\"Integer value.\"},\"ratio\":{\"type\":\"number\",\"description\":\"Numeric value.\"},\"enabled\":{\"type\":\"boolean\",\"description\":\"Boolean value.\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
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
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Text value.\"},\"count\":{\"type\":\"integer\",\"description\":\"Integer value.\"},\"ratio\":{\"type\":\"number\",\"description\":\"Numeric value.\"},\"enabled\":{\"type\":\"boolean\",\"description\":\"Boolean value.\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false}",
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

        private static ToolDefinition BuildVbaUserFormPackageForTest()
        {
            var entryCode =
                "Option Explicit\n" +
                "' <RNAssistantTool>\n" +
                "' {\"protocolVersion\":1,\"id\":\"excel.form_tool\",\"name\":\"Form Tool\",\"description\":\"Show a code-only form.\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"ShowForm\",\"components\":[\"RNA_FormTool\",\"RNA_FormToolForm\"],\"argumentOrder\":[],\"parameters\":{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                "' </RNAssistantTool>\n" +
                "Public Function ShowForm() As String\n" +
                "    RNA_FormToolForm.Show\n" +
                "    ShowForm = \"shown\"\n" +
                "End Function";
            var formCode =
                "Option Explicit\n" +
                "Private WithEvents btnOK As MSForms.CommandButton\n" +
                "Private Sub UserForm_Initialize()\n" +
                "    Me.Caption = \"Parameters\"\n" +
                "    Set btnOK = Me.Controls.Add(\"Forms.CommandButton.1\", \"btnOK\", True)\n" +
                "End Sub\n" +
                "Private Sub btnOK_Click()\n" +
                "    Unload Me\n" +
                "End Sub";
            return new ToolDefinition
            {
                Id = "excel.form_tool",
                Host = "Excel",
                Name = "Form Tool",
                Description = "Show a code-only form.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}",
                Executor = "vba",
                Code = entryCode,
                Enabled = true,
                BuiltIn = false,
                MutatesDocument = true,
                RequiresConfirmation = true,
                AgentCanRun = false,
                RiskLevel = 3,
                PackageVersion = "1.0.0",
                EntryPoint = "ShowForm",
                Components = new List<VbaToolComponent>
                {
                    new VbaToolComponent { Name = "RNA_FormTool", Type = "StdModule", Code = entryCode },
                    new VbaToolComponent { Name = "RNA_FormToolForm", Type = "MSForm", Code = formCode }
                }
            };
        }
    }
}
