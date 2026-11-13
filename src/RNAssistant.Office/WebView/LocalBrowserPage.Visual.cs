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
        internal BrowserAnnotationContext AnnotationContext()
        {
            if (string.IsNullOrEmpty(State.OwnerScope?.ChatId)) return null;
            if (Content.Plugin != null && State.OwnerScope.Kind == BrowserOwnerKind.Workspace &&
                Content.Plugin.Release.SourceWorkspaceId == State.OwnerScope.ScopeId)
                return new BrowserAnnotationContext { PluginInstanceId = Content.Plugin.Id,
                    SourceId = Content.Plugin.Release.ReleaseId, SourceRevision = 1 };
            if (Content.HtmlWorkspace != null && State.OwnerScope.Kind == BrowserOwnerKind.Document &&
                !string.IsNullOrEmpty(Content.Target.ArtifactId) && Content.Target.Revision > 0)
                return new BrowserAnnotationContext { SourceId = Content.Target.ArtifactId, SourceRevision = Content.Target.Revision };
            return null;
        }
        private async Task InitializeVisualAsync()
        {
            if (_visualGeneration > 0) await Evaluate<bool>("if(globalThis.__rnaVisualControl)__rnaVisualControl({generation:" + _visualGeneration + ",operation:'dispose'});true");
            _visualGeneration = State.DocumentGeneration;
            State.Visual = null; State.AnnotationContext = AnnotationContext();
            if (State.AnnotationContext == null) return;
            var script = File.ReadAllText(Path.Combine(_assets, "js", "app-visual-inspector.js"));
            await Evaluate<bool>(script + "\n;RNAssistantVisualInspector.install(" + State.DocumentGeneration +
                ",function(value){__rnaVisualEvent(JSON.stringify(value));});true");
        }
        private void AcceptVisual(string payload)
        {
            if (_closed || payload == null || payload.Length > 65536 || State.AnnotationContext == null) return;
            try {
                var value = JsonConvert.DeserializeObject<BrowserVisualEvent>(payload, new JsonSerializerSettings { MaxDepth = 16, MissingMemberHandling = MissingMemberHandling.Error });
                if (value == null || !Enum.IsDefined(typeof(BrowserVisualEventKind), value.Type) || value.Generation != State.DocumentGeneration) return;
                if (value.Type == BrowserVisualEventKind.Selected) HtmlVisualSelectionPolicy.Capture(value.Selection, State.Title);
                else value.Selection = null;
                if (value.Locations?.Length > 100 || value.Locations?.Any(item => item == null || item.Id == null || item.Id.Length > 128) == true) return;
                value.Sequence = ++_visualSequence; State.Visual = value; Publish();
            } catch { /* Malformed DOM evidence never acquires source authority. */ }
        }
        internal async Task VisualControlAsync(BrowserVisualControl value)
        {
            if (value == null || !Enum.IsDefined(typeof(BrowserVisualOperation), value.Operation) || value.Generation != State.DocumentGeneration || !Ready || AnnotationContext() == null)
                throw new InvalidOperationException("Страница выделения изменилась. Выберите элемент заново.");
            if (value.Watches?.Length > 100 || value.Watches?.Any(item => item == null || item.Id == null || item.Id.Length > 128 ||
                item.Selector == null || item.Selector.Length > 1024 || item.Tag == null || item.Tag.Length > 32 || item.StableUiId?.Length > 128) == true || value.Id?.Length > 128)
                throw new InvalidOperationException("Выделение превышает допустимые границы.");
            Content.Validate();
            if (value.Operation == BrowserVisualOperation.Activate) PhysicalInput();
            await Evaluate<bool>("__rnaVisualControl(" + JsonConvert.SerializeObject(value) + ");true");
        }
    }
}
