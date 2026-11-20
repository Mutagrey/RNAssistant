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

            OpenWithShell(path, "Не удалось открыть документ. Откройте файл вручную.");
        }

        public static void OpenFolder(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException("Папка недоступна.");
            }

            OpenWithShell(path, "Не удалось открыть папку.");
        }

        public static void OpenContainingFolder(string documentPath)
        {
            Uri location;
            if (!Uri.TryCreate(documentPath, UriKind.Absolute, out location) ||
                !location.IsFile || !Path.IsPathRooted(documentPath))
            {
                throw new InvalidOperationException("У документа нет локальной папки.");
            }

            OpenFolder(Path.GetDirectoryName(documentPath));
        }

        private static void OpenWithShell(string path, string errorMessage)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(errorMessage, ex);
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
