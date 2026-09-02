using RNAssistant.Core.Tools;
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolCatalogEntry DisabledPipeline()
        {
            return new ToolCatalogEntry
            {
                Id = "excel.old_pipeline", Host = "Excel", Executor = "pipeline",
                Enabled = true, AgentCanRun = true, RequiresConfirmation = true,
                ArgumentSchemaJson = EmptyFormalToolSchema,
            };
        }

        private static void PipelinesCannotExecute()
        {
            WithTempExecutor((executor, adapter) =>
            {
                var pipeline = DisabledPipeline();
                pipeline.Executor = "PiPeLiNe";
                foreach (var manual in new[] { false, true })
                foreach (var dry in new[] { false, true })
                {
                    var result = executor.ExecuteManual(Command(pipeline.Id), new[] { pipeline },
                        new AppSettings { AutoConfirmToolActions = true }, dry, manual);
                    AssertEqual("pipeline_disabled", result.ErrorCode, "pipeline rejected before dispatch/confirmation");
                    AssertEqual(false, result.Retryable, "disabled feature is not retryable");
                }
                AssertEqual(0, adapter.TotalBackendCallCount, "no nested tool executed");
                AssertTrue(!ToolSafetyPolicy.Resolve(pipeline, new[] { pipeline }).Valid, "no nested safety traversal");
                var direct = executor.ExecuteManual(Command("excel.add_sheet", "name", "Direct"), OfficeToolCatalog.ForHost(adapter.HostName).ToList(),
                    new AppSettings { AutoConfirmToolActions = true }, false, false,
                    NewSession(adapter));
                AssertTrue(direct.Success && adapter.HasSheet("Direct"), "direct tools remain available");
            });
        }

        private static void PipelinesAreNotLoadedOrAdvertised()
        {
            WithTempPaths(paths =>
            {
                var pipeline = DisabledPipeline();
                var directory = Path.Combine(paths.ToolsDirectory, "excel", "old_pipeline");
                Directory.CreateDirectory(directory);
                var metadata = JsonConvert.SerializeObject(pipeline);
                var path = Path.Combine(directory, "tool.json");
                File.WriteAllText(path, metadata);
                File.WriteAllText(Path.Combine(directory, "pipeline.json"), "invalid obsolete sidecar");
                var store = new ToolStore(paths);
                AssertEqual(0, store.Load().Count, "old pipelines skipped without sidecar parsing");
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                AssertTrue(!HasTool(new ToolCatalogService(adapter, executor, store).GetVisibleTools(), pipeline.Id), "manual catalog excludes pipelines");
                AssertTrue(!HasTool(ConversationRunService.PrepareToolsForRun(new[] { pipeline }), pipeline.Id), "injected model catalog excludes pipelines");
                AssertEqual(string.Empty, ToolPackSnapshotFactory.ExecutionFingerprint(new[] { pipeline }, pipeline.Id), "no resumable pipeline fingerprint");
                var read = executor.ExecuteManual(Command(CapabilityToolCatalog.ReadToolId, "id", pipeline.Id),
                    new[] { pipeline }, new AppSettings(), false, true);
                AssertTrue(!read.Success, "direct capability read cannot advertise an injected pipeline");
                store.Save(new[] { CustomTool("Excel", "excel.current") }, "Excel");
                AssertEqual(metadata, File.ReadAllText(path), "unrelated save does not migrate or delete old files");
            });
        }

        private static void PipelinesCannotBeAuthored()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var pipeline = DisabledPipeline();
                AssertEqual("pipeline_disabled", executor.ValidateToolDefinition(pipeline).ErrorCode, "UI save validation rejects pipeline");
                var rejected = false;
                try { store.SaveOne(pipeline); }
                catch (NotSupportedException) { rejected = true; }
                AssertTrue(rejected && store.Load().Count == 0, "storage rejects pipeline writes");
                AssertTrue(FindTool(executor.GetControllerTools(),
                        "common.tools_validate") == null,
                    "model-facing validation is removed");
                var schema = JObject.Parse(FindTool(
                    executor.GetControllerTools(),
                    ToolAuthoringCatalog.UpsertToolId).ArgumentSchemaJson);
                AssertTrue(schema.SelectToken("properties.pipeline") == null &&
                        schema.SelectToken("properties.pipelineSteps") == null &&
                        schema.SelectToken("properties.executor") == null,
                    "model authoring exposes neither pipeline nor executor choice");
                var result = executor.ExecuteManual(
                    Command(ToolAuthoringCatalog.UpsertToolId,
                        "id", pipeline.Id, "executor", "pipeline"),
                    executor.GetControllerTools().ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false, true);
                AssertTrue(!result.Success &&
                        result.Status != "awaiting_confirmation",
                    "removed executor argument is rejected before authoring");
            });
        }
    }
}
