using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Test-only loopback adapter for the SAME application resource handler.
        // Production remains serverless; no HTTP listener is shipped to Office.
        private static int TabularSourceHost()
        {
            WithTempPaths(paths => RunTabularSourceHost(paths).GetAwaiter().GetResult());
            return 0;
        }

        private static async Task RunTabularSourceHost(AppDataPaths paths)
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
                        { await session.CloseAsync(); Console.WriteLine(JsonConvert.SerializeObject(new { revoked = true })); }
                        if (command == "status") Console.WriteLine(JsonConvert.SerializeObject(new {
                            closed = session.IsClosed, activeStorage = new ChatRunRegistry(paths).HasExternalRuns(),
                            reads = Interlocked.Read(ref reads), bytes = Interlocked.Read(ref bytes) }));
                    }
                }
                finally
                {
                    await session.CloseAsync();
                    listener.Stop();
                    await accepting;
                    await Task.WhenAll(requests);
                    await app.CloseAsync();
                }
                Console.WriteLine(JsonConvert.SerializeObject(new { stopped = true, activeStorage = new ChatRunRegistry(paths).HasExternalRuns() }));
            }
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
