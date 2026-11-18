using System;
using System.Threading.Tasks;

namespace RNAssistant.Office
{
    public interface IOfficeStaDispatcher
    {
        bool CheckAccess { get; }
        T Invoke<T>(Func<T> action);
    }

    // Optional native-operation lifetime. A panel may request dispatcher disposal
    // while a retained document operation still needs its owner STA.
    public interface IOfficeDispatcherLifetime
    {
        IDisposable RetainForDocumentOperation();
        Task DocumentOperationCompletion { get; }
    }

    // Optional owner-STA observation. Null is unavailable evidence; false is a
    // positively observed open document. A generic COM error is never closure.
    public interface IOfficeDocumentClosureProbe
    {
        bool? ProbeDocumentClosed();
    }

    // Implementations represent one live document object, not a snapshot of its name/path.
    // A durable identity change must not replace the runtime identity, object or gate.
    // Host/runtime identity, dispatcher and gate are immutable cached metadata, safe
    // to inspect before dispatch. Stable identity, liveness and the bound object must
    // be inspected on StaDispatcher, within the document access scope.
    public interface IOfficeDocumentSession
    {
        string Host { get; }
        string StableDocumentId { get; }
        string RuntimeDocumentId { get; }
        object BoundDocumentObject { get; }
        bool IsAlive { get; }
        IOfficeStaDispatcher StaDispatcher { get; }
        object MutationGate { get; }
    }

    public interface IOfficeDocumentSessionProvider
    {
        // This reference is fixed for the adapter's lifetime. Rebinding requires a
        // new adapter; a getter must not select another document or perform COM work.
        // Null means the adapter has not switched to a bound document session.
        IOfficeDocumentSession DocumentSession { get; }
    }

    public interface IOfficeDispatcherProvider
    {
        IOfficeStaDispatcher StaDispatcher { get; }
    }
}
