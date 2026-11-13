using System;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // Admission belongs to the host, never to a page, setting, environment flag or model.
    // B02 remains open: no supported WebView2 transport isolation has been qualified.
    public static class BrowserIsolationPolicy
    {
        public static BrowserRefusalCode ExecutionRefusal => BrowserRefusalCode.IsolationUnqualified;
        public const string Message = "Исполнение страницы недоступно: сетевая изоляция WebView2 ещё не подтверждена. Исходники и версии доступны.";
        public static void RequireExecution()
        { throw new InvalidOperationException(Message); }
        public static BrowserTargetDescriptor SourceOnly(BrowserTargetDescriptor target)
        {
            if (target?.Capabilities == null) return target;
            target.Capabilities.Preview = false;
            target.Capabilities.Interact = false;
            target.Capabilities.Media = false;
            target.Capabilities.Annotations = false;
            target.Capabilities.PreviewReason = ExecutionRefusal;
            return target;
        }
    }
}
