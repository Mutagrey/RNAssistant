using System;
using System.Collections.Generic;
using System.Threading;

namespace RNAssistant.Office.WebView
{
    internal sealed class BridgeRequestCancellationRegistry : IDisposable
    {
        private static readonly HashSet<string> CancellableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sendChat",
            "runTool",
            "runVbaMacro",
            "htmlFetch",
            "resourceDataOpen",
            "beginChatResourceUpload",
            "completeChatResourceUpload",
            "exportChatTrajectory",
            "getChatEventPayload",
            "getVbaModule",
            "readSkillSource",
            "readToolSource",
            "readPromptSource",
            "beginPromptMutationUpload",
            "beginHtmlWorkspaceMutationUpload",
            "readHtmlWorkspaceSource",
            "saveHtmlWorkspaceFile",
            "saveHtmlWorkspaceData",
            "saveSettings",
            "getToolDocumentation",
            "beginSkillMutationUpload",
            "beginToolMutationUpload",
            "saveTools",
            "saveSkills",
            "saveSkillReference",
            "beginVbaModuleUpload",
            "saveVbaModule",
            "createVbaModule",
            "readArtifactViewerPage",
            "readArtifactImage",
            "readArtifactImageThumbnail",
            "readArtifactPdfPage",
            "readArtifactPdfThumbnail",
            "compactChatContext",
            "editMessage",
            "confirmAgentTool",
            "testModelConnection",
            "testModelCompatibility"
        };

        private readonly object _sync = new object();
        private readonly Dictionary<string, CancellationTokenSource> _sources =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public CancellationTokenSource Create(string requestId, string requestType)
        {
            if (string.IsNullOrWhiteSpace(requestId) || !CancellableTypes.Contains(requestType ?? string.Empty))
            {
                return null;
            }

            var source = new CancellationTokenSource();
            lock (_sync)
            {
                if (_disposed)
                {
                    source.Dispose();
                    throw new ObjectDisposedException("BridgeRequestCancellationRegistry");
                }
                if (_sources.ContainsKey(requestId))
                {
                    source.Dispose();
                    throw new InvalidOperationException("Duplicate WebView bridge request id.");
                }
                _sources[requestId] = source;
            }
            return source;
        }

        public bool Cancel(string requestId)
        {
            CancellationTokenSource source;
            lock (_sync)
            {
                _sources.TryGetValue(requestId ?? string.Empty, out source);
            }

            try
            {
                if (source == null) return false;
                source.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Release(string requestId, CancellationTokenSource source)
        {
            if (source == null) return;
            lock (_sync)
            {
                CancellationTokenSource current;
                if (_sources.TryGetValue(requestId ?? string.Empty, out current) && ReferenceEquals(current, source))
                {
                    _sources.Remove(requestId ?? string.Empty);
                }
            }
            source.Dispose();
        }

        public void ThrowIfDisposed()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("BridgeRequestCancellationRegistry");
            }
        }

        public void Dispose()
        {
            List<CancellationTokenSource> sources;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                sources = new List<CancellationTokenSource>(_sources.Values);
                _sources.Clear();
            }

            foreach (var source in sources)
            {
                try
                {
                    if (!source.IsCancellationRequested) source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
