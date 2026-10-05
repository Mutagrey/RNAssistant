using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceWebVerifier
    {
        private sealed class BrowserCheckTarget
        {
            public string Error { get; set; }
            public string Text { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
        }

        private sealed class BrowserDownload
        {
            public string Id;
            public string State;
            public double Bytes;
            public bool AllowedSource;
        }

        private static async Task RunChecksAsync(DevToolsClient devtools, WebFunctionalChecks checks,
            List<WebCheckResult> results, string profile, CancellationToken token)
        {
            for (var index = 0; index < checks.Steps.Count; index++)
            {
                var step = checks.Steps[index];
                BrowserCheckTarget target;
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    do
                    {
                        target = await CheckTargetAsync(devtools, step, false, token).ConfigureAwait(false);
                        if (target.Error != null || !step.IsAssertion || step.Operation == WebCheckOperation.DownloadCsvEquals ||
                            step.Accepts(target.Text)) break;
                        await Task.Delay(50, token).ConfigureAwait(false);
                    } while (elapsed.Elapsed < TimeSpan.FromSeconds(2));
                    if (target.Error == null)
                    {
                        if (step.Operation == WebCheckOperation.Click)
                            await ClickAsync(devtools, target, token).ConfigureAwait(false);
                        else if (step.Operation == WebCheckOperation.DownloadCsvEquals)
                            target.Text = await DownloadCsvAsync(devtools, target, profile, token).ConfigureAwait(false);
                        else if (step.Operation == WebCheckOperation.UploadCsv)
                        {
                            // The only files this runner creates/reads are bounded fixtures
                            // and downloads in the private browser profile, never source files.
                            var fixture = Path.Combine(profile, "fixture-" + Guid.NewGuid().ToString("N") + ".csv");
                            await File.WriteAllTextAsync(fixture, step.Expected, StrictUtf8, token).ConfigureAwait(false);
                            var document = await devtools.CommandAsync("DOM.getDocument", null, token).ConfigureAwait(false);
                            var node = await devtools.CommandAsync("DOM.querySelector", new JObject {
                                ["nodeId"] = document["result"]["root"]["nodeId"], ["selector"] = step.Selector }, token).ConfigureAwait(false);
                            await devtools.CommandAsync("DOM.setFileInputFiles", new JObject {
                                ["nodeId"] = node["result"]["nodeId"], ["files"] = new JArray(fixture) }, token).ConfigureAwait(false);
                            target = await CheckTargetAsync(devtools, step, true, token).ConfigureAwait(false);
                        }
                        else if (step.Operation == WebCheckOperation.SelectValue || step.Operation == WebCheckOperation.InputValue)
                            target = await CheckTargetAsync(devtools, step, true, token).ConfigureAwait(false);
                    }
                    if (devtools.Errors.Count != 0) target.Error = "Browser reported an error during functional checks.";
                    if (target.Error == null && !step.Accepts(target.Text))
                        target.Error = (step.Operation == WebCheckOperation.TextEquals ? "Text differs" : step.Operation + " differs") +
                            " from expected: " + step.Expected;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { target = new BrowserCheckTarget { Error = ex.Message.Length > 700 ? ex.Message.Substring(0, 700) : ex.Message }; }
                results[index] = new WebCheckResult(step.Id, target.Error == null ? WebCheckStatus.Passed : WebCheckStatus.Failed,
                    target.Text, target.Error);
                if (target.Error != null) return; // Remaining steps are explicitly NotRun.
            }
        }

        private static async Task<BrowserCheckTarget> CheckTargetAsync(DevToolsClient devtools, WebCheckStep step,
            bool act, CancellationToken token)
        {
            // Only JSON-encoded data is interpolated. Callers cannot submit JS.
            var expression = "(async () => { try { const nodes=document.querySelectorAll(" + JsonConvert.SerializeObject(step.Selector) + "); " +
                "if(nodes.length!==1)return {Error:'Selector must match exactly one element; found '+nodes.length}; " +
                "const el=nodes[0]; if(!(el instanceof HTMLElement || el instanceof SVGSVGElement))return {Error:'Invalid target element'}; " +
                "el.scrollIntoView({block:'center',inline:'center'}); const r=el.getBoundingClientRect(),s=getComputedStyle(el); " +
                "if(s.visibility!=='visible'||s.display==='none'||Number(s.opacity)===0" +
                (step.Operation == WebCheckOperation.TableEquals ? "" : "||!r.width||!r.height") + ")return {Error:'Target is not visible'}; " +
                "const expected=" + JsonConvert.SerializeObject(step.Expected) + "; " + TargetOperation(step.Operation, act) +
                "}catch(e){return {Error:String(e).slice(0,700)}}})()";
            var evaluated = await devtools.CommandAsync("Runtime.evaluate", new JObject {
                ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true, ["timeout"] = 1000 }, token).ConfigureAwait(false);
            var evaluation = evaluated["result"];
            var target = evaluation?["exceptionDetails"] == null
                ? evaluation?["result"]?["value"]?.ToObject<BrowserCheckTarget>() : null;
            if (target == null) throw new IOException("Browser could not evaluate the functional check target.");
            if (target.Text?.Length > 700) return new BrowserCheckTarget { Error = "Observed value exceeds 700 characters." };
            if (devtools.Errors.Count != 0) target.Error = "Browser reported an error during functional checks.";
            return target;
        }

        private static string TargetOperation(WebCheckOperation operation, bool act)
        {
            switch (operation)
            {
                case WebCheckOperation.Click:
                case WebCheckOperation.DownloadCsvEquals:
                    return "if(el.matches(':disabled')||el.getAttribute('aria-disabled')==='true')return {Error:'Target is disabled'}; " +
                        "const x=r.x+r.width/2,y=r.y+r.height/2,hit=document.elementFromPoint(x,y); " +
                        "if(!hit||!(hit===el||el.contains(hit)))return {Error:'Target is obscured'}; return {X:x,Y:y};";
                case WebCheckOperation.UploadCsv:
                    return "if(!(el instanceof HTMLInputElement)||el.type!=='file'||el.disabled)return {Error:'Target must be an enabled file input'}; " +
                        (act ? "if(el.files.length!==1||el.files[0].size>2048)return {Error:'Expected one bounded upload'}; return {Text:await el.files[0].text()};" : "return {}; ");
                case WebCheckOperation.SelectValue:
                    return "if(!(el instanceof HTMLSelectElement)||el.disabled||el.multiple)return {Error:'Target must be an enabled single select'}; " +
                        "if(!Array.from(el.options).some(o=>o.value===expected&&!o.disabled))return {Error:'Requested option is unavailable'}; " +
                        (act ? "Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype,'value').set.call(el,expected); el.dispatchEvent(new Event('change',{bubbles:true}));" : "") +
                        "return {Text:el.value};";
                case WebCheckOperation.InputValue:
                    return "if(!(el instanceof HTMLInputElement)||!['text','number','search'].includes(el.type)||el.disabled||el.readOnly)" +
                        "return {Error:'Target must be an editable text, number or search input'}; " +
                        (act ? "Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,expected); " +
                            "el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true}));" : "") +
                        "return {Text:el.value};";
                case WebCheckOperation.TableEquals:
                    return "if(!(el instanceof HTMLTableSectionElement))return {Error:'Target must be a table section'}; " +
                        "if(el.rows.length>12)return {Error:'Table exceeds 12 rows'}; const rows=Array.from(el.rows); " +
                        "if(rows.some(row=>row.cells.length>8||!row.getBoundingClientRect().height||getComputedStyle(row).visibility!=='visible'))" +
                        "return {Error:'Table cells exceed bounds or rows are hidden'}; " +
                        "const text=rows.map(row=>Array.from(row.cells,c=>'\"'+(c.textContent||'').trim().replaceAll('\"','\"\"')+'\"').join(',')).join('\\n'); " +
                        "return text.length>700?{Error:'Table exceeds 700 characters'}:{Text:text};";
                case WebCheckOperation.BarChartEquals:
                    return "if(!(el instanceof SVGSVGElement))return {Error:'Target must be an SVG bar chart'}; " +
                        "const bars=Array.from(el.querySelectorAll('rect')); if(bars.length>12)return {Error:'Chart exceeds 12 bars'}; " +
                        "const sizes=[]; for(const bar of bars){const b=bar.getBoundingClientRect(),s=getComputedStyle(bar); " +
                        "if(s.visibility!=='visible'||Number(s.opacity)===0||!b.width||!b.height||b.x<r.x-1||b.y<r.y-1||b.right>r.right+1||b.bottom>r.bottom+1)" +
                        "return {Error:'Chart bar is hidden or clipped'}; sizes.push(b.width+','+b.height)} return {Text:sizes.join('\\n')};";
                default:
                    return "const text=(el.textContent||'').trim(); return text.length>700?{Error:'Text exceeds 700 characters'}:{Text:text};";
            }
        }

        private static async Task ClickAsync(DevToolsClient devtools, BrowserCheckTarget target, CancellationToken token)
        {
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await devtools.CommandAsync("Input.dispatchMouseEvent", new JObject {
                    ["type"] = type, ["x"] = target.X, ["y"] = target.Y, ["button"] = "left", ["clickCount"] = 1 }, token).ConfigureAwait(false);
        }

        private static async Task<string> DownloadCsvAsync(DevToolsClient devtools, BrowserCheckTarget target,
            string profile, CancellationToken token)
        {
            var directory = Path.Combine(profile, "downloads");
            Directory.CreateDirectory(directory);
            var before = devtools.Downloads.Count;
            await devtools.CommandAsync("Browser.setDownloadBehavior", new JObject {
                ["behavior"] = "allowAndName", ["downloadPath"] = directory, ["eventsEnabled"] = true }, token).ConfigureAwait(false);
            try
            {
                await ClickAsync(devtools, target, token).ConfigureAwait(false);
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
                {
                    // A command drains CDP events without cancelling the shared socket.
                    await devtools.CommandAsync("Runtime.evaluate", new JObject { ["expression"] = "0" }, token).ConfigureAwait(false);
                    var received = devtools.Downloads.Skip(before).ToArray();
                    if (received.Length > 1) throw new IOException("Expected exactly one CSV download.");
                    if (received.Length == 1)
                    {
                        var download = received[0];
                        if (!download.AllowedSource || download.Bytes > 65536 || download.State == "canceled")
                            throw new IOException("Download source, size or completion is invalid.");
                        if (download.State == "completed")
                        {
                            // allowAndName uses a runtime UUID, never a suggested filename.
                            var path = Path.Combine(directory, download.Id);
                            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                if (stream.Length > 65536) throw new IOException("CSV download exceeds 64 KiB.");
                                using (var reader = new StreamReader(stream, StrictUtf8, true))
                                {
                                    var buffer = new char[701]; var count = 0;
                                    while (count < buffer.Length)
                                    {
                                        var read = await reader.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                                        if (read == 0) break;
                                        count += read;
                                    }
                                    if (count > 700) throw new IOException("CSV download exceeds 700 characters.");
                                    return new string(buffer, 0, count);
                                }
                            }
                        }
                    }
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                throw new IOException("Expected CSV download did not complete within five seconds.");
            }
            finally
            {
                // Scope download permission to this one assertion. Browser teardown
                // also kills any pending transfer on cancellation/protocol failure.
                await devtools.CommandAsync("Browser.setDownloadBehavior", new JObject { ["behavior"] = "deny" }, token).ConfigureAwait(false);
                foreach (var download in devtools.Downloads.Skip(before).Where(item => item.State != "completed" && item.State != "canceled"))
                    await devtools.CommandAsync("Browser.cancelDownload", new JObject { ["guid"] = download.Id }, token).ConfigureAwait(false);
                if (devtools.Downloads.Count > before + 1)
                    throw new IOException("Expected exactly one CSV download.");
            }
        }
    }
}
