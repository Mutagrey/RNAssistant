using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TabularChannelAcknowledgesAndConfirmsCleanup()
        {
            foreach (var mode in new[] { "unreleased", "success", "cleanup-failure" })
            {
                var released = mode != "unreleased";
                var sink = new TabularChannelTestSink();
                var commands = new List<TabularExecutionCommand>();
                var channel = TestChannel(sink, commands, () => released);
                SendTabular(channel, TabularMessageKind.Ready, "");
                var id = commands.Single().JobId;
                SendTabular(channel, TabularMessageKind.Batch, id, 1, 2, "{\"records\":[\"тест\",\"точность\"]}");
                AssertEqual(2, commands.Count, "ack after sink accepts the batch");
                SendTabular(channel, TabularMessageKind.Validate, id, 2, 2, "{\"recordCount\":2}");
                AssertTrue(!channel.Completion.IsCompleted && sink.Validated, "validated output is still provisional");
                SendTabular(channel, TabularMessageKind.Succeeded, id, 3, 2);
                if (released)
                {
                    var result = channel.Completion.GetAwaiter().GetResult();
                    AssertEqual(2, result.RecordCount, "native count"); AssertEqual(1, result.BatchCount, "native batches");
                    channel.ReceiveAsync("late invalid message").GetAwaiter().GetResult();
                    AssertEqual(0, sink.Aborts, "late inbound delivery cannot alter a terminal result");
                    if (mode == "success")
                    { channel.Dispose(); AssertEqual(0, sink.Aborts, "normal cleanup preserves provisional output"); }
                    else
                    {
                        channel.Stop(new Exception("cleanup failed"));
                        AssertEqual(1, sink.Aborts, "post-result native failure revokes provisional output");
                    }
                }
                else
                {
                    AssertChannelFailure(channel);
                    AssertEqual(1, sink.Aborts, "no success without a native cleanup receipt");
                }
                channel.Dispose(); channel.Drained.GetAwaiter().GetResult();
            }
        }

        private static void TabularChannelRevokesPendingSink()
        {
            foreach (var hold in new[] { "Batch", "Validate" })
            {
                var sink = new TabularChannelTestSink { Hold = hold };
                var commands = new List<TabularExecutionCommand>();
                using (var channel = TestChannel(sink, commands, () => true))
                {
                    SendTabular(channel, TabularMessageKind.Ready, "");
                    var id = commands.Single().JobId;
                    if (hold == "Validate") SendTabular(channel, TabularMessageKind.Batch, id, 1, 1, "{}");
                    var priorCommands = commands.Count;
                    var pending = channel.ReceiveAsync(TabularMessage(hold == "Batch" ? TabularMessageKind.Batch : TabularMessageKind.Validate,
                        id, hold == "Batch" ? 1 : 2, 1, "{}"));
                    AssertTrue(!pending.IsCompleted && commands.Count == priorCommands, "backpressure blocks the ack");
                    channel.Stop(new OperationCanceledException("test cancellation"));
                    AssertChannelFailure(channel);
                    AssertTrue(sink.Aborts == 1 && !channel.Drained.IsCompleted, "revoke immediately but retain native drain ownership");
                    sink.Release.TrySetResult(true); pending.GetAwaiter().GetResult();
                    channel.Drained.GetAwaiter().GetResult();
                    AssertEqual(priorCommands, commands.Count, "late sink completion cannot acknowledge or revive the job");
                    AssertEqual(0, sink.Batches.Count, "no staged records survive cancellation");
                }
            }
        }

        private static void TabularChannelRefusesMalformedAndForeignMessages()
        {
            var bad = new Func<string, string>[] {
                id => TabularMessage(TabularMessageKind.Batch, "foreign", 1, 1, "{}"),
                id => TabularMessage(TabularMessageKind.Batch, id, 2, 1, "{}"),
                id => TabularMessage(TabularMessageKind.Batch, id, 1, 65, "{}"),
                id => TabularMessage(TabularMessageKind.Batch, id, 1, 1, new string('x', TabularExecutionChannel.MaximumPayloadCharacters + 1)),
                id => TabularMessage(TabularMessageKind.Batch, id, 1, 1, "{}").Replace("\"version\":1", "\"version\":1,\"version\":1"),
                id => TabularMessage(TabularMessageKind.Batch, id, 1, 1, "{}").Replace("\"version\":1", "\"version\":\"1\""),
                id => TabularMessage(TabularMessageKind.Ready, ""),
                id => TabularMessage(TabularMessageKind.Succeeded, id, 1, 0),
                id => TabularMessage(TabularMessageKind.Progress, id)
            };
            foreach (var make in bad)
            {
                var sink = new TabularChannelTestSink(); var commands = new List<TabularExecutionCommand>();
                using (var channel = TestChannel(sink, commands, () => true))
                {
                    SendTabular(channel, TabularMessageKind.Ready, "");
                    channel.ReceiveAsync(make(commands[0].JobId)).GetAwaiter().GetResult();
                    AssertChannelFailure(channel); AssertEqual(1, sink.Aborts, "invalid input aborts exactly once");
                    AssertEqual(0, sink.Batches.Count, "invalid message never reaches sink");
                }
            }
        }

        private static void TabularResponseBodiesAreBoundedAndRevoked()
        {
            var pool = new TabularResponseBodyPool();
            var sources = new List<MemoryStream>(); var bodies = new List<Stream>();
            Func<TabularResourceResponse> response = () => {
                var source = new MemoryStream(new byte[WorkspaceSourceReader.MaximumReadBytes], false);
                sources.Add(source);
                return new TabularResourceResponse(200, "application/octet-stream", source, new Dictionary<string, string>());
            };
            for (var i = 0; i < 8; i++) bodies.Add(pool.Take(response()));
            AssertEqual((long)TabularResponseBodyPool.MaximumBytes, pool.RetainedBytes, "bounded live response buffers");
            using (var refused = response())
            {
                ExpectSourceQueueError("tabular_response_limit", () => pool.Take(refused));
                AssertTrue(refused.Body.CanRead, "refused transfer remains caller-owned");
            }
            bodies[0].CopyTo(Stream.Null);
            AssertTrue(!sources[0].CanRead, "consumption disposes the original response");
            var retained = pool.RetainedBytes;
            for (var i = 0; i < 24; i++)
            {
                using (var body = pool.Take(response())) body.CopyTo(Stream.Null);
                AssertEqual(retained, pool.RetainedBytes, "total processed bytes do not accumulate in the native owner");
            }
            pool.Dispose();
            AssertEqual(0L, pool.RetainedBytes, "native close revokes all remaining responses");
            AssertTrue(sources.All(source => !source.CanRead), "all captured response streams released");
            var buffer = new byte[1];
            AssertEqual(0, bodies[1].Read(buffer, 0, 1), "late browser reads cannot recover revoked bytes");
            using (var refused = response())
            {
                var rejected = false;
                try { pool.Take(refused); } catch (ObjectDisposedException) { rejected = true; }
                AssertTrue(rejected && refused.Body.CanRead, "closed owner cannot accept new bodies");
            }
        }

        private static TabularExecutionChannel TestChannel(TabularChannelTestSink sink, List<TabularExecutionCommand> commands, Func<bool> released)
        { return new TabularExecutionChannel("{}", new string('a', 64), 10000, sink,
            json => commands.Add(JsonConvert.DeserializeObject<TabularExecutionCommand>(json)), released); }
        private static string TabularMessage(TabularMessageKind kind, string job, int sequence = 0, int count = 0, string payload = null)
        { return JsonConvert.SerializeObject(new TabularExecutionMessage { Version = 1, JobId = job, Kind = kind,
            Sequence = sequence, RecordCount = count, PayloadJson = payload }); }
        private static void SendTabular(TabularExecutionChannel channel, TabularMessageKind kind, string job, int sequence = 0, int count = 0, string payload = null)
        { channel.ReceiveAsync(TabularMessage(kind, job, sequence, count, payload)).GetAwaiter().GetResult(); }
        private static void AssertChannelFailure(TabularExecutionChannel channel)
        {
            AssertTrue(channel.Completion.IsFaulted, "expected terminal channel failure");
            var observed = channel.Completion.Exception;
        }

        private sealed class TabularChannelTestSink : ITabularResultSink
        {
            private readonly object _sync = new object();
            public readonly List<TabularOutputBatch> Batches = new List<TabularOutputBatch>();
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Hold;
            public bool Fail, Validated;
            public int Aborts;
            public Action<string> Held;
            public async Task WriteBatchAsync(TabularOutputBatch batch, CancellationToken token)
            {
                if (Fail) throw new WorkspaceApplicationException("test_sink_failed", "Test native sink failed.");
                await WaitAsync("Batch"); token.ThrowIfCancellationRequested();
                lock (_sync) { if (Aborts != 0) throw new OperationCanceledException(); Batches.Add(batch); }
            }
            public async Task ValidateAsync(TabularOutputResult result, CancellationToken token)
            {
                await WaitAsync("Validate"); token.ThrowIfCancellationRequested();
                lock (_sync) { if (Aborts != 0) throw new OperationCanceledException(); Validated = true; }
            }
            private async Task WaitAsync(string kind)
            { if (Hold == kind) { if (Held != null) Held(kind); await Release.Task; } }
            public void Abort()
            { lock (_sync) { Aborts++; Batches.Clear(); Validated = false; } }
        }
    }
}
