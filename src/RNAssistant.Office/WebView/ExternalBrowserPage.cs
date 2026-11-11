using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    // Owns one unprivileged browser. Placement belongs to ExternalBrowserDockHost.
    internal sealed class ExternalBrowserPage : IDisposable
    {
        private readonly string _profile = Path.Combine(Path.GetTempPath(), "RNAssistant", "BrowserPilot", Guid.NewGuid().ToString("N"));
        private readonly string _assets;
        private CoreWebView2Environment _environment;
        private bool _disposed;
        private long _revision;
        private ulong _navigationId;
        private bool _navigationRefused;
        private bool _canGoBack, _canGoForward;
        private string _runtime = "—";
        private string _url = ExternalBrowserPolicy.ProbeUrl, _title = "Внешняя страница", _message = "Запуск WebView2…";
        private ExternalBrowserStatus _status = ExternalBrowserStatus.Starting;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public WebView2 Control { get; }
        public event Action Changed;

        public ExternalBrowserPage(string assets)
        {
            _assets = assets;
            Control = new WebView2 { Visible = false, TabStop = true };
            Control.Enter += FocusChanged; Control.Leave += FocusChanged;
        }

        public async Task InitializeAsync()
        {
            // Hidden controls still need an HWND; waiting for Ready before creating it would deadlock startup.
            _ = Control.Handle;
            Directory.CreateDirectory(_profile);
            _environment = await CoreWebView2Environment.CreateAsync(null, _profile);
            _runtime = _environment.BrowserVersionString;
            _environment.BrowserProcessExited += BrowserExited;
            if (_disposed) { CleanupProfile(); throw new ObjectDisposedException(nameof(ExternalBrowserPage)); }
            if (!string.Equals(Path.GetFullPath(_environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_profile).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 переопределил временный профиль. Пилот не использует чужой профиль.");
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "ExternalBrowser"; options.IsInPrivateModeEnabled = true;
            await Control.EnsureCoreWebView2Async(_environment, options);
            if (_disposed) throw new ObjectDisposedException(nameof(ExternalBrowserPage));
            var core = Control.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.SourceChanged += SourceChanged;
            core.HistoryChanged += HistoryChanged;
            core.DocumentTitleChanged += TitleChanged;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
            core.SetVirtualHostNameToFolderMapping("rna-browser-probe.invalid", _assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate(_url);
        }

        public ExternalBrowserStateDto Snapshot(string placement)
        {
            return new ExternalBrowserStateDto
            {
                SessionId = SessionId, Revision = _revision, Status = _status, Url = _url, Title = _title,
                CanGoBack = !_disposed && _canGoBack, CanGoForward = !_disposed && _canGoForward,
                Message = _message,
                Diagnostic = "P5a · WinForms child · Runtime " + _runtime +
                    " · profile InPrivate · " + placement
            };
        }

        public void Execute(ExternalBrowserCommandPayload request)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed || Control.CoreWebView2 == null)
                throw new InvalidOperationException("Страница недоступна. Закройте вкладку и откройте её снова.");
            var core = Control.CoreWebView2;
            switch (request.Action)
            {
                case ExternalBrowserAction.Navigate: core.Navigate(ExternalBrowserPolicy.NavigationUrl(request.Url)); break;
                case ExternalBrowserAction.Back: if (core.CanGoBack) core.GoBack(); break;
                case ExternalBrowserAction.Forward: if (core.CanGoForward) core.GoForward(); break;
                case ExternalBrowserAction.Reload: core.Reload(); break;
                case ExternalBrowserAction.Focus: if (Control.Visible) Control.Focus(); break;
                default: throw new InvalidOperationException("Недопустимая команда страницы.");
            }
        }

        private void Publish() { if (!_disposed) { _revision++; Changed?.Invoke(); } }
        private void FocusChanged(object sender, EventArgs e) { Publish(); }
        public void Fail(Exception error)
        {
            if (_disposed) return;
            _status = ExternalBrowserStatus.Failed; Control.Visible = false;
            var message = error.GetBaseException().Message;
            _message = "WebView2: " + message.Substring(0, Math.Min(message.Length, 400)) + ". Закройте вкладку и откройте снова.";
            Publish();
        }
        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed) { e.Cancel = true; return; }
            _navigationId = e.NavigationId; _navigationRefused = false;
            try { ExternalBrowserPolicy.NavigationUrl(e.Uri); _message = "Загрузка…"; }
            catch (InvalidOperationException) { e.Cancel = true; _navigationRefused = true; _message = "Переход запрещён: поддерживаются только HTTP(S)-адреса без credentials."; }
            Publish();
        }
        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed) return;
            if (e.NavigationId != _navigationId || _navigationRefused) return;
            _status = ExternalBrowserStatus.Ready;
            _message = e.IsSuccess ? "Экспериментальный браузер · Windows-проверка открыта" : "Ошибка загрузки: " + e.WebErrorStatus;
            Publish();
        }
        private void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e) { _url = Control.CoreWebView2.Source; Publish(); }
        private void HistoryChanged(object sender, object e)
        { _canGoBack = Control.CoreWebView2.CanGoBack; _canGoForward = Control.CoreWebView2.CanGoForward; Publish(); }
        private void TitleChanged(object sender, object e)
        {
            var title = Control.CoreWebView2.DocumentTitle ?? "";
            _title = title.Substring(0, Math.Min(title.Length, 160)); Publish();
        }
        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        { e.Handled = true; _message = "Новое окно заблокировано: в пилоте одна страница."; Publish(); }
        private void DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        { e.Cancel = true; _message = "Загрузка файлов пока недоступна в пилоте."; Publish(); }
        private void PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        { e.State = CoreWebView2PermissionState.Deny; _message = "Доступ запрещён: " + e.PermissionKind; Publish(); }
        private void ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _status = ExternalBrowserStatus.Failed; Control.Visible = false;
            _message = "WebView2: " + e.ProcessFailedKind + ". Закройте вкладку и откройте снова; страница автоматически не повторяется.";
            Publish();
        }

        private void BrowserExited(object sender, CoreWebView2BrowserProcessExitedEventArgs e)
        {
            _environment.BrowserProcessExited -= BrowserExited;
            CleanupProfile();
        }
        private void CleanupProfile()
        {
            // Only this randomly created temporary directory; never the user's browser/profile.
            Task.Run(() => { try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } });
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _status = ExternalBrowserStatus.Closed; _revision++;
            Changed = null;
            Control.Enter -= FocusChanged; Control.Leave -= FocusChanged;
            try
            {
                var core = Control.CoreWebView2;
                if (core != null)
                {
                    core.NavigationStarting -= NavigationStarting; core.NavigationCompleted -= NavigationCompleted;
                    core.SourceChanged -= SourceChanged; core.HistoryChanged -= HistoryChanged; core.DocumentTitleChanged -= TitleChanged;
                    core.NewWindowRequested -= NewWindowRequested; core.DownloadStarting -= DownloadStarting;
                    core.PermissionRequested -= PermissionRequested; core.ProcessFailed -= ProcessFailed;
                }
            }
            catch (Exception error) { Diagnostics.RuntimeLog.Error("External browser event cleanup failed.", error); }
            finally { Control.Dispose(); CleanupProfile(); }
        }
    }
}
