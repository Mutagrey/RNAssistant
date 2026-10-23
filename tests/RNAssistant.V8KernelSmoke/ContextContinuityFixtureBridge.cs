using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    // Source-linked context fixtures use the same temporary storage and fake host
    // setup as the full Harness while its other v7 sources are still being ported.
    public static class V8ContextContinuityFixtureBridge
    {
        public static Task RunAsync() { return Program.RunContextContinuityFixtureSuiteAsync(); }
    }

    internal static partial class Program
    {
        private static readonly AsyncLocal<AppDataPaths> FixturePaths = new AsyncLocal<AppDataPaths>();

        private static AcceptedToolCallOrigin FixtureCallOrigin(string stepId = "fixture-step",
            string modelAttemptId = null, int callIndex = 0)
        {
            return new AcceptedToolCallOrigin(stepId, modelAttemptId ?? Guid.NewGuid().ToString("N"), callIndex);
        }

        private static void WithTempPaths(Action<AppDataPaths> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-v8-context-" + Guid.NewGuid().ToString("N"));
            var previous = FixturePaths.Value;
            try
            {
                FixturePaths.Value = AppDataPaths.CreateForRoot(root);
                action(FixturePaths.Value);
            }
            finally
            {
                FixturePaths.Value = previous;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void WithTempExecutor(FakeOfficeAdapter adapter, Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempPaths(paths =>
            {
                var settings = new AppSettings();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), () => settings, value => settings = value, paths);
                action(executor, adapter);
            });
        }

        private static ConversationRunService CreateConversationRunService(
            RNAssistant.Office.IOfficeApplicationAdapter adapter, OfficeToolExecutor executor,
            LlmCompletionDelegate completion)
        {
            var store = new ChatStore(FixturePaths.Value);
            return new ConversationRunService(adapter, executor,
                new ChatConversationStoreAdapter(store), new ChatEventStoreAdapter(store), completion);
        }

        private static ChatSession NewSession(FakeOfficeAdapter adapter)
        {
            return new ChatSession { Host = adapter.HostName, DocumentKey = adapter.DocumentKey,
                DocumentTitle = adapter.DocumentTitle, Title = "New chat" };
        }

        private static DocumentContext NewContext(FakeOfficeAdapter adapter)
        {
            return new DocumentContext { Host = adapter.HostName, DocumentKey = adapter.DocumentKey,
                Title = adapter.DocumentTitle };
        }

        private static string FlattenSimple(IEnumerable<ChatMessage> messages)
        {
            return string.Join("\n", (messages ?? new ChatMessage[0]).Where(item => item != null)
                .Select(item => item.Content ?? string.Empty));
        }

        private static TException RuntimeThrows<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException error) { return error; }
            throw new InvalidOperationException("Expected " + typeof(TException).Name);
        }

        internal static async Task RunContextContinuityFixtureSuiteAsync()
        {
            var methods = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => method.Name.StartsWith("ContextContinuity", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0 &&
                    (method.ReturnType == typeof(void) || method.ReturnType == typeof(Task)))
                .OrderBy(method => method.Name, StringComparer.Ordinal);
            foreach (var method in methods)
            {
                var result = method.Invoke(null, null);
                if (result is Task) await (Task)result;
                Console.WriteLine("PASS context fixture " + method.Name);
            }
        }
    }
}
