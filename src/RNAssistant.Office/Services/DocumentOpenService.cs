using System;
using System.Diagnostics;
using System.IO;

namespace RNAssistant.Office.Services
{
    public static class DocumentOpenService
    {
        public static void Open(string path)
        {
            if (!IsAvailable(path))
            {
                throw new InvalidOperationException("Путь к документу недоступен. Откройте файл вручную.");
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Не удалось открыть документ. Откройте файл вручную.", ex);
            }
        }

        public static bool IsAvailable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            Uri uri;
            if (Uri.TryCreate(path, UriKind.Absolute, out uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return File.Exists(path);
        }

        public static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            Uri leftUri;
            Uri rightUri;
            if (Uri.TryCreate(left.Trim(), UriKind.Absolute, out leftUri) &&
                Uri.TryCreate(right.Trim(), UriKind.Absolute, out rightUri) &&
                (string.Equals(leftUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(leftUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(rightUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rightUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return Uri.Compare(
                    leftUri,
                    rightUri,
                    UriComponents.HttpRequestUrl,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;
            }

            return string.Equals(NormalizeFilePath(left), NormalizeFilePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFilePath(string path)
        {
            return (path ?? string.Empty).Trim().TrimEnd('\\', '/').Replace('/', '\\');
        }
    }
}
