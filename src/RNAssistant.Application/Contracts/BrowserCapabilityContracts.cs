using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Application.Contracts
{
    // Browser is independently contributed to Office and standalone Workspace runs.
    // No Office target, native controller or alternate context/store is introduced here.
    public interface IBrowserCapabilityContribution
    {
        BrowserCapabilitySet ForOwner(BrowserOwnerScope owner, ChatSession session, AppSettings settings,
            string runId, Action<string, IReadOnlyList<ChatAttachment>> captureMedia);
    }

    public sealed class BrowserCapabilitySet
    {
        public IReadOnlyList<WorkspaceToolContribution> Tools { get; set; }
        public IReadOnlyToolHandler ResourceFind { get; set; }
        public IReadOnlyToolHandler ResourceRead { get; set; }
        public IReadOnlyToolHandler ResourceSearch { get; set; }
        public RNAssistant.Core.Services.ModelResultProjection ModelProjection { get; set; }
    }
}
