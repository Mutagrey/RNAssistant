using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using RNAssistant.Core.Models;
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
        private readonly OfficeUiDispatcher _officeDispatcher;
        private bool _screenCaptureProtectionEnabled;
        private bool _disposed;

        private InProcessPanelSession(
            OfficeHostKind hostKind,
            long officeHwnd,
            string rootPath,
            IOfficeApplicationAdapter adapter,
            OfficeUiDispatcher officeDispatcher,
            AssistantRuntime runtime,
            Control panelControl,
            bool screenCaptureProtectionEnabled)
        {
            HostKind = hostKind;
            OfficeHwnd = officeHwnd;
            RootPath = rootPath;
            _adapter = adapter;
            _officeDispatcher = officeDispatcher;
            Runtime = runtime;
            PanelControl = panelControl;
            _screenCaptureProtectionEnabled = screenCaptureProtectionEnabled;
            Runtime.Controller.SettingsChanged += OnSettingsChanged;
        }

        public OfficeHostKind HostKind { get; private set; }
        public long OfficeHwnd { get; private set; }
        public string RootPath { get; private set; }
        public AssistantRuntime Runtime { get; private set; }
        public Control PanelControl { get; private set; }
        public bool ScreenCaptureProtectionEnabled
        {
            get { return _screenCaptureProtectionEnabled; }
        }

        public event Action<bool> ScreenCaptureProtectionChanged;

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
            IOfficeApplicationAdapter innerAdapter = null;
            OfficeUiDispatcher officeDispatcher = null;
            AssistantRuntime runtime = null;
            try
            {
                // Agent continuations run on worker threads; all in-process Office COM calls
                // must return to the UI STA to preserve COM and document identity.
                officeDispatcher = new OfficeUiDispatcher();
                innerAdapter = new OfficeComAdapterProvider().Create(host, target);
                var adapter = new UiThreadOfficeApplicationAdapter(innerAdapter, officeDispatcher);
                runtime = new AssistantRuntime(adapter, rootPath);
                var control = runtime.CreatePaneControl();
                control.Dock = DockStyle.Fill;
                var screenCaptureProtectionEnabled = runtime.Controller.GetSettings().Settings.ScreenCaptureProtectionEnabled;
                return new InProcessPanelSession(
                    kind,
                    officeHwnd,
                    rootPath,
                    innerAdapter,
                    officeDispatcher,
                    runtime,
                    control,
                    screenCaptureProtectionEnabled);
            }
            catch
            {
                try
                {
                    if (runtime != null)
                    {
                        runtime.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        var disposable = innerAdapter as IDisposable;
                        if (disposable != null)
                        {
                            disposable.Dispose();
                        }
                    }
                    finally
                    {
                        if (officeDispatcher != null)
                        {
                            officeDispatcher.Dispose();
                        }
                    }
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
            try
            {
                if (Runtime != null)
                {
                    Runtime.Controller.SettingsChanged -= OnSettingsChanged;
                    Runtime.Dispose();
                    Runtime = null;
                }
                if (PanelControl != null)
                {
                    PanelControl.Dispose();
                    PanelControl = null;
                }
            }
            finally
            {
                try
                {
                    var disposable = _adapter as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
                finally
                {
                    if (_officeDispatcher != null)
                    {
                        _officeDispatcher.Dispose();
                    }
                }
            }
        }

        private void OnSettingsChanged(AppSettings settings)
        {
            var enabled = settings == null || settings.ScreenCaptureProtectionEnabled;
            if (_screenCaptureProtectionEnabled == enabled)
            {
                return;
            }

            _screenCaptureProtectionEnabled = enabled;
            var changed = ScreenCaptureProtectionChanged;
            if (changed != null)
            {
                changed(enabled);
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
