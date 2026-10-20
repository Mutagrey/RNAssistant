using System;
using RNAssistant.Core.Models;
using RNAssistant.Runtime.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    internal sealed class WorkspaceOfficeCapabilities : IWorkspaceOfficeCapabilities
    {
        private readonly Func<AssistantController> _controller;
        internal WorkspaceOfficeCapabilities(Func<AssistantController> controller)
        { _controller = controller ?? throw new ArgumentNullException(nameof(controller)); }

        public WorkspaceOfficeRunTarget CaptureCurrentTarget()
        { return _controller().CaptureWorkspaceOfficeTarget(); }

        public WorkspaceOfficeCapabilitySet ForTarget(WorkspaceOfficeRunTarget target,
            ChatSession workspaceSession, AppSettings settings, string runId)
        { return _controller().CaptureWorkspaceOfficeCapabilities(target, workspaceSession, settings, runId); }
    }

    public sealed partial class AssistantController
    {
        internal WorkspaceOfficeRunTarget CaptureWorkspaceOfficeTarget()
        { return new WorkspaceOfficeCapabilityService(_adapter, _toolExecutor, _paths).CaptureCurrentTarget(); }

        internal WorkspaceOfficeCapabilitySet CaptureWorkspaceOfficeCapabilities(WorkspaceOfficeRunTarget target,
            ChatSession workspaceSession, AppSettings settings, string runId)
        { return new WorkspaceOfficeCapabilityService(_adapter, _toolExecutor, _paths)
            .ForTarget(target, workspaceSession, settings, runId); }
    }
}
