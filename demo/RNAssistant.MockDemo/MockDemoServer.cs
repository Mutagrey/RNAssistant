using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RNAssistant.MockDemo
{
    internal sealed class MockDemoServer
    {
        private readonly DemoOptions _options;
        private readonly MockBridgeHost _bridgeHost;
        private readonly string _webRoot;
        private HttpListener _listener;

        public MockDemoServer(DemoOptions options, MockBridgeHost bridgeHost, string webRoot)
        {
            _options = options;
            _bridgeHost = bridgeHost;
            _webRoot = webRoot;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_options.BaseUrl + "/");
            _listener.Start();

            Console.WriteLine("RNAssistant mock demo");
            Console.WriteLine("Host: " + _options.Host);
            Console.WriteLine("Data: " + _options.DataRoot);
            Console.WriteLine("URL:  " + _options.BaseUrl + "/");
            Console.WriteLine("Models: " + string.Join(", ", ScriptedDemoLlm.ModelIds));
            Console.WriteLine("Press Ctrl+C to stop.");

            using (cancellationToken.Register(delegate { Stop(); }))
            {
                while (_listener != null && _listener.IsListening && !cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    _ = Task.Run(delegate { HandleContext(context); });
                }
            }
        }

        public void Stop()
        {
            if (_listener == null)
            {
                return;
            }

            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _listener = null;
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            try
            {
                var method = context.Request.HttpMethod ?? "GET";
                var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(path, "/bridge", StringComparison.OrdinalIgnoreCase))
                {
                    HandleBridgeAsync(context).GetAwaiter().GetResult();
                    return;
                }

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(context, 405, "Method not allowed", "text/plain");
                    return;
                }

                if (path == "/" || string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    WriteIndex(context);
                    return;
                }

                if (string.Equals(path, "/mock-bridge.js", StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(context, 200, MockBridgeScript.Script, "application/javascript");
                    return;
                }

                if (string.Equals(path, "/config/models.json", StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(context, 200, ScriptedDemoLlm.CatalogJson(), "application/json");
                    return;
                }

                WriteStatic(context, path);
            }
            catch (Exception ex)
            {
                WriteText(context, 500, ex.ToString(), "text/plain");
            }
        }

        private async Task HandleBridgeAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var packet = await _bridgeHost.HandleAsync(body).ConfigureAwait(false);
            WriteText(context, 200, JsonConvert.SerializeObject(packet), "application/json");
        }

        private void WriteIndex(HttpListenerContext context)
        {
            var indexPath = Path.Combine(_webRoot, "index.html");
            var html = File.ReadAllText(indexPath);
            html = html.Replace(
                "<script src=\"js/app-core.js",
                "<script src=\"/mock-bridge.js\"></script>\n  <script src=\"js/app-core.js");
            WriteText(context, 200, html, "text/html; charset=utf-8");
        }

        private void WriteStatic(HttpListenerContext context, string path)
        {
            var relative = Uri.UnescapeDataString((path ?? string.Empty).TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                Path.IsPathRooted(relative))
            {
                WriteText(context, 404, "Not found", "text/plain");
                return;
            }

            var file = Path.Combine(_webRoot, relative);
            if (!File.Exists(file))
            {
                WriteText(context, 404, "Not found", "text/plain");
                return;
            }

            var bytes = File.ReadAllBytes(file);
            context.Response.StatusCode = 200;
            context.Response.ContentType = ContentType(file);
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void WriteText(HttpListenerContext context, int statusCode, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static string ContentType(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".html":
                    return "text/html; charset=utf-8";
                case ".js":
                    return "application/javascript";
                case ".css":
                    return "text/css";
                case ".json":
                    return "application/json";
                case ".svg":
                    return "image/svg+xml";
                case ".png":
                    return "image/png";
                case ".woff":
                    return "font/woff";
                case ".woff2":
                    return "font/woff2";
                default:
                    return "application/octet-stream";
            }
        }
    }
}
