using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        private readonly string _historyController = Guid.NewGuid().ToString("N");
        private long _historySequence;
        private TaskCompletionSource<bool> _navigationWait;
        private int? _waitingEntry;
        private string _waitingLocator;
        private sealed class NativeHistoryReply
        {
            public int currentIndex { get; set; }
            public NativeHistoryEntry[] entries { get; set; }
        }
        private sealed class NativeHistoryEntry { public int id { get; set; } public string url { get; set; } }

        private async void HistoryChanged(object sender, object args)
        {
            if (_closed || !HasLoaded || !_loaded.Task.IsCompleted) return;
            try { await RefreshHistoryAsync(true); }
            catch (Exception error) {
                if (_closed) return;
                State.Status = BrowserPageStatus.Failed; State.Message = error.Message;
                _navigationWait?.TrySetException(error); Publish();
            }
        }

        private async Task RefreshHistoryAsync(bool sameDocument)
        {
            var sequence = ++_historySequence; var navigation = _navigation;
            if (sameDocument) { State.DocumentGeneration++; InvalidateAutomation(); }
            var json = await Core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
            if (_closed || sequence != _historySequence || navigation != _navigation || !HasLoaded) return;
            if (json.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Browser history exceeds its bound.");
            var history = JsonConvert.DeserializeObject<NativeHistoryReply>(json);
            if (history?.entries == null || history.currentIndex < 0 || history.currentIndex >= history.entries.Length)
                throw new InvalidOperationException("Native Browser history is unavailable.");
            var current = history.entries[history.currentIndex]; string member;
            if (!Content.TryMember(current.url, out member) || current.url != Core.Source)
                throw new InvalidOperationException("Native Browser history changed during capture.");
            var start = Math.Max(0, history.currentIndex - 32);
            var entries = history.entries.Skip(start).Take(64).Where(entry => Content.TryMember(entry.url, out member))
                .Select(entry => new BrowserNativeHistoryEntry { Id = entry.id, Locator = Content.Location(entry.url) }).ToArray();
            Content.UpdateLocation(State, current.url);
            State.NativeHistory = new BrowserNativeHistory { ControllerId = _historyController, Revision = sequence,
                CurrentEntryId = current.id, Entries = entries };
            State.Title = (Core.DocumentTitle ?? State.Title ?? "Browser");
            if (State.Title.Length > 512) State.Title = State.Title.Substring(0, 512);
            if (sameDocument) await InitializeVisualAsync();
            if (_closed || sequence != _historySequence || navigation != _navigation) return;
            State.Status = BrowserPageStatus.Ready; State.Message = null; State.Reason = BrowserRefusalCode.None;
            if (_waitingEntry == current.id || _waitingLocator != null && _waitingLocator == State.Url)
                _navigationWait?.TrySetResult(true);
            Publish();
        }

        internal async Task NavigateHistoryAsync(int? entryId, string locator, CancellationToken token)
        {
            if (!Ready || _navigationWait != null) throw new InvalidOperationException("Browser navigation is already in progress.");
            Content.Validate();
            BrowserLocalLocator parsed = null; string url = null;
            if (entryId.HasValue) {
                if (State.NativeHistory?.ControllerId != _historyController || !State.NativeHistory.Entries.Any(entry => entry.Id == entryId))
                    throw new InvalidOperationException("Native history entry expired.");
            } else {
                if (!BrowserLocalLocator.TryParse(locator, out parsed)) throw new InvalidOperationException("Invalid Browser address.");
                if (locator == State.Url) return;
                url = Content.Url(parsed.RelativePath) + parsed.Query + parsed.Fragment;
                if (Content.Location(url) != locator) throw new InvalidOperationException("Address outside the current Browser content.");
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigationWait = completion; _waitingEntry = entryId; _waitingLocator = locator;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cancel.Token))
            using (linked.Token.Register(() => completion.TrySetCanceled()))
            try {
                if (entryId.HasValue) await Core.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", JsonConvert.SerializeObject(new HistoryNavigation { entryId = entryId.Value }));
                else Core.Navigate(url);
                if (await Task.WhenAny(completion.Task, Task.Delay(15000, linked.Token)) != completion.Task)
                    throw new TimeoutException("Native Browser navigation was not confirmed.");
                await completion.Task;
            } finally { _navigationWait = null; _waitingEntry = null; _waitingLocator = null; }
        }
        private sealed class HistoryNavigation { public int entryId { get; set; } }
    }
}
