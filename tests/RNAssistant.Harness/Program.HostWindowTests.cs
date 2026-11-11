using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HostWindowTransitions()
        {
            var policy = new AssistantWindowPolicy();
            Action<HostWindowCommand> apply = command => policy.Apply(new HostWindowCommandPayload { Command = command });
            apply(HostWindowCommand.ShowCompact); apply(HostWindowCommand.Maximize); apply(HostWindowCommand.Park);
            policy.Resume();
            AssertEqual(HostWindowPresentation.FullMaximized, policy.Presentation, "companion restores exact maximized presentation");
            apply(HostWindowCommand.Restore);
            AssertEqual(HostWindowPresentation.Compact, policy.Presentation, "independent pre-maximize return point");
            apply(HostWindowCommand.Maximize); apply(HostWindowCommand.SystemMinimize); apply(HostWindowCommand.Restore);
            AssertEqual(HostWindowPresentation.FullMaximized, policy.Presentation, "system restore does not consume maximize return point");
            apply(HostWindowCommand.Restore);
            AssertEqual(HostWindowPresentation.Compact, policy.Presentation, "next restore returns compact");
            var old = policy.Revision;
            policy.Apply(new HostWindowCommandPayload { Command = HostWindowCommand.SetPinned, Value = true });
            AssertTrue(!policy.Collapse(old, true, false) && !policy.Collapse(policy.Revision, true, false), "pin/stale timer cannot collapse");
            apply(HostWindowCommand.Park); policy.Resume();
            AssertTrue(policy.Pinned, "explicit park does not change pin");
            policy.Apply(new HostWindowCommandPayload { Command = HostWindowCommand.SetPinned, Value = false });
            AssertTrue(!policy.Collapse(policy.Revision, false, false), "NULL foreground is not a focus loss");
            AssertTrue(!policy.Collapse(policy.Revision, true, true), "modal/move operation protects window");
            old = policy.Revision; policy.Resume();
            AssertTrue(!policy.Collapse(old, true, false), "new launcher supersedes deferred focus decision");
            AssertTrue(policy.Collapse(policy.Revision, true, false), "known focus loss parks");
            policy.CompanionVisibility(true); apply(HostWindowCommand.Dismiss); policy.CompanionVisibility(true);
            AssertEqual(HostWindowVisibility.Dismissed, policy.Visibility, "returning to Excel cannot resurrect dismissed companion");
            policy.Resume(); apply(HostWindowCommand.SystemMinimize); policy.CompanionVisibility(true);
            AssertEqual(HostWindowVisibility.SystemMinimized, policy.Visibility, "system minimize never creates companion");
            apply(HostWindowCommand.Shutdown); policy.Resume();
            AssertEqual(HostWindowVisibility.ShuttingDown, policy.Visibility, "late launcher cannot revive shutdown");
        }

        private static async Task HostWindowPreferenceIsolation()
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-window-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = AppDataPaths.CreateForRoot(root);
                var excel = new AssistantWindowPreferenceStore(paths, "excel");
                var word = new AssistantWindowPreferenceStore(paths, "word");
                await Task.WhenAll(Task.Run(() => excel.Save(new AssistantWindowPreferences { Last = HostWindowPresentation.Compact, Pinned = true,
                    Full = new AssistantWindowRectangle { Monitor = "left", X = -50, Y = 20, Width = 900, Height = 700 } })),
                    Task.Run(() => word.Save(new AssistantWindowPreferences { Last = HostWindowPresentation.FullMaximized })));
                AssertTrue(excel.Load().Pinned && excel.Load().Full.X == -50, "profile merge retains logical geometry and pin");
                AssertEqual(HostWindowPresentation.FullMaximized, word.Load().Last, "other profile survives concurrent writer");
                File.WriteAllText(paths.NativeWindowStateFile, "{\"Version\":99,\"Profiles\":{}}");
                var rejected = false;
                try { excel.Load(); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected, "unsupported preferences are explicit, not silently reinterpreted");
                AssertTrue(!File.Exists(paths.SettingsFile) && Directory.GetFiles(paths.ChatDirectory).Length == 0,
                    "window persistence is independent of settings/run journals");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private sealed class HarnessWindowHost : IAssistantWindowHost
        {
            internal readonly AssistantWindowPolicy Policy = new AssistantWindowPolicy();
            internal int Commands;
            public HostWindowBootstrapDto Snapshot { get { return new HostWindowBootstrapDto {
                Capabilities = new HostWindowCapabilitiesDto { Pin = true, CompactPresentation = true, Companion = true }, State = Policy.Snapshot() }; } }
            public event Action<HostWindowStateDto> StateChanged;
            public event Action ShutdownRequested { add { } remove { } }
            public Task<HostWindowCommandResponse> ExecuteAsync(HostWindowCommandPayload command, string id)
            {
                Policy.Apply(command); Commands++; StateChanged?.Invoke(Policy.Snapshot());
                return Task.FromResult(new HostWindowCommandResponse { Accepted = true, State = Policy.Snapshot() });
            }
            public void ResponsePosted(string id) { }
            public void ConfigureChrome(bool supported) { }
            public void InvalidateChrome() { }
        }
        private static async Task HostWindowBridgeTrustAndSwitch()
        {
            var posts = new List<string>(); var host = new HarnessWindowHost();
            using (var bridge = new AssistantWebBridge(new AssistantController(), posts.Add))
            {
                var token = BridgeToken(bridge);
                Func<string, object, string, Task<string>> send = (type, body, auth) => bridge.HandleMessageAsync(JsonConvert.SerializeObject(new {
                    contractVersion = BridgeOperationCatalog.Version, id = Guid.NewGuid().ToString("N"), type = type, bridgeToken = auth, payload = body }));
                var payload = new HostWindowCommandPayload { Command = HostWindowCommand.SetPinned, Value = true };
                AssertTrue(!(bool)JObject.Parse(await send("hostWindowCommand", payload, token))["ok"], "unsupported host rejects command");
                bridge.WindowHost = host;
                AssertTrue(!(bool)JObject.Parse(await send("hostWindowCommand", payload, "wrong"))["ok"], "window commands require current token");
                AssertEqual(0, host.Commands, "invalid token cannot reach host");
                AssertTrue(!(bool)JObject.Parse(await send("hostWindowCommand", new { command = "MoveArbitraryHwnd", hwnd = 123 }, token))["ok"], "closed command vocabulary");
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<OfficeHostChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridge.OfficeHostChatRequested = name => { entered.SetResult(true); return release.Task; };
                var switching = send("createOfficeHostChat", new OfficeHostChatPayload { Host = "Excel" }, token);
                await entered.Task;
                AssertTrue(bridge.HostSwitchPending, "fixture owns exclusive domain switch");
                AssertTrue((bool)JObject.Parse(await send("hostWindowCommand", payload, token))["ok"], "presentation remains available during domain switch");
                AssertTrue(!(bool)JObject.Parse(await send("hostWindowCommand", payload, "wrong"))["ok"], "switch does not weaken token gate");
                AssertTrue(host.Policy.Pinned && posts.Exists(p => p.Contains("hostWindowState")), "accepted command publishes typed state");
                bridge.RebindController(new AssistantController()); // count must still be exactly one exclusive request
                release.SetResult(new OfficeHostChatResponse { Host = "Excel", ChatId = "chat-window", Init = new InitResponse { Host = "Excel", ActiveChatId = "chat-window" } });
                AssertTrue((bool)JObject.Parse(await switching)["ok"], "window command did not interfere with binding ownership");
                AssertTrue(WebViewSecurityPolicy.IsTrustedDocument("file:///app/index.html", "file:///app/index.html") &&
                    !WebViewSecurityPolicy.IsTrustedDocument("https://preview.invalid/", "file:///app/index.html"), "root trust boundary excludes preview");
            }
        }
    }
}
