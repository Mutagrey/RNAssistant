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

        private ResourceReadSelection ReadPowerPointCharacterWindow(ChatSession session,
            ResourceRef reference, string representation, int startChar, int charCount,
            CancellationToken cancellationToken)
        {
            if (representation != ResourceRepresentations.Text) return null;
            if (_powerPoint == null)
                throw new ResourceRequestException("The bound PowerPoint reader is unavailable.",
                    "RESOURCE_PROVIDER_UNAVAILABLE", false);
            cancellationToken.ThrowIfCancellationRequested();
            return _scope.Read(session, () =>
            {
                string target;
                if (!TryParseUri(session, reference?.Uri, out target) || !IsPowerPointSlide(target))
                    return null;
                var slideIndex = int.Parse(target.Substring(6), CultureInfo.InvariantCulture);
                PowerPointSlideTextWindowSnapshot window;
                try { window = new PowerPointService(_powerPoint).CaptureSlideTextWindow(
                    new PowerPointSlideTextWindowRequest { SlideIndex = slideIndex,
                        StartChar = startChar, CharCount = charCount }, cancellationToken); }
                catch (PowerPointBackendException error)
                { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
                var end = startChar + window.Text.Length;
                var selectedTarget = "slide-window-" + slideIndex.ToString(CultureInfo.InvariantCulture) +
                    "-" + startChar.ToString(CultureInfo.InvariantCulture) +
                    "-" + end.ToString(CultureInfo.InvariantCulture);
                var uri = CreateUri(session, selectedTarget);
                var hash = RNAssistant.Core.Tools.TextPatternEngine.Sha256(window.Text);
                var descriptor = new ResourceDescriptor { Reference = new ResourceRef(uri),
                    Provider = ProviderName, Kind = PowerPointSlideKind,
                    Title = slideIndex.ToString(CultureInfo.InvariantCulture),
                    MimeType = "text/plain; charset=utf-8", Mutable = true,
                    Parent = new ResourceRef(CreateUri(session, target)), ContentSha256 = hash };
                descriptor.Representations.Add(ResourceRepresentations.Text);
                return new ResourceReadSelection { Result = new ResourceReadResult {
                    Resource = descriptor, Representation = ResourceRepresentations.Text,
                    Text = window.Text, ContentSha256 = hash,
                    Offset = 0, ReturnedCharacters = window.Text.Length,
                    TotalCharacters = window.Text.Length, Coverage = ResourceCoverage.Whole(),
                    SourceCoverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                        start: startChar, end: end), Complete = true, SourceComplete = false,
                    SourceEndChar = window.TotalCharacters,
                    RawContentIncluded = true
                }, ResourceRefs = new[] { new ResourceRef(uri) } };
            });
        }

        private string ReadPowerPointStructure(string target)
        {
            var slideIndex = int.Parse(target.Substring(6), CultureInfo.InvariantCulture);
            try
            {
                var bounds = new PowerPointService(_powerPoint).CaptureSlideTextWindow(
                    new PowerPointSlideTextWindowRequest { SlideIndex = slideIndex,
                        StartChar = 0, CharCount = 0 }, CancellationToken.None);
                return new JObject { ["type"] = "rnassistant.powerPointSlide",
                    ["slideIndex"] = slideIndex, ["slideId"] = bounds.SlideId,
                    ["textStart"] = 0, ["textEnd"] = bounds.TotalCharacters,
                    ["windowSelector"] = "startChar + charCount" }.ToString(Formatting.None);
            }
            catch (PowerPointBackendException error)
            { throw new ResourceRequestException(error.Message, error.ErrorCode, error.Retryable); }
        }
    }
}
