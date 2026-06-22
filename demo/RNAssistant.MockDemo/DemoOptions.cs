using System;
using System.IO;

namespace RNAssistant.MockDemo
{
    internal sealed class DemoOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string DataRoot { get; set; }
        public bool Reset { get; set; }
        public bool SelfTest { get; set; }

        public DemoOptions()
        {
            Host = "Excel";
            Port = 5179;
            DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rnassistant-mock-demo");
        }

        public static DemoOptions Parse(string[] args)
        {
            var options = new DemoOptions();
            for (var i = 0; i < (args == null ? 0 : args.Length); i++)
            {
                var arg = args[i] ?? string.Empty;
                if (string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Host = NormalizeHost(args[++i]);
                }
                else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int port;
                    if (int.TryParse(args[++i], out port) && port > 0)
                    {
                        options.Port = port;
                    }
                }
                else if (string.Equals(arg, "--data-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.DataRoot = ExpandHome(args[++i]);
                }
                else if (string.Equals(arg, "--reset", StringComparison.OrdinalIgnoreCase))
                {
                    options.Reset = true;
                }
                else if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    options.SelfTest = true;
                }
            }

            return options;
        }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port; }
        }

        private static string NormalizeHost(string value)
        {
            if (string.Equals(value, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "PowerPoint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(value, "PowerPoint", StringComparison.OrdinalIgnoreCase))
                {
                    return "PowerPoint";
                }

                return string.Equals(value, "Outlook", StringComparison.OrdinalIgnoreCase) ? "Outlook" : "Word";
            }

            return "Excel";
        }

        private static string ExpandHome(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("~", StringComparison.Ordinal))
            {
                return value;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }
}
