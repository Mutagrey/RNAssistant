using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class ThreadRecordingOfficeAdapter : IOfficeApplicationAdapter
        {
            private readonly IOfficeApplicationAdapter _inner;
            private readonly Action _record;

            public ThreadRecordingOfficeAdapter(IOfficeApplicationAdapter inner, Action record)
            {
                _inner = inner;
                _record = record;
            }

            public string HostName { get { _record(); return _inner.HostName; } }
            public string DocumentKey { get { _record(); return _inner.DocumentKey; } }
            public string LegacyDocumentKey { get { _record(); return _inner.LegacyDocumentKey; } }
            public string RuntimeDocumentKey { get { _record(); return _inner.RuntimeDocumentKey; } }
            public string DocumentTitle { get { _record(); return _inner.DocumentTitle; } }

            public string GetDocumentSnapshot(int maxChars)
            {
                _record();
                return _inner.GetDocumentSnapshot(maxChars);
            }

            public string GetVbaSnapshot(int maxChars)
            {
                _record();
                return _inner.GetVbaSnapshot(maxChars);
            }

            public void PrepareForContextCapture()
            {
                _record();
                _inner.PrepareForContextCapture();
            }

            public ContextNote CaptureSelectionContext(string mode, int maxChars)
            {
                _record();
                return _inner.CaptureSelectionContext(mode, maxChars);
            }

            public IEnumerable<ToolDefinition> GetBuiltInTools()
            {
                _record();
                return _inner.GetBuiltInTools();
            }

            public ToolResult ExecuteTool(ToolCommand command)
            {
                _record();
                return _inner.ExecuteTool(command);
            }
        }
    }
}
