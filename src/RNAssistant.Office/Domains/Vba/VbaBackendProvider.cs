namespace RNAssistant.Office.Domains.Vba
{
    public static class VbaBackendProvider
    {
        public static IVbaHostBackend Resolve(IOfficeApplicationAdapter adapter)
        {
            var provider = adapter as IVbaHostBackendProvider;
            return provider == null ? null : provider.VbaHostBackend;
        }
    }
}
