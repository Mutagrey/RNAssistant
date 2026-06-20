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
                else if (string.Equals(key, "--hwnd", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target.Hwnd = ParseInt64(value);
                    i++;
                }
                else if (string.Equals(key, "--pid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "--process-id", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target.ProcessId = ParseInt32(value);
                    i++;
                }
                else if (string.Equals(key, "--document-path", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target.FullName = value;
                    activation.Target.Path = value;
                    i++;
                }
                else if (string.Equals(key, "--document-title", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target.Name = value;
                    i++;
                }
                else if (string.Equals(key, "--selection", StringComparison.OrdinalIgnoreCase))
                {
                    activation.Target.Selection = value;
                    i++;
                }
                else if (string.Equals(key, "--target", StringComparison.OrdinalIgnoreCase))
                {
                    MergeTarget(activation.Target, ParseTarget(value));
                    i++;
                }
                else if (string.Equals(key, "--target-base64", StringComparison.OrdinalIgnoreCase))
                {
                    MergeTarget(activation.Target, OfficeTargetDescriptor.FromBase64Json(value));
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

            if (string.IsNullOrWhiteSpace(activation.Target.Host))
            {
                activation.Target.Host = activation.Host;
            }

            return activation;
        }

        private static int ParseInt32(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static long ParseInt64(string value)
        {
            long result;
            return long.TryParse(value, out result) ? result : 0;
        }

        private static void MergeTarget(OfficeTargetDescriptor target, OfficeTargetDescriptor source)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(source.Host)) target.Host = source.Host;
            if (!string.IsNullOrWhiteSpace(source.FullName)) target.FullName = source.FullName;
            if (!string.IsNullOrWhiteSpace(source.Path)) target.Path = source.Path;
            if (!string.IsNullOrWhiteSpace(source.Name)) target.Name = source.Name;
            if (!string.IsNullOrWhiteSpace(source.DocumentKey)) target.DocumentKey = source.DocumentKey;
            if (!string.IsNullOrWhiteSpace(source.EntryId)) target.EntryId = source.EntryId;
            if (!string.IsNullOrWhiteSpace(source.FolderPath)) target.FolderPath = source.FolderPath;
            if (!string.IsNullOrWhiteSpace(source.Selection)) target.Selection = source.Selection;
            if (!string.IsNullOrWhiteSpace(source.Action)) target.Action = source.Action;
            if (source.Hwnd != 0) target.Hwnd = source.Hwnd;
            if (source.ProcessId != 0) target.ProcessId = source.ProcessId;
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
