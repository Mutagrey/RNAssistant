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
        private static ToolDefinition DisabledPipeline()
        {
            return new ToolDefinition
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
                    var result = executor.Execute(Command(pipeline.Id), new[] { pipeline },
                        new AppSettings { AutoConfirmToolActions = true }, dry, manual);
                    AssertEqual("pipeline_disabled", result.ErrorCode, "pipeline rejected before dispatch/confirmation");
                    AssertEqual(false, result.Retryable, "disabled feature is not retryable");
                }
                AssertEqual(0, adapter.Executed.Count, "no nested tool executed");
                AssertTrue(!ToolSafetyPolicy.Resolve(pipeline, new[] { pipeline }).Valid, "no nested safety traversal");
                var direct = executor.Execute(Command("excel.add_sheet", "name", "Direct"), adapter.GetBuiltInTools().ToList(),
                    new AppSettings { AutoConfirmToolActions = true }, false, false);
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
                AssertEqual(string.Empty, ConversationRunService.ToolExecutionFingerprint(new[] { pipeline }, pipeline.Id), "no resumable pipeline fingerprint");
                var read = executor.Execute(Command(CapabilityDiscoveryExecutor.ReadToolId, "id", pipeline.Id),
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
                foreach (var id in new[] { "common.tools_validate", "common.tools_upsert" })
                {
                    var schema = JObject.Parse(FindTool(executor.GetControllerTools(), id).ArgumentSchemaJson);
                    AssertTrue(schema.SelectToken("properties.pipeline") == null && schema.SelectToken("properties.pipelineSteps") == null,
                        "pipeline authoring fields removed");
                    var result = executor.Execute(Command(id, "id", pipeline.Id, "executor", "pipeline"),
                        executor.GetControllerTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, true);
                    AssertTrue(!result.Success && result.Status != "waiting_confirmation", "model/manual authoring rejected");
                }
            });
        }
    }
}
