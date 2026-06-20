using System;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Desktop
{
    internal sealed class DesktopActivation
    {
        public string Host { get; set; }
        public string Action { get; set; }
        public OfficeTargetDescriptor Target { get; set; }

        public static DesktopActivation Parse(string[] args)
        {
            var activation = new DesktopActivation { Target = new OfficeTargetDescriptor() };
            for (var i = 0; i < (args == null ? 0 : args.Length); i++)
            {
                var key = args[i] ?? string.Empty;
                var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
                if (string.Equals(key, "--host", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Host = value;
                    i++;
                }
                else if (string.Equals(key, "--action", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Action = value;
                    i++;
                }
                else if (string.Equals(key, "--target", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target = ParseTarget(value);
                    i++;
                }
                else if (string.Equals(key, "--target-base64", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target = OfficeTargetDescriptor.FromBase64Json(value);
                    i++;
                }
            }

            if (activation.Target == null)
            {
                activation.Target = new OfficeTargetDescriptor();
            }

            if (string.IsNullOrWhiteSpace(activation.Host))
            {
                activation.Host = activation.Target.Host;
            }

            if (string.IsNullOrWhiteSpace(activation.Action))
            {
                activation.Action = activation.Target.Action;
            }

            return activation;
        }

        private static OfficeTargetDescriptor ParseTarget(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new OfficeTargetDescriptor();
            }

            var trimmed = value.Trim();
            return trimmed.StartsWith("{", StringComparison.Ordinal)
                ? OfficeTargetDescriptor.FromJson(trimmed)
                : OfficeTargetDescriptor.FromBase64Json(trimmed);
        }
    }
}
