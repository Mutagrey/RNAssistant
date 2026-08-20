using System;

namespace RNAssistant.Office.Services
{
    internal sealed class OfficeSnapshotReader
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public OfficeSnapshotReader(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
        }

        public OfficeSnapshot Read()
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

            return snapshot;
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
