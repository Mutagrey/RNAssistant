using System.Reflection;

namespace RNAssistant.Office.Services
{
    public static class ApplicationVersionService
    {
        private static readonly string CurrentVersion = ResolveCurrentVersion();

        public static string Current
        {
            get { return CurrentVersion; }
        }

        private static string ResolveCurrentVersion()
        {
            var assembly = typeof(ApplicationVersionService).Assembly;
            var attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (attributes.Length > 0)
            {
                var informational = ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    return informational.Trim();
                }
            }

            var version = assembly.GetName().Version;
            if (version == null)
            {
                return "0.0.0";
            }

            return string.Format(
                "{0}.{1}.{2}",
                version.Major,
                version.Minor,
                version.Build < 0 ? 0 : version.Build);
        }
    }
}
