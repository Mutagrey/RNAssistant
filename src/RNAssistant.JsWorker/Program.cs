using System;
using System.IO;
using System.Threading.Tasks;
using Jint;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.JsWorker
{
    internal static class Program
    {
        // The only host capability is a validated resource request over this pipe.
        private static readonly string Api = @"
            const RN = Object.freeze({ resources: Object.freeze({
              names: () => JSON.parse(__namesJson),
              open: async name => {
                const opened = JSON.parse(__bridge(JSON.stringify({operation:'open', name:name})));
                if (!opened.ok) throw Error(opened.error);
                let closed = false, offset = 0, done = false;
                const handle = {
                  descriptor: Object.freeze(opened.descriptor),
                  read: async (options = {}) => {
                    if (closed || done) throw Error('RESOURCE_LEASE_CLOSED');
                    if (options.view && options.view !== opened.descriptor.view) throw Error('RESOURCE_VIEW_UNSUPPORTED');
                    if (options.path && options.path !== opened.descriptor.path) throw Error('RESOURCE_VIEW_PATH_UNSUPPORTED');
                    const result = JSON.parse(__bridge(JSON.stringify({operation:'read', leaseId:opened.leaseId,
                      offset:offset, limit:options.limit || opened.descriptor.maxBatchItems, fields:options.fields || null})));
                    if (!result.ok) throw Error(result.error);
                    offset = result.batch.nextOffset; done = result.batch.done;
                    if (result.batch.bytes) result.batch.bytes = Uint8Array.from(result.batch.bytes);
                    return result.batch;
                  },
                  stream: async function* (options = {}) {
                    try { while (!done && !closed) yield await handle.read(options); }
                    finally { await handle.close(); }
                  },
                  close: async () => {
                    if (closed) return;
                    closed = true;
                    const result = JSON.parse(__bridge(JSON.stringify({operation:'close', leaseId:opened.leaseId})));
                    if (!result.ok) throw Error(result.error);
                  }
                };
                return Object.freeze(handle);
              }
            }) });";

        private static int Main()
        {
            try { return RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Out.WriteLine(new JObject { ["type"] = "error", ["message"] = ex.Message }.ToString(Formatting.None));
                Console.Out.Flush();
                return 1;
            }
        }

        private static async Task<int> RunAsync()
        {
            var start = Console.In.ReadLine();
            if (start == null) throw new InvalidDataException("Missing JS request.");
            var request = JObject.Parse(start);
            var code = (string)request["code"];
            if (string.IsNullOrWhiteSpace(code) || code.Length > 1000000)
                throw new InvalidDataException("JS source is empty or exceeds 1000000 characters.");
            var names = request["names"] as JArray ?? new JArray();
            var args = request["args"] as JObject ?? new JObject();
            var engine = new Engine(options => {
                options.LimitMemory(64 * 1024 * 1024);
                options.TimeoutInterval(TimeSpan.FromSeconds(10));
                options.MaxStatements(1000000);
                options.LimitRecursion(100);
            });
            engine.SetValue("__bridge", new Func<string, string>(Bridge));
            engine.SetValue("__namesJson", names.ToString(Formatting.None));
            engine.Execute(Api);
            var call = "globalThis.__resultJson = JSON.stringify(await (async function(args) {\n" +
                code + "\n})(" + args.ToString(Formatting.None) + "));";
            await engine.ExecuteAsync("(async () => { " + call + " })()").ConfigureAwait(false);
            var value = engine.GetValue("__resultJson");
            if (value.IsUndefined() || value.IsNull())
                throw new InvalidDataException("JS must return a JSON-serializable value.");
            var result = value.AsString();
            if (result.Length > 1024 * 1024)
                throw new InvalidDataException("JS result exceeds 1 MiB.");
            Console.Out.WriteLine(new JObject { ["type"] = "result", ["json"] = result }.ToString(Formatting.None));
            Console.Out.Flush();
            return 0;
        }

        private static string Bridge(string json)
        {
            Console.Out.WriteLine(new JObject { ["type"] = "request", ["body"] = JObject.Parse(json) }.ToString(Formatting.None));
            Console.Out.Flush();
            var response = Console.In.ReadLine();
            if (response == null) throw new IOException("Resource host disconnected.");
            return response;
        }
    }
}
