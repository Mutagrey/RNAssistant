using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        private long _visualSequence;
        private long _visualGeneration;
        internal event Action FindRequested;
        internal BrowserAnnotationContext AnnotationContext()
        {
            string path;
            return State.ChatBound && Content != null && State.OwnerScope?.Matches(Content.Target.OwnerScope) == true && Controller != null &&
                Content.TryMember(Core.Source, out path) ? Content.AnnotationContext(path) : null;
        }
        internal BrowserAnnotationCapture CaptureAnnotation(BrowserAnnotationRequest request)
        {
            string path;
            if (!State.ChatBound || !Ready || request.SessionId != State.SessionId || request.Generation != State.DocumentGeneration ||
                !Content.TryMember(Core.Source, out path))
                throw new InvalidOperationException("Страница аннотации изменилась. Выберите элемент заново.");
            var capture = Content.Annotation(path);
            if (capture == null || !capture.Owner.Matches(State.OwnerScope) || capture.Context.SourceId != request.SourceId)
                throw new InvalidOperationException("Источник или чат аннотации изменился.");
            HtmlVisualSelectionPolicy.Capture(request.Selection, capture.Title, request.Comment, request.AnnotationNumber);
            return capture;
        }
        private async Task InitializeVisualAsync()
        {
            if (_visualGeneration > 0) await Evaluate<bool>("if(globalThis.__rnaVisualControl)__rnaVisualControl({generation:" + _visualGeneration + ",operation:'dispose'});true");
            _visualGeneration = State.DocumentGeneration;
            State.Visual = null; State.AnnotationContext = AnnotationContext();
            var script = File.ReadAllText(Path.Combine(_assets, "js", "app-visual-inspector.js"));
            await Evaluate<bool>(script + "\n;RNAssistantVisualInspector.install(" + State.DocumentGeneration +
                ",function(value){__rnaVisualEvent(JSON.stringify(value));});true");
        }
        private void AcceptVisual(string payload)
        {
            if (_closed || payload == null || payload.Length > 65536) return;
            try {
                var value = JsonConvert.DeserializeObject<BrowserVisualEvent>(payload, new JsonSerializerSettings { MaxDepth = 16, MissingMemberHandling = MissingMemberHandling.Error });
                if (value == null || !Enum.IsDefined(typeof(BrowserVisualEventKind), value.Type) || value.Generation != State.DocumentGeneration) return;
                if (value.Type == BrowserVisualEventKind.FindOpen && AgentOwnsInput) return;
                if (State.AnnotationContext == null && value.Type != BrowserVisualEventKind.Ready && value.Type != BrowserVisualEventKind.Find && value.Type != BrowserVisualEventKind.FindOpen) return;
                if (value.Type == BrowserVisualEventKind.Find && (value.Find == null || value.Find.Query?.Length > 160 || value.Find.RequestId < 1 || value.Find.Count < 0 || value.Find.Count > 160 || value.Find.Index < 0 || value.Find.Index > value.Find.Count)) return;
                if (value.Type == BrowserVisualEventKind.Selected) HtmlVisualSelectionPolicy.Capture(value.Selection, State.Title);
                else value.Selection = null;
                if (value.Locations?.Length > 100 || value.Locations?.Any(item => item == null || item.Id == null || item.Id.Length > 128) == true) return;
                if (value.Type == BrowserVisualEventKind.FindOpen) FindRequested?.Invoke();
                value.Sequence = ++_visualSequence; State.Visual = value; Publish();
            } catch { /* Malformed DOM evidence never acquires source authority. */ }
        }
        internal async Task VisualControlAsync(BrowserVisualControl value)
        {
            if (value == null || !Enum.IsDefined(typeof(BrowserVisualOperation), value.Operation) || value.Generation != State.DocumentGeneration || !Ready ||
                AnnotationContext() == null && value.Operation != BrowserVisualOperation.Find && value.Operation != BrowserVisualOperation.Probe)
                throw new InvalidOperationException("Страница выделения изменилась. Выберите элемент заново.");
            if (value.Operation == BrowserVisualOperation.Find && (value.Find == null || value.Find.Query == null || value.Find.Query.Length > 160 ||
                value.Find.RequestId < 1 || value.Find.RequestId > 9007199254740991L || value.Find.Direction < -1 || value.Find.Direction > 1))
                throw new InvalidOperationException("Некорректный поиск на странице.");
            if (value.Watches?.Length > 100 || value.Watches?.Any(item => item == null || item.Id == null || item.Id.Length > 128 ||
                item.Selector == null || item.Selector.Length > 1024 || item.Tag == null || item.Tag.Length > 32 || item.StableUiId?.Length > 128) == true || value.Id?.Length > 128)
                throw new InvalidOperationException("Выделение превышает допустимые границы.");
            Content.Validate();
            if (value.Operation == BrowserVisualOperation.Activate || value.Operation == BrowserVisualOperation.Find && value.Find.Query.Length > 0) PhysicalInput();
            await Evaluate<bool>("__rnaVisualControl(" + JsonConvert.SerializeObject(value) + ");true");
        }
    }
}
