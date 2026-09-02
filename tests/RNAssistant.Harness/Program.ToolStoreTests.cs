using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ValidatesToolSaveAndPreservesMetadata()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    store);
                var invalid = new ToolCatalogEntry
                {
                    Id = "excel.invalid",
                    Host = "Excel",
                    Executor = "pipeline",
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    Enabled = true
                };

                var invalidResult = executor.ValidateToolDefinition(invalid);
                AssertTrue(!invalidResult.Success, "invalid tool rejected");
                AssertContains(invalidResult.Message, "disabled", "invalid tool error");

                var valid = CustomTool("Excel", "excel.safe_report", "Safe report");
                valid.UseWhen = "Create a report.";

                AssertTrue(executor.ValidateToolDefinition(valid).Success, "valid tool accepted");
                var invalidHost = valid.Clone();
                invalidHost.Host = "UnknownOffice";
                AssertEqual("invalid_tool_host", executor.ValidateToolDefinition(invalidHost).ErrorCode, "unknown tool host rejected");
                var oversizedCatalogEntry = valid.Clone();
                oversizedCatalogEntry.AgentCanRun = true;
                oversizedCatalogEntry.Description = new string('x', 7000);
                AssertTrue(executor.ValidateToolDefinition(oversizedCatalogEntry).Success,
                    "storage validation does not duplicate runtime prompt budgeting");
                oversizedCatalogEntry.AgentCanRun = true;
                oversizedCatalogEntry.Description = new string('x', 7000);
                AssertTrue(ConversationRunService.PrepareToolsForRun(
                        OfficeToolCatalog.ForHost(adapter.HostName).Concat(new[] { oversizedCatalogEntry }))
                    .Any(tool => string.Equals(tool.Id, oversizedCatalogEntry.Id, StringComparison.OrdinalIgnoreCase)),
                    "valid catalog entry remains runnable and the complete prompt budget decides whether the request fits");
                store.SaveOne(valid);
                var loaded = store.Load().First(t => string.Equals(t.Id, valid.Id, StringComparison.OrdinalIgnoreCase));
                AssertTrue(loaded.MutatesDocument, "mutation metadata preserved");
                AssertTrue(!loaded.AgentCanRun, "agent run metadata preserved");
                AssertEqual(3, loaded.RiskLevel, "risk metadata preserved");
                AssertEqual(valid.UseWhen, loaded.UseWhen, "useWhen preserved");
            });
        }

        private static void ToolCatalogMergesVisibleTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var toolStore = new ToolStore(paths);
                toolStore.Save(new[]
                {
                    CustomTool("Excel", "excel.custom"),
                    CustomTool("Word", "word.hidden")
                });
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var catalog = new ToolCatalogService(adapter, executor, toolStore).GetVisibleTools();

                AssertTrue(HasTool(catalog, "excel.add_sheet"), "built-in tool visible");
                AssertTrue(HasTool(catalog, "common.vba_apply_patch"), "common controller VBA tool visible");
                AssertTrue(HasTool(catalog, "common.resources_read"), "common built-in tool visible");
                AssertTrue(HasTool(catalog, "excel.custom"), "host custom tool visible");
                AssertTrue(!HasTool(catalog, "word.hidden"), "other host custom tool hidden");
            });
        }

        private static void VbaFacadeIsCommonAcrossHosts()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                foreach (var host in new[] { "Excel", "Word", "PowerPoint" })
                {
                    var adapter = FakeOfficeAdapter.ForHost(host);
                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                    var tools = executor.GetControllerTools().ToList();
                    AssertTrue(HasTool(tools, ResourceToolCatalog.ReadToolId) &&
                        HasTool(tools, ResourceToolCatalog.FindToolId),
                        host + " exposes shared resource reads");
                    AssertTrue(HasTool(tools, "common.vba_write_module"), host + " exposes common VBA upsert");
                    AssertTrue(HasTool(tools, "common.vba_apply_patch"), host + " exposes common VBA patch");
                    AssertTrue(!HasTool(tools, "common.vba_read_module") &&
                        !HasTool(tools, "common.vba_search_code") &&
                        !HasTool(tools, "common.vba_list_backups") &&
                        !HasTool(tools, "common.vba_read_lines") &&
                        !HasTool(tools, "common.vba_list_modules") &&
                        !HasTool(tools, "common.vba_replace_text") &&
                        !HasTool(tools, "common.vba_create_module"), host + " omits redundant public aliases");
                    var vbaTools = tools.Where(tool => (tool.Id ?? string.Empty).StartsWith("common.vba_", StringComparison.OrdinalIgnoreCase)).ToList();
                    AssertEqual(4, vbaTools.Count, host + " exposes only the four mutation-specific VBA tools");
                    AssertTrue(vbaTools.All(tool => string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase)), host + " VBA facade is host-neutral");
                    AssertTrue(!HasTool(tools, host.ToLowerInvariant() + ".vba_apply_patch"), host + " does not publish a host-specific patch facade");
                    var hostPrefix = host.ToLowerInvariant() + ".";
                    AssertTrue(!OfficeToolCatalog.ForHost(adapter.HostName).Any(tool =>
                        (tool.Id ?? string.Empty).StartsWith(hostPrefix + "vba_", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tool.Id, hostPrefix + "insert_vba_module", StringComparison.OrdinalIgnoreCase)),
                        host + " omits internal VBA backend from the visible tool catalog");
                    AssertTrue(!OfficeToolCatalog.ForHost(adapter.HostName).Any(tool =>
                        string.Equals(tool.Id, hostPrefix + "run_macro", StringComparison.OrdinalIgnoreCase)),
                        host + " keeps the host macro backend out of the public adapter catalog");
                    var runMacro = tools.Single(tool =>
                        string.Equals(tool.Id, "common.office_run_macro", StringComparison.OrdinalIgnoreCase));
                    AssertTrue(runMacro.AgentCanRun && runMacro.MutatesDocument && runMacro.RequiresConfirmation &&
                        runMacro.RiskLevel == 3,
                        host + " publishes one host-neutral confirmed macro tool with high-risk metadata");
                    AssertContains(runMacro.ArgumentSchemaJson, "\"arguments\"", host + " macro tool accepts positional arguments");
                }

                var outlook = FakeOfficeAdapter.ForHost("Outlook");
                var outlookExecutor = new OfficeToolExecutor(outlook, new VbaJournalStore(paths), new SkillStore(paths));
                AssertTrue(!HasTool(outlookExecutor.GetControllerTools(), "common.vba_apply_patch"), "Outlook does not expose VBA facade");
                AssertTrue(!HasTool(outlookExecutor.GetControllerTools(), "common.office_run_macro"), "Outlook does not expose unsupported Application.Run");

                var excel = FakeOfficeAdapter.ForHost("Excel");
                var store = new ToolStore(paths);
                var excelExecutor = new OfficeToolExecutor(excel, new VbaJournalStore(paths), new SkillStore(paths), store);

                foreach (var removedId in new[]
                {
                    "common.vba_list_modules",
                    "common.vba_read_lines",
                    "common.vba_replace_text",
                    "common.vba_create_module",
                    "excel.vba_read_module",
                    "excel.vba_apply_patch"
                })
                {
                    var result = excelExecutor.ExecuteManual(
                        new ToolInvocation { ToolId = removedId },
                        OfficeToolCatalog.ForHost(excel.HostName).Concat(excelExecutor.GetControllerTools()).ToList(),
                        new AppSettings(),
                        false,
                        false);
                    AssertEqual("unknown_tool", result.ErrorCode, removedId + " is removed");
                }

                var macro = excelExecutor.RunVbaMacro("Module1.DemoMacro", NewSession(excel));
                AssertEqual("unknown", macro.Status,
                    "typed macro execution does not infer an unverified effect");
                AssertEqual(false, macro.Retryable,
                    "dispatched macro is not automatically retryable");
                AssertEqual("Module1.DemoMacro", excel.RanMacros.Last(), "typed macro name reaches the adapter");
            });
        }

        private static void BuiltInToolIdsCannotBeShadowed()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var shadow = CustomTool("Excel", "excel.add_sheet");
                var store = new ToolStore(paths);
                store.SaveOne(shadow);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);

                var catalogTool = FindTool(new ToolCatalogService(adapter, executor, store).GetVisibleTools(), shadow.Id);
                AssertTrue(catalogTool != null && catalogTool.BuiltIn, "catalog keeps built-in definition");
                var command = new ToolInvocation { ToolId = shadow.Id };
                command.Arguments["name"] = "Protected";
                var result = executor.ExecuteManual(command, new[] { shadow },
                    new AppSettings { AutoConfirmToolActions = true }, false, false,
                    NewSession(adapter));

                AssertTrue(result.Success, "built-in executes despite custom collision");
                AssertTrue(adapter.HasSheet("Protected"), "built-in add sheet was executed");
                AssertEqual(1, adapter.ExcelSheetRequests.Count(item => string.Equals(item.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase)), "built-in add sheet executed once");

                var save = new ToolInvocation { ToolId = "common.tools_upsert" };
                save.Arguments["id"] = shadow.Id;
                save.Arguments["host"] = "Excel";
                save.Arguments["description"] = "Invalid shadow.";
                save.Arguments["executor"] = "vba";
                save.Arguments["parameters"] = JObject.Parse(EmptyFormalToolSchema);
                save.Arguments["components"] = ToolComponentsPayload(shadow);
                var saveResult = executor.ExecuteManual(save, OfficeToolCatalog.ForHost(adapter.HostName).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!saveResult.Success, "controller rejects reserved id");
                AssertEqual("reserved_tool_id", saveResult.ErrorCode, "reserved id error code");

            });
        }

        private static void RefreshedCustomToolGetsEffectiveSafety()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).ToList();
                var tool = CustomTool("Excel", "excel.dynamic_mutation");
                tool.AgentCanRun = false;
                tool.MutatesDocument = false;
                tool.RiskLevel = 0;

                tools.Add(tool);
                var profile = ToolSafetyPolicy.Resolve(tool, tools);
                AssertTrue(profile.MutatesDocument, "VBA mutation propagated");
                AssertTrue(!profile.AgentCanRun, "VBA mutation agent safety propagated");
                AssertTrue(profile.RiskLevel > 0, "VBA mutation risk propagated");
                AssertTrue(!tool.MutatesDocument, "source tool remains unchanged");
            });
        }

        private static void ExpandedBuiltInToolsAreVisible()
        {
            var excel = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost("Excel"));
            AssertTrue(HasTool(excel, "excel.inspect"), "excel inspection facade visible");
            AssertContains(FindTool(excel, "excel.inspect").Description, "Not a write preflight",
                "excel inspection contract discourages unchanged preflight loops");
            AssertTrue(HasTool(excel, "excel.read_range"), "excel range reader visible");
            AssertTrue(HasTool(excel, "excel.find_cells"), "excel find cells visible");
            AssertTrue(FindTool(excel, "excel.replace_cells").ArgumentSchemaJson.IndexOf("expectedScopeSha256", StringComparison.Ordinal) < 0,
                "excel replacement owns current-scope checks");
            AssertTrue(HasTool(excel, "excel.add_table"), "excel add table visible");
            AssertTrue(HasTool(excel, "excel.upsert_chart"), "excel chart upsert facade visible");
            AssertTrue(FindTool(excel, "excel.clear_range").RequiresConfirmation, "excel clear requires confirmation");
            AssertTrue(!HasTool(excel, "excel.run_macro"), "excel macro backend is hidden from built-ins");
            AssertTrue(!HasTool(excel, "excel.get_context") && !HasTool(excel, "excel.get_selection"),
                "generic Excel context and selection reads use document resources");

            var word = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost("Word"));
            AssertTrue(!HasTool(word, "word.get_context"), "generic Word context uses document resources");
            AssertTrue(HasTool(word, "word.read_text"), "word text reader facade visible");
            AssertTrue(HasTool(word, "word.inspect"), "word inspection facade visible");
            AssertTrue(HasTool(word, "word.find_text"), "word find text visible");
            AssertTrue(FindTool(word, "word.replace_text").ArgumentSchemaJson.IndexOf("expectedMatches", StringComparison.Ordinal) < 0,
                "word replacement has no model-owned precondition");
            AssertTrue(HasTool(word, "word.format_text"), "word formatting facade visible");
            AssertTrue(HasTool(word, "word.add_table"), "word add table visible");
            AssertTrue(!HasTool(word, "word.run_macro"), "word macro backend is hidden from built-ins");

            var powerpoint = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost("PowerPoint"));
            AssertTrue(!HasTool(powerpoint, "powerpoint.get_context") &&
                !HasTool(powerpoint, "powerpoint.get_selection"),
                "generic PowerPoint context and selection reads use document resources");
            AssertTrue(HasTool(powerpoint, "powerpoint.list_objects"), "powerpoint list facade visible");
            AssertTrue(HasTool(powerpoint, "powerpoint.set_text") && HasTool(powerpoint, "powerpoint.add_object"), "powerpoint mutation facades visible");
            AssertTrue(FindTool(powerpoint, "powerpoint.move_slide").RequiresConfirmation, "powerpoint move requires confirmation");
            AssertTrue(!HasTool(powerpoint, "powerpoint.run_macro"), "powerpoint macro backend is hidden from built-ins");

            var outlook = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost("Outlook"));
            AssertTrue(!HasTool(outlook, "outlook.get_context"), "generic Outlook context uses document resources");
            AssertTrue(HasTool(outlook, "outlook.search_mail"), "outlook search visible");
            AssertTrue(HasTool(outlook, "outlook.create_draft"), "outlook draft facade visible");
            AssertTrue(FindTool(outlook, "outlook.update_mail").AgentCanRun, "outlook mail updates remain runnable");

            var catalogs = new Dictionary<string, List<ToolCatalogEntry>>
            {
                { "Excel", excel },
                { "Word", word },
                { "PowerPoint", powerpoint },
                { "Outlook", outlook }
            };
            AssertEqual(15, excel.Count, "complete Excel tool count");
            AssertEqual(9, word.Count, "complete Word tool count");
            AssertEqual(9, powerpoint.Count, "complete PowerPoint tool count");
            AssertEqual(5, outlook.Count, "complete Outlook tool count");
            foreach (var catalog in catalogs)
            {
                AssertEqual(catalog.Value.Count, catalog.Value.Select(tool => tool.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), catalog.Key + " ids are unique");
                var adapter = FakeOfficeAdapter.ForHost(catalog.Key);
                WithTempExecutor(adapter, delegate(OfficeToolExecutor executor, FakeOfficeAdapter fake)
                {
                    foreach (var tool in catalog.Value)
                    {
                        JObject schema;
                        string schemaError;
                        AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out schemaError), tool.Id + " schema: " + schemaError);
                        var variants = schema["anyOf"] is JArray
                            ? ((JArray)schema["anyOf"]).OfType<JObject>().ToArray()
                            : new[] { schema };
                        foreach (var variant in variants)
                        {
                            var arguments = MinimalValidArguments(variant);
                            string argumentError;
                            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError), tool.Id + " variant arguments: " + argumentError);
                            var command = new ToolInvocation { ToolId = tool.Id };
                            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
                            var result = executor.ExecuteManual(command, catalog.Value, new AppSettings { AutoConfirmToolActions = true }, false, true);
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "unknown_tool", StringComparison.OrdinalIgnoreCase), tool.Id + " dispatch is registered");
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "invalid_arguments", StringComparison.OrdinalIgnoreCase), tool.Id + " published schema reaches its handler");
                        }
                    }
                });
            }

            JObject readRangeSchema;
            string readRangeSchemaError;
            AssertTrue(ToolSchemaSupport.TryParse(FindTool(excel, "excel.read_range"), out readRangeSchema, out readRangeSchemaError), "excel.read_range schema parses");
            string invalidRangeError;
            AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["kind"] = "values" }, readRangeSchema, true, out invalidRangeError), "excel.read_range rejects foreign kind");
            AssertContains(invalidRangeError, "kind", "excel.read_range invalid field diagnostic");
        }

        private static JObject MinimalValidArguments(JObject schema)
        {
            var alternatives = schema == null ? null : schema["anyOf"] as JArray;
            if (alternatives != null)
            {
                var first = alternatives.OfType<JObject>().FirstOrDefault();
                if (first != null) return MinimalValidArguments(first);
            }
            var result = new JObject();
            var properties = schema == null ? null : schema["properties"] as JObject ?? new JObject();
            foreach (var name in (schema == null ? new JArray() : schema["required"] as JArray ?? new JArray()).Values<string>())
            {
                var property = properties[name] as JObject;
                if (property != null) result[name] = MinimalSchemaValue(property);
            }
            return result;
        }

        private static JToken MinimalSchemaValue(JObject schema)
        {
            if (schema["default"] != null) return schema["default"].DeepClone();
            var alternatives = schema["anyOf"] as JArray;
            if (alternatives != null)
            {
                var first = alternatives.OfType<JObject>().FirstOrDefault();
                return first == null ? JValue.CreateNull() : MinimalSchemaValue(first);
            }
            var values = schema["enum"] as JArray;
            if (values != null && values.Count > 0) return values[0].DeepClone();
            var type = schema["type"];
            var typeName = type is JArray
                ? ((JArray)type).Values<string>().FirstOrDefault(value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                : (string)type;
            if (string.Equals(typeName, "object", StringComparison.OrdinalIgnoreCase)) return MinimalValidArguments(schema);
            if (string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase))
            {
                var array = new JArray();
                var count = Math.Max(1, (int?)schema["minItems"] ?? 0);
                var items = schema["items"] as JObject ?? new JObject { ["type"] = "string" };
                for (var index = 0; index < count; index++) array.Add(MinimalSchemaValue(items));
                return array;
            }
            if (string.Equals(typeName, "boolean", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(typeName, "integer", StringComparison.OrdinalIgnoreCase)) return (int?)schema["minimum"] ?? 1;
            if (string.Equals(typeName, "number", StringComparison.OrdinalIgnoreCase)) return (double?)schema["minimum"] ?? 1d;
            var minLength = Math.Max(1, (int?)schema["minLength"] ?? 1);
            return new string('x', minLength);
        }

        private static void PromptToolMetadataIsWeakModelFriendly()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            WithTempExecutor(adapter, delegate(OfficeToolExecutor executor, FakeOfficeAdapter fake)
            {
                var tools = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(fake.HostName));
                tools.AddRange(executor.GetControllerTools());
                var prompt = FlattenMessages(new ConversationPromptComposer().BuildMessages(
                    ChatModes.Agent,
                    "Test request",
                    fake,
                    tools,
                    new SkillDefinition[0],
                    new DocumentContext(),
                    new AppSettings(),
                    NewSession(fake),
                    null));

                AssertContains(prompt, "\"mutates_document\":false",
                    "prompt includes typed read safety");
                AssertContains(prompt, "\"mutates_document\":true",
                    "prompt includes typed mutation safety");
                AssertContains(prompt, "\"requires_confirmation\":true",
                    "prompt includes typed confirmation metadata");
                AssertTrue(prompt.IndexOf("\"optional\"", StringComparison.OrdinalIgnoreCase) < 0, "prompt has no literal optional args");
                AssertContains(prompt, "common.tools_validate", "prompt includes tool validation");
                AssertContains(prompt, "common.prompts_read", "prompt includes prompt reader");

                var promptTools = ConversationPromptComposer.BuildTools(tools);
                var bindParameters = (JObject)promptTools.OfType<JObject>()
                    .Single(item => string.Equals((string)item.SelectToken("function.name"), HtmlWorkspaceToolCatalog.BindDataToolId, StringComparison.OrdinalIgnoreCase))
                    .SelectToken("function.parameters");
                AssertTrue(bindParameters["properties"] is JObject &&
                        bindParameters["anyOf"] == null,
                    "prompt exposes one semantic HTML bind schema");
                AssertTrue(bindParameters.SelectToken("properties.sourceTool") == null &&
                        bindParameters.SelectToken("properties.sourceArguments") == null,
                    "prompt does not expose nested source execution");

                var skillParameters = (JObject)promptTools.OfType<JObject>()
                    .Single(item => string.Equals((string)item.SelectToken("function.name"), "common.skills_upsert", StringComparison.OrdinalIgnoreCase))
                    .SelectToken("function.parameters");
                AssertTrue(skillParameters["properties"] == null && ((JArray)skillParameters["anyOf"]).Count == 2,
                    "prompt exposes separate skill core and reference calls without a mixed union envelope");
            });
        }

        private static void ToolStoreSavesAndUpdatesCustomTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var initial = CustomTool("Excel", "excel.custom_report", "Initial report");
                initial.Readme = "Creates a report sheet.";
                var otherHost = CustomTool("Word", "word.review");
                store.Save(new[] { initial, otherHost });

                var loadedInitial = FindTool(store.Load(), "excel.custom_report");
                AssertTrue(loadedInitial != null, "initial custom tool loaded");
                AssertEqual("Initial report", loadedInitial.Name, "initial name");
                AssertContains(loadedInitial.Code, "Initial report", "initial source");
                AssertContains(loadedInitial.Readme, "report sheet", "initial readme");
                AssertTrue(!string.IsNullOrWhiteSpace(loadedInitial.StoragePath), "storage path set");

                var edited = CustomTool("Excel", "excel.custom_report", "Updated report");
                edited.RequiresConfirmation = true;
                edited.MutatesDocument = true;
                edited.RiskLevel = 2;
                store.Save(new[] { edited }, "Excel");

                var loaded = store.Load();
                var updated = FindTool(loaded, "excel.custom_report");
                AssertTrue(updated != null, "updated custom tool loaded");
                AssertEqual("Updated report", updated.Name, "updated name");
                AssertTrue(updated.RequiresConfirmation, "updated confirmation flag");
                AssertContains(updated.Code, "Updated report", "updated source");
                AssertTrue(HasTool(loaded, "word.review"), "other host preserved");
            });
        }

        private static void ToolStoreSkipsBrokenCustomToolFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var validDirectory = Path.Combine(paths.ToolsDirectory, "excel", "valid");
                var brokenDirectory = Path.Combine(paths.ToolsDirectory, "excel", "broken");
                var oversizedDirectory = Path.Combine(paths.ToolsDirectory, "excel", "oversized");
                var duplicateDirectory = Path.Combine(paths.ToolsDirectory, "excel", "duplicate");
                var invalidUtf8Directory = Path.Combine(paths.ToolsDirectory, "excel", "invalid_utf8");
                Directory.CreateDirectory(validDirectory);
                Directory.CreateDirectory(brokenDirectory);
                Directory.CreateDirectory(oversizedDirectory);
                Directory.CreateDirectory(duplicateDirectory);
                Directory.CreateDirectory(invalidUtf8Directory);
                File.WriteAllText(Path.Combine(validDirectory, "tool.json"), JsonConvert.SerializeObject(CustomTool("Excel", "excel.valid")));
                Directory.CreateDirectory(Path.Combine(validDirectory, "src"));
                File.WriteAllText(Path.Combine(validDirectory, "src", "RNA_Test.bas"), CustomTool("Excel", "excel.valid").Code);
                File.WriteAllText(Path.Combine(brokenDirectory, "tool.json"), "{ broken");
                File.WriteAllText(Path.Combine(oversizedDirectory, "tool.json"), JsonConvert.SerializeObject(CustomTool("Excel", "excel.oversized")));
                Directory.CreateDirectory(Path.Combine(oversizedDirectory, "src"));
                File.WriteAllText(Path.Combine(oversizedDirectory, "src", "RNA_Test.bas"), new string('x', 4100001));
                var duplicateJson = JsonConvert.SerializeObject(CustomTool("Excel", "excel.duplicate"));
                File.WriteAllText(Path.Combine(duplicateDirectory, "tool.json"),
                    duplicateJson.Insert(1, "\"Id\":\"excel.shadow\","));
                File.WriteAllBytes(Path.Combine(invalidUtf8Directory, "tool.json"),
                    new byte[] { 0xef, 0xbb, 0xbf, 0xc3, 0x28 });

                var loaded = new ToolStore(paths).Load();

                AssertEqual(1, loaded.Count, "loaded tool count");
                AssertEqual("excel.valid", loaded[0].Id, "loaded tool id");
                AssertContains(loaded[0].Code, "Public Function Run", "source sidecar loaded");
                new ToolStore(paths).SaveOne(CustomTool("Excel", "excel.new"));
                AssertTrue(File.Exists(Path.Combine(brokenDirectory, "tool.json")), "broken tool preserved during save");
            });
        }

        private static void ToolStorePreservesExtraFilesAndOtherTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var first = CustomTool("Excel", "excel.first");
                var second = CustomTool("Word", "word.second");
                store.Save(new[] { first, second });

                var firstStored = FindTool(store.Load(), first.Id);
                var extraPath = Path.Combine(firstStored.StoragePath, "notes.txt");
                File.WriteAllText(extraPath, "keep");
                first.Name = "Updated";
                store.SaveOne(first);

                AssertTrue(File.Exists(extraPath), "tool extra file preserved");
                AssertTrue(HasTool(store.Load(), second.Id), "other tool preserved");
                AssertTrue(store.Delete(first.Id), "first tool deleted");
                AssertTrue(HasTool(store.Load(), second.Id), "other tool survives delete");

                var dotted = CustomTool("Excel", "excel.collision");
                var underscored = CustomTool("Excel", "excel_collision");
                store.SaveOne(dotted);
                store.SaveOne(underscored);
                var collisionTools = store.Load();
                AssertTrue(HasTool(collisionTools, dotted.Id), "dotted tool id survives storage collision");
                AssertTrue(HasTool(collisionTools, underscored.Id), "underscored tool id survives storage collision");
                AssertTrue(!string.Equals(FindTool(collisionTools, dotted.Id).StoragePath, FindTool(collisionTools, underscored.Id).StoragePath, StringComparison.OrdinalIgnoreCase), "colliding safe ids use distinct directories");
                AssertEqual(0, Directory.GetFiles(paths.ToolsDirectory, "*.tmp", SearchOption.AllDirectories).Length, "no tool temp files");
            });
        }

        private static void UnboundCatalogEntryCannotDispatch()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName));
                tools.Add(new ToolCatalogEntry
                {
                    Id = "excel.metadata_mutation",
                    Host = "Excel",
                    Name = "metadata mutation",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true
                });
                var command = new ToolInvocation { ToolId = "excel.metadata_mutation" };

                var blocked = executor.ExecuteManual(command, tools,
                    new AppSettings { AutoConfirmToolActions = false },
                    false, false);
                AssertTrue(!blocked.Success &&
                    blocked.Status == "error",
                    "catalog metadata cannot invent execution authority");
                AssertEqual(0, adapter.TotalBackendCallCount,
                    "unbound entry never reaches a backend");
            });
        }
        private static void AgentToolCrudPreservesOmittedFields()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var definitions = executor.GetControllerTools()
                    .Where(tool => ToolAuthoringCatalog.Owns(tool.Id))
                    .ToList();
                AssertEqual(4, definitions.Count,
                    "complete tool authoring family registered");
                foreach (var definition in definitions)
                {
                    AssertEqual("agent", string.Join(",",
                        definition.Policy.AllowedModes),
                        "tool authoring is Agent-only");
                }
                var readDefinition = definitions.Single(tool =>
                    tool.Id == ToolAuthoringCatalog.DefinitionReadToolId);
                var upsertDefinition = definitions.Single(tool =>
                    tool.Id == ToolAuthoringCatalog.UpsertToolId);
                AssertEqual(ToolEffect.Read,
                    readDefinition.Policy.Effect,
                    "tool definition read effect");
                AssertTrue(readDefinition.Policy.IndependentLocalRead,
                    "tool definition read is independently batchable");
                AssertEqual(ToolEffect.Write,
                    upsertDefinition.Policy.Effect,
                    "tool upsert effect");
                AssertEqual(ToolVerification.Tool,
                    upsertDefinition.Policy.Verification,
                    "tool upsert requires handler verification");
                AssertTrue(upsertDefinition.Policy.RequiresConfirmation,
                    "tool upsert requires confirmation");
                var native = executor.CreateNativeRuntime(
                    NewSession(adapter), definitions,
                    new AppSettings { AutoConfirmToolActions = false },
                    "agent", false,
                    (execution, preparation) => "tool_authoring_pending");
                AssertTrue(native.Describe(new ToolCall(
                        "tool_authoring_read",
                        ToolAuthoringCatalog.DefinitionReadToolId,
                        "{}")) != null,
                    "exact tool authoring id has a native binding");
                AssertTrue(native.Describe(new ToolCall(
                        "tool_authoring_alias",
                        ToolAuthoringCatalog.DefinitionReadToolId
                            .ToUpperInvariant(), "{}")) == null,
                    "tool authoring has no case alias");
                AssertEqual("tools.upsert.v1",
                    DirectToolBindingCatalog.Resolve(
                        ToolAuthoringCatalog.UpsertToolId).HandlerId,
                    "tool upsert binding");

                var command = new ToolInvocation { ToolId = "common.tools_upsert" };
                command.Arguments["id"] = "excel.generated_report";
                command.Arguments["host"] = "Excel";
                command.Arguments["name"] = "Generated report";
                command.Arguments["description"] = "Create a generated report sheet.";
                command.Arguments["parameters"] = JObject.Parse(SheetFormalToolSchema);
                command.Arguments["executor"] = "vba";
                command.Arguments["components"] = ToolComponentsPayload(CustomTool("Excel", "excel.generated_report"));
                command.Arguments["enabled"] = true;
                command.Arguments["requiresConfirmation"] = true;
                command.Arguments["mutatesDocument"] = true;
                command.Arguments["agentCanRun"] = false;
                command.Arguments["riskLevel"] = 2;

                var blocked = executor.ExecuteManual(command, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings { AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "tool create should require confirmation");
                AssertContains(blocked.Status, "awaiting_confirmation", "blocked status");

                var pending = ExecuteToolAuthoringNative(
                    native, ToolAuthoringCatalog.UpsertToolId,
                    JObject.FromObject(command.Arguments));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,
                    pending.Outcome,
                    "native tool create waits for confirmation");
                AssertTrue(!string.IsNullOrWhiteSpace(
                        pending.PreparedStateJson),
                    "native tool create persists a preparation guard");
                AssertTrue(!new ToolStore(FixturePaths.Value).Load().Any(),
                    "tool preparation does not write storage");
                var saved = ConfirmToolAuthoringNative(native, pending);
                AssertEqual(ToolExecutionOutcome.Ok, saved.Outcome,
                    "confirmed tool create succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    saved.Evidence.Dispatch,
                    "tool create marks its dispatch boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    saved.Evidence.Effect,
                    "tool create verifies its read-back");
                AssertContains(saved.Message, "created", "create message");

                var update = new ToolInvocation { ToolId = "common.tools_upsert" };
                update.Arguments["id"] = "excel.generated_report";
                update.Arguments["readme"] = "Updated report notes";
                var updated = executor.ExecuteManual(update, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(updated.Success, "partial tool update should succeed");

                var read = executor.ExecuteManual(new ToolInvocation { ToolId = ToolAuthoringCatalog.DefinitionReadToolId, Arguments = { ["id"] = "excel.generated_report" } }, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings(), false, false);
                AssertTrue(read.Success, "tool read should succeed");
                AssertContains(read.DataJson, "Public Function Run", "saved VBA source");
                AssertContains(read.DataJson, "\"parameters\":{", "schema returned as native object");
                AssertContains(read.DataJson, "Updated report", "updated field returned");
                AssertContains(read.DataJson, "Test custom tool.", "omitted manifest description preserved");

                var unchangedPending = ExecuteToolAuthoringNative(
                    native, ToolAuthoringCatalog.UpsertToolId,
                    new JObject
                    {
                        ["id"] = "excel.generated_report",
                        ["readme"] = "Updated report notes"
                    });
                var unchanged = ConfirmToolAuthoringNative(
                    native, unchangedPending);
                AssertEqual(ToolExecutionOutcome.Ok, unchanged.Outcome,
                    "unchanged tool update succeeds");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    unchanged.Evidence.Dispatch,
                    "unchanged tool update avoids a write");
                AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                    unchanged.Evidence.Effect,
                    "unchanged tool update is explicit");

                var stalePending = ExecuteToolAuthoringNative(
                    native, ToolAuthoringCatalog.UpsertToolId,
                    new JObject
                    {
                        ["id"] = "excel.generated_report",
                        ["readme"] = "Intended report notes"
                    });
                var store = new ToolStore(FixturePaths.Value);
                var externallyChanged = store.Load().Single(tool =>
                    tool.Id == "excel.generated_report");
                externallyChanged.Readme = "External report notes";
                store.SaveOne(externallyChanged);
                var stale = ConfirmToolAuthoringNative(
                    native, stalePending);
                AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                    "stale tool preparation is rejected");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    stale.Evidence.Dispatch,
                    "stale tool update does not dispatch");
                AssertContains(stale.Result.DataJson,
                    "tool_definition_changed",
                    "stale tool update exposes a stable error code");

                var emptyUpdate = executor.ExecuteManual(Command("common.tools_upsert", "id", "excel.generated_report"), new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings(), false, false);
                AssertTrue(!emptyUpdate.Success, "empty tool update fails before confirmation");
                AssertEqual("tool_update_empty", emptyUpdate.ErrorCode, "empty tool update error");

                var missingDelete = executor.ExecuteManual(Command("common.tools_delete", "id", "excel.missing"), new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings(), false, false);
                AssertTrue(!missingDelete.Success, "missing tool delete fails before confirmation");
                AssertEqual("tool_not_found", missingDelete.ErrorCode, "missing tool delete error");

                var deletePending = ExecuteToolAuthoringNative(
                    native, ToolAuthoringCatalog.DeleteToolId,
                    new JObject { ["id"] = "excel.generated_report" });
                var deleted = ConfirmToolAuthoringNative(
                    native, deletePending);
                AssertEqual(ToolExecutionOutcome.Ok, deleted.Outcome,
                    "confirmed tool delete succeeds");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    deleted.Evidence.Effect,
                    "tool delete verifies absence");
                AssertTrue(!store.Load().Any(tool =>
                        tool.Id == "excel.generated_report"),
                    "tool delete removes storage");
            });
        }

        private static void ToolLibraryMutationsAreRevisionGuarded()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor,
                    FakeOfficeAdapter adapter)
                {
                    var intended = CustomTool(
                        "Excel", "excel.library_guard");
                    var created = executor.ExecuteToolLibraryMutation(
                        new ToolLibraryCoreMutation
                        {
                            Kind = "upsert",
                            BaseId = string.Empty,
                            ExpectedRevision = string.Empty,
                            Intended = intended
                        });
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok,
                        created.Outcome.Status,
                        "typed library create status");
                    AssertTrue(created.DispatchPossible &&
                        !string.IsNullOrWhiteSpace(created.Revision),
                        "typed library create has dispatch and revision");

                    var updatedEntry = created.Package.Clone();
                    updatedEntry.Readme = "Updated through typed UI.";
                    var updated = executor.ExecuteToolLibraryMutation(
                        new ToolLibraryCoreMutation
                        {
                            Kind = "upsert",
                            BaseId = created.Package.Id,
                            ExpectedRevision = created.Revision,
                            Intended = updatedEntry
                        });
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok,
                        updated.Outcome.Status,
                        "typed library update status");
                    AssertTrue(updated.Revision != created.Revision,
                        "typed library update advances revision");

                    var stale = executor.ExecuteToolLibraryMutation(
                        new ToolLibraryCoreMutation
                        {
                            Kind = "delete",
                            BaseId = updated.Package.Id,
                            ExpectedRevision = created.Revision
                        });
                    AssertEqual(ToolAuthoringOutcomeStatus.Error,
                        stale.Outcome.Status,
                        "stale typed library mutation rejected");
                    AssertEqual("tool_package_changed",
                        stale.Outcome.ErrorCode,
                        "stale typed library error code");
                    AssertTrue(!stale.DispatchPossible,
                        "stale typed library mutation does not dispatch");

                    var deleted = executor.ExecuteToolLibraryMutation(
                        new ToolLibraryCoreMutation
                        {
                            Kind = "delete",
                            BaseId = updated.Package.Id,
                            ExpectedRevision = updated.Revision
                        });
                    AssertEqual(ToolAuthoringOutcomeStatus.Ok,
                        deleted.Outcome.Status,
                        "typed library delete status");
                    AssertTrue(deleted.DispatchPossible &&
                        string.IsNullOrWhiteSpace(deleted.Revision),
                        "typed library delete verifies absence");
                });
        }

        private static ToolExecutionRecord ExecuteToolAuthoringNative(
            NativeToolRuntimeAdapter runtime,
            string toolId,
            JObject arguments)
        {
            var call = new ToolCall(
                "tool_authoring_" + Guid.NewGuid().ToString("N"),
                toolId,
                (arguments ?? new JObject()).ToString(Formatting.None));
            var policy = runtime.Describe(call);
            if (policy == null)
                throw new InvalidOperationException(
                    "Tool authoring native policy was not captured: " +
                    toolId);
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        call, policy, "run-tool-authoring-native",
                        "turn-tool-authoring-native", call.Id + ":1",
                        DateTime.UtcNow, false, 5),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static ToolExecutionRecord ConfirmToolAuthoringNative(
            NativeToolRuntimeAdapter runtime,
            ToolExecutionRecord pending)
        {
            if (pending == null || pending.Outcome !=
                ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException(
                    "A native pending tool authoring mutation is required.");
            var source = pending.Context;
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        source.Call, source.Policy, source.RunId,
                        source.TurnId, source.StepId, DateTime.UtcNow,
                        true, 5, pending.PreparedStateJson),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static ToolExecutionRecord ExecuteSkillAuthoringNative(
            NativeToolRuntimeAdapter runtime,
            string toolId,
            JObject arguments)
        {
            var call = new ToolCall(
                "skill_authoring_" + Guid.NewGuid().ToString("N"),
                toolId,
                (arguments ?? new JObject()).ToString(Formatting.None));
            var policy = runtime.Describe(call);
            if (policy == null)
                throw new InvalidOperationException(
                    "Skill authoring native policy was not captured: " +
                    toolId);
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        call, policy, "run-skill-authoring-native",
                        "turn-skill-authoring-native", call.Id + ":1",
                        DateTime.UtcNow, false, 5),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static ToolExecutionRecord ConfirmSkillAuthoringNative(
            NativeToolRuntimeAdapter runtime,
            ToolExecutionRecord pending)
        {
            if (pending == null || pending.Outcome !=
                ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException(
                    "A native pending skill authoring mutation is required.");
            var source = pending.Context;
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        source.Call, source.Policy, source.RunId,
                        source.TurnId, source.StepId, DateTime.UtcNow,
                        true, 5, pending.PreparedStateJson),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static void AgentSkillCrudPreservesOmittedFields()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var definitions = executor.GetControllerTools()
                    .Where(tool => SkillAuthoringCatalog.Owns(tool.Id))
                    .ToList();
                AssertEqual(2, definitions.Count,
                    "complete skill authoring family registered");
                AssertTrue(definitions.All(tool =>
                        string.Join(",", tool.Policy.AllowedModes) == "agent" &&
                        tool.Policy.Effect == ToolEffect.Write &&
                        tool.Policy.Verification == ToolVerification.Tool &&
                        tool.Policy.RequiresConfirmation),
                    "skill authoring uses Agent-only confirmed write policy");
                var native = executor.CreateNativeRuntime(
                    NewSession(adapter), definitions,
                    new AppSettings { AutoConfirmToolActions = false },
                    "agent", false,
                    (execution, preparation) => "skill_authoring_pending");
                AssertTrue(native.Describe(new ToolCall(
                        "skill_authoring_exact",
                        SkillAuthoringCatalog.UpsertToolId, "{}")) != null,
                    "exact skill authoring id has a native binding");
                AssertTrue(native.Describe(new ToolCall(
                        "skill_authoring_alias",
                        SkillAuthoringCatalog.UpsertToolId.ToUpperInvariant(),
                        "{}")) == null,
                    "skill authoring has no case alias");
                AssertEqual("skills.upsert.v1",
                    DirectToolBindingCatalog.Resolve(
                        SkillAuthoringCatalog.UpsertToolId).HandlerId,
                    "skill upsert binding");

                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var create = Command(
                    "common.skills_upsert",
                    "id", "excel.review_style",
                    "host", "Excel",
                    "name", "Review style",
                    "description", "Review workbook style consistently.",
                    "version", "1.0.0",
                    "bodyMarkdown", "# Review style\n\nPreserve workbook conventions.",
                    "enabled", true);
                AssertEqual("awaiting_confirmation",
                    executor.ExecuteManual(create, tools, new AppSettings(),
                        false, false).Status,
                    "skill create waits for confirmation");
                var createPending = ExecuteSkillAuthoringNative(
                    native, SkillAuthoringCatalog.UpsertToolId,
                    JObject.FromObject(create.Arguments));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,
                    createPending.Outcome,
                    "native skill create waits for confirmation");
                AssertTrue(!new SkillStore(FixturePaths.Value).Load().Any(),
                    "skill preparation does not write storage");
                var created = ConfirmSkillAuthoringNative(
                    native, createPending);
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                    "confirmed skill create succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    created.Evidence.Dispatch,
                    "skill create marks dispatch");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    created.Evidence.Effect,
                    "skill create verifies read-back");
                AssertEqual(1, (int)JObject.Parse(
                    created.Result.DataJson)["contractVersion"],
                    "skill authoring result contract is versioned");

                var update = Command("common.skills_upsert", "id", "excel.review_style", "description", "Review workbook formatting consistently.");
                var updated = executor.ExecuteManual(update, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(updated.Success, "partial skill update succeeds");

                var read = executor.ExecuteManual(Command("common.capabilities_read", "id", "excel.review_style"), tools, new AppSettings(), false, true);
                AssertTrue(read.Success, "skill read succeeds");
                AssertContains(read.DataJson, "Review workbook formatting consistently", "skill description updated");
                AssertContains(read.DataJson, "Preserve workbook conventions", "omitted skill body preserved");
                AssertContains(read.DataJson, "\"version\":\"1.0.0\"", "omitted version preserved");
                AssertEqual("# Review style\n\nPreserve workbook conventions.",
                    (string)JObject.Parse(read.DataJson)["bodyMarkdown"],
                    "skill persistence does not accumulate separator blank lines");
                var storedAfterUpdate = new SkillStore(FixturePaths.Value)
                    .Load().Single(skill =>
                        skill.Id == "excel.review_style");
                AssertContains(read.DataJson, "\"revision\":\"" +
                    SkillRevision.Compute(storedAfterUpdate) + "\"",
                    "skill read returns complete package revision");

                var unchangedPending = ExecuteSkillAuthoringNative(
                    native, SkillAuthoringCatalog.UpsertToolId,
                    new JObject
                    {
                        ["id"] = "excel.review_style",
                        ["description"] =
                            "Review workbook formatting consistently."
                    });
                var unchanged = ConfirmSkillAuthoringNative(
                    native, unchangedPending);
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    unchanged.Evidence.Dispatch,
                    "unchanged skill update avoids storage dispatch");
                AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                    unchanged.Evidence.Effect,
                    "unchanged skill update is explicit");

                var stalePending = ExecuteSkillAuthoringNative(
                    native, SkillAuthoringCatalog.UpsertToolId,
                    new JObject
                    {
                        ["id"] = "excel.review_style",
                        ["description"] = "Intended description"
                    });
                var externalStore = new SkillStore(FixturePaths.Value);
                var external = externalStore.Load().Single(skill =>
                    skill.Id == "excel.review_style");
                external.Description = "External description";
                externalStore.SaveOne(external);
                var stale = ConfirmSkillAuthoringNative(native, stalePending);
                AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                    "stale skill preparation is rejected");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    stale.Evidence.Dispatch,
                    "stale skill update does not dispatch");
                AssertContains(stale.Result.DataJson,
                    "skill_package_changed",
                    "stale skill update exposes stable error code");

                var restore = Command("common.skills_upsert",
                    "id", "excel.review_style", "description",
                    "Review workbook formatting consistently.");
                AssertTrue(executor.ExecuteManual(restore, tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false, false).Success,
                    "skill can be updated after explicit refresh");

                var referenceUpsert = Command(
                    "common.skills_upsert",
                    "id", "excel.review_style",
                    "referencePath", "references/checklist.md",
                    "referenceMarkdown", "# Checklist\n\n- Preserve formats.");
                AssertEqual("awaiting_confirmation",
                    executor.ExecuteManual(referenceUpsert, tools, new AppSettings(), false, false).Status,
                    "agent reference upsert requires confirmation");
                var referenceCreated = executor.ExecuteManual(
                    referenceUpsert, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(referenceCreated.Success, "agent reference upsert succeeds");
                AssertContains(referenceCreated.DataJson, "references/checklist.md", "reference mutation returns path");

                var mixed = Command(
                    "common.skills_upsert",
                    "id", "excel.review_style",
                    "description", "Mixed core change",
                    "referencePath", "references/mixed.md",
                    "referenceMarkdown", "# Mixed");
                var mixedResult = executor.ExecuteManual(mixed, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertEqual("invalid_arguments", mixedResult.ErrorCode, "mixed skill core/reference call is rejected by its published schema");
                AssertTrue(!string.Equals(mixedResult.ErrorCode, "mixed_skill_reference_update", StringComparison.Ordinal), "mixed call never reaches the old runtime trap");

                var upsertDefinition = executor.GetControllerTools().Single(item => item.Id == "common.skills_upsert");
                var responseSchema = JObject.Parse(ConversationResponseSchemaBuilder.Build(new[] { upsertDefinition }));
                var upsertVariants = responseSchema.SelectToken("properties.tool_calls.items.anyOf[0].properties.arguments.anyOf") as JArray;
                AssertEqual(2, upsertVariants == null ? 0 : upsertVariants.Count, "skill upsert strict schema separates core and reference calls");
                AssertTrue(upsertVariants.OfType<JObject>().Any(item => item.SelectToken("properties.referencePath") != null && item.SelectToken("properties.description") == null),
                    "reference branch excludes core fields");

                var referenceRead = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", "excel.review_style", "referencePath", "references/checklist.md"),
                    tools, new AppSettings(), false, true);
                AssertTrue(referenceRead.Success, "agent-created reference can be read");
                AssertContains(referenceRead.DataJson, "Preserve formats", "agent-created reference content");

                var referenceDelete = Command(
                    "common.skills_delete", "id", "excel.review_style", "referencePath", "references/checklist.md");
                AssertEqual("awaiting_confirmation",
                    executor.ExecuteManual(referenceDelete, tools, new AppSettings(), false, false).Status,
                    "agent reference delete requires confirmation");
                AssertTrue(executor.ExecuteManual(
                    referenceDelete, tools, new AppSettings { AutoConfirmToolActions = true }, false, false).Success,
                    "agent reference delete succeeds");

                var emptyUpdate = executor.ExecuteManual(Command("common.skills_upsert", "id", "excel.review_style"), tools, new AppSettings(), false, false);
                AssertTrue(!emptyUpdate.Success, "empty skill update fails before confirmation");
                AssertEqual("skill_update_empty", emptyUpdate.ErrorCode, "empty skill update error");

                var missingDelete = executor.ExecuteManual(Command("common.skills_delete", "id", "excel.missing_skill"), tools, new AppSettings(), false, false);
                AssertTrue(!missingDelete.Success, "missing skill delete fails before confirmation");
                AssertEqual("skill_not_found", missingDelete.ErrorCode, "missing skill delete error");

                var deleted = executor.ExecuteManual(Command("common.skills_delete", "id", "excel.review_style"), tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(deleted.Success, "skill delete succeeds");
            });
        }

        private static void SkillUiMutationsUseTypedRevisionGuards()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                (executor, adapter) =>
            {
                var store = new SkillStore(FixturePaths.Value);
                var original = store.SaveOne(new SkillDefinition
                {
                    Id = "excel.ui_guard",
                    Host = "Excel",
                    Name = "UI guard",
                    Description = "Original description.",
                    Version = "1.0.0",
                    BodyMarkdown = "# UI guard",
                    Enabled = true
                });
                var originalRevision = SkillRevision.Compute(original);
                var update = executor.ExecuteSkillLibraryMutation(
                    new SkillLibraryCoreMutation
                    {
                        Kind = "upsert",
                        BaseId = original.Id,
                        ExpectedRevision = originalRevision,
                        Intended = new SkillDefinition
                        {
                            Id = original.Id,
                            Host = original.Host,
                            Name = original.Name,
                            Description = "Updated description.",
                            Version = original.Version,
                            BodyMarkdown = original.BodyMarkdown,
                            Enabled = original.Enabled
                        }
                    });
                AssertEqual(SkillAuthoringOutcomeStatus.Ok,
                    update.Outcome.Status,
                    "typed UI core update succeeds");
                AssertEqual(SkillAuthoringEffect.VerifiedChange,
                    update.Outcome.Effect,
                    "typed UI core update verifies read-back");
                AssertTrue(update.DispatchPossible,
                    "typed UI core update marks dispatch");
                AssertTrue(update.Package != null &&
                    update.Package.Revision != originalRevision,
                    "typed UI core update returns the new package revision");

                var stale = executor.ExecuteSkillLibraryMutation(
                    new SkillLibraryCoreMutation
                    {
                        Kind = "upsert",
                        BaseId = original.Id,
                        ExpectedRevision = originalRevision,
                        Intended = original
                    });
                AssertEqual(SkillAuthoringOutcomeStatus.Error,
                    stale.Outcome.Status,
                    "stale UI core update is rejected");
                AssertEqual("skill_package_changed",
                    stale.Outcome.ErrorCode,
                    "stale UI core update has a stable code");
                AssertTrue(!stale.DispatchPossible,
                    "stale UI core update does not dispatch");

                var reference = executor
                    .ExecuteSkillLibraryReferenceMutation(
                        "upsert", original.Id,
                        "references/rules.md", "# Rules",
                        update.Package.Revision);
                AssertEqual(SkillAuthoringOutcomeStatus.Ok,
                    reference.Outcome.Status,
                    "typed UI reference update succeeds");
                AssertEqual(SkillAuthoringEffect.VerifiedChange,
                    reference.Outcome.Effect,
                    "typed UI reference update verifies package read-back");
                var read = executor.ReadSkillLibraryReference(
                    original.Id, "references/rules.md",
                    reference.Package.Revision);
                AssertEqual("# Rules", read.Content,
                    "typed UI reference read is revision-bound");
                var staleRead = false;
                try
                {
                    executor.ReadSkillLibraryReference(
                        original.Id, "references/rules.md",
                        update.Package.Revision);
                }
                catch (InvalidOperationException)
                {
                    staleRead = true;
                }
                AssertTrue(staleRead,
                    "stale UI reference read fails closed");

                var deletedReference = executor
                    .ExecuteSkillLibraryReferenceMutation(
                        "delete", original.Id,
                        "references/rules.md", null,
                        reference.Package.Revision);
                AssertEqual(SkillAuthoringOutcomeStatus.Ok,
                    deletedReference.Outcome.Status,
                    "typed UI reference delete succeeds");
                AssertTrue(deletedReference.Package.References.Count == 0,
                    "typed UI reference delete returns exact package metadata");

                var renamed = executor.ExecuteSkillLibraryMutation(
                    new SkillLibraryCoreMutation
                    {
                        Kind = "upsert",
                        BaseId = original.Id,
                        ExpectedRevision =
                            deletedReference.Package.Revision,
                        Intended = new SkillDefinition
                        {
                            Id = "excel.ui_guard_renamed",
                            Host = "Excel",
                            Name = "UI guard renamed",
                            Description = "Updated description.",
                            Version = "1.0.0",
                            BodyMarkdown = "# UI guard",
                            Enabled = true
                        }
                    });
                AssertEqual(SkillAuthoringOutcomeStatus.Ok,
                    renamed.Outcome.Status,
                    "typed UI rename succeeds through the domain owner");
                AssertEqual("excel.ui_guard_renamed",
                    renamed.Package.Id,
                    "typed UI rename returns the exact new identity");
                AssertTrue(!store.Load().Any(skill =>
                        skill.Id == original.Id),
                    "typed UI rename removes the old identity without alias");
            });
        }

        private static void SkillRevisionAndValidationAreDeterministic()
        {
            var unix = new SkillDefinition
            {
                Id = "common.revision",
                Description = "Revision test.",
                BodyMarkdown = "# Revision\n\nFirst line.\nSecond line."
            };
            var windows = new SkillDefinition
            {
                Id = unix.Id,
                Description = unix.Description,
                BodyMarkdown = "# Revision\r\n\r\nFirst line.\r\nSecond line."
            };
            AssertEqual(SkillRevision.Compute(unix), SkillRevision.Compute(windows),
                "skill revision normalizes line endings");

            windows.BodyMarkdown += "\r\nChanged.";
            AssertTrue(!string.Equals(SkillRevision.Compute(unix), SkillRevision.Compute(windows), StringComparison.Ordinal),
                "skill revision changes with Markdown body");

            windows.BodyMarkdown = unix.BodyMarkdown;
            windows.Description = "Changed metadata.";
            AssertTrue(!string.Equals(
                    SkillRevision.Compute(unix),
                    SkillRevision.Compute(windows),
                    StringComparison.Ordinal),
                "skill revision includes package front matter");

            AssertEqual("Skill description is required.", SkillStore.ValidateDefinition(new SkillDefinition
            {
                Id = "common.no_description",
                BodyMarkdown = "# Body"
            }), "skill description is required by shared validation");
            AssertEqual("Skill bodyMarkdown is required.", SkillStore.ValidateDefinition(new SkillDefinition
            {
                Id = "common.no_body",
                Description = "Missing body."
            }), "skill body is required by shared validation");
        }

        private static void SkillReferencesAreRevisionedAndRuntimeContinued()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new SkillStore(paths);
                store.SaveOne(new SkillDefinition
                {
                    Id = "common.reference_test",
                    Host = "Common",
                    Name = "Reference test",
                    Description = "Test progressive skill references.",
                    BodyMarkdown = "# Reference test\n\nRead [details](references/details.md) when needed.",
                    Enabled = true
                });
                var stored = store.Load().Single(item => item.Id == "common.reference_test");
                var bodyOnlyRevision = SkillRevision.Compute(stored);
                var referenceContent = "# Details\n\nABCDEFGHIJ" +
                    new string('A', 24000) + "TAIL";
                string saveError;
                SkillReferenceMetadata savedReference;
                AssertTrue(store.TrySaveReference(
                    stored,
                    "references/details.md",
                    referenceContent,
                    out savedReference,
                    out saveError), "reference save succeeds: " + saveError);
                AssertTrue(!store.TrySaveReference(
                    stored,
                    "references/bad:name.md",
                    "invalid",
                    out savedReference,
                    out saveError), "reference rejects platform-sensitive file names");
                AssertTrue(store.TrySaveReference(
                    stored,
                    "references/DETAILS.md",
                    referenceContent,
                    out savedReference,
                    out saveError), "case-insensitive reference update preserves one file: " + saveError);

                stored = store.Load().Single(item => item.Id == "common.reference_test");
                AssertEqual(1, stored.References.Count, "one direct Markdown reference discovered");
                AssertEqual("references/details.md", stored.References[0].Path, "reference path is package-relative");
                AssertTrue(!string.Equals(bodyOnlyRevision, SkillRevision.Compute(stored), StringComparison.Ordinal),
                    "reference manifest changes package revision");

                stored.Host = "Excel";
                stored = store.SaveOne(stored);
                AssertEqual("Excel", stored.Host, "skill host updated");
                AssertEqual(1, stored.References.Count, "host move preserves skill references");
                string movedContent;
                SkillReferenceMetadata movedMetadata;
                string movedError;
                AssertTrue(store.TryReadReference(
                    stored,
                    "references/details.md",
                    out movedContent,
                    out movedMetadata,
                    out movedError), "moved skill reference remains readable: " + movedError);
                AssertContains(movedContent, "ABCDEFGHIJ", "moved reference content preserved");

                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var runtimeSkills = new[] { stored };
                var main = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", stored.Id), tools, new AppSettings(), false, false,
                    new ChatSession(), 40, runtimeSkills, CancellationToken.None);
                var mainData = JObject.Parse(main.DataJson);
                AssertEqual(true, (bool)mainData["loaded"], "complete core read declares loaded state");
                AssertEqual("references/details.md", (string)mainData.SelectToken("references[0].path"),
                    "core read lists references without their bodies");

                var readSession = new ChatSession();
                var firstCommand = Command("common.capabilities_read",
                    "id", stored.Id,
                    "referencePath", "references/details.md");
                firstCommand.ToolCallId = "skill_reference_first";
                var first = executor.ExecuteManual(
                    firstCommand,
                    tools, new AppSettings(), false, false, readSession, 40, runtimeSkills, CancellationToken.None);
                var firstData = JObject.Parse(first.DataJson);
                AssertEqual("reference", (string)firstData["kind"], "reference result kind");
                AssertEqual(24000, (int)firstData["returnedChars"], "reference chunk uses the runtime bound");
                AssertEqual(false, (bool)firstData["complete"], "first reference chunk is incomplete");
                AssertEqual(24000, (int)firstData["progressCharacters"], "runtime result records continuation progress");
                AssertTrue(firstData["offset"] == null && firstData["nextOffset"] == null,
                    "reference result exposes no caller-owned offset");
                AssertTrue(firstData["loaded"] == null, "reference chunk does not load the core skill");
                readSession.Messages.Add(AgentJsonProtocol.CreateToolResultMessage(
                    firstCommand,
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok(
                        first.Message,
                        first.DataJson,
                        first.ModelResourceRefs)));

                var rest = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", stored.Id,
                        "referencePath", "references/details.md", "action", "next"),
                    tools, new AppSettings(), false, false, readSession, 40, runtimeSkills, CancellationToken.None);
                var restData = JObject.Parse(rest.DataJson);
                AssertEqual(true, (bool)restData["complete"], "final reference chunk is complete");
                AssertEqual(referenceContent.Substring(24000), (string)restData["content"],
                    "semantic next resumes at the runtime-owned exact offset");

                var traversal = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", stored.Id, "referencePath", "references/../secret.md"),
                    tools, new AppSettings(), false, false, new ChatSession(), 40, runtimeSkills, CancellationToken.None);
                AssertTrue(!traversal.Success, "reference traversal is rejected");

                var referencePath = Path.Combine(stored.StoragePath, "references", "details.md");
                File.WriteAllText(referencePath, "# Details\n\nChanged");
                var stale = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", stored.Id, "referencePath", "references/details.md"),
                    tools, new AppSettings(), false, false, new ChatSession(), 40, runtimeSkills, CancellationToken.None);
                AssertEqual("skill_reference_changed", stale.ErrorCode, "stale reference snapshot is rejected");

                stored = store.Load().Single(item => item.Id == "common.reference_test");
                string deleteError;
                AssertTrue(store.TryDeleteReference(stored, "references/details.md", out deleteError),
                    "reference delete succeeds: " + deleteError);
                AssertEqual(0, store.Load().Single(item => item.Id == "common.reference_test").References.Count,
                    "deleted reference leaves the package manifest");
            });
        }

        private static void SkillIdsDoNotCollideAndDisabledReadsFail()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new SkillStore(paths);
                store.Save(new[]
                {
                    new SkillDefinition
                    {
                        Id = "common.a.b",
                        Host = "Common",
                        Name = "Dot id",
                        Description = "Enabled skill.",
                        BodyMarkdown = "DOT_SKILL",
                        Enabled = true
                    },
                    new SkillDefinition
                    {
                        Id = "common.a_b",
                        Host = "Common",
                        Name = "Underscore id",
                        Description = "Disabled skill.",
                        BodyMarkdown = "DISABLED_SKILL",
                        Enabled = false
                    }
                });
                var loaded = store.Load();
                AssertEqual(2, loaded.Count, "similar skill ids are stored separately");
                AssertTrue(!string.Equals(loaded[0].StoragePath, loaded[1].StoragePath, StringComparison.OrdinalIgnoreCase),
                    "skill directories do not collide");
                var invalidDirectory = Path.Combine(paths.SkillsDirectory, "common", "invalid_external");
                Directory.CreateDirectory(invalidDirectory);
                File.WriteAllText(Path.Combine(invalidDirectory, "SKILL.md"),
                    "---\nid: common.invalid\nhost: Common\nname: Invalid\ndescription: " +
                    new string('x', 4001) + "\n---\nBody");
                AssertEqual(2, store.Load().Count, "invalid external skill metadata is skipped");

                var unclosedDirectory = Path.Combine(paths.SkillsDirectory, "common", "unclosed_external");
                Directory.CreateDirectory(unclosedDirectory);
                File.WriteAllText(Path.Combine(unclosedDirectory, "SKILL.md"),
                    "---\nid: common.unclosed\nhost: Common\nname: Unclosed\ndescription: Invalid front matter.\nBody");
                var invalidUtf8Directory = Path.Combine(paths.SkillsDirectory, "common", "invalid_utf8_external");
                Directory.CreateDirectory(invalidUtf8Directory);
                File.WriteAllBytes(Path.Combine(invalidUtf8Directory, "SKILL.md"), new byte[] { 0xef, 0xbb, 0xbf, 0xc3, 0x28 });
                AssertEqual(2, store.Load().Count, "malformed front matter and non-UTF8 skill bodies are skipped");

                var excessReferencesDirectory = Path.Combine(paths.SkillsDirectory, "common", "excess_references_external");
                Directory.CreateDirectory(Path.Combine(excessReferencesDirectory, "references"));
                File.WriteAllText(Path.Combine(excessReferencesDirectory, "SKILL.md"),
                    "---\nid: common.excess_refs\nhost: Common\nname: Excess refs\ndescription: Too many references.\n---\nBody");
                for (var index = 0; index < 65; index++)
                {
                    File.WriteAllText(Path.Combine(excessReferencesDirectory, "references", "ref" + index + ".md"), "Reference");
                }
                AssertEqual(2, store.Load().Count, "skill packages above the documented reference limit are skipped");
                var whitespaceIdRejected = false;
                try
                {
                    store.SaveOne(new SkillDefinition
                    {
                        Id = " common.bad ",
                        Host = "Common",
                        Name = "Bad id",
                        Description = "Invalid id.",
                        BodyMarkdown = "INVALID",
                        Enabled = true
                    });
                }
                catch (ArgumentException)
                {
                    whitespaceIdRejected = true;
                }
                AssertTrue(whitespaceIdRejected, "skill ids with surrounding whitespace are rejected");

                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var enabledRead = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", "common.a.b"), tools, new AppSettings(), false, false,
                    new ChatSession(), 40, loaded, CancellationToken.None);
                AssertTrue(enabledRead.Success, "enabled runtime skill can be read");

                var disabledRead = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", "common.a_b"), tools, new AppSettings(), false, false,
                    new ChatSession(), 40, loaded, CancellationToken.None);
                AssertTrue(!disabledRead.Success, "disabled runtime skill cannot be read by agent");
                AssertTrue(disabledRead.DataJson == null || disabledRead.DataJson.IndexOf("DISABLED_SKILL", StringComparison.Ordinal) < 0,
                    "disabled skill body is not exposed");
                var confirmedRuntimeRead = executor.ExecuteManual(
                    Command("common.capabilities_read", "id", "common.a_b"), tools, new AppSettings(), false, true,
                    new ChatSession(), 40, loaded.Where(item => item.Enabled).ToList(), CancellationToken.None);
                AssertTrue(!confirmedRuntimeRead.Success, "confirmation bypass does not broaden the runtime skill catalog");
            });
        }
    }
}
