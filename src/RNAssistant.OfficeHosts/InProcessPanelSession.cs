using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using RNAssistant.Office;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.OfficeHosts
{
    public enum OfficeHostKind
    {
        Unknown = 0,
        Excel = 1,
        Word = 2,
        PowerPoint = 3,
        Outlook = 4
    }

    public sealed class InProcessPanelSession : IDisposable
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private bool _disposed;

        private InProcessPanelSession(
            OfficeHostKind hostKind,
            long officeHwnd,
            string rootPath,
            IOfficeApplicationAdapter adapter,
            AssistantRuntime runtime,
            Control panelControl)
        {
            HostKind = hostKind;
            OfficeHwnd = officeHwnd;
            RootPath = rootPath;
            _adapter = adapter;
            Runtime = runtime;
            PanelControl = panelControl;
        }

        public OfficeHostKind HostKind { get; private set; }
        public long OfficeHwnd { get; private set; }
        public string RootPath { get; private set; }
        public AssistantRuntime Runtime { get; private set; }
        public Control PanelControl { get; private set; }

        public static InProcessPanelSession Create(int hostKind, long officeHwnd, string rootPath)
        {
            var kind = (OfficeHostKind)hostKind;
            var host = HostName(kind);
            if (officeHwnd == 0)
            {
                throw new ArgumentException("Office HWND is required.", "officeHwnd");
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("Portable root path is required.", "rootPath");
            }

            rootPath = Path.GetFullPath(rootPath);
            if (!Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException("Portable root path was not found: " + rootPath);
            }

            RuntimeLog.Configure(rootPath);
            RuntimeLog.Info("Creating in-process panel. Host=" + host + ", hwnd=" + officeHwnd + ", root=" + rootPath);

            var target = new OfficeTargetDescriptor
            {
                Host = host,
                Hwnd = officeHwnd,
                ProcessId = Process.GetCurrentProcess().Id
            };
            var adapter = new OfficeComAdapterProvider().Create(host, target);
            try
            {
                var runtime = new AssistantRuntime(adapter, rootPath);
                var control = runtime.CreatePaneControl();
                control.Dock = DockStyle.Fill;
                return new InProcessPanelSession(kind, officeHwnd, rootPath, adapter, runtime, control);
            }
            catch
            {
                var disposable = adapter as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RuntimeLog.Info("Disposing in-process panel session.");
            if (PanelControl != null)
            {
                PanelControl.Dispose();
                PanelControl = null;
            }

            var disposable = _adapter as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }

        private static string HostName(OfficeHostKind kind)
        {
            switch (kind)
            {
                case OfficeHostKind.Excel:
                    return "Excel";
                case OfficeHostKind.Word:
                    return "Word";
                case OfficeHostKind.PowerPoint:
                    return "PowerPoint";
                case OfficeHostKind.Outlook:
                    return "Outlook";
                default:
                    throw new ArgumentOutOfRangeException("kind", "Unsupported Office host kind: " + (int)kind);
            }
        }
    }
}
