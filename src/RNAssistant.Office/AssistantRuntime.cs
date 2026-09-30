using System;
using System.IO;
using System.Threading.Tasks;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.WebView;

namespace RNAssistant.Office
{
    public sealed class AssistantRuntime : IDisposable
    {
        private IOfficeApplicationAdapter _adapter;
        private bool _ownsAdapter;
        private AssistantPaneControl _paneControl;
        private bool _disposed;

        public AssistantRuntime(IOfficeApplicationAdapter adapter)
            : this(adapter, null)
        {
        }

        public AssistantRuntime(IOfficeApplicationAdapter adapter, string rootPath)
        {
            _adapter = adapter;
            RootPath = rootPath;
            Controller = new AssistantController(adapter);
        }

        public AssistantController Controller { get; private set; }
        public Task ShutdownCompletion { get; private set; } = Task.FromResult(true);
        public string RootPath { get; private set; }
        public Action<string, string, bool> MailboxNavigationRequested { get; set; }
        public Func<string, Task<OfficeHostChatResponse>> OfficeHostChatRequested { get; set; }
        public Func<string, Task<OfficeHostChatResponse>> OfficeChatSelectionRequested { get; set; }
        public event Action<AssistantController, AssistantController> ControllerChanged;

        public async Task<InitResponse> SwitchToAdapterAsync(
            IOfficeApplicationAdapter adapter, string chatId, string expectedHost, string expectedDocumentKey)
        {
            if (adapter == null) throw new ArgumentNullException("adapter");
            AssistantController nextController = null;
            try
            {
                ThrowIfDisposed();
                if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("A chat id is required.", "chatId");
                Controller.EnsureHostSwitchReady();
                nextController = new AssistantController(adapter);
                if (!string.Equals(nextController.HostName, expectedHost, StringComparison.Ordinal) ||
                    !string.Equals(nextController.DocumentKey, expectedDocumentKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("Office target изменился до переключения панели.");
                var selected = nextController.SelectChat(chatId);
                if (!string.Equals(selected.ActiveChatId, chatId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Созданный чат не выбран в целевом документе.");
                var init = nextController.Initialize();
                if (!string.Equals(init.Host, expectedHost, StringComparison.Ordinal) ||
                    !string.Equals(init.DocumentKey, expectedDocumentKey, StringComparison.Ordinal) ||
                    !string.Equals(init.ActiveChatId, chatId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Привязка панели к созданному чату не подтверждена.");
                init.OfficeHostChatAvailable = OfficeHostChatRequested != null;
                if (_paneControl == null || _paneControl.IsDisposed)
                    throw new InvalidOperationException("Панель RN Assistant недоступна для переключения.");
                Controller.EnsureHostSwitchReady();
                await _paneControl.RebindControllerAsync(nextController).ConfigureAwait(false);
                ThrowIfDisposed();
                var previousController = Controller;
                var previousAdapter = _adapter;
                var ownedPreviousAdapter = _ownsAdapter;
                Controller = nextController;
                _adapter = adapter;
                _ownsAdapter = true;
                var changed = ControllerChanged;
                if (changed != null)
                {
                    try { changed(previousController, nextController); }
                    catch (Exception ex) { RuntimeLog.Error("Controller change notification failed.", ex); }
                }
                try { previousController.Dispose(); }
                catch (Exception ex) { RuntimeLog.Error("Previous Office controller cleanup failed.", ex); }
                if (ownedPreviousAdapter)
                {
                    var disposable = previousAdapter as IDisposable;
                    if (disposable != null)
                    {
                        try { disposable.Dispose(); }
                        catch (Exception ex) { RuntimeLog.Error("Previous Office adapter cleanup failed.", ex); }
                    }
                }
                return init;
            }
            catch
            {
                if (!object.ReferenceEquals(Controller, nextController))
                {
                    var disposable = adapter as IDisposable;
                    try { if (nextController != null) nextController.Dispose(); }
                    finally { if (disposable != null) disposable.Dispose(); }
                }
                throw;
            }
        }

        public AssistantPaneControl CreatePaneControl()
        {
            ThrowIfDisposed();
            if (_paneControl != null && !_paneControl.IsDisposed)
            {
                return _paneControl;
            }

            _paneControl = new AssistantPaneControl(Controller, ResolveWebRoot(RootPath));
            _paneControl.MailboxNavigationRequested = MailboxNavigationRequested;
            _paneControl.OfficeHostChatRequested = OfficeHostChatRequested;
            _paneControl.OfficeChatSelectionRequested = OfficeChatSelectionRequested;
            return _paneControl;
        }

        public void RunQuickAction(string action)
        {
            ThrowIfDisposed();
            Controller.QueueQuickAction(action);
            if (_paneControl != null)
            {
                _paneControl.RunQuickAction(action);
            }
        }

        public void BlurComposer()
        {
            if (_paneControl != null)
            {
                _paneControl.BlurComposer();
            }
        }

        public void ReleaseKeyboardFocusToHost()
        {
            if (_paneControl != null)
            {
                _paneControl.ReleaseKeyboardFocusToHost(_adapter.PrepareForContextCapture);
                return;
            }

            _adapter.PrepareForContextCapture();
        }

        public void AddSelectionContext(string mode)
        {
            ThrowIfDisposed();
            Controller.AddSelectionContext(mode);
            if (_paneControl != null)
            {
                _paneControl.RefreshContext();
            }
        }

        public void RefreshState()
        {
            if (_paneControl != null)
            {
                _paneControl.RefreshState();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var pane = _paneControl;
            _paneControl = null;
            var controller = Controller;
            var adapter = _adapter;
            var ownsAdapter = _ownsAdapter;
            try
            {
                if (pane != null) pane.Dispose();
            }
            finally
            {
                var drained = pane == null ? Task.FromResult(true) : pane.RequestsDrained;
                if (drained.IsCompleted)
                    ReleaseRuntimeResources(controller, adapter, ownsAdapter);
                else
                    ShutdownCompletion = drained.ContinueWith(
                        ignored => ReleaseRuntimeResources(controller, adapter, ownsAdapter),
                        TaskScheduler.Default);
            }
        }

        private static void ReleaseRuntimeResources(
            AssistantController controller, IOfficeApplicationAdapter adapter, bool ownsAdapter)
        {
            try { controller.Dispose(); }
            catch (Exception ex) { RuntimeLog.Error("Office controller cleanup failed.", ex); }
            if (!ownsAdapter) return;
            var disposable = adapter as IDisposable;
            if (disposable == null) return;
            try { disposable.Dispose(); }
            catch (Exception ex) { RuntimeLog.Error("Office adapter cleanup failed.", ex); }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("AssistantRuntime");
            }
        }

        private static string ResolveWebRoot(string rootPath)
        {
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                return Path.Combine(Path.GetFullPath(rootPath), "web");
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var webRoot = Path.Combine(baseDirectory, "web");
            return Directory.Exists(webRoot) ? webRoot : Path.Combine(baseDirectory, "..", "..", "web");
        }
    }
}
