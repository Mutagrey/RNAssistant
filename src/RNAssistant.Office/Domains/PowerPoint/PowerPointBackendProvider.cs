namespace RNAssistant.Office.Domains.PowerPoint
{
    // One provider instance belongs to one already-bound presentation session.
    // Implementations must not resolve ActivePresentation during tool execution.
    public interface IPowerPointBackendProvider
    {
        IPowerPointBackend PowerPointBackend { get; }
    }
}
