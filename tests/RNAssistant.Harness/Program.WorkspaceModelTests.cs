using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceChatModelPreferences()
        {
            WithTempPaths(paths =>
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                {
                    var observed = new List<string>();
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        try
                        {
                            for (var i = 0; i < 5; i++)
                            {
                                var context = await listener.GetContextAsync().ConfigureAwait(false);
                                using (var reader = new StreamReader(context.Request.InputStream))
                                    observed.Add((string)JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false))["model"]);
                                var decision = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                                    "Answer the greeting.", "Здравствуйте.", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                var bytes = Encoding.UTF8.GetBytes(new JObject { ["choices"] = new JArray(new JObject {
                                    ["message"] = new JObject { ["content"] = decision } }) }.ToString());
                                context.Response.ContentLength64 = bytes.Length;
                                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                                context.Response.Close();
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                    });
                    try
                    {
                        var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "models"));
                        var other = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "other"));
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "default-a",
                            StreamResponses = false, AutoCompressContext = false, SmartChatTitles = false,
                            AgentResponseMode = AgentResponseModes.JsonObject };
                        string selected, inherited;
                        using (var service = new WorkspaceConversationService(paths))
                        {
                            selected = service.CreateSession(workspace, "Selected").Id;
                            inherited = service.CreateSession(workspace, "Default").Id;
                            AssertEqual("chosen-model", service.SetModel(workspace, selected, " chosen-model ").Model, "trim and persist the chat preference");
                            var leasePath = Path.Combine(paths.WorkspaceDirectory, "run-leases", TextPatternEngine.Sha256(selected) + ".lck");
                            using (new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                            {
                                var busy = false;
                                var timer = System.Diagnostics.Stopwatch.StartNew();
                                try { service.SetModel(workspace, selected, "busy-model"); }
                                catch (WorkspaceSessionLeaseUnavailableException) { busy = true; }
                                AssertTrue(busy && timer.ElapsedMilliseconds < 3000, "model selection refuses a held lease without waiting for the run");
                            }
                            var refused = false;
                            try { service.SetModel(other, selected, "foreign"); } catch (InvalidOperationException) { refused = true; }
                            AssertTrue(refused, "a foreign workspace cannot change the chat model");
                            var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = selected, Message = "Hello" }, settings, () => "").GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "selected model completes a real wire request");
                            AssertEqual("default-a", settings.Model, "chat preference never mutates applied settings");
                            AssertEqual("chosen-model", service.ReadState(workspace, selected).ModelConfiguration.Model, "run metadata records the effective model");
                        }
                        using (var service = new WorkspaceConversationService(paths))
                        {
                            AssertEqual("chosen-model", service.ReadState(workspace, selected).Model, "preference survives owner recreation");
                            settings.Model = "default-b";
                            Action<string> run = id => {
                                var result = service.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                    SessionId = id, Message = "Hello again" }, settings, () => "").GetAwaiter().GetResult();
                                AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "model preference run completes");
                            };
                            run(selected); run(inherited);
                            AssertTrue(string.IsNullOrEmpty(service.ReadState(workspace, inherited).Model), "default mode stays inherited after a run");
                            settings.Model = "default-c"; run(inherited);
                            service.SetModel(workspace, selected, null); run(selected);
                            AssertTrue(string.IsNullOrEmpty(service.ReadState(workspace, selected).Model), "reset remains persisted as default mode");
                        }
                        AssertTrue(server.Wait(5000), "all model requests drained");
                        AssertTrue(observed.SequenceEqual(new[] { "chosen-model", "chosen-model", "default-b", "default-c", "default-c" }),
                            "wire follows only the exact chat selection or the current default");
                    }
                    finally { listener.Stop(); server.GetAwaiter().GetResult(); }
                }
            });
        }
    }
}
