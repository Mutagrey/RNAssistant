namespace RNAssistant.Office.Domains.Excel
{
    // Production composition exposes typed host backends for one already-bound
    // document session. Implementations must not resolve an active document.
    public interface IExcelBackendProvider
    {
        IExcelReadBackend ExcelReadBackend { get; }
        IExcelWriteBackend ExcelWriteBackend { get; }
    }
}
