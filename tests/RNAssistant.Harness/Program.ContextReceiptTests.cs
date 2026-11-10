using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ContextReceiptBoundsExcludedHistory()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(null),
                "receipt-tools", new SkillCatalogSnapshot(null), null, 1);
            var settings = new AppSettings();
            var active = new ChatMessage { Id = "active-user", Role = "user", Content = "Keep this exact active request." };
            var compiler = new ModelContextCompiler();
            compiler.Compile(authority, null, new[] { active }, null, null, settings, 4096);
            var counts = new List<int>();
            string expectedWire = null;
            foreach (var history in new[] { 100, 1000, 10000 })
            {
                var facts = Enumerable.Range(0, history).Select(index => new ChatMessage {
                    Id = "excluded-" + index.ToString("D5", CultureInfo.InvariantCulture), Role = "assistant",
                    Content = "Excluded historical text", ExcludeFromModelContext = true }).ToList();
                facts.Add(active);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                var snapshot = compiler.Compile(authority, null, facts, null, null, settings, 4096);
                var receipt = snapshot.Receipt;
                var bytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(receipt));
                clock.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine("context-receipt H=" + history + " details=" + receipt.Messages.Count +
                    " receiptBytes=" + bytes + " allocated=" + allocated + " wallMs=" +
                    clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
                counts.Add(receipt.Messages.Count);
                var wire = JsonConvert.SerializeObject(new LlmMessageBuilder().Build(snapshot.Messages, settings).Messages);
                if (expectedWire == null) expectedWire = wire;
                AssertEqual(expectedWire, wire, "excluded archive does not change actual model delivery");
                AssertEqual(1, receipt.Messages.Count(item => item.MessageIndex.HasValue), "every delivered message keeps its exact position");
                AssertTrue(receipt.Messages.Where(item => !item.MessageIndex.HasValue).All(item =>
                    item.Presentation == ContextPresentationKind.Excluded && item.ExclusionKind == ContextExclusionKind.Explicit),
                    "omitted history never claims delivery");
                AssertEqual(0, receipt.HydratedPayloads, "excluded history causes no body hydration");
                AssertEqual(history - ContextReceipt.MaximumExcludedDetails, receipt.OmittedContext["Explicit"],
                    "every excluded source beyond the sample remains counted");
            }
            AssertTrue(counts.All(count => count == 17), "receipt keeps one delivered message and at most sixteen excluded details");
            var selected = Enumerable.Range(0, 40).Select(index => new ChatMessage {
                Id = "selected-" + index, Role = "user", Content = "Active input " + index }).ToList();
            var excluded = new ChatMessage { Role = "assistant", Content = "Local only", ExcludeFromModelContext = true };
            selected.Add(excluded); selected.Add(excluded);
            var largeActive = compiler.Compile(authority, null, selected, null, null, settings, 4096).Receipt;
            AssertEqual(40, largeActive.Messages.Count(item => item.MessageIndex.HasValue),
                "the diagnostic sample limit never truncates actual delivery provenance");
            AssertEqual(1, largeActive.Messages.Count(item => item.SourceMessageId == excluded.Id),
                "duplicate source identity is counted once");
            AssertEqual(0, largeActive.OmittedContext.Count, "no omitted count below the diagnostic limit");
        }
    }
}
