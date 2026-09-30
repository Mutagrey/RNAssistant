using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Office.Core;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
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
            runtime.Controller.EnsureHostSwitchReady();
            var chat = await Task.Run(() => runtime.Controller.ListChats().Chats
                .FirstOrDefault(item => string.Equals(item.Id, chatId, StringComparison.Ordinal)))
                .ConfigureAwait(false);
            if (chat == null || string.IsNullOrWhiteSpace(chat.Host) ||
                string.IsNullOrWhiteSpace(chat.DocumentKey))
                throw new InvalidOperationException("Чат не найден в каталоге документов.");
            if (string.Equals(runtime.Controller.HostName, chat.Host, StringComparison.Ordinal) &&
                string.Equals(runtime.Controller.DocumentKey, chat.DocumentKey, StringComparison.Ordinal))
            {
                var state = await Task.Run(() => runtime.Controller.SelectChat(chatId)).ConfigureAwait(false);
                return new OfficeHostChatResponse
                {
                    Host = chat.Host, ChatId = chatId,
                    DocumentTitle = chat.DocumentTitle, State = state
                };
            }

            using (var opened = await OpenExistingAsync(chat.Host, chat.DocumentKey, chatId).ConfigureAwait(false))
            {
                var init = await runtime.SwitchToAdapterAsync(
                    opened.TakeAdapter(), chatId, chat.Host, chat.DocumentKey).ConfigureAwait(false);
                return new OfficeHostChatResponse
                {
                    Host = chat.Host, ChatId = chatId,
                    DocumentTitle = chat.DocumentTitle, Init = init
                };
            }
        }

        public static Task<OfficeHostChatCreation> OpenExistingAsync(
            string host, string documentKey, string chatId)
        {
            if (string.IsNullOrWhiteSpace(documentKey) || string.IsNullOrWhiteSpace(chatId))
                throw new ArgumentException("An exact document and chat are required.");
            return Task.Run(delegate
            {
                var provider = new OfficeComAdapterProvider();
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    dispatcher.Invoke(() => OfficeHostLauncher.OpenOrActivate(host));
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
                throw new InvalidOperationException(
                    "Документ этого чата сейчас не открыт в " + host + ". Откройте его и повторите действие.");
            });
        }

        public static async Task<OfficeHostChatResponse> CreateFromPanelAsync(
            AssistantRuntime runtime, string host)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            runtime.Controller.EnsureHostSwitchReady();
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
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    dispatcher.Invoke(() => OfficeHostLauncher.OpenOrActivate(host));
                    var provider = new OfficeComAdapterProvider();
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        var targets = dispatcher.Invoke(() => provider.ListOpenTargets(host)
                            .Where(target => host != "Outlook" || (outlookMailbox
                                ? !string.IsNullOrWhiteSpace(target.StoreId)
                                : target.Hwnd != 0))
                            .ToArray());
                        if (targets.Length == 0 && host != "Outlook")
                        {
                            dispatcher.Invoke(delegate { TryCreateBlankDocument(host); });
                            targets = dispatcher.Invoke(() => provider.ListOpenTargets(host).ToArray());
                        }
                        if (targets.Length > 0)
                        {
                            var target = dispatcher.Invoke(() => SelectTarget(host, targets, outlookMailbox));
                            if (host == "Outlook" && !outlookMailbox &&
                                string.IsNullOrWhiteSpace(target.EntryId) &&
                                string.IsNullOrWhiteSpace(target.FolderPath))
                                throw new InvalidOperationException(
                                    "Сохраните черновик Outlook перед созданием чата для письма.");
                            var adapter = new DispatchedOfficeApplicationAdapter(
                                owner => provider.Create(host, target, owner));
                            try
                            {
                                using (var controller = new AssistantController(adapter))
                                {
                                    var state = controller.CreatePersistentChat("Новый чат");
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
                            catch { adapter.Dispose(); throw; }
                        }
                        await Task.Delay(750).ConfigureAwait(false);
                    }
                    throw new InvalidOperationException(host == "Outlook"
                        ? outlookMailbox
                            ? "Outlook не предоставил почтовый ящик. Проверьте профиль и повторите действие."
                            : "Outlook не предоставил окно письма или папки. Откройте Outlook и повторите действие."
                        : host + " не предоставил документ для создания чата.");
                }
            });
        }

        private static OfficeTargetDescriptor SelectTarget(
            string host, OfficeTargetDescriptor[] targets, bool outlookMailbox)
        {
            if (host == "Outlook" && !outlookMailbox)
            {
                var hwnd = ActiveWindowHwnd(host);
                var activeWindow = targets.Where(target => target.Hwnd == hwnd && hwnd != 0).ToArray();
                if (activeWindow.Length == 1) return activeWindow[0];
            }
            var activeIdentity = ActiveIdentity(host);
            if (!string.IsNullOrWhiteSpace(activeIdentity))
            {
                var active = targets.Where(target => string.Equals(
                    host == "Outlook" ? target.StoreId : target.FullName,
                    activeIdentity, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (active.Length == 1) return active[0];
                if (active.Length > 1)
                {
                    var hwnd = ActiveWindowHwnd(host);
                    var window = active.Where(target => target.Hwnd == hwnd && hwnd != 0).ToArray();
                    if (window.Length == 1) return window[0];
                }
            }
            if (targets.Length == 1) return targets[0];
            throw new InvalidOperationException(
                "Открыто несколько документов или почтовых ящиков " + host +
                ". Выберите нужный в приложении и повторите действие.");
        }

        private static string ActiveIdentity(string host)
        {
            try
            {
                if (host == "Excel")
                {
                    var application = (Excel.Application)Marshal.GetActiveObject("Excel.Application");
                    return application.ActiveWorkbook == null ? null : application.ActiveWorkbook.FullName;
                }
                if (host == "Word")
                {
                    var application = (Word.Application)Marshal.GetActiveObject("Word.Application");
                    return application.ActiveDocument == null ? null : application.ActiveDocument.FullName;
                }
                if (host == "PowerPoint")
                {
                    var application = (PowerPoint.Application)Marshal.GetActiveObject("PowerPoint.Application");
                    return application.ActivePresentation == null ? null : application.ActivePresentation.FullName;
                }
                if (host == "Outlook")
                {
                    var application = (Outlook.Application)Marshal.GetActiveObject("Outlook.Application");
                    var explorer = application.ActiveExplorer();
                    return explorer == null || explorer.CurrentFolder == null
                        ? null : explorer.CurrentFolder.StoreID;
                }
            }
            catch (COMException) { }
            return null;
        }

        private static long ActiveWindowHwnd(string host)
        {
            try
            {
                if (host == "Excel")
                    return NativeWindowInfo.ReadLongMemberPath(
                        Marshal.GetActiveObject("Excel.Application"), "ActiveWindow", "Hwnd");
                if (host == "Word")
                    return NativeWindowInfo.ReadLongMemberPath(
                        Marshal.GetActiveObject("Word.Application"), "ActiveWindow", "Hwnd");
                if (host == "PowerPoint")
                    return NativeWindowInfo.ReadLongMemberPath(
                        Marshal.GetActiveObject("PowerPoint.Application"), "ActiveWindow", "HWND");
                if (host == "Outlook")
                {
                    var application = (Outlook.Application)Marshal.GetActiveObject("Outlook.Application");
                    return NativeWindowInfo.ReadOutlookWindowHandle(application.ActiveWindow());
                }
            }
            catch (COMException) { }
            return 0;
        }

        private static bool TryCreateBlankDocument(string host)
        {
            try
            {
                if (host == "Excel")
                {
                    var application = (Excel.Application)Marshal.GetActiveObject("Excel.Application");
                    if (application.Workbooks.Count == 0)
                    {
                        var workbook = application.Workbooks.Add(Type.Missing);
                        EnsureNewDocumentIdentity("Excel", () => workbook.CustomDocumentProperties);
                    }
                    return true;
                }
                if (host == "Word")
                {
                    var application = (Word.Application)Marshal.GetActiveObject("Word.Application");
                    if (application.Documents.Count == 0)
                    {
                        object missing = Type.Missing;
                        var document = application.Documents.Add(ref missing, ref missing, ref missing, ref missing);
                        EnsureNewDocumentIdentity("Word", () => document.CustomDocumentProperties);
                    }
                    return true;
                }
                if (host == "PowerPoint")
                {
                    var application = (PowerPoint.Application)Marshal.GetActiveObject("PowerPoint.Application");
                    if (application.Presentations.Count == 0)
                    {
                        var presentation = application.Presentations.Add(MsoTriState.msoTrue);
                        EnsureNewDocumentIdentity("PowerPoint", () => presentation.CustomDocumentProperties);
                    }
                    return true;
                }
            }
            catch (COMException) { }
            return false;
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
