using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.WebView
{
    // The Harness runs cross-platform and does not host WebView2. Production
    // compilation uses JsWebViewExecutor.cs from RNAssistant.Office.
    internal static class JsWebViewExecutor
    {
        internal static Task<string> ExecuteAsync(string code, JObject args, string[] names,
            Func<JObject, CancellationToken, JObject> resourceRequest, CancellationToken cancellationToken)
        {
            return Task.FromException<string>(new NotSupportedException(
                "JavaScript execution requires the Windows WebView2 host."));
        }
    }
}
