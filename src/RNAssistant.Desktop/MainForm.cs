using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Desktop
{
    internal sealed class MainForm : Form
    {
        private readonly OfficeComAdapterProvider _adapterProvider;
        private readonly OfficeTargetRegistry _targetRegistry;
        private readonly TargetSelectionBar _targetBar;
        private readonly Panel _content;
        private readonly Timer _autoFollowTimer;
        private Label _placeholder;
        private AssistantRuntime _runtime;
        private IDisposable _currentAdapter;
        private Task _pendingRuntimeShutdown = Task.FromResult(true);
        private Rectangle _restoreBounds;
        private FormWindowState _restoreWindowState;
        private bool _fullScreen;
        private bool _restoringFullScreen;
        private bool _applyWidthOnRestore;
        private int _desktopWindowWidth = AppSettings.DefaultDesktopWindowWidth;
        private volatile OpenOfficeDocumentDto[] _openMailboxDocuments = new OpenOfficeDocumentDto[0];
        private DateTime _nextMailboxRefreshUtc;
        private string _pendingLaunchHost;
        private string _emptyLaunchHost;

        public MainForm()
        {
            _adapterProvider = new OfficeComAdapterProvider();
            _targetRegistry = new OfficeTargetRegistry();
            _targetBar = new TargetSelectionBar();
            Text = "RN Assistant";
            try
            {
                _desktopWindowWidth = new SettingsService(AppDataPaths.CreateDefault()).Load().DesktopWindowWidth;
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Could not load Desktop window width.", ex);
            }
            Width = Math.Min(_desktopWindowWidth, Screen.PrimaryScreen.WorkingArea.Width);
            Height = 820;
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            _content = new Panel { Dock = DockStyle.Fill };
            _autoFollowTimer = new Timer { Interval = 750 };
            _autoFollowTimer.Tick += delegate
            {
                if (DateTime.UtcNow >= _nextMailboxRefreshUtc)
                {
                    _nextMailboxRefreshUtc = DateTime.UtcNow.AddMinutes(1);
                    RefreshMailboxTargets();
                }
                if (_pendingLaunchHost != null ||
                    _targetRegistry.Mode != TargetSelectionMode.AutoFollow || ContainsFocus)
                {
                    return;
                }
                try
                {
                    var activation = ForegroundOfficeDetector.Detect();
                    if (activation != null && !string.IsNullOrWhiteSpace(activation.Host))
                    {
                        if (string.Equals(activation.Host, _emptyLaunchHost, StringComparison.OrdinalIgnoreCase))
                            return;
                        _emptyLaunchHost = null;
                        ApplyActivation(activation, false);
                    }
                }
                catch
                {
                }
            };
            _autoFollowTimer.Start();
            Shown += delegate
            {
                ApplyDesktopWindowWidth(_desktopWindowWidth);
                RefreshMailboxTargets();
            };
            Controls.Add(_content);
            Controls.Add(_targetBar);
            _targetBar.UseActiveRequested += AttachForegroundOffice;
            _targetBar.RefreshRequested += RefreshOpenTargets;
            _targetBar.HostFilterChanged += delegate { RefreshTargetUi(null); };
            _targetBar.TargetSelected += SelectTarget;
            _targetBar.ModeChanged += SetTargetMode;
            ShowPlaceholder("No Office attached.", true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_applyWidthOnRestore && !_fullScreen && !_restoringFullScreen &&
                WindowState == FormWindowState.Normal)
                ApplyDesktopWindowWidth(_desktopWindowWidth);
        }

        private void ToggleFullScreen()
        {
            if (_fullScreen)
            {
                _fullScreen = false;
                _restoringFullScreen = true;
                try
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                    WindowState = FormWindowState.Normal;
                    Bounds = _restoreBounds;
                    WindowState = _restoreWindowState;
                }
                finally
                {
                    _restoringFullScreen = false;
                }
                if (_applyWidthOnRestore && WindowState == FormWindowState.Normal)
                    ApplyDesktopWindowWidth(_desktopWindowWidth);
                return;
            }

            _restoreBounds = Bounds;
            _restoreWindowState = WindowState;
            _fullScreen = true;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }

        public void ApplyActivation(string[] args)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ApplyActivation(args)));
                return;
            }

            var activation = DesktopActivation.Parse(args);
            ApplyActivation(activation, false);
        }

        private void ApplyActivation(DesktopActivation activation, bool forceSelect)
        {
            if (activation == null || string.IsNullOrWhiteSpace(activation.Host))
            {
                ShowPlaceholder("No Office attached.", true);
                return;
            }

            var entry = _targetRegistry.Upsert(activation.Target);
            var selected = _targetRegistry.SelectedTarget;
            if (!forceSelect && string.IsNullOrWhiteSpace(activation.Action) &&
                selected != null && selected.Target != null &&
                !string.IsNullOrWhiteSpace(selected.Target.StoreId) &&
                string.Equals(activation.Host, "Outlook", StringComparison.OrdinalIgnoreCase))
                return;
            var shouldSwitch = forceSelect
                || selected == null
                || _targetRegistry.Mode == TargetSelectionMode.AutoFollow
                || (entry != null && string.Equals(entry.Id, _targetRegistry.SelectedTargetId, StringComparison.OrdinalIgnoreCase));

            if (!shouldSwitch)
            {
                RefreshTargetUi("Target added. Manual mode keeps current document locked.");
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                return;
            }

            if (entry == null)
            {
                ShowPlaceholder("Office target was not detected.", true);
                return;
            }

            if (!forceSelect && _runtime != null &&
                string.Equals(entry.Id, _targetRegistry.SelectedTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _targetRegistry.Select(entry.Id);
            AttachTarget(entry, activation.Action);
        }

        private void AttachTarget(
            OfficeTargetEntry entry, string action, string chatId = null, string expectedDocumentKey = null,
            DispatchedOfficeApplicationAdapter suppliedAdapter = null)
        {
            if (entry == null || entry.Target == null)
            {
                ShowPlaceholder("No Office target selected.", true);
                return;
            }

            DispatchedOfficeApplicationAdapter adapter = null;
            AssistantRuntime runtime = null;
            var attachTimer = Stopwatch.StartNew();
            try
            {
                DesktopLog.Info("Attach requested. Target=" + entry.DisplayName + ", hwnd=" + entry.Target.Hwnd + ", pid=" + entry.Target.ProcessId);
                var target = CloneTarget(entry.Target);
                adapter = suppliedAdapter ?? new DispatchedOfficeApplicationAdapter(delegate(IOfficeStaDispatcher dispatcher)
                {
                    return _adapterProvider.Create(target.Host, target, dispatcher);
                });
                var adapterMs = attachTimer.ElapsedMilliseconds;
                runtime = new AssistantRuntime(adapter);
                runtime.Controller.ExternalDocumentsProvider = () => _openMailboxDocuments;
                runtime.MailboxNavigationRequested = NavigateMailbox;
                runtime.OfficeHostChatRequested = CreateOfficeHostChatAsync;
                runtime.OfficeChatSelectionRequested = SelectOfficeChatAsync;
                if (!string.IsNullOrWhiteSpace(expectedDocumentKey) &&
                    !string.Equals(runtime.Controller.DocumentKey, expectedDocumentKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("Office target изменился до открытия нового чата.");
                if (!string.IsNullOrWhiteSpace(chatId)) runtime.Controller.SelectChat(chatId);
                var runtimeMs = attachTimer.ElapsedMilliseconds - adapterMs;
                DisposeCurrentRuntime();
                ClearContent();
                DisposeCurrentAdapter();
                var replaceMs = attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs;
                _runtime = runtime;
                runtime = null;
                _runtime.Controller.SettingsChanged += OnSettingsChanged;
                _currentAdapter = adapter;
                adapter = null;
                var pane = _runtime.CreatePaneControl();
                pane.Dock = DockStyle.Fill;
                _content.Controls.Add(pane);
                _emptyLaunchHost = null;
                var paneMs = attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs - replaceMs;
                Text = "RN Assistant - " + _runtime.Controller.HostName;
                if (!string.IsNullOrWhiteSpace(action))
                {
                    _runtime.RunQuickAction(action);
                }
                RefreshTargetUi("Attached: " + entry.DisplayName);
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                if (attachTimer.ElapsedMilliseconds >= 500)
                    DesktopLog.Info("Attach timing: adapter=" + adapterMs + "ms, runtime=" +
                        runtimeMs + "ms, replace=" + replaceMs + "ms, pane=" + paneMs +
                        "ms, ui=" + (attachTimer.ElapsedMilliseconds - adapterMs - runtimeMs -
                            replaceMs - paneMs) + "ms.");
            }
            catch (Exception ex)
            {
                if (runtime != null)
                {
                    runtime.Dispose();
                }
                if (adapter != null)
                {
                    adapter.Dispose();
                }
                DesktopLog.Error("Attach failed.", ex);
                RefreshTargetUi("Attach failed: " + ex.Message);
                DisposeCurrentRuntime();
                ClearContent();
                ShowPlaceholder(ex.Message, true);
                DisposeCurrentAdapter();
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoFollowTimer.Stop();
            _autoFollowTimer.Dispose();
            DisposeCurrentRuntime();
            ClearContent();
            DisposeCurrentAdapter();
            base.OnFormClosed(e);
        }

        private void AttachForegroundOffice()
        {
            _pendingLaunchHost = null;
            _emptyLaunchHost = null;
            try
            {
                ApplyActivation(ForegroundOfficeDetector.Detect(), true);
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Foreground attach failed.", ex);
                if (TryAttachSingleOpenTarget())
                {
                    return;
                }

                var message = "No active Office window detected. Select a document from the list or bring Office to the front and try again.";
                RefreshTargetUi(message);
                if (_runtime == null)
                {
                    ShowPlaceholder(message, true);
                }
            }
        }

        private void SetTargetMode(TargetSelectionMode mode)
        {
            _targetRegistry.Mode = mode;
            RefreshTargetUi(null);
        }

        private void SelectTarget(string id)
        {
            _emptyLaunchHost = null;
            _pendingLaunchHost = null;
            var entry = _targetRegistry.Select(id);
            AttachTarget(entry, null);
        }

        private void RefreshOpenTargets()
        {
            try
            {
                var host = _targetBar == null ? "All" : _targetBar.SelectedHost;
                _targetRegistry.UpsertMany(_adapterProvider.ListOpenTargets(host));
                RefreshMailboxTargets();
                RefreshTargetUi("Open document list refreshed.");
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Could not refresh Office targets.", ex);
                RefreshTargetUi("Refresh failed: " + ex.Message);
            }
        }

        private void RefreshMailboxTargets()
        {
            _nextMailboxRefreshUtc = DateTime.UtcNow.AddMinutes(1);
            try
            {
                var targets = _adapterProvider.ListOpenTargets("Outlook")
                    .Where(item => !string.IsNullOrWhiteSpace(item.StoreId)).ToArray();
                _targetRegistry.UpsertMany(targets);
                if (_runtime == null && targets.Length > 0 && _targetRegistry.SelectedTarget == null &&
                    (_pendingLaunchHost == null || _pendingLaunchHost == "Outlook"))
                {
                    var first = _targetRegistry.Select(targets[0]);
                    AttachTarget(first, null);
                }
                var selected = _targetRegistry.SelectedTarget;
                _openMailboxDocuments = targets.Select(item => new OpenOfficeDocumentDto {
                    Host = "Outlook", DocumentKey = item.DocumentKey, Title = item.Name,
                    IsActive = _runtime != null &&
                        string.Equals(_runtime.Controller.HostName, "Outlook", StringComparison.OrdinalIgnoreCase) &&
                        selected != null && selected.Target != null &&
                        string.Equals(selected.Target.DocumentKey, item.DocumentKey, StringComparison.Ordinal)
                }).ToArray();
                _runtime?.RefreshState();
                RefreshTargetUi(null);
            }
            catch (Exception ex) { DesktopLog.Error("Outlook mailbox discovery failed.", ex); }
        }

        private void NavigateMailbox(string documentKey, string chatId, bool createNew)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => NavigateMailbox(documentKey, chatId, createNew)));
                return;
            }
            try
            {
                _pendingLaunchHost = null;
                _emptyLaunchHost = null;
                var target = _adapterProvider.ListOpenTargets("Outlook").FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.StoreId) &&
                    string.Equals(item.DocumentKey, documentKey, StringComparison.Ordinal));
                if (target == null) throw new InvalidOperationException("Outlook mailbox is not connected.");
                var entry = _targetRegistry.Upsert(target);
                if (_runtime == null || !string.Equals(_targetRegistry.SelectedTargetId, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _targetRegistry.Select(entry.Id);
                    AttachTarget(entry, null);
                }
                if (_runtime == null || !string.Equals(_runtime.Controller.HostName, "Outlook", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Outlook mailbox attachment failed.");
                if (createNew) _runtime.Controller.CreateChat("Новый чат");
                else if (!string.IsNullOrWhiteSpace(chatId)) _runtime.Controller.SelectChat(chatId);
                _runtime.RefreshState();
                RefreshMailboxTargets();
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Outlook mailbox navigation failed.", ex);
                MessageBox.Show(this, ex.Message, "RN Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task<OfficeHostChatResponse> CreateOfficeHostChatAsync(string host)
        {
            if (IsDisposed || !IsHandleCreated)
                throw new InvalidOperationException("RN Assistant window is unavailable.");
            if (InvokeRequired)
            {
                var completion = new TaskCompletionSource<OfficeHostChatResponse>();
                BeginInvoke(new Action(async delegate
                {
                    try { completion.SetResult(await CreateOfficeHostChatAsync(host)); }
                    catch (Exception ex) { completion.SetException(ex); }
                }));
                return await completion.Task.ConfigureAwait(false);
            }

            if (_pendingLaunchHost != null)
                throw new InvalidOperationException("Создание Office-чата уже выполняется.");
            _pendingLaunchHost = host;
            _emptyLaunchHost = null;
            RefreshTargetUi("Создаю чат в " + host + "…");
            try
            {
                if (_runtime != null &&
                    string.Equals(_runtime.Controller.HostName, host, StringComparison.Ordinal) &&
                    (host != "Outlook" || _runtime.Controller.DocumentKey.StartsWith(
                        "outlook-mailbox:", StringComparison.Ordinal)))
                {
                    var state = _runtime.Controller.CreatePersistentChat("Новый чат");
                    _runtime.RefreshState();
                    return new OfficeHostChatResponse
                    {
                        Host = host,
                        ChatId = state.ActiveChatId,
                        DocumentTitle = _runtime.Controller.DocumentTitle,
                        State = state
                    };
                }

                using (var created = await OfficeHostChatCoordinator.CreateAsync(host))
                {
                    var entry = _targetRegistry.Upsert(created.Target);
                    if (entry == null)
                        throw new InvalidOperationException("Новый чат создан, но Office target не найден.");
                    _targetRegistry.Select(entry.Id);
                    AttachTarget(entry, null, created.ChatId, created.DocumentKey, created.TakeAdapter());
                    if (_runtime == null ||
                        !string.Equals(_runtime.Controller.DocumentKey, created.DocumentKey, StringComparison.Ordinal))
                        throw new InvalidOperationException("Новый чат создан, но привязка к документу не подтверждена.");
                    _runtime.RefreshState();
                    if (host == "Outlook") RefreshMailboxTargets();
                    return new OfficeHostChatResponse
                    {
                        Host = host,
                        ChatId = created.ChatId,
                        DocumentTitle = created.DocumentTitle
                    };
                }
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Office host chat creation failed.", ex);
                RefreshTargetUi(ex.Message);
                if (_placeholder != null && !_placeholder.IsDisposed) _placeholder.Text = ex.Message;
                throw;
            }
            finally
            {
                _pendingLaunchHost = null;
            }
        }

        private async Task<OfficeHostChatResponse> SelectOfficeChatAsync(string chatId)
        {
            if (IsDisposed || !IsHandleCreated || _runtime == null)
                throw new InvalidOperationException("RN Assistant window is unavailable.");
            if (InvokeRequired)
            {
                var completion = new TaskCompletionSource<OfficeHostChatResponse>();
                BeginInvoke(new Action(async delegate
                {
                    try { completion.SetResult(await SelectOfficeChatAsync(chatId)); }
                    catch (Exception ex) { completion.SetException(ex); }
                }));
                return await completion.Task.ConfigureAwait(false);
            }

            var currentState = await Task.Run(() => _runtime.Controller.TrySelectCurrentDocumentChat(chatId));
            if (currentState != null)
                return new OfficeHostChatResponse
                {
                    Host = _runtime.Controller.HostName, ChatId = chatId,
                    DocumentTitle = _runtime.Controller.DocumentTitle, State = currentState
                };

            var chat = _runtime.Controller.ListChats().Chats.FirstOrDefault(item =>
                string.Equals(item.Id, chatId, StringComparison.Ordinal));
            if (chat == null || string.IsNullOrWhiteSpace(chat.Host) ||
                string.IsNullOrWhiteSpace(chat.DocumentKey))
                throw new InvalidOperationException("Чат не найден в каталоге документов.");
            if (string.Equals(_runtime.Controller.HostName, chat.Host, StringComparison.Ordinal) &&
                string.Equals(_runtime.Controller.DocumentKey, chat.DocumentKey, StringComparison.Ordinal))
            {
                var state = _runtime.Controller.SelectChat(chatId);
                return new OfficeHostChatResponse
                {
                    Host = chat.Host, ChatId = chatId,
                    DocumentTitle = chat.DocumentTitle, State = state
                };
            }

            _pendingLaunchHost = chat.Host;
            try
            {
                using (var opened = await OfficeHostChatCoordinator.OpenExistingAsync(
                    chat.Host, chat.DocumentKey, chatId))
                {
                    var entry = _targetRegistry.Upsert(opened.Target);
                    if (entry == null) throw new InvalidOperationException("Office target не найден.");
                    _targetRegistry.Select(entry.Id);
                    AttachTarget(entry, null, chatId, chat.DocumentKey, opened.TakeAdapter());
                    if (_runtime == null ||
                        !string.Equals(_runtime.Controller.DocumentKey, chat.DocumentKey, StringComparison.Ordinal))
                        throw new InvalidOperationException("Привязка к документу этого чата не подтверждена.");
                    if (chat.Host == "Outlook") RefreshMailboxTargets();
                    return new OfficeHostChatResponse
                    {
                        Host = chat.Host, ChatId = chatId,
                        DocumentTitle = chat.DocumentTitle
                    };
                }
            }
            finally { _pendingLaunchHost = null; }
        }

        private bool TryAttachSingleOpenTarget()
        {
            try
            {
                var host = _targetBar == null ? "All" : _targetBar.SelectedHost;
                _targetRegistry.UpsertMany(_adapterProvider.ListOpenTargets(host));
                var entries = _targetRegistry.ForHost(host);
                if (entries.Count == 1)
                {
                    _targetRegistry.Select(entries[0].Id);
                    AttachTarget(entries[0], null);
                    return true;
                }

                RefreshTargetUi(entries.Count == 0
                    ? "No open Office documents found."
                    : "Multiple Office documents found. Choose one from the document list.");
            }
            catch (Exception ex)
            {
                DesktopLog.Error("Open target fallback failed.", ex);
                RefreshTargetUi("Could not refresh Office targets: " + ex.Message);
            }

            return false;
        }

        private void RefreshTargetUi(string status)
        {
            if (_targetBar == null)
            {
                return;
            }

            _targetBar.RefreshFrom(_targetRegistry, status);
        }

        private void ShowPlaceholder(string text, bool showAttach = false)
        {
            ClearContent();
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = showAttach ? 3 : 2,
                Padding = new Padding(24)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 168f));
            if (showAttach)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
            }

            _placeholder = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                Padding = new Padding(24)
            };
            layout.Controls.Add(_placeholder, 0, 0);

            var launchRows = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                var selectedHost = host;
                var launchButton = new Button
                {
                    Width = 210,
                    Height = 32,
                    Text = "Новый чат: " + host,
                    Font = new Font("Segoe UI", 9f)
                };
                launchButton.Click += async delegate
                {
                    try { await CreateOfficeHostChatAsync(selectedHost); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "RN Assistant",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                launchRows.Controls.Add(launchButton);
            }
            layout.Controls.Add(launchRows, 0, 1);

            if (showAttach)
            {
                var button = new Button
                {
                    Dock = DockStyle.Top,
                    Height = 36,
                    Text = "Attach to active Office",
                    Font = new Font("Segoe UI", 9f)
                };
                button.Click += delegate { AttachForegroundOffice(); };
                layout.Controls.Add(button, 0, 2);
            }

            _content.Controls.Add(layout);
        }

        private void ClearContent()
        {
            while (_content.Controls.Count > 0)
            {
                var control = _content.Controls[0];
                _content.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private void DisposeCurrentAdapter()
        {
            var adapter = _currentAdapter;
            _currentAdapter = null;
            if (adapter == null) return;
            var shutdown = _pendingRuntimeShutdown;
            if (shutdown.IsCompleted) ReleaseAdapter(adapter);
            else shutdown.ContinueWith(ignored => ReleaseAdapter(adapter), TaskScheduler.Default);
        }

        private void DisposeCurrentRuntime()
        {
            var runtime = _runtime;
            _runtime = null;
            if (runtime == null) return;
            runtime.Controller.SettingsChanged -= OnSettingsChanged;
            try { runtime.Dispose(); }
            catch (Exception ex) { DesktopLog.Error("Desktop runtime cleanup failed.", ex); }
            finally { _pendingRuntimeShutdown = runtime.ShutdownCompletion; }
        }

        private static void ReleaseAdapter(IDisposable adapter)
        {
            try { adapter.Dispose(); }
            catch (Exception ex) { DesktopLog.Error("Desktop Office adapter cleanup failed.", ex); }
        }

        private void OnSettingsChanged(AppSettings settings)
        {
            if (settings == null || IsDisposed) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                    BeginInvoke(new Action<AppSettings>(OnSettingsChanged), settings);
                return;
            }
            ApplyDesktopWindowWidth(settings.DesktopWindowWidth);
        }

        private void ApplyDesktopWindowWidth(int width)
        {
            _desktopWindowWidth = width;
            if (_fullScreen || WindowState != FormWindowState.Normal)
            {
                _applyWidthOnRestore = true;
                return;
            }

            var workingArea = Screen.FromControl(this).WorkingArea;
            var displayWidth = Math.Max(MinimumSize.Width, Math.Min(width, workingArea.Width));
            var left = Math.Max(workingArea.Left, Math.Min(Left, workingArea.Right - displayWidth));
            _applyWidthOnRestore = false;
            SetBounds(left, Top, displayWidth, Height);
        }

        private static OfficeTargetDescriptor CloneTarget(OfficeTargetDescriptor source)
        {
            return new OfficeTargetDescriptor
            {
                Host = source.Host,
                FullName = source.FullName,
                Path = source.Path,
                Name = source.Name,
                DocumentKey = source.DocumentKey,
                EntryId = source.EntryId,
                StoreId = source.StoreId,
                FolderPath = source.FolderPath,
                Selection = source.Selection,
                Action = source.Action,
                Hwnd = source.Hwnd,
                ProcessId = source.ProcessId
            };
        }

    }
}
