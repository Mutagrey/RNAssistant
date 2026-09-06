using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.PowerPoint;

namespace RNAssistant.Office.Services
{
    internal sealed partial class LiveDocumentResourceProvider
    {
        internal const string PowerPointSlideKind = "powerpoint-slide";
        internal const string PowerPointSearchKind = "powerpoint-search-scope";
        private readonly IPowerPointBackend _powerPoint;
        internal bool IsPowerPoint { get { return string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase); } }

        internal ResourceDescriptor ResolvePowerPointSearch(ChatSession session, string scope)
        {
            if (!IsPowerPoint || _powerPoint == null)
                throw new ResourceRequestException("The bound PowerPoint reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var key = "search-" + scope.Replace(':', '-');
            if (!IsPowerPointSearch(key) || scope != key.Substring(7).Replace("slide-", "slide:"))
                throw new ResourceRequestException("Use deck or slide:N, optionally followed by +notes.", "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () => Describe(session, key));
        }

        private static bool IsPowerPointSearch(string target)
        {
            if (target == null || !target.StartsWith("search-", StringComparison.Ordinal)) return false;
            var scope = target.Substring(7);
            if (scope.EndsWith("+notes", StringComparison.Ordinal)) scope = scope.Substring(0, scope.Length - 6);
            return scope == "deck" || IsPowerPointSlide(scope);
        }

        private string ReadPowerPointSearch(string target)
        {
            var notes = target.EndsWith("+notes", StringComparison.Ordinal);
            var scope = target.Substring(7);
            if (notes) scope = scope.Substring(0, scope.Length - 6);
            try
            {
                var snapshot = new PowerPointService(_powerPoint).CaptureSearch(scope == "deck" ? 0 :
                    int.Parse(scope.Substring(6), CultureInfo.InvariantCulture), notes, CancellationToken.None);
                var json = new JObject { ["slideIndex"] = snapshot.SlideIndex, ["includeNotes"] = notes,
                    ["targets"] = new JArray(snapshot.Targets.Select(item => new JObject {
                        ["slideIndex"] = item.SlideIndex, ["shapeName"] = item.ShapeName,
                        ["kind"] = item.Kind, ["text"] = item.Text })) }.ToString(Formatting.None);
                if (json.Length > MaximumMaterializedCharacters)
                    throw new ResourceRequestException("Choose a smaller PowerPoint search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                return json;
            }
            catch (PowerPointBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }

        internal ResourceDescriptor ResolvePowerPointSlide(ChatSession session, string target)
        {
            if (!IsPowerPoint || _powerPoint == null)
                throw new ResourceRequestException("The bound PowerPoint reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var key = "slide-" + target.Substring("PowerPoint slide: ".Length);
            if (!IsPowerPointSlide(key))
                throw new ResourceRequestException("Use PowerPoint slide: N with a positive one-based index.", "RESOURCE_TARGET_INVALID", false);
            return _scope.Read(session, () => Describe(session, key));
        }

        private static bool IsPowerPointSlide(string target)
        {
            int index;
            return target != null && target.StartsWith("slide-", StringComparison.Ordinal) &&
                int.TryParse(target.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out index) &&
                index > 0 && target == "slide-" + index.ToString(CultureInfo.InvariantCulture);
        }

        private string ReadPowerPointSource(string target, bool includeNotes)
        {
            if (_powerPoint == null)
                throw new ResourceRequestException("The bound PowerPoint reader is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
            var request = new PowerPointReadSlidesRequest
            {
                HasSlideIndex = target != "root",
                SlideIndex = target == "root" ? 0 : int.Parse(target.Substring(6), CultureInfo.InvariantCulture),
                MaxSlides = PowerPointService.MaxSlides,
                MaxShapesPerSlide = PowerPointService.MaxShapesPerSlide,
                MaxCharacters = PowerPointService.MaximumTextCharacters
            };
            try
            {
                var snapshot = new PowerPointService(_powerPoint).CaptureSlides(request, CancellationToken.None);
                var content = includeNotes
                    ? new JArray(snapshot.Slides.Select(slide => new JObject
                    {
                        ["slideId"] = slide.SlideId, ["index"] = slide.Index,
                        ["text"] = slide.Text, ["notes"] = slide.Notes
                    })).ToString(Formatting.None)
                    : string.Concat(snapshot.Slides.Select(slide =>
                        "Slide " + slide.Index.ToString(CultureInfo.InvariantCulture) + ":\n" + slide.Text));
                if (content.Length > MaximumMaterializedCharacters)
                    throw new ResourceRequestException("Choose a smaller PowerPoint source.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                return content;
            }
            catch (PowerPointBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }
    }
}
