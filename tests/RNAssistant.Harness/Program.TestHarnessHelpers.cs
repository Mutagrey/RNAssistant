using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WithTempExecutor(Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempExecutor(new FakeOfficeAdapter(), action);
        }

        private static void WithTempExecutor(FakeOfficeAdapter adapter, Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var settings = new AppSettings();
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => settings,
                    value => settings = value,
                    paths);
                action(executor, adapter);
            });
        }

        private static ToolCommand PendingCommand(ChatSession session)
        {
            var call = session.LastRun.KernelState.Summary.PendingConfirmation.Call;
            return new ToolCommand { ToolId = call.Name, ToolCallId = call.Id,
                Arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(call.ArgumentsJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) };
        }

        private static AcceptedToolCallOrigin FixtureCallOrigin(string stepId = "fixture-step",
            string modelAttemptId = null, int callIndex = 0)
        {
            return new AcceptedToolCallOrigin(stepId, modelAttemptId ?? Guid.NewGuid().ToString("N"), callIndex);
        }

        private static readonly AsyncLocal<AppDataPaths> FixturePaths = new AsyncLocal<AppDataPaths>();

        private static ConversationRunService CreateConversationRunService(IOfficeApplicationAdapter adapter,
            OfficeToolExecutor executor, LlmCompletionDelegate completion, ContextCompactionService compaction = null,
            Func<IMaterializedModelProtocol> modelProtocolFactory = null)
        {
            if (FixturePaths.Value == null) throw new InvalidOperationException("Conversation tests require WithTempPaths.");
            return new ConversationRunService(adapter, executor, new ChatStore(FixturePaths.Value), completion, compaction, modelProtocolFactory);
        }

        private static void WithTempPaths(Action<AppDataPaths> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness." + Guid.NewGuid().ToString("N"));
            var previousPaths = FixturePaths.Value;
            try
            {
                FixturePaths.Value = AppDataPaths.CreateForRoot(root);
                action(FixturePaths.Value);
            }
            finally
            {
                FixturePaths.Value = previousPaths;
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(name + ": expected '" + expected + "', got '" + actual + "'");
            }
        }

        private static void AssertTrue(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name + " was false");
            }
        }

        private static void AssertContains(string value, string expected, string name)
        {
            if ((value ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(name + ": expected '" + value + "' to contain '" + expected + "'");
            }
        }
    }
}
