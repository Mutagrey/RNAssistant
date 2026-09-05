using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ProductionProjectsIncludeAllSourceFiles()
        {
            var root = FindHarnessRepositoryRoot();
            var sourceRoot = Path.Combine(root, "src");
            var missing = new List<string>();

            foreach (var projectPath in Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var projectDirectory = Path.GetDirectoryName(projectPath);
                var document = XDocument.Load(projectPath);
                var included = new HashSet<string>(
                    document.Descendants()
                        .Where(element => element.Name.LocalName == "Compile")
                        .Select(element => (string)element.Attribute("Include"))
                        .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            value.IndexOf('*') < 0 && value.IndexOf('?') < 0)
                        .Select(value => Path.GetFullPath(Path.Combine(
                            projectDirectory,
                            value.Replace('\\', Path.DirectorySeparatorChar)))),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var sourcePath in Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsGeneratedProjectPath(sourcePath) || included.Contains(Path.GetFullPath(sourcePath))) continue;
                    missing.Add(Path.GetRelativePath(root, sourcePath).Replace('\\', '/'));
                }
            }

            AssertEqual(0, missing.Count,
                "old-style production projects must explicitly include every source file: " +
                string.Join(", ", missing.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()));

            var packRoot = Path.Combine(sourceRoot, "RNAssistant.Office", "Qualification", "Packs");
            var packPaths = Directory.GetFiles(packRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resourceProblems = new List<string>();
            foreach (var relativeProject in new[]
            {
                "src/RNAssistant.Office/RNAssistant.Office.csproj",
                "tests/RNAssistant.Harness/RNAssistant.Harness.csproj",
                "demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj"
            })
            {
                var projectPath = Path.Combine(root,
                    relativeProject.Replace('/', Path.DirectorySeparatorChar));
                var projectDirectory = Path.GetDirectoryName(projectPath);
                var resources = XDocument.Load(projectPath).Descendants()
                    .Where(element => element.Name.LocalName == "EmbeddedResource")
                    .Select(element => new
                    {
                        Path = ResolveProjectInclude(projectDirectory, (string)element.Attribute("Include")),
                        LogicalName = (string)element.Attribute("LogicalName") ??
                            (string)element.Elements().FirstOrDefault(child => child.Name.LocalName == "LogicalName")
                    })
                    .Where(resource => resource.Path != null)
                    .ToList();
                foreach (var packPath in packPaths)
                {
                    var resource = resources.FirstOrDefault(candidate =>
                        string.Equals(candidate.Path, packPath, StringComparison.OrdinalIgnoreCase));
                    var fileName = Path.GetFileName(packPath);
                    var expectedName = "RNAssistant.Office.Qualification.Packs." + fileName;
                    if (resource == null)
                        resourceProblems.Add(relativeProject + ": missing " + fileName);
                    else if (!string.Equals(resource.LogicalName, expectedName, StringComparison.Ordinal))
                        resourceProblems.Add(relativeProject + ": invalid LogicalName for " + fileName);
                }
            }
            AssertEqual(0, resourceProblems.Count,
                "production and source-linked hosts must embed every qualification pack under its canonical name: " +
                string.Join(", ", resourceProblems.ToArray()));
        }

        private static string ResolveProjectInclude(string projectDirectory, string include)
        {
            if (string.IsNullOrWhiteSpace(include) || include.IndexOf('*') >= 0 || include.IndexOf('?') >= 0)
                return null;
            return Path.GetFullPath(Path.Combine(projectDirectory,
                include.Replace('\\', Path.DirectorySeparatorChar)));
        }

        private static void PortablePublishReplacesOwnedDestination()
        {
            var root = FindHarnessRepositoryRoot();
            var projectPath = Path.Combine(root, "build", "RNAssistant.NativePortable.proj");
            var document = XDocument.Load(projectPath);
            var prepare = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Target" &&
                    string.Equals((string)element.Attribute("Name"),
                        "PrepareDestination", StringComparison.Ordinal));
            var publish = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Target" &&
                    string.Equals((string)element.Attribute("Name"),
                        "Publish", StringComparison.Ordinal));

            AssertTrue(prepare != null && publish != null,
                "portable publishing requires explicit prepare and publish targets");
            AssertTrue(prepare.Elements().Any(element =>
                    element.Name.LocalName == "RemoveDir" &&
                    ((string)element.Attribute("Condition") ?? string.Empty)
                        .IndexOf("DestinationIsKnown", StringComparison.Ordinal) >= 0 &&
                    ((string)element.Attribute("Condition") ?? string.Empty)
                        .IndexOf("DestinationMarker", StringComparison.Ordinal) >= 0),
                "portable publishing must clean only known or previously owned destinations");
            AssertTrue(prepare.Elements().Any(element =>
                    element.Name.LocalName == "Error" &&
                    ((string)element.Attribute("Text") ?? string.Empty)
                        .IndexOf("unowned portable destination", StringComparison.Ordinal) >= 0),
                "portable publishing must reject an existing unowned custom destination");
            AssertTrue(prepare.Elements().Any(element =>
                    element.Name.LocalName == "Error" &&
                    string.Equals((string)element.Attribute("Condition"),
                        "!Exists('%(RequiredFile.Identity)')", StringComparison.Ordinal)),
                "portable publishing must validate every required input before cleanup");
            AssertTrue(prepare.Elements().Any(element =>
                    element.Name.LocalName == "WriteLinesToFile" &&
                    string.Equals((string)element.Attribute("File"),
                        "$(DestinationMarker)", StringComparison.Ordinal)),
                "portable publishing must mark destination ownership after cleanup");
            AssertTrue(((string)publish.Attribute("DependsOnTargets") ?? string.Empty)
                    .Split(';').Contains("PrepareDestination", StringComparer.Ordinal),
                "publish must prepare an exact destination before copying files");
            AssertTrue(!publish.Descendants().Where(element =>
                    element.Name.LocalName == "Copy").Any(element =>
                    element.Attribute("SkipUnchangedFiles") != null),
                "exact portable publishing must copy every current file after cleanup");

            var source = File.ReadAllText(projectPath);
            AssertContains(source, "C:\\Temp\\RNAssistant\\",
                "x64 local temp output is an explicitly known clean destination");
            AssertContains(source, "C:\\Temp\\RNAssistant-x86\\",
                "x86 local temp output is an explicitly known clean destination");
        }

        private static string FindHarnessRepositoryRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(start);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "RNAssistant.sln")) &&
                        Directory.Exists(Path.Combine(current.FullName, "src")))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            throw new InvalidOperationException("RNAssistant repository root was not found.");
        }

        private static void OfficeEventConsumersUseTypedPort()
        {
            var root = FindHarnessRepositoryRoot();
            var officeRoot = Path.Combine(root, "src", "RNAssistant.Office");
            var forbiddenMembers = new[]
            {
                ".AppendTrace(",
                ".AppendTraceBytes(",
                ".ReadEvents(",
                ".ReadCompleteEvents(",
                ".ReadEventPayload("
            };
            var offenders = new List<string>();
            foreach (var path in Directory.GetFiles(officeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedProjectPath(path)) continue;
                var source = File.ReadAllText(path);
                foreach (var member in forbiddenMembers)
                {
                    if (source.IndexOf(member, StringComparison.Ordinal) >= 0)
                    {
                        offenders.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + ": " + member);
                    }
                }
            }
            AssertEqual(0, offenders.Count,
                "Office event consumers must use IEventStore instead of broad ChatStore event members: " +
                string.Join(", ", offenders.ToArray()));

            var causal = File.ReadAllText(Path.Combine(officeRoot, "Services", "RunCausalTrace.cs"));
            AssertContains(causal, "public CausalTraceRecord(SessionEventKind kind)",
                "causal records require a closed event kind at construction");
            AssertContains(causal, "descriptor.Lane != SessionEventLane.DomainDiagnostic",
                "causal records cannot claim Agent authority");
            AssertTrue(causal.IndexOf("public string Stage { get; set; }", StringComparison.Ordinal) < 0,
                "causal records cannot inject an arbitrary persisted event type");

            var publicChatStoreMethods = typeof(RNAssistant.Core.Storage.ChatStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Select(method => method.Name)
                .ToArray();
            foreach (var broadMember in new[]
            {
                "AppendTrace",
                "AppendTraceBytes",
                "ReadEvents",
                "ReadCompleteEvents",
                "ReadEventPayload"
            })
            {
                AssertTrue(!publicChatStoreMethods.Contains(broadMember, StringComparer.Ordinal),
                    "replaced broad ChatStore event API is not externally callable: " + broadMember);
            }
        }

        private static void OfficeConversationConsumersUseTypedPort()
        {
            var root = FindHarnessRepositoryRoot();
            var officeRoot = Path.Combine(root, "src", "RNAssistant.Office");
            var allowedConcreteCalls = new[]
            {
                ".LoadArtifactBody",
                ".LoadArtifactBodies",
                ".TryActivateHtmlWorkspaceRevision"
            };
            var offenders = new List<string>();
            foreach (var path in Directory.GetFiles(officeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedProjectPath(path)) continue;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.IndexOf("_chatStore.", StringComparison.Ordinal) < 0 ||
                        allowedConcreteCalls.Any(call => line.IndexOf(call, StringComparison.Ordinal) >= 0))
                    {
                        continue;
                    }
                    offenders.Add(relative + ": " + line.Trim());
                }
            }
            AssertEqual(0, offenders.Count,
                "Office conversation consumers must use IConversationStore; concrete ChatStore is reserved for artifact/CAS operations: " +
                string.Join(", ", offenders.ToArray()));

            AssertEqual(typeof(RNAssistant.Core.Persistence.IConversationStore),
                typeof(RNAssistant.Office.Services.ChatSessionService)
                    .GetField("_conversations", BindingFlags.Instance | BindingFlags.NonPublic).FieldType,
                "chat session service depends on the conversation port");

            var portMethods = typeof(RNAssistant.Core.Persistence.IConversationStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public);
            AssertEqual(15, portMethods.Length, "conversation port contains only current aggregate operations");
            foreach (var forbidden in new[]
            {
                "LoadArtifactBody",
                "ReadEvents",
                "AppendTrace",
                "CollectCasReferences",
                "ClearMessages"
            })
            {
                AssertTrue(!portMethods.Any(method => string.Equals(method.Name, forbidden, StringComparison.Ordinal)),
                    "conversation port excludes storage/artifact internals: " + forbidden);
            }

            var publicChatStoreMethods = typeof(RNAssistant.Core.Storage.ChatStore)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Select(method => method.Name)
                .ToArray();
            foreach (var replaced in new[]
            {
                "LoadOrCreateActive",
                "Create",
                "CreateTransient",
                "Load",
                "Save",
                "IsPersisted",
                "List",
                "ListHeaders",
                "Move",
                "MoveDocument",
                "Delete",
                "DeleteDocument",
                "ClearMessages",
                "LoadActiveSessionId",
                "SaveActiveSessionId",
                "CloseOpenSteps",
                "HasOpenToolExecution"
            })
            {
                AssertTrue(!publicChatStoreMethods.Contains(replaced, StringComparer.Ordinal),
                    "replaced broad ChatStore conversation API is not externally callable: " + replaced);
            }
        }

        private static void RunViewConsumersUseTypedProjection()
        {
            var root = FindHarnessRepositoryRoot();
            var chatExecution = File.ReadAllText(Path.Combine(root, "src", "RNAssistant.Office",
                "Controller", "AssistantController.ChatExecution.cs"));
            AssertTrue(chatExecution.IndexOf(
                "session.LastRun.KernelState = session.LastRun.KernelState.Interrupt(ex is OperationCanceledException, ex.Message, runId);",
                StringComparison.Ordinal) >= 0,
                "new-run controller errors must close the authoritative kernel lifecycle before persisting the UI projection");

            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Office.Services.ChatTurnResult).GetProperty("RunViewState").PropertyType,
                "application result exposes one typed run projection");
            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("RunViewState").PropertyType,
                "bridge exposes the same typed run projection");
            AssertEqual(typeof(long),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("SessionRevision").PropertyType,
                "bridge projection carries its durable ordering revision");
            AssertEqual(typeof(RNAssistant.Core.Models.RunViewState),
                typeof(RNAssistant.Core.Models.ChatSessionSummary).GetProperty("RunViewState").PropertyType,
                "chat catalog uses the same projection");

            foreach (var type in new[]
            {
                typeof(RNAssistant.Office.Services.ChatTurnResult),
                typeof(RNAssistant.Office.Contracts.ChatStateResponse),
                typeof(RNAssistant.Office.Contracts.SendChatResponse),
                typeof(RNAssistant.Core.Models.ChatSessionSummary)
            })
            {
                AssertTrue(type.GetProperty("ExecutionSummary") == null && type.GetProperty("RunStatus") == null,
                    "active application/bridge/UI DTO has no flat run projection: " + type.FullName);
            }
            AssertTrue(typeof(RNAssistant.Office.Contracts.ChatStateResponse).GetProperty("ResponseStatus") == null &&
                typeof(RNAssistant.Office.Contracts.SendChatResponse).GetProperty("ResponseStatus") == null,
                "model response status is not a UI bridge lifecycle");
            AssertTrue(typeof(RNAssistant.Core.Models.ChatMessage).Assembly.GetType(
                "RNAssistant.Core.Models.RunExecutionSummary", false) == null,
                "the replaced flat projection type is physically removed");
        }

        private static void MandatoryDependencyDirection()
        {
            var root = FindHarnessRepositoryRoot();
            var coreRoot = Path.Combine(root, "src", "RNAssistant.Core");
            var officeRoot = Path.Combine(root, "src", "RNAssistant.Office");
            var hostsRoot = Path.Combine(root, "src", "RNAssistant.OfficeHosts");

            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "AssistantRuntime.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Runtime", "AssistantRuntime.cs")),
                "application lifetime facade must live at the Office root, outside document/tool Runtime");

            AssertNoForbiddenDependencies(root,
                SourceFiles(Path.Combine(coreRoot, "Agent")),
                new[] { "RNAssistant.Office", "Microsoft.Office", "Microsoft.Web.WebView2", "System.Windows.Forms" },
                "Core.Agent must stay independent of Office and UI");

            AssertNoForbiddenDependencies(root,
                SourceFiles(Path.Combine(coreRoot, "ModelProtocol")),
                new[]
                {
                    "RNAssistant.Office", "IToolRuntime", "IToolHandler", "ToolExecutionContext",
                    "ToolExecutionRecord", "ToolExecutionOutcome"
                },
                "ModelProtocol may use typed tool wire/schema contracts but not tool execution");

            AssertNoForbiddenDependencies(root,
                SourceFiles(Path.Combine(officeRoot, "Vba")),
                new[]
                {
                    "RNAssistant.Office.WebView", "AssistantWebBridge", "AssistantPaneControl",
                    "Microsoft.Web.WebView2", "System.Windows.Forms"
                },
                "VBA domain code must stay independent of UI");

            var resourceFiles = SourceFiles(coreRoot)
                .Concat(SourceFiles(officeRoot))
                .Where(path => Path.GetFileName(path).IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertNoForbiddenDependencies(root,
                resourceFiles,
                new[]
                {
                    "RNAssistant.Core.Agent", "AgentKernel", "ConversationKernelAdapter",
                    "LegacyToolDefinitionAdapter"
                },
                "resource data-plane/catalog owners must not depend on AgentKernel or legacy execution adapters");

            AssertNoForbiddenDependencies(root,
                SourceFiles(hostsRoot),
                new[]
                {
                    "RNAssistant.Office.WebView", "AssistantWebBridge", "AssistantPaneControl",
                    "Microsoft.Web.WebView2"
                },
                "OfficeHosts may compose the application facade but must not depend on WebView types");

            var excelBackendPath = Path.Combine(hostsRoot, "Excel", "ExcelInteropBackend.cs");
            var excelFindReplaceBackendPath = Path.Combine(
                hostsRoot, "Excel", "ExcelFindReplaceInteropBackend.cs");
            var excelSheetBackendPath = Path.Combine(
                hostsRoot, "Excel", "ExcelSheetInteropBackend.cs");
            var excelRangeMutationBackendPath = Path.Combine(
                hostsRoot, "Excel", "ExcelRangeMutationInteropBackend.cs");
            var excelTableBackendPath = Path.Combine(
                hostsRoot, "Excel", "ExcelTableInteropBackend.cs");
            var excelChartBackendPath = Path.Combine(
                hostsRoot, "Excel", "ExcelChartInteropBackend.cs");
            var excelSessionPath = Path.Combine(hostsRoot, "Excel", "ExcelDocumentSession.cs");
            var excelAdapterPath = Path.Combine(hostsRoot, "ExcelAdapter.cs");
            var excelFactoryPath = Path.Combine(hostsRoot, "Targets", "OfficeComAdapterProvider.cs");
            AssertTrue(File.Exists(excelBackendPath) &&
                File.Exists(excelFindReplaceBackendPath) &&
                File.Exists(excelSheetBackendPath) &&
                File.Exists(excelRangeMutationBackendPath) &&
                File.Exists(excelTableBackendPath) &&
                File.Exists(excelChartBackendPath) &&
                File.Exists(excelSessionPath),
                "production Excel families require one bound session and direct backends");
            foreach (var removedPath in new[]
            {
                Path.Combine(officeRoot, "Tools", "ExcelReadCompatibilityBackend.cs"),
                Path.Combine(officeRoot, "Tools", "ExcelWriteCompatibilityBackend.cs"),
                Path.Combine(hostsRoot, "ExcelAdapter.WriteRange.cs")
            })
            {
                AssertTrue(!File.Exists(removedPath),
                    "replaced Excel compatibility path must be physically removed: " +
                    Path.GetFileName(removedPath));
            }

            var excelBackendSource = File.ReadAllText(excelBackendPath);
            var excelFindReplaceBackendSource = File.ReadAllText(excelFindReplaceBackendPath);
            var excelSheetBackendSource = File.ReadAllText(excelSheetBackendPath);
            var excelRangeMutationBackendSource =
                File.ReadAllText(excelRangeMutationBackendPath);
            var excelTableBackendSource = File.ReadAllText(excelTableBackendPath);
            var excelChartBackendSource = File.ReadAllText(excelChartBackendPath);
            var excelSessionSource = File.ReadAllText(excelSessionPath);
            var excelAdapterSource = File.ReadAllText(excelAdapterPath);
            var excelFactorySource = File.ReadAllText(excelFactoryPath);
            AssertTrue(
                excelSessionSource.IndexOf("StableKey(_workbook, RuntimeDocumentId)", StringComparison.Ordinal) >= 0 &&
                excelSessionSource.IndexOf("RequireOwnerAccess();", StringComparison.Ordinal) >= 0,
                "Excel session stable identity must stay bound-object and owner-STA derived");
            AssertTrue(
                excelBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelBackendSource.IndexOf("OfficeTargetDescriptor", StringComparison.Ordinal) < 0 &&
                excelBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel direct backend must use only its exact bound workbook");
            AssertTrue(
                excelFindReplaceBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelFindReplaceBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelFindReplaceBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                excelFindReplaceBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel find/replace backend must use only typed bound-document contracts");
            AssertTrue(
                excelSheetBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelSheetBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelSheetBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                excelSheetBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel sheet backend must use only typed bound-document contracts");
            AssertTrue(
                excelRangeMutationBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelRangeMutationBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelRangeMutationBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                excelRangeMutationBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel range mutation backend must use only typed bound-document contracts");
            AssertTrue(
                excelTableBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelTableBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelTableBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                excelTableBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel table backend must use only typed bound-document contracts");
            AssertTrue(
                excelChartBackendSource.IndexOf("session.BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                excelChartBackendSource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelChartBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                excelChartBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0,
                "Excel chart backend must use only typed bound-document contracts");
            AssertTrue(
                excelAdapterSource.IndexOf("private Excel.Workbook ActiveWorkbook(", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("OfficeTargetDescriptor", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("_target.", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.find_cells\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.replace_cells\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.add_sheet\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.rename_sheet\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.clear_range\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.sort_range\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.filter_range\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.format_range\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.add_table\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.create_chat_chart\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.upsert_chart\"", StringComparison.Ordinal) < 0 &&
                excelAdapterSource.IndexOf("case \"excel.delete_chart\"", StringComparison.Ordinal) < 0,
                "Excel execution adapter must not retain ActiveWorkbook or descriptor fallback");
            AssertTrue(
                excelFactorySource.IndexOf("ActiveWorkbook", StringComparison.Ordinal) < 0 &&
                excelFactorySource.IndexOf("HasExcelWindow", StringComparison.Ordinal) >= 0,
                "desktop/native Excel binding must resolve the exact selected window or descriptor");
            var excelProductionSources = SourceFiles(officeRoot)
                .Concat(SourceFiles(hostsRoot))
                .Select(File.ReadAllText)
                .ToArray();
            foreach (var removedId in new[]
            {
                "excel.inspect_internal",
                "excel.read_range_internal",
                "excel.write_range_read_internal",
                "excel.write_range_apply_internal"
            })
            {
                AssertTrue(!excelProductionSources.Any(source =>
                    source.IndexOf(removedId, StringComparison.Ordinal) >= 0),
                    "internal Excel compatibility command must be physically removed: " + removedId);
            }

            var vbaBackendPath = Path.Combine(
                hostsRoot, "Vba", "VbaInteropBackend.cs");
            var vbaContractsPath = Path.Combine(
                officeRoot, "Domains", "Vba", "VbaHostContracts.cs");
            var vbaReaderPath = Path.Combine(
                officeRoot, "Vba", "VbaReader.cs");
            var vbaHandlerPath = Path.Combine(
                officeRoot, "Tools", "VbaToolHandler.cs");
            AssertTrue(
                File.Exists(vbaBackendPath) && File.Exists(vbaContractsPath),
                "VBA requires one direct typed backend contract and host implementation");
            AssertTrue(!File.Exists(Path.Combine(
                officeRoot, "Tools", "VbaMutationToolAdapter.cs")),
                "replaced VBA legacy command adapter must be physically removed");
            AssertTrue(File.Exists(vbaHandlerPath) &&
                File.ReadAllText(vbaHandlerPath).IndexOf(
                    "IPreparableToolHandler", StringComparison.Ordinal) >= 0,
                "public VBA requires one native preparable handler");
            var vbaBackendSource = File.ReadAllText(vbaBackendPath);
            var vbaReaderSource = File.ReadAllText(vbaReaderPath);
            var vbaSupportSources = SourceFiles(Path.Combine(hostsRoot, "Vba"))
                .Select(File.ReadAllText)
                .ToArray();
            AssertTrue(
                vbaBackendSource.IndexOf("BoundDocumentObject", StringComparison.Ordinal) >= 0 &&
                vbaBackendSource.IndexOf("ToolInvocation", StringComparison.Ordinal) < 0 &&
                vbaBackendSource.IndexOf("ToolResult", StringComparison.Ordinal) < 0 &&
                vbaBackendSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0 &&
                vbaReaderSource.IndexOf("ExecuteTool(", StringComparison.Ordinal) < 0 &&
                !vbaSupportSources.Any(source =>
                    source.IndexOf("ToolResult", StringComparison.Ordinal) >= 0),
                "VBA read/mutation/package host path must be bound and typed end to end");
            var productionHostAdapterSources = new[]
            {
                excelAdapterSource,
                File.ReadAllText(Path.Combine(hostsRoot, "WordAdapter.cs")),
                File.ReadAllText(Path.Combine(hostsRoot, "PowerPointAdapter.cs"))
            };
            foreach (var retiredId in new[]
            {
                "vba_list_project_components_internal",
                "vba_read_module",
                "vba_replace_module",
                "vba_create_module_internal",
                "vba_rename_module_internal",
                "vba_delete_module_internal",
                "vba_install_package_internal",
                "vba_remove_package_internal",
                ".run_macro"
            })
            {
                AssertTrue(!productionHostAdapterSources.Any(source =>
                    source.IndexOf(retiredId, StringComparison.Ordinal) >= 0),
                    "retired VBA host command must be physically removed: " +
                    retiredId);
            }
            var officeProductionSources = SourceFiles(officeRoot)
                .Select(File.ReadAllText)
                .ToArray();
            var officeToolExecutorSource = File.ReadAllText(Path.Combine(
                officeRoot, "Tools", "OfficeToolExecutor.cs"));
            var vbaToolSources = SourceFiles(Path.Combine(
                    officeRoot, "Tools"))
                .Where(path => Path.GetFileName(path).StartsWith(
                    "VbaTool", StringComparison.Ordinal))
                .Select(File.ReadAllText)
                .ToArray();
            AssertTrue(
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind.Vba", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind.UserQuestion", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind.PlanDocument", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind.TaskList", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind.ToolAuthoring", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ControllerExecutorKind", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "ExecuteControllerTool(", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "PrepareControllerTool", StringComparison.Ordinal) < 0 &&
                officeToolExecutorSource.IndexOf(
                    "PreviewPreparedControllerTool", StringComparison.Ordinal) < 0 &&
                !vbaToolSources.Any(source =>
                    source.IndexOf("ExecuteControllerTool(", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("PrepareControllerTool(", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("PreviewPreparedControllerTool(", StringComparison.Ordinal) >= 0) &&
                !vbaToolSources.Any(source => source.IndexOf(
                    "RuntimeGuardJson", StringComparison.Ordinal) >= 0),
                "migrated native families must not retain controller execution or VBA compatibility guard paths");
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "SkillAuthoringToolHandler.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "SkillAuthoringService.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "SkillToolExecutor.cs")),
                "skill authoring must use its exact native handler without a controller executor");
            var skillControllerSources = SourceFiles(Path.Combine(
                    officeRoot, "Controller"))
                .Select(File.ReadAllText)
                .ToArray();
            AssertTrue(!skillControllerSources.Any(source =>
                    source.IndexOf("_skillStore", StringComparison.Ordinal) >= 0),
                "Skills UI controller must call the typed authoring owner instead of mutating SkillStore");
            AssertTrue(
                typeof(RNAssistant.Office.Contracts.SkillMutationWriteRequest)
                    .GetProperty("Skills") == null &&
                typeof(RNAssistant.Office.Contracts.SkillMutationWriteRequest)
                    .GetProperty("Mutations") == null &&
                typeof(RNAssistant.Office.Contracts.SkillMutationWriteRequest)
                    .GetProperty("UploadLeaseId").PropertyType == typeof(string) &&
                typeof(RNAssistant.Office.Contracts.SkillLibraryMutationBatch)
                    .GetProperty("Mutations").PropertyType ==
                    typeof(List<RNAssistant.Office.Contracts.SkillCoreMutationPayload>) &&
                typeof(RNAssistant.Office.Contracts.InitResponse)
                    .GetProperty("Skills").PropertyType ==
                    typeof(RNAssistant.Office.Contracts.SkillLibraryResponse) &&
                typeof(RNAssistant.Office.Contracts.SendChatResponse)
                    .GetProperty("Skills").PropertyType ==
                    typeof(RNAssistant.Office.Contracts.SkillLibraryResponse),
                "Skills bridge must expose only versioned package/result DTOs and explicit mutations");

            var toolModelAssembly = typeof(ToolCatalogEntry).Assembly;
            AssertTrue(
                !File.Exists(Path.Combine(coreRoot, "Models",
                    "ToolModels.cs")) &&
                File.Exists(Path.Combine(coreRoot, "Tools",
                    "ToolCatalogEntry.cs")) &&
                File.Exists(Path.Combine(coreRoot, "Tools",
                    "ToolInvocation.cs")) &&
                toolModelAssembly.GetType(
                    "RNAssistant.Core.Models.ToolDefinition", false) == null &&
                toolModelAssembly.GetType(
                    "RNAssistant.Core.Models.ToolCommand", false) == null &&
                toolModelAssembly.GetType(
                    "RNAssistant.Core.Models.ToolResult", false) == null,
                "retired generic tool DTOs must be physically removed from Core.Models");
            foreach (var removedPath in new[]
            {
                Path.Combine(officeRoot, "Runtime",
                    "LegacyToolDefinitionAdapter.cs"),
                Path.Combine(officeRoot, "Runtime",
                    "LegacyToolResultAdapter.cs"),
                Path.Combine(officeRoot, "Services",
                    "ToolResultUiProjection.cs"),
                Path.Combine(officeRoot, "Tools",
                    "OfficeBuiltInToolCatalog.cs"),
                Path.Combine(officeRoot, "Tools",
                    "ControllerToolDefinition.cs")
            })
            {
                AssertTrue(!File.Exists(removedPath),
                    "final generic tool compatibility path must be removed: " +
                    Path.GetFileName(removedPath));
            }
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "OfficeToolCatalog.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "ControllerToolCatalogEntry.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "DirectToolBindingCatalog.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Contracts",
                    "BridgeDtos.Tools.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Contracts",
                    "ToolRunResult.cs")),
                "direct catalog, binding and typed Tool Library contracts must be explicit sources");
            AssertTrue(
                typeof(RNAssistant.Office.IOfficeApplicationAdapter).GetMethod(
                    "GetBuiltInTools") == null &&
                typeof(RNAssistant.Office.IOfficeApplicationAdapter).GetMethod(
                    "ExecuteTool") == null,
                "Office adapter boundary must not expose generic catalog or command dispatch");
            AssertTrue(
                typeof(RNAssistant.Office.Contracts.SaveToolsPayload)
                    .GetProperty("Tools") == null &&
                typeof(RNAssistant.Office.Contracts.SaveToolsPayload)
                    .GetProperty("Mutations").PropertyType ==
                    typeof(List<RNAssistant.Office.Contracts.ToolCoreMutationPayload>) &&
                typeof(RNAssistant.Office.Contracts.InitResponse)
                    .GetProperty("Tools").PropertyType ==
                    typeof(RNAssistant.Office.Contracts.ToolLibraryResponse) &&
                typeof(RNAssistant.Office.Contracts.SendChatResponse)
                    .GetProperty("Tools").PropertyType ==
                    typeof(RNAssistant.Office.Contracts.ToolLibraryResponse) &&
                typeof(RNAssistant.Office.Contracts.VbaToolPackageResponse)
                    .GetProperty("Tools").PropertyType ==
                    typeof(RNAssistant.Office.Contracts.ToolLibraryResponse),
                "Tools bridge must expose only versioned library/result DTOs and explicit guarded mutations");
            var toolSnapshotSource = File.ReadAllText(Path.Combine(
                officeRoot, "Services", "ToolPackSnapshotFactory.cs"));
            AssertTrue(
                toolSnapshotSource.IndexOf("definition.Binding",
                    StringComparison.Ordinal) >= 0 &&
                toolSnapshotSource.IndexOf("definition.Policy",
                    StringComparison.Ordinal) >= 0 &&
                toolSnapshotSource.IndexOf("DirectToolBindingCatalog",
                    StringComparison.Ordinal) < 0 &&
                toolSnapshotSource.IndexOf("PolicyFor(",
                    StringComparison.Ordinal) < 0,
                "ToolPack capture must consume source-owned policy/binding without reconstructing authority");
            var toolControllerSource = File.ReadAllText(Path.Combine(
                officeRoot, "Controller", "AssistantController.Tools.cs"));
            AssertTrue(toolControllerSource.IndexOf("_toolStore",
                    StringComparison.Ordinal) < 0,
                "Tools UI controller must call the typed authoring owner instead of mutating ToolStore");
            var allHostAdapterSources = productionHostAdapterSources.Concat(
                new[] { File.ReadAllText(Path.Combine(hostsRoot,
                    "OutlookAdapter.cs")) }).ToArray();
            AssertTrue(!allHostAdapterSources.Any(source =>
                    source.IndexOf("GetBuiltInTools", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("ExecuteTool(", StringComparison.Ordinal) >= 0),
                "production host adapters must not retain generic tool catalog or dispatch methods");
            var fakeAdapterSources = SourceFiles(Path.Combine(root,
                    "tests", "RNAssistant.Testing"))
                .Select(File.ReadAllText).ToArray();
            AssertTrue(!fakeAdapterSources.Any(source =>
                    source.IndexOf("GetBuiltInTools", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("QueueResult", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("BeforeExecuteTool", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("ThrowOnToolId", StringComparison.Ordinal) >= 0 ||
                    source.IndexOf("ExecuteTool(", StringComparison.Ordinal) >= 0),
                "test fakes must script direct typed backends without retired generic queues");
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "UserQuestionToolHandler.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "UserQuestionToolExecutor.cs")),
                "questions_ask must use its exact native handler without a controller executor");
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "PlanDocumentToolCatalog.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "PlanDocumentToolHandler.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "PlanDocumentToolExecutor.cs")),
                "plan_doc tools must use exact native handlers without a controller executor");
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "TaskListToolCatalog.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "TaskListToolHandler.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Services",
                    "TaskListService.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "TaskListToolExecutor.cs")),
                "task-list tools must use their typed service and exact native handlers");
            AssertTrue(
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "ToolAuthoringCatalog.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "ToolAuthoringToolHandler.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "ToolAuthoringService.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "ToolAuthoringExecutor.cs")),
                "tool authoring must use typed services and exact native handlers");
            AssertTrue(
                File.Exists(Path.Combine(coreRoot, "Tools",
                    "ToolPackageSource.cs")) &&
                File.Exists(Path.Combine(officeRoot, "Tools",
                    "VbaPackageToolHandler.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "VbaPackageToolAdapter.cs")) &&
                !File.Exists(Path.Combine(officeRoot, "Tools",
                    "VbaLegacyResultProjection.cs")),
                "custom VBA packages must use the versioned source/result boundary and exact native handler");
            foreach (var removedAdapter in new[]
            {
                "VbaMutationDocumentContextAdapter",
                "VbaMutationReaderAdapter",
                "VbaMutationBackendAdapter",
                "VbaPackageBackendAdapter",
                "VbaMutationToolResultMapper",
                "VbaPackageToolAdapter",
                "VbaLegacyResultProjection"
            })
            {
                AssertTrue(!officeProductionSources.Any(source =>
                    source.IndexOf(removedAdapter, StringComparison.Ordinal) >= 0),
                    "replaced VBA compatibility adapter must be removed: " +
                    removedAdapter);
            }

            AssertNoForbiddenDependencies(root,
                SourceFiles(officeRoot),
                new[] { "VbaProjectSupport.", "DocumentIdentity." },
                "host-specific helpers must not be consumed by the Office assembly");

            var markerContract = typeof(RNAssistant.Office.Vba.VbaPackageOwnershipMarker);
            AssertTrue(
                markerContract.IsPublic &&
                markerContract.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static) != null,
                "OfficeHosts VBA guard must use an explicit public Office.Vba marker contract");
            AssertTrue(
                !SourceFiles(officeRoot).Any(path =>
                {
                    var source = File.ReadAllText(path);
                    return source.IndexOf("InternalsVisibleTo", StringComparison.Ordinal) >= 0 &&
                        source.IndexOf("RNAssistant.OfficeHosts", StringComparison.Ordinal) >= 0;
                }),
                "Office must not grant broad friend-assembly access to OfficeHosts");

            var uiFiles = SourceFiles(Path.Combine(officeRoot, "WebView"))
                .Concat(Directory.GetFiles(Path.Combine(root, "web"), "*.js", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(Path.Combine(root, "web"), "*.html", SearchOption.AllDirectories));
            AssertNoForbiddenDependencies(root,
                uiFiles,
                new[]
                {
                    "RNAssistant.Office.Tools", "RNAssistant.Office.Domains", "RNAssistant.Office.Vba",
                    "OfficeToolExecutor", "VbaToolExecutor", "ExcelReadService", "ExcelWriteService", "ToolRuntime"
                },
                "UI and bridge code must use application contracts instead of domain executors");
        }

        private static IEnumerable<string> SourceFiles(string directory)
        {
            return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGeneratedProjectPath(path));
        }

        private static void AssertNoForbiddenDependencies(
            string root,
            IEnumerable<string> paths,
            IEnumerable<string> forbiddenTokens,
            string boundary)
        {
            var offenders = new List<string>();
            foreach (var path in (paths ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = File.ReadAllText(path);
                foreach (var token in forbiddenTokens ?? new string[0])
                {
                    if (source.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                    offenders.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + ": " + token);
                }
            }
            AssertEqual(0, offenders.Count, boundary + ": " + string.Join(", ", offenders.ToArray()));
        }

        private static bool IsGeneratedProjectPath(string path)
        {
            var normalized = Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void VersioningOrdinaryBuildsNeedNoBump()
        {
            using (var fixture = new VersioningFixture())
            {
                var props = File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.props"));
                var head = fixture.Git("rev-parse", "HEAD").Trim();
                var expected = fixture.ProductVersion + "+g" + head.Substring(0, 12);
                for (var i = 0; i < 2; i++)
                {
                    var output = fixture.Build("PrintBuildIdentity", true, "-p:RNAssistantBuildNumber=42");
                    AssertContains(output, "Identity=" + expected, "same product version supports repeated builds");
                    AssertContains(output, "File=" + fixture.VersionPrefix + ".42", "build number is not product version");
                    AssertContains(output, "Application=" + fixture.VersionPrefix + ".42", "ClickOnce uses numeric version");
                }
                AssertEqual("", fixture.Git("status", "--porcelain").Trim(), "validation does not modify tracked source");
                File.AppendAllText(Path.Combine(fixture.Repository, "CHANGELOG.md"), "\nLocal work\n");
                AssertContains(fixture.Build("PrintBuildIdentity", true), expected + ".dirty", "dirty builds remain allowed and identifiable");
                fixture.Commit("ordinary change without bump");
                var nextHead = fixture.Git("rev-parse", "HEAD").Trim();
                AssertTrue(head != nextHead, "fixture creates a distinct ordinary commit");
                AssertContains(fixture.Build("PrintBuildIdentity", true),
                    "Identity=" + fixture.ProductVersion + "+g" + nextHead.Substring(0, 12), "new commit needs no product bump");
                AssertEqual(props, File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.props")), "product version is unchanged");
                AssertEqual("", fixture.Git("tag", "--list").Trim(), "ordinary builds and commits create no tags");
            }
        }

        private static void VersioningSourceArchivesBuildWithoutGit()
        {
            using (var fixture = new VersioningFixture())
            {
                var head = fixture.Git("rev-parse", "HEAD").Trim();
                var archivePath = Path.Combine(fixture.TemporaryRoot, "source.zip");
                var archiveRoot = Path.Combine(fixture.TemporaryRoot, "archive");
                fixture.Git("archive", "--format=zip", "--output=" + archivePath, "HEAD");
                ZipFile.ExtractToDirectory(archivePath, archiveRoot);
                AssertContains(
                    File.ReadAllText(Path.Combine(archiveRoot, "build", "RNAssistant.GitArchive.props")),
                    head,
                    "Git archive substitutes the exact source commit");
                var archived = fixture.BuildAt(archiveRoot, "PrintBuildIdentity", true);
                AssertContains(archived,
                    "Identity=" + fixture.ProductVersion + "+g" + head.Substring(0, 12) + ".unknown",
                    "GitHub-style archive recovers commit identity without a checkout");
                AssertContains(archived, "working-tree state is unknown",
                    "archive commit does not invent post-extraction cleanliness");
                fixture.BuildAt(archiveRoot, "GenerateRNAssistantVersionInfo", true);
                var archivedAttributes = File.ReadAllText(
                    Path.Combine(archiveRoot, "obj", "RNAssistant.Version.g.cs"));
                AssertContains(archivedAttributes,
                    "AssemblyMetadataAttribute(\"CommitSha\", \"" + head + "\")",
                    "archive assembly metadata keeps the full substituted commit");
                AssertContains(archivedAttributes,
                    "AssemblyMetadataAttribute(\"WorkingTreeState\", \"unknown\")",
                    "archive assembly metadata keeps unknown post-extraction state");

                Directory.Move(Path.Combine(fixture.Repository, ".git"), Path.Combine(fixture.TemporaryRoot, "saved-git"));
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var output = fixture.Build("PrintBuildIdentity", true, "-p:Configuration=" + configuration);
                    AssertContains(output, "Identity=" + fixture.ProductVersion + "+source-archive.unknown", "ordinary archive build is visibly unidentified");
                    AssertContains(output, "Git provenance is unknown", "archive provenance warning is explicit");
                }
                fixture.Build("GenerateRNAssistantVersionInfo", true);
                var generated = File.ReadAllText(Path.Combine(fixture.Repository, "obj", "RNAssistant.Version.g.cs"));
                foreach (var field in new[] { "CommitSha", "Branch", "WorkingTreeState" })
                    AssertContains(generated, "AssemblyMetadataAttribute(\"" + field + "\", \"unknown\")", "archive metadata does not invent provenance");

                AssertContains(fixture.Build("PrintBuildIdentity", true, "-p:RNAssistantCommitSha=" + head),
                    fixture.ProductVersion + "+g" + head.Substring(0, 12) + ".unknown", "partial archive metadata does not imply a clean tree");
                File.WriteAllText(Path.Combine(fixture.Repository, "Directory.Build.local.props"),
                    "<Project><PropertyGroup><RNAssistantCommitSha>" + head + "</RNAssistantCommitSha>" +
                    "<RNAssistantBranch>stabilization/16.1</RNAssistantBranch>" +
                    "<RNAssistantWorkingTreeState>dirty</RNAssistantWorkingTreeState></PropertyGroup></Project>");
                var identified = fixture.Build("PrintBuildIdentity", true);
                AssertContains(identified, fixture.ProductVersion + "+g" + head.Substring(0, 12) + ".dirty", "explicit archive provenance is preserved");
                AssertTrue(!identified.Contains("Git provenance is unknown"), "identified archive needs no warning");
                AssertContains(fixture.Build("ValidateVersionFormat", false, "-p:RNAssistantCommitSha=invalid"),
                    "full Git commit SHA", "malformed archive SHA still fails");
                AssertContains(fixture.ReleaseBuild("ValidateRNAssistantRelease", false),
                    "Release requires a Git checkout", "explicit metadata does not bypass live release checks");
                File.Delete(Path.Combine(fixture.Repository, "Directory.Build.local.props"));
                AssertContains(fixture.ReleaseBuild("PrintBuildIdentity", false),
                    "Release requires explicit Git provenance", "release tag cannot qualify an unknown archive");
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseBuild=true"),
                    "Release requires explicit Git provenance", "explicit release flag cannot qualify an unknown archive");
                AssertContains(fixture.Build("ValidateRNAssistantRelease", false),
                    "Release requires explicit Git provenance", "direct release validation cannot qualify an unknown archive");
            }
        }

        private static void VersioningRejectsMalformedMetadata()
        {
            using (var fixture = new VersioningFixture())
            {
                var cases = new[]
                {
                    new[] { "-p:RNAssistantVersionPrefix=01.1.0", "SemVer core" },
                    new[] { "-p:RNAssistantVersionSuffix=beta.01", "SemVer prerelease" },
                    new[] { "-p:RNAssistantBuildNumber=-1", "RNAssistantBuildNumber" },
                    new[] { "-p:RNAssistantBuildNumber=65535", "RNAssistantBuildNumber" },
                    new[] { "-p:Version=99.0.0", "Version must be derived" },
                    new[] { "-p:AssemblyVersion=99.0.0.0", "AssemblyVersion must match" },
                    new[] { "-p:InformationalVersion=unidentified", "InformationalVersion must include" },
                    new[] { "-p:RNAssistantCommitSha=unknown", "full Git commit SHA" },
                    new[] { "-p:RNAssistantWorkingTreeState=unknown", "RNAssistantWorkingTreeState must be clean or dirty" },
                    new[] { "-p:RNAssistantBuildEvidenceSignerSha256=abc", "certificate fingerprint" },
                    new[] { "-p:RNAssistantRuntimePlatform=../x64", "bounded platform name" }
                };
                foreach (var item in cases)
                    AssertContains(fixture.Build("ValidateVersionFormat", false, item[0]), item[1], "invalid build metadata fails");
                fixture.Build("ValidateVersionFormat", true, "-p:RNAssistantVersionSuffix=beta.1");
            }
        }

        private static void VersioningReleaseGatesAreExplicit()
        {
            using (var fixture = new VersioningFixture())
            {
                AssertContains(fixture.Build("ValidateReleaseEvidenceSigner", false),
                    "pinned RNAssistantBuildEvidenceSignerSha256",
                    "release admission cannot trust an unpinned evidence signer");
                fixture.ReleaseBuild("ValidateRNAssistantRelease", true);
                fixture.ReleaseBuild("PrintBuildIdentity", true);
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseBuild=true"),
                    "RNAssistantReleaseTag must exactly match", "release builds cannot omit their tag");
                AssertContains(fixture.Build("PrintBuildIdentity", false, "-p:RNAssistantReleaseTag=v99.0.0"),
                    "RNAssistantReleaseTag must exactly match", "tag builds automatically enable release checks");
                AssertContains(fixture.Build("ValidateReleaseTagMatchesProductVersion", false,
                    "-p:RNAssistantVersionPrefix=16.1.0", "-p:RNAssistantVersionSuffix=dev", "-p:RNAssistantReleaseTag=v16.1.0-dev"),
                    "development builds cannot be tagged", "dev is not a release milestone");

                File.WriteAllText(Path.Combine(fixture.Repository, "untracked.txt"), "not releasable");
                fixture.Build("PrintBuildIdentity", true);
                AssertContains(fixture.ReleaseBuild("PrintBuildIdentity", false), "clean working tree", "untracked files block release builds");
                fixture.Git("add", "untracked.txt");
                AssertContains(fixture.ReleaseBuild("ValidateRNAssistantRelease", false), "clean working tree", "staged files also block release");
                fixture.Commit("fixture cleanup");

                var changelog = Path.Combine(fixture.Repository, "CHANGELOG.md");
                File.WriteAllText(changelog, "# Changelog\n\n## [16.1.0-rc.1] - 2026-08-28\n\n### Fixed\n\n## [16.0.4] - 2026-08-27\n- Old note\n");
                AssertContains(fixture.ReleaseBuild("ValidateReleaseChangelog", false), "at least one release note", "older notes cannot qualify an empty release");
                File.Delete(changelog);
                AssertContains(fixture.ReleaseBuild("ValidateReleaseChangelog", false), "requires CHANGELOG.md", "missing changelog blocks release");
            }
            var releaseScript = File.ReadAllText(Path.Combine(
                FindHarnessRepositoryRoot(), "tools", "Prepare-Release.ps1"));
            AssertContains(releaseScript, "Assert-TrackedReleaseVersion",
                "finalization checks the tracked source version, not only MSBuild overrides");
            AssertContains(releaseScript, "Assert-SignedBuildEvidence",
                "finalization verifies detached evidence before tagging");
            AssertContains(releaseScript, "if ($Finalize)",
                "tag creation is isolated in the explicit finalization stage");
            AssertEqual(1, Regex.Matches(releaseScript,
                "Arguments @\\(\\\"tag\\\"", RegexOptions.CultureInvariant).Count,
                "release script has one non-reusable tag creation site");
            AssertContains(releaseScript, "Prepared release commit $preparedCommit without a tag",
                "preparation explicitly stops before tag creation");
        }

        private static void VersioningTagsCannotBeReused()
        {
            using (var fixture = new VersioningFixture())
            {
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false),
                    "Unable to verify remote release tag absence", "missing remote fails closed");
                var remote = Path.Combine(fixture.TemporaryRoot, "remote.git");
                fixture.Git("init", "--quiet", "--bare", remote);
                fixture.Git("remote", "add", "origin", remote);
                fixture.ReleaseBuild("ValidateTagDoesNotExist", true);
                // These refs exist only in disposable fixture repositories, never in the working repository.
                fixture.Git("push", "--quiet", "origin", "HEAD:refs/tags/v16.1.0-rc.1");
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false), "already exists remotely", "remote tags cannot be reused");
                fixture.Git("update-ref", "refs/tags/v16.1.0-rc.1", "HEAD");
                AssertContains(fixture.ReleaseBuild("ValidateTagDoesNotExist", false), "already exists locally", "local tags cannot be reused");
            }
        }

        private static void VersioningGeneratesAssemblyMetadata()
        {
            var assembly = typeof(Program).Assembly;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            var identity = metadata["CommitSha"] == "unknown"
                ? "+source-archive" : "+g" + metadata["CommitSha"].Substring(0, 12);
            AssertContains(informational, metadata["ProductVersion"] + identity,
                "SDK assembly carries product and commit identity or explicit archive provenance");
            AssertTrue(metadata["BuildUtc"].EndsWith("Z", StringComparison.Ordinal), "SDK assembly records UTC");
            AssertTrue(!string.IsNullOrWhiteSpace(metadata["Branch"]) && !string.IsNullOrWhiteSpace(metadata["Channel"]), "SDK assembly records branch/channel");
            AssertTrue(!string.IsNullOrWhiteSpace(metadata["Configuration"]) &&
                !string.IsNullOrWhiteSpace(metadata["RuntimePlatform"]),
                "SDK assembly records configuration/runtime platform");
            AssertEqual("unavailable", metadata["BuildEvidenceSignerSha256"],
                "ordinary build does not invent a trusted evidence signer");
            using (var fixture = new VersioningFixture())
            {
                fixture.Build("GenerateRNAssistantVersionInfo", true);
                var generated = File.ReadAllText(Path.Combine(fixture.Repository, "obj", "RNAssistant.Version.g.cs"));
                AssertContains(generated, "AssemblyInformationalVersionAttribute", "old-style project emits informational version");
                AssertContains(generated, fixture.ProductVersion + "+g", "old-style project includes build identity");
                AssertContains(generated, "AssemblyMetadataAttribute(\"CommitSha\", \"" + fixture.Git("rev-parse", "HEAD").Trim() + "\")",
                    "old-style project emits the full commit SHA");
                AssertContains(generated, "AssemblyMetadataAttribute(\"BuildUtc\"", "old-style project emits UTC metadata");
                AssertContains(generated, "AssemblyMetadataAttribute(\"BuildEvidenceSignerSha256\", \"unavailable\")",
                    "old-style ordinary build marks evidence signer unavailable");
            }
        }

        private sealed class VersioningFixture : IDisposable
        {
            public string TemporaryRoot { get; private set; }
            public string Repository { get; private set; }
            public string VersionPrefix { get; private set; }
            public string ProductVersion { get; private set; }

            public VersioningFixture()
            {
                TemporaryRoot = Path.Combine(Path.GetTempPath(), "RNAssistant versioning " + Guid.NewGuid().ToString("N"));
                Repository = Path.Combine(TemporaryRoot, "repo");
                Directory.CreateDirectory(Path.Combine(Repository, "build"));
                Directory.CreateDirectory(Path.Combine(Repository, "obj"));
                var root = FindHarnessRepositoryRoot();
                foreach (var file in new[]
                {
                    ".gitattributes", "Directory.Build.props", "Directory.Build.targets",
                    "build/RNAssistant.GitArchive.props", "build/RNAssistant.Release.targets"
                })
                    File.Copy(Path.Combine(root, file), Path.Combine(Repository, file));
                var props = XDocument.Load(Path.Combine(Repository, "Directory.Build.props"));
                VersionPrefix = props.Descendants().Single(element => element.Name.LocalName == "RNAssistantVersionPrefix").Value;
                var suffix = props.Descendants().Single(element => element.Name.LocalName == "RNAssistantVersionSuffix").Value;
                ProductVersion = VersionPrefix + (suffix.Length == 0 ? "" : "-" + suffix);
                File.WriteAllText(Path.Combine(Repository, ".gitignore"), "obj/\n");
                File.WriteAllText(Path.Combine(Repository, "CHANGELOG.md"),
                    "# Changelog\n\n## [Unreleased]\n\n## [16.1.0-rc.1] - 2026-08-28\n\n### Fixed\n\n- Don't lose build identity.\n");
                File.WriteAllText(Path.Combine(Repository, "Versioning.csproj"), @"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""Directory.Build.props"" />
  <PropertyGroup><IntermediateOutputPath>obj/</IntermediateOutputPath></PropertyGroup>
  <Import Project=""Directory.Build.targets"" />
  <Target Name=""PrepareForBuild"" />
  <Target Name=""PrintBuildIdentity"" DependsOnTargets=""PrepareForBuild"">
    <Message Importance=""high"" Text=""Product=$(Version) Identity=$(InformationalVersion) File=$(FileVersion) Application=$(ApplicationVersion)"" />
  </Target>
</Project>");
                Git("init", "--quiet", "--initial-branch=stabilization/16.1");
                Commit("versioning fixture");
            }

            public string Git(params string[] arguments)
            {
                return RunVersioningCommand(Repository, "git", true, new[]
                {
                    "-c", "user.name=RNAssistant Harness", "-c", "user.email=harness@example.invalid",
                    "-c", "commit.gpgsign=false", "-c", "core.hooksPath="
                }.Concat(arguments).ToArray());
            }

            public void Commit(string message)
            {
                Git("add", ".");
                Git("commit", "--quiet", "-m", message);
            }

            public string Build(string target, bool success, params string[] properties)
            {
                return BuildAt(Repository, target, success, properties);
            }

            public string BuildAt(string repository, string target, bool success, params string[] properties)
            {
                return RunVersioningCommand(repository, "dotnet", success, new[]
                {
                    "msbuild", "Versioning.csproj", "-nologo", "-v:minimal", "-t:" + target
                }.Concat(properties).ToArray());
            }

            public string ReleaseBuild(string target, bool success)
            {
                return Build(target, success, "-p:RNAssistantVersionPrefix=16.1.0",
                    "-p:RNAssistantVersionSuffix=rc.1", "-p:RNAssistantReleaseTag=v16.1.0-rc.1",
                    "-p:RNAssistantBuildEvidenceSignerSha256=" + new string('a', 64),
                    "-p:RNAssistantRuntimePlatform=x64");
            }

            public void Dispose()
            {
                foreach (var path in Directory.GetFiles(TemporaryRoot, "*", SearchOption.AllDirectories))
                    File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(TemporaryRoot, true);
            }
        }

        private static string RunVersioningCommand(string directory, string executable, bool success, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    throw new InvalidOperationException("Versioning fixture command timed out: " + executable);
                }
                var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                AssertEqual(success, process.ExitCode == 0, executable + " " + string.Join(" ", arguments) + "\n" + output);
                return output;
            }
        }
    }
}
