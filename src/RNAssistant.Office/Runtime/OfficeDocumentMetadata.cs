namespace RNAssistant.Office
{
    // Owner-STA metadata for chat navigation after the bound document closes.
    // These cached labels never establish liveness or execution authority.
    internal sealed class OfficeDocumentMetadata
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly IOfficeDocumentSession _session;
        private readonly string _host;
        private readonly string _runtimeKey;
        private string _documentKey;
        private string _title;

        internal OfficeDocumentMetadata(IOfficeApplicationAdapter adapter, IOfficeDocumentSession session)
        {
            _adapter = adapter;
            _session = session;
            _host = adapter.HostName;
            _runtimeKey = adapter.RuntimeDocumentKey;
            _documentKey = adapter.DocumentKey;
            _title = adapter.DocumentTitle;
        }

        internal string HostName { get { return _session == null ? _adapter.HostName : _host; } }
        internal string RuntimeDocumentKey { get { return _session == null ? _adapter.RuntimeDocumentKey : _runtimeKey; } }

        internal string DocumentKey
        {
            get
            {
                if (_session == null || _session.IsAlive) _documentKey = _adapter.DocumentKey;
                return _documentKey;
            }
        }

        internal string DocumentTitle
        {
            get
            {
                if (_session == null || _session.IsAlive) _title = _adapter.DocumentTitle;
                return _title;
            }
        }
    }
}
