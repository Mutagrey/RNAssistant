using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private void RecoverAfterRunStoreFailure(
            ChatRunLease runLease,
            ChatSession failedProjection,
            string sessionId,
            ref RunCausalTrace causalTrace)
        {
            try
            {
                // Recovery must observe canonical storage after this invocation no
                // longer owns either the in-process registry entry or run file lock.
                ReleaseControllerRun(runLease, ref causalTrace);
            }
            catch (Exception releaseError)
            {
                RuntimeLog.Error(
                    "Run-store failure ownership release failed for session " + sessionId +
                    "; recovery deferred.",
                    releaseError);
                return;
            }

            try
            {
                _chatSessions.ReloadAndReconcileInterruptedRun(
                    failedProjection == null ? null : failedProjection.Host,
                    failedProjection == null ? null : failedProjection.DocumentKey,
                    sessionId);
            }
            catch (Exception recoveryError)
            {
                // Never replace the original RunStoreException or persist its
                // in-memory summary. Startup recovery remains the fallback.
                RuntimeLog.Error(
                    "Run-store failure canonical recovery failed for session " + sessionId +
                    "; startup recovery required.",
                    recoveryError);
            }
        }
    }
}
