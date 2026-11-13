using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        private readonly Func<BrowserContentSession, BrowserResourceBinding> _bindings;
        private BrowserResourceBinding _resources;
        private readonly Dictionary<string, byte[]> _assembledHtml = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private async Task BuildHtmlAsync(CancellationToken token)
        {
            if (Content.HtmlWorkspace == null) return;
            // Reuse the product assembler in an isolated, still blank world. Authored
            // scripts are strings here; they execute only after the scoped response loads.
            var frame = await Protocol<FrameTreeResponse>("Page.getFrameTree", new { });
            var world = await Protocol<World>("Page.createIsolatedWorld", new { frameId = frame.frameTree.frame.id, worldName = "RNAssistantHtmlAssembly", grantUniveralAccess = false });
            _world = world.executionContextId;
            await Evaluate<bool>("globalThis.__rnaVendors={};globalThis.send=function(type,args){if(type!=='resolveHtmlVendor')throw Error('Assembly operation denied');return Promise.resolve(__rnaVendors[args.id+'@'+args.version]);};true");
            foreach (var file in new[] { "app-html-vendor-catalog.js", "app-html-vendor-runtime.js", "app-html-workspace-preview.js" }) {
                var script = await Task.Run(() => File.ReadAllText(Path.Combine(_assets, "js", file), Encoding.UTF8), token);
                await Evaluate<bool>(script + "\n;true");
            }
            await Evaluate<bool>("globalThis.__rnaWorkspace=" + JsonConvert.SerializeObject(Content.HtmlWorkspace) + ";true");
            var vendors = await Evaluate<PluginVendorRequest[]>("RNAssistantHtmlVendorRuntime.used(__rnaWorkspace.Files).map(function(v){return {id:v.id,version:v.version}})");
            foreach (var vendor in vendors) {
                token.ThrowIfCancellationRequested();
                var source = await Task.Run(() => _plugins.ResolveHtmlVendor(vendor), token);
                if ((long)(source.Js?.Length ?? 0) + (source.Css?.Length ?? 0) > 16 * 1024 * 1024) throw new InvalidOperationException("Local HTML vendor exceeds 16 MiB.");
                await Evaluate<bool>("__rnaVendors[" + JsonConvert.SerializeObject(vendor.Id + "@" + vendor.Version) + "]=" + JsonConvert.SerializeObject(source) + ";true");
            }
            await Evaluate<bool>("RNAssistantHtmlWorkspacePreview.ensureVendors(__rnaWorkspace.Files).then(function(){return true})");
            long total = 0;
            foreach (var file in Content.HtmlWorkspace.Files.Where(file => file.Kind == "html")) {
                var html = await Evaluate<string>("RNAssistantHtmlWorkspacePreview.build({files:__rnaWorkspace.Files,dataSources:__rnaWorkspace.DataSources,activeFileId:" + JsonConvert.SerializeObject(file.Id) + ",hostBridge:true,nativeHost:true})");
                token.ThrowIfCancellationRequested();
                if (html == null || html.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Assembled HTML exceeds 32 MiB.");
                var bytes = new UTF8Encoding(false, true).GetBytes(html); total += bytes.Length;
                if (total > 32 * 1024 * 1024) throw new InvalidOperationException("Assembled HTML pages exceed 32 MiB.");
                _assembledHtml.Add(file.Path, bytes);
            }
            _resources = _bindings(Content);
        }
        internal void ResourceChanged(ResourceChangedMessage change)
        {
            if (_closed || !Ready || _resources == null || Content.Target.Immutable || change.Scope != "document:" + Content.Target.DocumentId) return;
            try { Core.PostWebMessageAsJson(JsonConvert.SerializeObject(new BrowserResourceChangedMessage {
                Names = Content.HtmlWorkspace.DataSources.Select(source => source.Name).ToArray(), Generation = change.Generation })); } catch { }
        }
        private async void ResourceControl(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (_closed || _resources == null || _rpcPending >= 32) return; _rpcPending++;
            try {
                var uri = new Uri(args.Source); if (uri.GetLeftPart(UriPartial.Authority) != Content.Origin) return;
                var json = args.TryGetWebMessageAsString(); if (json == null || json.Length > 2048) return;
                var request = JsonConvert.DeserializeObject<BrowserResourceControlRequest>(json,
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 8 });
                var result = await Task.Run(() => _resources.Control(request, _cancel.Token), _cancel.Token);
                if (!_closed) Core.PostWebMessageAsJson(JsonConvert.SerializeObject(result));
            } catch (Exception) { /* A malformed unprivileged request receives no bridge access. */ }
            finally { _rpcPending--; }
        }
    }
}
