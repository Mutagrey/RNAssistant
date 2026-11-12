using System;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    public static class ExternalBrowserPolicy
    {
        public const string ProbeUrl = "https://rna-browser-probe.invalid/index.html";

        internal static string SafeActionCode(string code)
        {
            switch (code)
            {
                case "browser_user_control": case "browser_site_denied": case "browser_navigation_changed":
                case "browser_effect_unknown": case "browser_observation_stale":
                case "browser_action_invalid": case "browser_observation_incomplete": case "browser_element_ambiguous":
                case "browser_element_stale": case "browser_element_not_interactable": case "browser_verification_already_present":
                case "browser_preparation_stale": case "browser_verification_failed": case "browser_verified": return code;
                default: return "browser_action_failed";
            }
        }

        public static void RequireAccess(string url, Func<Uri, bool> allowed)
        {
            var uri = new Uri(NavigationUrl(url));
            if (uri.AbsoluteUri == ProbeUrl) return; // Packaged fixture, not a network grant.
            if (allowed == null || !allowed(uri))
                throw ExternalBrowserResourceProvider.Error("Сайт запрещён политикой Browser. Требуется подписанный файл доступа от разработчика.", "browser_site_denied");
        }

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
