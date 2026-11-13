using System;
using System.Threading.Tasks;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        private readonly Func<string, string, Task<Action>> _pauseRun;
        internal sealed class ManualOwner { internal Action Resume; internal bool Pending; internal bool Failed; }
        internal ManualOwner Manual { get; set; } = new ManualOwner();
        internal bool AgentOwnsInput { get; private set; }
        internal async Task SetAgentInput(bool value)
        {
            if (_closed || Controller == null) return;
            AgentOwnsInput = value; Core.Settings.AreDefaultScriptDialogsEnabled = !value;
            await Core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", value ? "{\"enabled\":true}" : "{\"enabled\":false}");
        }
        private async void ManualDialogs() { try { await SetAgentInput(false); } catch { } }
        internal bool NativeFocused { get; private set; }
        private bool _physicalNotificationQueued;
        internal void PhysicalInput()
        {
            if (_closed) return;
            InvalidateAutomation(); State.ControlRevision++; State.ViewportRevision++; State.ManualControl = true;
            if (_physicalNotificationQueued) return; _physicalNotificationQueued = true;
            // The OS hook only invalidates admission; presentation and host work run later.
            Control.BeginInvoke(new Action(async () => {
                _physicalNotificationQueued = false; if (_closed) return;
                if (AgentOwnsInput) ManualDialogs(); HideAction(); Publish();
                if (Manual.Pending || Manual.Resume != null || string.IsNullOrEmpty(State.OwnerScope.ChatId)) return;
                Manual.Pending = true;
                try { Manual.Resume = await _pauseRun(State.OwnerScope.ChatId, Guid.NewGuid().ToString("N")); }
                catch { Manual.Failed = true; if (!_closed) { State.Message = "Управление агента приостановлено. Подтвердите продолжение в чате."; Publish(); } }
                finally { Manual.Pending = false; }
            }));
        }
    }
}
