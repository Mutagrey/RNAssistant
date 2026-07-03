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
    }
}
