using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class JsToolHandler : IReadOnlyToolHandler
    {
        internal const string RunId = "common.js_run";
        internal const string PackageHandlerId = "js.custom.package.execute.v1";
        internal static readonly ToolBinding Binding = new ToolBinding("js.run.v1");
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, false, new[] { "agent" });
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(RunId,
            "Run read-only JavaScript over the explicit resources inputs for this call. It does not execute page scripts or inherit HTML workspace bindings. Use data source targets; an HTML data target is binding metadata, whose text supplies sourceTarget. Return a JSON value. Use await RN.resources.open(name) and for await (const batch of handle.stream({limit:500})); records/table batches expose rows and columns, text/source batches expose text chunks. No document writes, network, filesystem or CLR access.",
            Parameters());

        private static string Parameters()
        {
            return new JObject { ["type"] = "object", ["properties"] = new JObject {
                ["code"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1000000,
                    ["description"] = "Body of an async JavaScript function; return a JSON-serializable value." },
                ["resources"] = new JObject { ["type"] = "array", ["maxItems"] = 32,
                    ["description"] = "Explicit named inputs for this invocation. Use [] if no inputs are needed; HTML bindings are not inherited.",
                    ["items"] = ResourceSelectorContract.NamedInput(false) } },
                ["required"] = new JArray("code", "resources"), ["additionalProperties"] = false }.ToString(Formatting.None);
        }


        private readonly ResourceGatewayService _gateway;
        private readonly ChatSession _session;
        private readonly string _source;

        internal JsToolHandler(ResourceGatewayService gateway, ChatSession session, string source = null)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _session = session;
            _source = source;
        }

        internal static bool IsDefinition(ToolCatalogEntry tool)
        {
            return tool != null && !tool.BuiltIn && string.Equals(tool.Executor, "js", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool Owns(ToolRegistration registration)
        {
            return registration != null && registration.Binding != null &&
                string.Equals(registration.Binding.HandlerId, PackageHandlerId, StringComparison.Ordinal);
        }

        internal static ToolBinding BindingFor(ToolCatalogEntry tool)
        {
            return IsDefinition(tool) ? new ToolBinding(PackageHandlerId, scope: tool.Scope, host: tool.Host) : null;
        }

        internal static ToolPolicy PolicyFor(ToolCatalogEntry tool)
        {
            return IsDefinition(tool) ? Policy : null;
        }

        public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            if (_session == null) return await OfficeToolFailure.Rejected("JS requires an active chat.", "resource_session_required");
            try
            {
                var arguments = JObject.FromObject(context.Arguments);
                var code = _source ?? (string)arguments["code"];
                if (string.IsNullOrWhiteSpace(code) || code.Length > 1000000)
                    return await OfficeToolFailure.Rejected("JS source is empty or too large.", "invalid_js_source");
                var input = arguments["resources"] as JArray;
                if (input == null || input.Count > 32)
                    return await OfficeToolFailure.Rejected("JS resources must be an array of at most 32 bindings.", "invalid_js_resources");
                var resources = new Dictionary<string, Input>(StringComparer.Ordinal);
                var evidence = new List<ResourceEvidence>();
                using (DocumentAccessGate.BeginOperation())
                {
                    foreach (var token in input)
                    {
                        var item = token as JObject;
                        var name = (string)item?["name"];
                        var target = (string)item?["target"];
                        var view = (string)item?["view"];
                        var path = (string)item?["path"];
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target) ||
                            string.IsNullOrWhiteSpace(view) || resources.ContainsKey(name))
                            throw new InvalidOperationException("Invalid or duplicate JS resource binding.");
                        var selected = _gateway.ResolveIntentTarget(_session, target);
                        var viewPath = ResourceSelectorContract.ResolvePath(selected, view, path, (int?)item?["pageIndex"]);
                        // A complete resource body is never materialized here. Capture only an exact view revision.
                        var first = _gateway.Read(_session, new ResourceReadRequest {
                            Reference = selected.Descriptor.Mutable ? new ResourceRef(selected.Reference.Uri) : selected.Reference,
                            Representation = view, ViewPath = viewPath, MaxChars = 1, MaxRows = 1 });
                        evidence.AddRange(_gateway.Evidence(_session, first.Result));
                        resources.Add(name, new Input { Reference = first.Result.Resource.Reference.Copy(),
                            View = first.Result.Representation, Path = viewPath });
                    }
                }
                arguments.Remove("code");
                arguments.Remove("resources");
                context.MarkDispatchPossible();
                var result = await RunWorkerAsync(code, arguments, resources, cancellationToken).ConfigureAwait(false);
                return new ToolHandlerResult(RuntimeResult.Ok("JavaScript completed.", result),
                    ToolEffectEvidence.None, resourceEvidence: evidence);
            }
            catch (ResourceRequestException ex) { return await OfficeToolFailure.Resource(ex); }
            catch (OperationCanceledException) { return await OfficeToolFailure.Rejected("JavaScript was cancelled.", "js_cancelled"); }
            catch (Exception ex)
            {
                return await OfficeToolFailure.Rejected(ex.Message, "js_execution_failed");
            }
        }

        private async Task<string> RunWorkerAsync(string code, JObject arguments,
            IDictionary<string, Input> resources, CancellationToken cancellationToken)
        {
            var worker = Path.Combine(Path.GetDirectoryName(typeof(JsToolHandler).Assembly.Location),
                "RNAssistant.JsWorker.exe");
            if (!File.Exists(worker)) throw new FileNotFoundException("JS worker is not deployed.", worker);
            using (var plane = new ResourceDataPlaneService(_gateway))
            using (var process = new Process { StartInfo = new ProcessStartInfo(worker) {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                CreateNoWindow = true } })
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                if (!process.Start()) throw new InvalidOperationException("JS worker could not start.");
                try
                {
                    using (deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                    {
                        var start = new JObject { ["code"] = code, ["args"] = arguments,
                            ["names"] = new JArray(resources.Keys.OrderBy(value => value, StringComparer.Ordinal)) };
                        var startJson = start.ToString(Formatting.None);
                        if (Encoding.UTF8.GetByteCount(startJson) > 2 * 1024 * 1024)
                            throw new InvalidDataException("JS request exceeds 2 MiB.");
                        await process.StandardInput.WriteLineAsync(startJson).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        var leases = new Dictionary<string, long>(StringComparer.Ordinal);
                        while (true)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                            if (line == null)
                            {
                                deadline.Token.ThrowIfCancellationRequested();
                                throw new InvalidOperationException("JS worker stopped before returning a result.");
                            }
                            if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("JS worker response exceeds its limit.");
                            var message = JObject.Parse(line);
                            var type = (string)message["type"];
                            if (type == "result")
                            {
                                var result = (string)message["json"];
                                if (result == null || Encoding.UTF8.GetByteCount(result) > 1024 * 1024)
                                    throw new InvalidDataException("JS result exceeds 1 MiB.");
                                JToken.Parse(result);
                                return result;
                            }
                            if (type == "error") throw new InvalidOperationException((string)message["message"] ?? "JS failed.");
                            if (type != "request") throw new InvalidDataException("Invalid JS worker message.");
                            JObject response;
                            try { response = HandleRequest(plane, resources, leases, message["body"] as JObject, deadline.Token); }
                            catch (Exception ex) { response = new JObject { ["ok"] = false, ["error"] = ex.Message }; }
                            await process.StandardInput.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                            await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        }
                    }
                }
                finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
            }
        }

        private JObject HandleRequest(ResourceDataPlaneService plane, IDictionary<string, Input> resources,
            IDictionary<string, long> leases, JObject request, CancellationToken token)
        {
            var operation = (string)request?["operation"];
            if (operation == "open")
            {
                Input input;
                if (!resources.TryGetValue((string)request["name"] ?? string.Empty, out input))
                    throw new InvalidOperationException("RESOURCE_BINDING_UNKNOWN");
                var opened = plane.Open(_session, "js:" + _session.Id, input.Reference, input.View, input.Path, token);
                leases.Add(opened.LeaseId, opened.Binary == null ? -1 : opened.Binary.Payload.ByteLength);
                return new JObject { ["ok"] = true, ["leaseId"] = opened.LeaseId,
                    ["descriptor"] = new JObject { ["kind"] = opened.Descriptor.Kind,
                        ["view"] = opened.View, ["path"] = opened.ViewPath,
                        ["binary"] = opened.Binary != null,
                        ["maxBatchItems"] = opened.Binary != null ? 256 * 1024 :
                            opened.View == "table" || opened.View == "records" ? 500 : 32000 } };
            }
            var leaseId = (string)request?["leaseId"] ?? string.Empty;
            long binaryLength;
            if (!leases.TryGetValue(leaseId, out binaryLength)) throw new InvalidOperationException("RESOURCE_LEASE_EXPIRED");
            if (operation == "close")
            {
                plane.Close(_session.Id, "js:" + _session.Id, leaseId);
                leases.Remove(leaseId);
                return new JObject { ["ok"] = true };
            }
            if (operation != "read") throw new InvalidOperationException("Unsupported JS resource operation.");
            var offset = (int?)request["offset"] ?? -1;
            var limit = (int?)request["limit"] ?? -1;
            var fields = (request["fields"] as JArray)?.Values<string>().ToArray();
            var bytes = plane.Read(leaseId, offset, limit, token, fields);
            var batch = binaryLength >= 0
                ? new JObject { ["bytes"] = new JArray(bytes.Select(value => (int)value)),
                    ["offset"] = offset, ["nextOffset"] = offset + bytes.Length,
                    ["done"] = offset + bytes.Length == binaryLength }
                : JObject.Parse(Encoding.UTF8.GetString(bytes));
            batch.Remove("resource");
            return new JObject { ["ok"] = true, ["batch"] = batch };
        }

        private sealed class Input
        {
            internal ResourceRef Reference;
            internal string View;
            internal string Path;
        }
    }
}
