using System;
using RNAssistant.Application.Conversations;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Test-only loopback adapter for the SAME application resource handler.
        // Production remains serverless; no HTTP listener is shipped to Office.
        private static int TabularSourceHost(string channelMode = null)
        {
            WithTempPaths(paths => RunTabularSourceHost(paths, channelMode).GetAwaiter().GetResult());
            return 0;
        }

        private static async Task RunTabularSourceHost(AppDataPaths paths, string channelMode)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var origin = "http://127.0.0.1:" + port;
            using (var app = new WorkspaceApplicationService(paths))
            using (var listener = new HttpListener())
            {
                var queue = PrepareNativeTabularFixture(app, paths);
                var bundle = TabularRuntimeBundle.Capture(Path.Combine(FindHarnessRepositoryRoot(), "web"));
                var session = await WorkspaceTabularResourceSession.OpenAsync(app, queue.QueueId, queue.Items[0].Id, bundle, origin: origin);
                var messages = new List<Task>();
                var sink = new TabularChannelTestSink { Fail = channelMode == "sink-failure",
                    Hold = channelMode == "hold-batch" ? "Batch" : channelMode == "hold-validation" ? "Validate" : null,
                    Held = kind => Console.WriteLine(JsonConvert.SerializeObject(new { held = kind })) };
                var channel = channelMode == null ? null : new TabularExecutionChannel(
                    File.ReadAllText(Path.Combine(FindHarnessRepositoryRoot(), "web/data-templates/core/examples/matrix-xlsx-extraction.json")),
                    bundle.Sha256, 10000, sink,
                    json => Console.WriteLine(JsonConvert.SerializeObject(new { command = json })), () => session.ClientCloseConfirmed);
                listener.Prefixes.Add(origin + "/");
                listener.Start();
                var requests = new List<Task>();
                long reads = 0, bytes = 0;
                var accepting = Task.Run(async () =>
                {
                    try
                    {
                        while (listener.IsListening)
                        {
                            var context = await listener.GetContextAsync();
                            requests.Add(ServeTabularResource(context, session, origin, count => {
                                Interlocked.Increment(ref reads); Interlocked.Add(ref bytes, count);
                            }));
                        }
                    }
                    catch (HttpListenerException) { }
                    catch (ObjectDisposedException) { }
                });
                Console.WriteLine(JsonConvert.SerializeObject(new { entry = session.EntryUri, bundleSha256 = bundle.Sha256 }));
                try
                {
                    string command;
                    while ((command = await Console.In.ReadLineAsync()) != null && command != "stop")
                    {
                        if (command == "cancel")
                        {
                            if (channel != null) channel.Stop(new OperationCanceledException("Test native cancellation."));
                            sink.Release.TrySetResult(true);
                            await session.CloseAsync(); Console.WriteLine(JsonConvert.SerializeObject(new { revoked = true }));
                        }
                        if (command.StartsWith("message ", StringComparison.Ordinal) && channel != null)
                            messages.Add(channel.ReceiveAsync(command.Substring(8)));
                        if (command == "result" && channel != null)
                            messages.Add(WriteTabularHostResult(channel, sink));
                        if (command == "status") Console.WriteLine(JsonConvert.SerializeObject(new {
                            closed = session.IsClosed, activeStorage = ConversationLeaseService.ForOffice(paths).AnyHeld(),
                            reads = Interlocked.Read(ref reads), bytes = Interlocked.Read(ref bytes) }));
                    }
                }
                finally
                {
                    if (channel != null) channel.Dispose();
                    sink.Release.TrySetResult(true);
                    await Task.WhenAll(messages);
                    await session.CloseAsync();
                    listener.Stop();
                    await accepting;
                    await Task.WhenAll(requests);
                    await app.CloseAsync();
                }
                Console.WriteLine(JsonConvert.SerializeObject(new { stopped = true, activeStorage = ConversationLeaseService.ForOffice(paths).AnyHeld() }));
            }
        }

        private static async Task WriteTabularHostResult(TabularExecutionChannel channel, TabularChannelTestSink sink)
        {
            string error = null; TabularOutputResult result = null;
            try { result = await channel.Completion; }
            catch (Exception ex) { error = (ex as WorkspaceApplicationException)?.Code ?? ex.GetType().Name; }
            var records = sink.Batches.SelectMany(batch => Newtonsoft.Json.Linq.JObject.Parse(batch.Json)["records"]).ToArray();
            Console.WriteLine(JsonConvert.SerializeObject(new { result = true, count = result?.RecordCount, error,
                staged = records.Length, validated = sink.Validated, aborts = sink.Aborts,
                sample = records.Length > 6 ? (string)records[6]["value"]["value"] : null,
                formula = records.Length > 6 ? (string)records[6]["provenance"]["value"]["evidence"][0]["cell"]["formula"]["text"] : null }));
        }

        private static async Task ServeTabularResource(HttpListenerContext context, WorkspaceTabularResourceSession session,
            string origin, Action<long> onRead)
        {
            try
            {
                using (var response = await session.HandleAsync(context.Request.HttpMethod, origin + context.Request.RawUrl,
                    context.Request.Headers["Origin"]))
                {
                    context.Response.StatusCode = response.StatusCode;
                    context.Response.ContentType = response.ContentType;
                    context.Response.ContentLength64 = response.Body.Length;
                    foreach (var header in response.Headers)
                        if (header.Key != "Content-Length") context.Response.Headers[header.Key] = header.Value;
                    if (response.StatusCode == 200 && response.Headers.ContainsKey("X-RNA-Source-Sequence")) onRead(response.Body.Length);
                    await response.Body.CopyToAsync(context.Response.OutputStream);
                }
            }
            catch (HttpListenerException) { /* Browser aborted an in-flight response. */ }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
    }
}
