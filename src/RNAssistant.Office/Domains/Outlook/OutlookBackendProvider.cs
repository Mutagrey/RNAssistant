namespace RNAssistant.Office.Domains.Outlook
{
    public interface IOutlookBackendProvider
    {
        IOutlookBackend OutlookBackend { get; }
    }
}
