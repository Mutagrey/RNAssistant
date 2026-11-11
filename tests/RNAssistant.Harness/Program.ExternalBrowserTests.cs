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
            var rule = HttpOriginPattern.Normalize("HTTPS://*.Example.COM:443/");
            AssertEqual("https://*.example.com", rule, "default port and case normalize");
            foreach (var url in new[] { "https://example.com/a", "https://a.example.com/", "https://deep.a.example.com/", "https://EXAMPLE.COM.:443/" })
                AssertTrue(HttpOriginPattern.Matches(rule, new Uri(url)), "root/subdomains match across a DNS label boundary");
            foreach (var url in new[] { "https://notexample.com/", "https://example.com.evil.test/", "http://example.com/", "https://example.com:444/", "https://user@example.com/" })
                AssertTrue(!HttpOriginPattern.Matches(rule, new Uri(url)), "host/scheme/port/credentials cannot widen the grant");
            AssertTrue(!HttpOriginPattern.Matches("https://example.com", new Uri("https://a.example.com")), "exact host does not grant subdomains");
            AssertTrue(HttpOriginPattern.Matches("https://*.пример.рф:8443", new Uri("https://sub.xn--e1afmkfd.xn--p1ai:8443/")), "IDN matches punycode");
            AssertEqual("http://[::1]:8080", HttpOriginPattern.Normalize("http://[::1]:8080/"), "explicit IPv6 origin");
            foreach (var invalid in new[] { "*example.com", "https://*example.com", "https://*", "*://example.com", "https://*.127.0.0.1", "https://*.com", "https://example.com/path", "https://example.com/a/..", "https://example.com?x", "https://example.com#x", "https://u@example.com", "https://example.com:*", "https://example.com//", "" })
                RuntimeThrows<InvalidOperationException>(() => HttpOriginPattern.Normalize(invalid));
            ExternalBrowserPolicy.RequireAgentAccess(ExternalBrowserPolicy.ProbeUrl, new string[0]);
            ExternalBrowserPolicy.RequireAgentAccess("https://sub.example.com/any/path", new[] { rule });
            RuntimeThrows<ResourceRequestException>(() => ExternalBrowserPolicy.RequireAgentAccess("https://example.com", new string[0]));
            AssertEqual("https://example.com/", ExternalBrowserPolicy.NavigationUrl("https://example.com"), "manual navigation does not consult agent grants");
            WithTempExecutor((executor, adapter) => {
                var service = new SettingsService(FixturePaths.Value);
                var settings = service.Load(); settings.BrowserAgentAllowedSites.Add("https://*.Example.COM:443/");
                var roundTrip = SettingsControlsDto.From(settings).ApplyTo(new AppSettings());
                service.Save(roundTrip);
                AssertEqual(rule, service.Load().BrowserAgentAllowedSites[0], "typed settings persist the normalized rule");
                var clone = settings.Clone(); clone.BrowserAgentAllowedSites.Clear();
                AssertEqual(1, settings.BrowserAgentAllowedSites.Count, "cloning does not share the list");
                roundTrip.BrowserAgentAllowedSites.Add("*example.com");
                RuntimeThrows<InvalidOperationException>(() => service.Save(roundTrip));
                AssertEqual(1, service.Load().BrowserAgentAllowedSites.Count, "invalid save preserves previous grants");
            });
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
