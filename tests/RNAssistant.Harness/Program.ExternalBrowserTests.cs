using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExternalBrowserSiteAccess()
        {
            ExternalBrowserPolicy.RequireAccess(ExternalBrowserPolicy.ProbeUrl);
            foreach (var url in new[] { "https://example.com/", "https://sub.example.com/", "http://localhost/",
                "http://127.0.0.1/", "http://[::1]/", "http://192.168.0.1/", "https://rna-browser-probe.invalid/other" })
                RuntimeThrows<ResourceRequestException>(() => ExternalBrowserPolicy.RequireAccess(url));
            var settings = JsonConvert.DeserializeObject<AppSettings>("{\"HtmlNetworkAllowedOrigins\":[\"https://example.com\"]}");
            AssertTrue(!JsonConvert.SerializeObject(SettingsControlsDto.From(settings)).Contains("HtmlNetworkAllowedOrigins"),
                "retired stored field cannot restore network capability");
        }

        private static void ExternalBrowserAdmission()
        {
            AssertEqual("https://example.com/", ExternalBrowserPolicy.NavigationUrl("https://example.com"), "ordinary HTTPS address");
            foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "data:text/html,test", "https://user:secret@example.com/", "//example.com", "" })
            {
                var refused = false;
                try { ExternalBrowserPolicy.NavigationUrl(url); } catch (InvalidOperationException) { refused = true; }
                AssertTrue(refused, "external navigation rejects unsupported scheme/credentials: " + url.Split(':')[0]);
            }
            var bounds = new ExternalBrowserBoundsDto { Visible = true, X = 20, Y = 80, Width = 300, Height = 400, ViewportWidth = 420, ViewportHeight = 800 };
            AssertTrue(ExternalBrowserPolicy.ValidBounds(bounds), "bounded visible viewport");
            bounds.X = double.NaN; AssertTrue(!ExternalBrowserPolicy.ValidBounds(bounds), "NaN cannot reach native coordinates");
            bounds.X = 200; AssertTrue(!ExternalBrowserPolicy.ValidBounds(bounds), "out-of-viewport native surface refused");
            bounds.X = -1; AssertTrue(!ExternalBrowserPolicy.ValidBounds(bounds), "negative native coordinates refused");
            AssertTrue(ExternalBrowserPolicy.ValidBounds(new ExternalBrowserBoundsDto()), "hidden does not require stale geometry");
        }

        private sealed class HarnessExternalBrowserHost : IExternalBrowserHost
        {
            internal int Calls;
            public Task<ExternalBrowserStateDto> ExecuteAsync(ExternalBrowserCommandPayload request, CancellationToken cancellationToken)
            { Calls++; return Task.FromResult(new ExternalBrowserStateDto { SessionId = "runtime-browser", Status = ExternalBrowserStatus.Starting }); }
        }
        private static async Task ExternalBrowserBridgeTrust()
        {
            using (var bridge = new AssistantWebBridge(new AssistantController(), ignored => { }))
            {
                var token = BridgeToken(bridge); var host = new HarnessExternalBrowserHost();
                Func<object, string, Task<string>> send = (body, auth) => bridge.HandleMessageAsync(JsonConvert.SerializeObject(new {
                    contractVersion = BridgeOperationCatalog.Version, id = Guid.NewGuid().ToString("N"), type = "externalBrowserCommand", bridgeToken = auth, payload = body }));
                var request = new ExternalBrowserCommandPayload { Action = ExternalBrowserAction.Open };
                AssertTrue(!(bool)JObject.Parse(await send(request, token))["ok"], "disabled pilot refuses open");
                bridge.ExternalBrowser = host;
                AssertTrue(!(bool)JObject.Parse(await send(request, "foreign"))["ok"], "root token required");
                AssertTrue(!(bool)JObject.Parse(await send(new { action = "ExecuteRawCdp" }, token))["ok"], "closed typed vocabulary");
                AssertEqual(0, host.Calls, "invalid requests never reach the native owner");
                AssertTrue((bool)JObject.Parse(await send(request, token))["ok"], "typed open reaches configured owner");
                AssertEqual(1, host.Calls, "one accepted dispatch");
            }
        }
    }
}
