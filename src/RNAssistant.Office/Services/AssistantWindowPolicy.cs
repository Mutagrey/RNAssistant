using System;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Host-neutral policy; geometry and HWND effects belong to OfficeHosts.
    public sealed class AssistantWindowPolicy
    {
        public HostWindowPresentation Presentation { get; private set; } = HostWindowPresentation.FullNormal;
        public HostWindowVisibility Visibility { get; private set; } = HostWindowVisibility.Visible;
        public HostWindowPresentation RestoreBeforeMaximize { get; private set; } = HostWindowPresentation.FullNormal;
        public HostWindowPresentation ReturnFromCompanion { get; private set; } = HostWindowPresentation.FullNormal;
        public bool Pinned { get; private set; }
        public bool AutoCollapse { get; private set; } = true;
        public long Revision { get; private set; } = 1;
        public bool Alive { get { return Visibility != HostWindowVisibility.ShuttingDown; } }

        public void Load(HostWindowPresentation last, HostWindowPresentation beforeMaximize, bool pinned, bool autoCollapse)
        {
            if (!Enum.IsDefined(typeof(HostWindowPresentation), last) || last == HostWindowPresentation.Companion ||
                beforeMaximize != HostWindowPresentation.FullNormal && beforeMaximize != HostWindowPresentation.Compact)
                throw new ArgumentException("Unsupported window presentation preferences.");
            Presentation = ReturnFromCompanion = last;
            RestoreBeforeMaximize = beforeMaximize;
            Pinned = pinned; AutoCollapse = autoCollapse; Revision++;
        }

        public void Touch() { Revision++; }
        public void Resume()
        {
            if (!Alive) return;
            if (Presentation == HostWindowPresentation.Companion) Presentation = ReturnFromCompanion;
            Visibility = HostWindowVisibility.Visible; Revision++;
        }

        public void Apply(HostWindowCommandPayload request)
        {
            if (request == null || !Enum.IsDefined(typeof(HostWindowCommand), request.Command))
                throw new ArgumentException("Unknown window command.");
            if (!Alive) throw new InvalidOperationException("Сеанс RNAssistant завершается.");
            var command = request.Command;
            var setting = command == HostWindowCommand.SetPinned || command == HostWindowCommand.SetAutoCollapse;
            if (setting != request.Value.HasValue) throw new ArgumentException("Invalid window command value.");
            switch (command)
            {
                case HostWindowCommand.SetPinned: Pinned = request.Value.Value; break;
                case HostWindowCommand.SetAutoCollapse: AutoCollapse = request.Value.Value; break;
                case HostWindowCommand.ShowFull:
                    Presentation = HostWindowPresentation.FullNormal; Visibility = HostWindowVisibility.Visible; break;
                case HostWindowCommand.ShowCompact:
                    Presentation = HostWindowPresentation.Compact; Visibility = HostWindowVisibility.Visible; break;
                case HostWindowCommand.Maximize:
                    Resume();
                    if (Presentation != HostWindowPresentation.FullMaximized) RestoreBeforeMaximize = Presentation;
                    Presentation = HostWindowPresentation.FullMaximized; break;
                case HostWindowCommand.Restore:
                    if (Visibility == HostWindowVisibility.SystemMinimized || Presentation == HostWindowPresentation.Companion) Resume();
                    else if (Presentation == HostWindowPresentation.FullMaximized) Presentation = RestoreBeforeMaximize;
                    break;
                case HostWindowCommand.Park:
                    if (Presentation != HostWindowPresentation.Companion) ReturnFromCompanion = Presentation;
                    Presentation = HostWindowPresentation.Companion; Visibility = HostWindowVisibility.ContextHidden; break;
                case HostWindowCommand.SystemMinimize:
                    Resume(); Visibility = HostWindowVisibility.SystemMinimized; break;
                case HostWindowCommand.Shutdown: case HostWindowCommand.SwitchDocument:
                    Visibility = HostWindowVisibility.ShuttingDown; break;
                // These invalidate pending focus decisions, too. Their effects are host-owned.
                case HostWindowCommand.ChromeReady: case HostWindowCommand.ResetPlacement: case HostWindowCommand.BeginMove: break;
            }
            if (Presentation != HostWindowPresentation.Companion) ReturnFromCompanion = Presentation;
            Revision++;
        }

        public bool Collapse(long expectedRevision, bool foregroundKnown, bool interacting)
        {
            if (!Alive || Revision != expectedRevision || !foregroundKnown || interacting || Pinned || !AutoCollapse ||
                Visibility != HostWindowVisibility.Visible || Presentation == HostWindowPresentation.Companion) return false;
            Apply(new HostWindowCommandPayload { Command = HostWindowCommand.Park });
            return true;
        }

        public void CompanionVisibility(bool visible)
        {
            if (!Alive || Presentation != HostWindowPresentation.Companion ||
                Visibility == HostWindowVisibility.SystemMinimized) return;
            var next = visible ? HostWindowVisibility.Visible : HostWindowVisibility.ContextHidden;
            if (Visibility != next) { Visibility = next; Revision++; }
        }

        public HostWindowStateDto Snapshot()
        {
            return new HostWindowStateDto { Revision = Revision, Presentation = Presentation, Visibility = Visibility,
                Pinned = Pinned, AutoCollapse = AutoCollapse, CanMaximize = Alive && Presentation != HostWindowPresentation.FullMaximized,
                CanRestore = Alive && (Presentation == HostWindowPresentation.FullMaximized || Visibility == HostWindowVisibility.SystemMinimized),
                CanCompact = Alive && Presentation != HostWindowPresentation.Compact };
        }
    }
}
