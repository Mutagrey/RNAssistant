using System;
using System.IO;

namespace RNAssistant.Office.WebView
{
    internal static class WebViewSecurityPolicy
    {
        public static string TrustedDocumentUri(string indexPath)
        {
            if (string.IsNullOrWhiteSpace(indexPath)) return string.Empty;
            return new Uri(Path.GetFullPath(indexPath)).AbsoluteUri;
        }

        public static bool IsTrustedDocument(string candidate, string trustedDocumentUri)
        {
            Uri candidateUri;
            Uri trustedUri;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out candidateUri) ||
                !Uri.TryCreate(trustedDocumentUri, UriKind.Absolute, out trustedUri))
            {
                return false;
            }

            var comparison = trustedUri.IsFile
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return Uri.Compare(
                candidateUri,
                trustedUri,
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.SafeUnescaped,
                comparison) == 0;
        }

        public static bool CanNavigateTopLevel(string candidate, string trustedDocumentUri)
        {
            return IsAbout(candidate, "blank") || IsTrustedDocument(candidate, trustedDocumentUri);
        }

        public static bool CanNavigateFrame(string candidate)
        {
            return IsAbout(candidate, "blank") || IsAbout(candidate, "srcdoc");
        }

        public static bool CanOpenExternally(string candidate)
        {
            Uri uri;
            return Uri.TryCreate(candidate, UriKind.Absolute, out uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAbout(string candidate, string path)
        {
            Uri uri;
            return Uri.TryCreate(candidate, UriKind.Absolute, out uri) &&
                string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uri.GetComponents(UriComponents.Path, UriFormat.Unescaped), path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
