using System;
using System.Threading;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private readonly DiagnosticLogStore _diagnosticLogs;

        public DiagnosticLogInventoryDto ListDiagnosticLogs(CancellationToken cancellationToken = default(CancellationToken))
        { return DiagnosticLogInventoryDto.From(_diagnosticLogs.List(cancellationToken)); }

        public DiagnosticLogPageDto ReadDiagnosticLog(DiagnosticLogReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return DiagnosticLogPageDto.From(_diagnosticLogs.Read(request.FileId, request.BeforeOffset,
                request.SnapshotToken, request.MaxBytes, cancellationToken));
        }

        public DiagnosticLogMutationDto ClearDiagnosticLog(DiagnosticLogFileRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return DiagnosticLogMutationDto.From(_diagnosticLogs.Clear(request.FileId));
        }

        public DiagnosticLogMutationDto DeleteDiagnosticLog(DiagnosticLogFileRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return DiagnosticLogMutationDto.From(_diagnosticLogs.Delete(request.FileId));
        }

        public DiagnosticLogMutationDto ClearDiagnosticLogs()
        { return DiagnosticLogMutationDto.From(_diagnosticLogs.ClearAll()); }

        public DiagnosticLogMutationDto PruneDiagnosticLogs()
        { return DiagnosticLogMutationDto.From(_diagnosticLogs.Prune()); }

        public DiagnosticLogMutationDto SaveDiagnosticLogOptions(DiagnosticLogOptionsDto request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return DiagnosticLogMutationDto.From(_diagnosticLogs.SaveOptions(request.ToOptions()));
        }

    }
}
