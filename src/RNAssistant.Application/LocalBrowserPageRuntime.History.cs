using System;
using System.Linq;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    public sealed partial class LocalBrowserPageRuntime
    {
        private BrowserPageStateDto AcceptNativeState(Page page, BrowserPageStateDto state)
        {
            var previous = page.State; var value = Copy(state);
            value.VisitRevision = Math.Max(value.VisitRevision, previous.VisitRevision);
            var history = value.NativeHistory;
            if (value.Status == BrowserPageStatus.Ready && history != null &&
                (previous.NativeHistory == null || history.ControllerId != previous.NativeHistory.ControllerId || history.Revision > previous.NativeHistory.Revision))
            {
                ReconcileHistory(page, history, value.OwnerScope);
                if (previous.Url != value.Url) value.VisitRevision++;
            }
            value.Revision = Math.Max(value.Revision, previous.Revision) + 1;
            value.CanGoBack = page.Position > 0; value.CanGoForward = page.Position + 1 < page.History.Count;
            page.State = value; return Copy(value);
        }

        private static void ReconcileHistory(Page page, BrowserNativeHistory history, BrowserOwnerScope owner)
        {
            var entries = history.Entries;
            if (string.IsNullOrEmpty(history.ControllerId) || entries == null || entries.Length == 0 || entries.Length > 64 ||
                entries.Select(entry => entry.Id).Distinct().Count() != entries.Length ||
                !entries.Any(entry => entry.Id == history.CurrentEntryId))
                throw new InvalidOperationException("Invalid native Browser history.");
            foreach (var entry in entries) {
                BrowserLocalLocator locator;
                if (!BrowserLocalLocator.TryParse(entry.Locator, out locator)) throw new InvalidOperationException("Invalid native history address.");
            }
            var start = page.History.FindIndex(visit => visit.NativeController == history.ControllerId);
            var count = 1;
            if (start < 0) start = page.Position;
            else {
                count = page.History.Skip(start).TakeWhile(visit => visit.NativeController == history.ControllerId).Count();
                // A new native entry after Back drops the global forward branch too.
                if (!page.History.Skip(start).Take(count).Any(visit => visit.NativeEntry == history.CurrentEntryId))
                    count = page.History.Count - start;
            }
            page.History.RemoveRange(start, count);
            page.History.InsertRange(start, entries.Select(entry => new Visit { Owner = owner, Locator = entry.Locator,
                NativeController = history.ControllerId, NativeEntry = entry.Id }));
            page.Position = start + Array.FindIndex(entries, entry => entry.Id == history.CurrentEntryId);
            if (page.History.Count > 64) {
                var trim = Math.Min(page.Position, page.History.Count - 64);
                page.History.RemoveRange(0, trim); page.Position -= trim;
                if (page.History.Count > 64) page.History.RemoveRange(64, page.History.Count - 64);
            }
        }
    }
}
