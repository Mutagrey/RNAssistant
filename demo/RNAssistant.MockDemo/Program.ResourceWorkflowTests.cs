using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Harness;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;

namespace RNAssistant.MockDemo
{
    internal static partial class Program
    {
        private static async Task<int> RunResourceWorkflowTestAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.ResourceWorkflow." + Guid.NewGuid().ToString("N"));
            AssistantController controller = null;
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var responses = new Queue<string>();
                var requireSelections = false;
                var compacting = false;
                var compactionCalls = 0;
                Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> model =
                    (settings, messages, token) => {
                        if (compacting)
                        {
                            compactionCalls++;
                            var source = messages.SelectMany(message => (message.Content ?? "").Split('\n'))
                                .Where(line => line.StartsWith("{\"sourceId\":", StringComparison.Ordinal))
                                .Select(Newtonsoft.Json.Linq.JObject.Parse).First(item => (bool?)item["userSource"] == true);
                            return Task.FromResult(new LlmCompletionResult { Content = JsonConvert.SerializeObject(new {
                                claims = new[] { new { kind = "constraint", text = (string)source["text"],
                                    sourceIds = new[] { (string)source["sourceId"] } } } }) });
                        }
                        if (requireSelections)
                            foreach (var skill in new[] { "common.task_tracking", "common.html_workspace_authoring" })
                                if (!messages.Any(message => (message.Content ?? "").StartsWith("SELECTED_SKILL " + skill + "\n", StringComparison.Ordinal)))
                                    throw new Exception("selected skill lost after confirmation/restart: " + skill);
                        if (responses.Count == 0) throw new Exception("unexpected extra model request");
                        var response = responses.Dequeue();
                        if (response == "fixture-finish") {
                            var refs = RNAssistant.Core.ModelProtocol.ModelReferenceProjection.Delivered(messages.ToArray());
                            response = RNAssistant.Core.ModelProtocol.ModelProtocolWire.WriteAcceptedDecision("finish", "Report the observed outcome.", "Done",
                                results: refs.Count == 0 ? new RNAssistant.Core.ModelProtocol.ConversationResultClaim[0] :
                                    new[] { new RNAssistant.Core.ModelProtocol.ConversationResultClaim("Requested change", new[] { refs.Last() },
                                        RNAssistant.Core.ModelProtocol.ConversationAssessment.Satisfied) });
                        }
                        return Task.FromResult(new LlmCompletionResult { Content = response });
                    };
                Func<string, object, string> tool = (name, arguments) => JsonConvert.SerializeObject(new {
                    message = "", action = "execute", reason = "Perform the checked step.", tool_calls = new[] { new { name, arguments } } });
                const string done = "fixture-finish";
                new SettingsService(paths).Save(new AppSettings { AutoConfirmToolActions = false, ContextWindowOverrideTokens = 131072 });
                controller = new AssistantController(adapter, paths, model);
                var id = controller.Initialize().ActiveChatId;
                await CheckResourceLibraryReadsAsync(controller, id);
                responses.Enqueue(tool("common.skills_load", new { id = "common.task_tracking" }));
                responses.Enqueue(tool("common.task_list_set", new { action = "save", goal = "Edit report", steps = new[] { new { text = "Update HTML", status = "in_progress" } } }));
                responses.Enqueue(tool("common.skills_load", new { id = "common.html_workspace_authoring" }));
                responses.Enqueue(tool("common.html_workspace_write_file", new { path = "index.html", content = "<main>Before</main>" }));
                await SubmitAndWaitAsync(controller, "Create a report and update its heading", id);
                var store = new ChatStore(paths);
                var session = store.Load(id);
                var pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null || session.LastRun.SelectedSkills.Count != 2 || string.IsNullOrEmpty(session.ActiveTaskListArtifactId))
                    throw new Exception("task setup or skill selection failed");
                requireSelections = true;
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(tool("common.html_workspace_apply_patch", new { path = "index.html", patch = new[] { new { op = "replace", find = "Before", text = "After" } } }));
                await controller.ConfirmAgentToolAsync(pending.PendingId, id, runId: session.LastRun.RunId);
                session = store.Load(id);
                if (session.LastRun.SelectedSkills.Count != 2)
                    throw new Exception("selected skills disappeared from durable run during confirmation");
                pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null) throw new Exception("next skill-bound write did not reach confirmation");
                if (!session.HtmlWorkspace.Files.Any(file => file.Path == "index.html"))
                    throw new Exception("confirmed create lost its effect: " + string.Join("; ", session.Messages.Where(message => message.Activity != null)
                        .Select(message => message.Activity.ToolId + ": " + message.Activity.ResultMessage)));
                var before = controller.GetHtmlWorkspace(id).Workspace.Files.Single(file => file.Path == "index.html").Source;
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(tool("common.html_workspace_write_file", new { path = "index.html", content = "<main>Final</main>" }));
                await controller.ConfirmAgentToolAsync(pending.PendingId, id, runId: session.LastRun.RunId);
                session = store.Load(id);
                if (session.HtmlWorkspace.Files.Single().Content != "<main>After</main>")
                    throw new Exception("confirmed patch did not persist exact final source");
                var after = controller.GetHtmlWorkspace(id).Workspace.Files.Single(file => file.Path == "index.html").Source;
                pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null) throw new Exception("full replacement did not reach confirmation");
                compacting = true;
                await controller.CompactChatContextAsync(id);
                compacting = false;
                if (compactionCalls != 1) throw new Exception("controller did not exercise structured compaction");
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(tool("common.html_workspace_write_file", new { path = "index.html", content = "<main>Final</main>" }));
                await controller.ConfirmAgentToolAsync(pending.PendingId, id, runId: session.LastRun.RunId);
                session = store.Load(id);
                if (session.HtmlWorkspace.Files.Single().Content != "<main>Final</main>")
                    throw new Exception("confirmed full replacement lost its current-source observation");
                var finalSnapshot = session.ActiveHtmlArtifactId;
                pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                if (pending == null) throw new Exception("identical write did not reach confirmation");
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(done);
                var result = await controller.ConfirmAgentToolAsync(pending.PendingId, id, runId: session.LastRun.RunId);
                session = store.Load(id);
                if (result.RunViewState.Lifecycle != "completed" || session.ActiveHtmlArtifactId != finalSnapshot ||
                    session.HtmlWorkspace.Files.Single().Content != "<main>Final</main>")
                    throw new Exception("identical confirmed write changed source or manufactured a revision");
                if (session.LastRun.KernelState.Summary.ToolCounts.WriteOk != 5 ||
                    result.RunViewState.ExecutionHealth != "clean" || result.RunViewState.NoChangeWrites != 1)
                    throw new Exception("completed narration masked a refused/unknown write or incorrect no-change evidence: " +
                        JsonConvert.SerializeObject(result.RunViewState));
                foreach (var pair in new[] { new { Resource = before, Expected = "<main>Before</main>" }, new { Resource = after, Expected = "<main>After</main>" } })
                {
                    var source = await controller.ReadHtmlWorkspaceSourceAsync(new HtmlWorkspaceSourceRequest { ChatId = id, Resource = pair.Resource }, CancellationToken.None);
                    using (var bytes = new MemoryStream())
                    {
                        for (var offset = 0L; offset < source.Data.Payload.ByteLength;)
                        {
                            var count = (int)Math.Min(source.Data.MaxChunkBytes, source.Data.Payload.ByteLength - offset);
                            using (var body = controller.HandleResourceData("GET", source.Data.Url + "?offset=" + offset + "&count=" + count, CancellationToken.None).Body)
                                body.CopyTo(bytes);
                            offset += count;
                        }
                        if (Encoding.UTF8.GetString(bytes.ToArray()) != pair.Expected)
                            throw new Exception("editor source is not the requested immutable snapshot");
                    }
                }
                controller.Dispose();
                controller = new AssistantController(adapter, paths, model);
                responses.Enqueue(done);
                await SubmitAndWaitAsync(controller, "Continue with the existing report", id);
                if (store.Load(id).LastRun.SelectedSkills.Count != 2) throw new Exception("follow-up lost selections after restart");
                requireSelections = false;
                responses.Enqueue(tool("common.skills_load", new { id = "common.task_tracking", action = "remove" }));
                responses.Enqueue(tool("common.skills_load", new { id = "common.html_workspace_authoring", action = "remove" }));
                responses.Enqueue(done);
                await SubmitAndWaitAsync(controller, "Switch tasks and remove the report procedures", id);
                if (store.Load(id).LastRun.SelectedSkills.Count != 0) throw new Exception("explicit removal retained selections");
                Console.WriteLine("PASS resource workflow: task list, selected skills across compaction, approval restarts and new input, exact HTML patch/full replacement/no-op and historical editor snapshots, explicit selection removal");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL resource workflow: " + error); return 1; }
            finally { controller?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static async Task CheckResourceLibraryReadsAsync(AssistantController controller, string chatId)
        {
            const string sourceText = "# Новый навык\r\nТочный текст 😀\r\n";
            var batch = new SkillLibraryMutationBatch { Type = SkillLibraryMutationBatch.ContractType, ContractVersion = 1,
                Mutations = new List<SkillCoreMutationPayload> { new SkillCoreMutationPayload { Kind = "upsert", Id = "common.review_fixture",
                    Host = "Common", Name = "Review fixture", Description = "Diagnostic source", BodyMarkdown = sourceText, Enabled = true } } };
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch));
            var upload = controller.BeginSkillMutationUpload(new SkillMutationUploadRequest { ChatId = chatId, ByteLength = bytes.Length }, CancellationToken.None);
            using (var body = new MemoryStream(bytes, false))
            using (var response = controller.HandleResourceData("POST", upload.Url + "?offset=0&count=" + bytes.Length, CancellationToken.None, body).Body) { }
            await controller.SaveSkillsAsync(new SkillMutationWriteRequest { ChatId = chatId, UploadLeaseId = upload.LeaseId,
                Sha256 = RNAssistant.Core.Tools.TextPatternEngine.Sha256(Encoding.UTF8.GetString(bytes)) }, CancellationToken.None);
            const string schema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
            var manifest = new { protocolVersion = 1, id = "excel.review_fixture", host = "Excel", name = "Review fixture", description = "Diagnostic tool",
                packageVersion = "1.0.0", entryPoint = "Run", components = new[] { "RNA_Review" }, argumentOrder = new string[0],
                parameters = Newtonsoft.Json.Linq.JObject.Parse(schema), mutatesDocument = true, agentCanRun = false, requiresConfirmation = true };
            var code = "Option Explicit\r\n' <RNAssistantTool>\r\n' " + JsonConvert.SerializeObject(manifest) +
                "\r\n' </RNAssistantTool>\r\nPublic Function Run() As String\r\n    Run = \"ok\"\r\nEnd Function\r\n";
            var toolBatch = new ToolLibraryMutationBatch { Type = ToolLibraryMutationBatch.ContractType, ContractVersion = 1,
                Mutations = new List<ToolCoreMutationPayload> { new ToolCoreMutationPayload { Kind = "upsert", Id = manifest.id, Host = "Excel",
                    Name = manifest.name, Description = manifest.description, Code = code, Readme = "# Новый tool\r\n😀", Enabled = true,
                    Executor = "vba", ArgumentSchemaJson = schema, RequiresConfirmation = true, MutatesDocument = true, RiskLevel = 1,
                    Components = new List<ToolPackageComponentDto> { new ToolPackageComponentDto { Name = "RNA_Review", Type = "StdModule", Code = code } } } } };
            bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(toolBatch));
            upload = controller.BeginToolMutationUpload(new ToolMutationUploadRequest { ChatId = chatId, ByteLength = bytes.Length }, CancellationToken.None);
            using (var body = new MemoryStream(bytes, false))
            using (var response = controller.HandleResourceData("POST", upload.Url + "?offset=0&count=" + bytes.Length, CancellationToken.None, body).Body) { }
            await controller.SaveToolsAsync(new ToolMutationWriteRequest { ChatId = chatId, UploadLeaseId = upload.LeaseId,
                Sha256 = RNAssistant.Core.Tools.TextPatternEngine.Sha256(Encoding.UTF8.GetString(bytes)) }, CancellationToken.None);
            if (!controller.GetTools().Tools.Any(tool => tool.Id == manifest.id)) throw new Exception("created tool is missing");
            var skills = controller.GetSkills().Skills;
            if (!skills.Any(skill => skill.Id == "common.review_fixture")) throw new Exception("created skill is missing");
            foreach (var skill in skills)
            {
                var source = await controller.ReadSkillSourceAsync(new SkillSourceReadRequest { Type = SkillSourceReadRequest.ContractType,
                    ContractVersion = 1, ChatId = chatId, SkillId = skill.Id, ExpectedPackageRevision = skill.Revision, Path = "" }, CancellationToken.None);
                ReadControllerDownload(controller, chatId, "skill-editor", source.Data, skill.Body.Sha256);
            }
            foreach (var tool in controller.GetTools().Tools)
            {
                var source = await controller.ReadToolSourceAsync(new ToolSourceReadRequest { Type = ToolSourceReadRequest.ContractType,
                    ContractVersion = 1, ChatId = chatId, ToolId = tool.Id, ExpectedRevision = tool.Revision }, CancellationToken.None);
                ReadControllerDownload(controller, chatId, "tool-editor", source.Data, tool.Source.Sha256);
            }
            var controls = controller.GetSettings().Settings;
            controls.EnableAgentJavaScript = true;
            controller.SaveSettings(new SaveSettingsPayload { ChatId = chatId, Settings = controls,
                ExpectedPromptPublication = controller.GetSettings().Prompts.Publication }, CancellationToken.None);
            var configured = controller.GetTools().Tools.Single(tool => tool.Id == "common.tools_upsert");
            var configuredSource = await controller.ReadToolSourceAsync(new ToolSourceReadRequest { Type = ToolSourceReadRequest.ContractType,
                ContractVersion = 1, ChatId = chatId, ToolId = configured.Id, ExpectedRevision = configured.Revision }, CancellationToken.None);
            ReadControllerDownload(controller, chatId, "tool-editor", configuredSource.Data, configured.Source.Sha256);
            var documentation = await controller.GetToolDocumentationAsync(new ToolLibraryDocumentationRequest { Type = ToolLibraryDocumentationRequest.ContractType,
                ContractVersion = 1, ChatId = chatId, ToolId = configured.Id, ExpectedRevision = configured.Revision }, CancellationToken.None);
            ReadControllerDownload(controller, chatId, "tool-editor", documentation.Data, documentation.Data.Payload.Sha256);
            controls.EnableAgentJavaScript = false;
            controller.SaveSettings(new SaveSettingsPayload { ChatId = chatId, Settings = controls,
                ExpectedPromptPublication = controller.GetSettings().Prompts.Publication }, CancellationToken.None);
            configured = controller.GetTools().Tools.Single(tool => tool.Id == "common.tools_upsert");
            configuredSource = await controller.ReadToolSourceAsync(new ToolSourceReadRequest { Type = ToolSourceReadRequest.ContractType,
                ContractVersion = 1, ChatId = chatId, ToolId = configured.Id, ExpectedRevision = configured.Revision }, CancellationToken.None);
            ReadControllerDownload(controller, chatId, "tool-editor", configuredSource.Data, configured.Source.Sha256);
            Console.WriteLine("PASS controller library: create/read sources, JavaScript toggle preserves exact source and documentation publications");
        }

        private static void ReadControllerDownload(AssistantController controller, string chatId, string owner,
            ResourceDownloadOpenResponse download, string expectedHash)
        {
            try
            {
                using (var bytes = new MemoryStream())
                {
                    for (var offset = 0L; offset < download.Payload.ByteLength;)
                    {
                        var count = (int)Math.Min(download.MaxChunkBytes, download.Payload.ByteLength - offset);
                        using (var body = controller.HandleResourceData("GET", download.Url + "?offset=" + offset + "&count=" + count, CancellationToken.None).Body)
                            body.CopyTo(bytes);
                        offset += count;
                    }
                    if (RNAssistant.Core.Tools.TextPatternEngine.Sha256(Encoding.UTF8.GetString(bytes.ToArray())) != expectedHash)
                        throw new Exception(owner + " source hash mismatch");
                }
            }
            finally { controller.CloseResourceData(new ResourceDataCloseRequest { ChatId = chatId, WorkspaceId = owner, LeaseId = download.LeaseId }); }
        }
    }
}
