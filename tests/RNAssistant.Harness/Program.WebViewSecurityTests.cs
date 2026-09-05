using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WebViewSecurityRestrictsMessagesAndNavigation()
        {
            const string trusted = "file:///C:/RNAssistant/web/index.html";

            AssertTrue(WebViewSecurityPolicy.IsTrustedDocument(trusted + "#chat", trusted), "trusted source with fragment");
            AssertTrue(!WebViewSecurityPolicy.IsTrustedDocument("https://example.com/index.html", trusted), "external message source rejected");
            AssertTrue(WebViewSecurityPolicy.CanNavigateTopLevel(trusted, trusted), "app navigation");
            AssertTrue(!WebViewSecurityPolicy.CanNavigateTopLevel("https://example.com", trusted), "external top navigation rejected");
            AssertTrue(WebViewSecurityPolicy.CanNavigateFrame("about:srcdoc#preview"), "srcdoc frame navigation");
            AssertTrue(!WebViewSecurityPolicy.CanNavigateFrame("https://example.com/frame"), "external frame navigation rejected");
            AssertTrue(WebViewSecurityPolicy.CanOpenExternally("https://example.com"), "https external link");
            AssertTrue(!WebViewSecurityPolicy.CanOpenExternally("file:///C:/secret.txt"), "file external link rejected");

            var cancellations = new BridgeRequestCancellationRegistry();
            var source = cancellations.Create("request-1", "editMessage");
            AssertTrue(source != null, "long bridge request tracked");
            AssertTrue(cancellations.Cancel("request-1"), "tracked bridge request cancelled");
            AssertTrue(source.IsCancellationRequested, "bridge cancellation token signalled");
            cancellations.Release("request-1", source);
            AssertTrue(!cancellations.Cancel("request-1"), "released bridge request removed");

            foreach (var type in new[] { "beginChatResourceUpload", "completeChatResourceUpload", "exportChatTrajectory", "getChatEventPayload" })
            {
                var uploadSource = cancellations.Create(type, type);
                AssertTrue(uploadSource != null && cancellations.Cancel(type) && uploadSource.IsCancellationRequested,
                    "upload control lifecycle participates in bridge cancellation");
                cancellations.Release(type, uploadSource);
            }

            var shutdownSource = cancellations.Create("request-2", "sendChat");
            cancellations.Dispose();
            AssertTrue(shutdownSource.IsCancellationRequested, "bridge disposal cancels active requests");
            cancellations.Release("request-2", shutdownSource);
        }
    }
}
