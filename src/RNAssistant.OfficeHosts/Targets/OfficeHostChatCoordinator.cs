using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Office.Core;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;
using Outlook = Microsoft.Office.Interop.Outlook;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    public sealed class OfficeHostChatCreation : IDisposable
    {
        public OfficeTargetDescriptor Target { get; set; }
        public string ChatId { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public DispatchedOfficeApplicationAdapter Adapter { get; set; }

        public DispatchedOfficeApplicationAdapter TakeAdapter()
        {
            var adapter = Adapter;
            if (adapter == null) throw new InvalidOperationException("Office chat binding was already transferred.");
            Adapter = null;
            return adapter;
        }

        public void Dispose()
        {
            var adapter = Adapter;
            Adapter = null;
            if (adapter != null) adapter.Dispose();
        }
    }

    public static class OfficeHostChatCoordinator
    {
        public static async Task<OfficeHostChatResponse> SelectFromPanelAsync(
            AssistantRuntime runtime, string chatId)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("A chat id is required.", "chatId");
            var currentState = await Task.Run(() => runtime.Controller.TrySelectCurrentDocumentChat(chatId))
                .ConfigureAwait(false);
            if (currentState != null)
                return new OfficeHostChatResponse
                {
                    Host = runtime.Controller.HostName, ChatId = chatId,
                    DocumentTitle = runtime.Controller.DocumentTitle, State = currentState
                };
            var catalog = await Task.Run(() => runtime.Controller.ListChats()).ConfigureAwait(false);
            var chat = catalog.Chats.FirstOrDefault(item => string.Equals(item.Id, chatId, StringComparison.Ordinal));
            if (chat == null || string.IsNullOrWhiteSpace(chat.Host) ||
                string.IsNullOrWhiteSpace(chat.DocumentKey))
                throw new InvalidOperationException("Чат не найден в каталоге документов.");
            using (var opened = await TryBindExistingAsync(chat.Host, chat.DocumentKey, chatId).ConfigureAwait(false))
            {
                if (opened == null)
                {
                    var state = await Task.Run(() => runtime.Controller.SelectChat(chatId)).ConfigureAwait(false);
                    state.Chats = catalog.Chats;
                    state.Documents = catalog.Documents;
                    return new OfficeHostChatResponse
                    {
                        Host = chat.Host, ChatId = chatId,
                        DocumentTitle = chat.DocumentTitle, State = state
                    };
                }
                var init = await runtime.SwitchToAdapterAsync(
                    opened.TakeAdapter(), chatId, chat.Host, chat.DocumentKey).ConfigureAwait(false);
                return new OfficeHostChatResponse
                {
                    Host = chat.Host, ChatId = chatId,
                    DocumentTitle = chat.DocumentTitle, Init = init
                };
            }
        }

        public static Task<OfficeHostChatCreation> TryBindExistingAsync(
            string host, string documentKey, string chatId)
        {
            if (string.IsNullOrWhiteSpace(documentKey) || string.IsNullOrWhiteSpace(chatId))
                throw new ArgumentException("An exact document and chat are required.");
            return Task.Run(delegate
            {
                var provider = new OfficeComAdapterProvider();
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var targets = dispatcher.Invoke(() => provider.ListOpenTargets(host).ToArray());
                    foreach (var target in targets)
                    {
                        if (!string.IsNullOrWhiteSpace(target.DocumentKey) &&
                            !string.Equals(target.DocumentKey, documentKey, StringComparison.Ordinal))
                            continue;
                        var adapter = new DispatchedOfficeApplicationAdapter(
                            owner => provider.Create(host, target, owner));
                        try
                        {
                            if (!string.Equals(adapter.DocumentKey, documentKey, StringComparison.Ordinal))
                            {
                                adapter.Dispose();
                                continue;
                            }
                            return new OfficeHostChatCreation
                            {
                                Target = target, ChatId = chatId,
                                DocumentKey = documentKey,
                                DocumentTitle = adapter.DocumentTitle,
                                Adapter = adapter
                            };
                        }
                        catch
                        {
                            adapter.Dispose();
                        }
                    }
                }
                return null;
            });
        }

        public static async Task<OfficeHostChatResponse> CreateFromPanelAsync(
            AssistantRuntime runtime, string host)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            if (string.Equals(runtime.Controller.HostName, host, StringComparison.Ordinal))
            {
                if (host == "Outlook" && runtime.Controller.DocumentKey.StartsWith(
                    "Outlook:Runtime:", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Сохраните черновик Outlook перед созданием чата для письма.");
                var state = runtime.Controller.CreatePersistentChat("Новый чат");
                return new OfficeHostChatResponse
                {
                    Host = host,
                    ChatId = state.ActiveChatId,
                    DocumentTitle = runtime.Controller.DocumentTitle,
                    State = state
                };
            }

            runtime.Controller.EnsureHostSwitchReady();
            using (var created = await CreateAsync(host, host != "Outlook").ConfigureAwait(false))
            {
                InitResponse init;
                try
                {
                    init = await runtime.SwitchToAdapterAsync(
                        created.TakeAdapter(), created.ChatId, host, created.DocumentKey).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Чат создан, но переключить эту панель RN Assistant не удалось: " + ex.Message, ex);
                }
                return new OfficeHostChatResponse
                {
                    Host = host,
                    ChatId = created.ChatId,
                    DocumentTitle = created.DocumentTitle,
                    Init = init
                };
            }
        }

        public static Task<OfficeHostChatCreation> CreateAsync(
            string host, bool outlookMailbox = true)
        {
            return Task.Run(async delegate
            {
                var timer = Stopwatch.StartNew();
                var stage = "application";
                object application = null;
                var documentPrepared = false;
                OfficeTargetDescriptor target = null;
                var provider = new OfficeComAdapterProvider();
                // Acquisition, discovery and the retained adapter share one owner
                // STA and the same application, including before ROT registration.
                var adapter = new DispatchedOfficeApplicationAdapter(
                    owner => provider.Create(host, target, owner, application));
                var dispatcher = adapter.StaDispatcher;
                try
                {
                    application = dispatcher.Invoke(() => OfficeHostLauncher.OpenOrActivate(host));
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (application == null)
                            application = dispatcher.Invoke(() => OfficeHostLauncher.TryGetRunningApplication(host));
                        if (application == null)
                        {
                            await Task.Delay(750).ConfigureAwait(false);
                            continue;
                        }
                        stage = "document";
                        if (host != "Outlook" && !documentPrepared)
                        {
                            dispatcher.Invoke<object>(() =>
                            {
                                EnsureBlankDocument(host, application);
                                return null;
                            });
                            documentPrepared = true;
                        }
                        stage = "discovery";
                        var targets = dispatcher.Invoke(() => OfficeTargetEnumerator.ListOpenTargets(host, _ => application)
                            .Where(item => host != "Outlook" || (outlookMailbox
                                ? !string.IsNullOrWhiteSpace(item.StoreId)
                                : item.Hwnd != 0))
                            .ToArray());
                        if (targets.Length > 0)
                        {
                            stage = "target";
                            target = dispatcher.Invoke(() => SelectTarget(host, targets, outlookMailbox, application));
                            if (host == "Outlook" && !outlookMailbox &&
                                string.IsNullOrWhiteSpace(target.EntryId) &&
                                string.IsNullOrWhiteSpace(target.FolderPath))
                                throw new InvalidOperationException(
                                    "Сохраните черновик Outlook перед созданием чата для письма.");
                            dispatcher.Invoke<object>(() =>
                            {
                                NativeWindowInfo.BringToForeground(
                                    target.Hwnd != 0 ? target.Hwnd : ActiveWindowHwnd(host, application));
                                return null;
                            });
                            stage = "binding";
                            using (var controller = new AssistantController(adapter))
                            {
                                stage = "chat";
                                var state = controller.CreatePersistentChat("Новый чат");
                                RuntimeLog.Info("Office chat creation completed. Host=" + host +
                                    ", elapsedMs=" + timer.ElapsedMilliseconds);
                                return new OfficeHostChatCreation
                                {
                                    Target = target,
                                    ChatId = state.ActiveChatId,
                                    DocumentKey = adapter.DocumentKey,
                                    DocumentTitle = adapter.DocumentTitle,
                                    Adapter = adapter
                                };
                            }
                        }
                        await Task.Delay(750).ConfigureAwait(false);
                    }
                    throw new InvalidOperationException(application == null
                        ? host + " запущен, но недоступен для подключения. Откройте документ, вернитесь в RN Assistant и повторите действие."
                        : host == "Outlook"
                        ? outlookMailbox
                            ? "Outlook не предоставил почтовый ящик. Проверьте профиль и повторите действие."
                            : "Outlook не предоставил окно письма или папки. Откройте Outlook и повторите действие."
                        : host + " не предоставил документ для создания чата.");
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Office chat creation failed. Host=" + host +
                        ", stage=" + stage + ", elapsedMs=" + timer.ElapsedMilliseconds, ex);
                    try { adapter.Dispose(); }
                    catch (Exception cleanupError)
                    { RuntimeLog.Error("Office chat adapter cleanup failed. Host=" + host, cleanupError); }
                    var cause = ex.GetBaseException();
                    var detail = ex.Message;
                    if (cause != ex && !string.Equals(cause.Message, detail, StringComparison.Ordinal))
                        detail += " Причина: " + cause.Message;
                    throw new InvalidOperationException(
                        "Создание чата " + host + " (этап " + stage + "): " + detail, ex);
                }
            });
        }

        private static OfficeTargetDescriptor SelectTarget(
            string host, OfficeTargetDescriptor[] targets, bool outlookMailbox, object application)
        {
            if (host == "Outlook" && !outlookMailbox)
            {
                var hwnd = ActiveWindowHwnd(host, application);
                var activeWindow = targets.Where(target => target.Hwnd == hwnd && hwnd != 0).ToArray();
                if (activeWindow.Length == 1) return activeWindow[0];
            }
            var activeIdentity = ActiveIdentity(host, application);
            if (!string.IsNullOrWhiteSpace(activeIdentity))
            {
                var active = targets.Where(target => string.Equals(
                    host == "Outlook" ? target.StoreId : target.FullName,
                    activeIdentity, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (active.Length == 1) return active[0];
                if (active.Length > 1)
                {
                    var hwnd = ActiveWindowHwnd(host, application);
                    var window = active.Where(target => target.Hwnd == hwnd && hwnd != 0).ToArray();
                    if (window.Length == 1) return window[0];
                }
            }
            if (targets.Length == 1) return targets[0];
            throw new InvalidOperationException(
                "Открыто несколько документов или почтовых ящиков " + host +
                ". Выберите нужный в приложении и повторите действие.");
        }

        private static string ActiveIdentity(string host, object acquiredApplication)
        {
            try
            {
                if (host == "Excel")
                {
                    var application = (Excel.Application)acquiredApplication;
                    return application.ActiveWorkbook == null ? null : application.ActiveWorkbook.FullName;
                }
                if (host == "Word")
                {
                    var application = (Word.Application)acquiredApplication;
                    return application.ActiveDocument == null ? null : application.ActiveDocument.FullName;
                }
                if (host == "PowerPoint")
                {
                    var application = (PowerPoint.Application)acquiredApplication;
                    return application.ActivePresentation == null ? null : application.ActivePresentation.FullName;
                }
                if (host == "Outlook")
                {
                    var application = (Outlook.Application)acquiredApplication;
                    var explorer = application.ActiveExplorer();
                    return explorer == null || explorer.CurrentFolder == null
                        ? null : explorer.CurrentFolder.StoreID;
                }
            }
            catch (COMException) { }
            return null;
        }

        private static long ActiveWindowHwnd(string host, object acquiredApplication)
        {
            try
            {
                if (host == "Excel")
                    return NativeWindowInfo.ReadLongMemberPath(
                        acquiredApplication, "ActiveWindow", "Hwnd");
                if (host == "Word")
                    return NativeWindowInfo.ReadLongMemberPath(
                        acquiredApplication, "ActiveWindow", "Hwnd");
                if (host == "PowerPoint")
                    return NativeWindowInfo.ReadLongMemberPath(
                        acquiredApplication, "ActiveWindow", "HWND");
                if (host == "Outlook")
                {
                    var application = (Outlook.Application)acquiredApplication;
                    return NativeWindowInfo.ReadOutlookWindowHandle(application.ActiveWindow());
                }
            }
            catch (COMException) { }
            return 0;
        }

        private static void EnsureBlankDocument(string host, object acquiredApplication)
        {
            if (host == "Excel")
            {
                var application = (Excel.Application)acquiredApplication;
                if (application.Workbooks.Count == 0)
                {
                    var workbook = application.Workbooks.Add(Type.Missing);
                    EnsureNewDocumentIdentity("Excel", () => workbook.CustomDocumentProperties);
                }
                return;
            }
            if (host == "Word")
            {
                var application = (Word.Application)acquiredApplication;
                if (application.Documents.Count == 0)
                {
                    object missing = Type.Missing;
                    var document = application.Documents.Add(ref missing, ref missing, ref missing, ref missing);
                    EnsureNewDocumentIdentity("Word", () => document.CustomDocumentProperties);
                }
                return;
            }
            if (host == "PowerPoint")
            {
                var application = (PowerPoint.Application)acquiredApplication;
                if (application.Presentations.Count == 0)
                {
                    var presentation = application.Presentations.Add(MsoTriState.msoTrue);
                    EnsureNewDocumentIdentity("PowerPoint", () => presentation.CustomDocumentProperties);
                }
            }
        }

        private static void EnsureNewDocumentIdentity(string host, Func<object> propertiesFactory)
        {
            try
            {
                DocumentIdentity.EnsureDocumentId(host, propertiesFactory,
                    (properties, id) => ((dynamic)properties).Add(
                        DocumentIdentity.PropertyName, false,
                        MsoDocProperties.msoPropertyTypeString, id, Type.Missing));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Пустой документ создан, но его устойчивый идентификатор не записан. Чат не создан.", ex);
            }
        }
    }
}
