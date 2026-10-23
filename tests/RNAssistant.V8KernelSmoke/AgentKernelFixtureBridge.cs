using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    // Compile and run the migrated kernel fixtures while unrelated v7 Harness
    // sources are still being ported. This file is linked only by run.sh.
    public static class V8AgentKernelFixtureBridge
    {
        public static Task RunAsync() { return Program.RunKernelFixtureSuiteAsync(); }
    }

    internal static partial class Program
    {
        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!object.Equals(expected, actual))
                throw new InvalidOperationException(name + ": expected '" + expected + "', got '" + actual + "'");
        }

        private static void AssertTrue(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name + " was false");
        }

        private static void AssertContains(string value, string expected, string name)
        {
            if ((value ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(name + ": expected '" + value + "' to contain '" + expected + "'");
        }

        internal static async Task RunKernelFixtureSuiteAsync()
        {
            var methods = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => method.Name.StartsWith("Kernel", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0 && method.ReturnType == typeof(Task))
                .OrderBy(method => method.Name, StringComparer.Ordinal);
            foreach (var method in methods)
                await CheckAsync(method.Name, () => (Task)method.Invoke(null, null));

            await CheckAsync("read ok", () => KernelAggregatesOutcome("read", ToolExecutionOutcome.Ok,
                ExecutionHealth.Clean, "1,0,0,0,0"));
            await CheckAsync("read error", () => KernelAggregatesOutcome("read", ToolExecutionOutcome.Error,
                ExecutionHealth.Errors, "0,1,0,0,0"));
            await CheckAsync("write ok", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Ok,
                ExecutionHealth.Clean, "0,0,1,0,0"));
            await CheckAsync("write error", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Error,
                ExecutionHealth.Errors, "0,0,0,1,0"));
            await CheckAsync("write unknown", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Unknown,
                ExecutionHealth.Unknown, "0,0,0,0,1"));
            await CheckAsync("external unknown", () => KernelAggregatesOutcome("external", ToolExecutionOutcome.Unknown,
                ExecutionHealth.Unknown, "0,0,0,0,1"));
            await CheckAsync("error then success", () => KernelPreservesCumulativeHealth(ToolExecutionOutcome.Error,
                ToolExecutionOutcome.Ok, ExecutionHealth.Errors, "0,0,1,1,0"));
            await CheckAsync("success then error", () => KernelPreservesCumulativeHealth(ToolExecutionOutcome.Ok,
                ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,0,1,1,0"));
            await CheckAsync("id collision in batch", () => KernelRejectsAllocationCollisions(false));
            await CheckAsync("id collision across steps", () => KernelRejectsAllocationCollisions(true));
            foreach (var boundary in new[] { AgentRunEventKind.Started, AgentRunEventKind.ModelStepStarted,
                AgentRunEventKind.ResponseAccepted, AgentRunEventKind.ToolStarted })
                await CheckAsync("cancel at " + boundary, () => KernelCancellationBeforeDispatch(boundary));
            await CheckAsync("cancel during policy recheck", () =>
                KernelCancellationBeforeDispatch(AgentRunEventKind.ToolStarted, true));
            await CheckAsync("cancel after write entry", () => KernelCancellationAfterPossibleDispatch(true));
            await CheckAsync("cancel after read entry", () => KernelCancellationAfterPossibleDispatch(false));
            await CheckAsync("iteration limit", () => KernelHonorsLimits(true));
            await CheckAsync("tool step limit", () => KernelHonorsLimits(false));
            await CheckAsync("confirmation preserves prior error", () => KernelConfirmationSharesAccounting(
                ToolExecutionOutcome.Error, ToolExecutionOutcome.Ok, ExecutionHealth.Errors, "0,0,1,1,0"));
            await CheckAsync("confirmation records error", () => KernelConfirmationSharesAccounting(
                ToolExecutionOutcome.Ok, ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,0,1,1,0"));
            await CheckAsync("confirmation records unknown", () => KernelConfirmationSharesAccounting(
                ToolExecutionOutcome.Ok, ToolExecutionOutcome.Unknown, ExecutionHealth.Unknown, "0,0,1,0,1"));
            await CheckAsync("policy change before dispatch", () => KernelPolicyChangeStopsDispatch(false));
            await CheckAsync("policy change before confirmation", () => KernelPolicyChangeStopsDispatch(true));
        }

        private static async Task CheckAsync(string name, Func<Task> test)
        {
            await test();
            Console.WriteLine("PASS kernel fixture " + name);
        }
    }
}
