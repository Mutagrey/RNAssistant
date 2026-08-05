using System;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class OfficeSnapshotReader
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public OfficeSnapshotReader(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
        }

        public OfficeSnapshot Read(AppSettings settings, string taskText)
        {
            var snapshot = new OfficeSnapshot
            {
                Host = _adapter.HostName,
                DocumentTitle = SafeRead(() => _adapter.DocumentTitle)
            };

            var contextProvider = _adapter as IOfficeContextProvider;
            if (contextProvider != null)
            {
                try
                {
                    var context = contextProvider.GetOfficeContext();
                    if (context != null)
                    {
                        snapshot.Host = AgentText.FirstNonEmpty(context.Host, snapshot.Host);
                        snapshot.DocumentTitle = AgentText.FirstNonEmpty(context.DocumentTitle, snapshot.DocumentTitle);
                        snapshot.ContainerName = context.ContainerName;
                        snapshot.SelectionAddress = context.SelectionAddress;
                        snapshot.SelectionText = context.SelectionText;
                    }
                }
                catch
                {
                }
            }

            var vba = ReadVba(settings, taskText);
            if (!string.IsNullOrWhiteSpace(vba))
            {
                snapshot.SnapshotText = "Current VBA project snapshot:\n" + vba;
            }
            return snapshot;
        }

        public static bool IsVbaTask(string text)
        {
            var value = text ?? string.Empty;
            return value.IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макрос", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макро", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("visual basic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string ReadVba(AppSettings settings, string taskText)
        {
            settings = settings ?? new AppSettings();
            if (!settings.IncludeVbaContext && !IsVbaTask(taskText))
            {
                return string.Empty;
            }

            try
            {
                return _adapter.GetVbaSnapshot(Math.Max(1000, settings.VbaContextCharLimit));
            }
            catch (Exception ex)
            {
                return "VBA project snapshot could not be read: " + ex.Message;
            }
        }

        private static string SafeRead(Func<string> read)
        {
            try
            {
                return read == null ? string.Empty : read() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
