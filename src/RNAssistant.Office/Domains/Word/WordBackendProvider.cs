namespace RNAssistant.Office.Domains.Word
{
    // One provider instance belongs to one already-bound Word document session.
    // Implementations must not resolve ActiveDocument during tool execution.
    public interface IWordBackendProvider
    {
        IWordBackend WordBackend { get; }
    }
}
