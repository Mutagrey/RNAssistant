using System;

namespace RNAssistant.Application
{
    // Applied as response headers to every document AND worker script. CSP alone
    // does not govern WebRTC/speculative connections; Connection-Allowlist does.
    public static class BrowserNetworkPolicy
    {
        public const int MinimumChromiumMajor = 152;
        public const string ConnectionAllowlist = "(response-origin)";
        public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; " +
            "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; " +
            "connect-src 'self'; worker-src 'self' blob:; frame-src 'self' blob: data:; object-src 'none'; base-uri 'self'; form-action 'self'";
        public const string BrowserArguments = "--host-resolver-rules=\"MAP * ~NOTFOUND\" --disable-quic " +
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --disable-background-networking --disable-features=NetworkPrediction";
        public static void RequireSupportedRuntime(string version)
        {
            Version parsed;
            if (!Version.TryParse((version ?? "").Split(' ')[0], out parsed) || parsed.Major < MinimumChromiumMajor)
                throw new InvalidOperationException("Для интерактивных локальных страниц обновите WebView2 до версии 152 или новее.");
        }
        public static string Headers(string mime)
        {
            if (string.IsNullOrEmpty(mime) || mime.Contains("\r") || mime.Contains("\n")) throw new ArgumentException("Invalid content type.");
            return "Content-Type: " + mime + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                "X-DNS-Prefetch-Control: off\r\nConnection-Allowlist: " + ConnectionAllowlist + "\r\nContent-Security-Policy: " + ContentSecurityPolicy + "\r\n";
        }
    }
}
