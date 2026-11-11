using System;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    public static class ExternalBrowserPolicy
    {
        public const string ProbeUrl = "https://rna-browser-probe.invalid/index.html";

        public static string NavigationUrl(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Введите HTTP(S)-адрес без имени пользователя и пароля.");
            return uri.AbsoluteUri;
        }

        public static bool ValidBounds(ExternalBrowserBoundsDto bounds)
        {
            if (bounds == null) return false;
            if (!bounds.Visible) return true;
            var values = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height, bounds.ViewportWidth, bounds.ViewportHeight };
            foreach (var value in values)
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 32768) return false;
            return bounds.Width >= 1 && bounds.Height >= 1 && bounds.ViewportWidth >= 1 && bounds.ViewportHeight >= 1 &&
                bounds.X + bounds.Width <= bounds.ViewportWidth + 1 && bounds.Y + bounds.Height <= bounds.ViewportHeight + 1;
        }
    }
}
