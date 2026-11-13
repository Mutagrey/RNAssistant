using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    public static partial class BrowserTargetResolver
    {
        // Catalogs are supplied by existing owners after caller binding validation, never from the page.
        public static BrowserResolveResult ResolveLocalLocator(BrowserOwnerScope caller, string locator,
            IEnumerable<BrowserTargetDescriptor> catalog)
        {
            BrowserLocalLocator parsed;
            if (!BrowserLocalLocator.TryParse(locator, out parsed)) return Refuse(BrowserRefusalCode.InvalidLocator, "Укажите локальный адрес Browser.");
            if (!ValidOwner(caller)) return Refuse(BrowserRefusalCode.OutsideScope, "Контекст Browser недоступен.");
            if (parsed.Kind == BrowserLocatorKind.Home)
                return new BrowserResolveResult { Locator = "/", Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.Home,
                    State = BrowserTargetState.Empty, OwnerScope = caller, DisplayLocator = "/", Title = "Главная", Capabilities = new BrowserTargetCapabilities() } };
            var matches = (catalog ?? Enumerable.Empty<BrowserTargetDescriptor>()).Where(item => Accessible(caller, item) &&
                item.DisplayLocator == parsed.ContentLocator).Take(2).ToArray();
            if (matches.Length != 1) return Refuse(matches.Length == 0 ? BrowserRefusalCode.TargetUnavailable : BrowserRefusalCode.AmbiguousTarget,
                "Локальная цель недоступна или неоднозначна. Выберите её из списка источников.");
            var target = matches[0];
            return new BrowserResolveResult { Locator = parsed.Canonical, Target = target, Reason = target.State == BrowserTargetState.Removed ? BrowserRefusalCode.Removed : target.Reason,
                Message = target.Message };
        }

        public static BrowserSuggestionPage QueryNavigationSuggestions(BrowserOwnerScope caller, BrowserSuggestionRequest request,
            IEnumerable<BrowserTargetDescriptor> catalog)
        {
            if (request == null || !ValidOwner(caller) || request.RequestGeneration < 0 || request.Limit < 1 || request.Limit > 20 ||
                (request.Prefix ?? "").Length > 2048 || (request.Prefix ?? "").Any(char.IsControl))
                throw new ArgumentException("Invalid local Browser suggestion request.");
            var prefix = request.Prefix ?? "";
            var items = (catalog ?? Enumerable.Empty<BrowserTargetDescriptor>()).Where(item => Accessible(caller, item) &&
                ((item.DisplayLocator ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                 (item.Title ?? "").IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)).Take(request.Limit)
                .Select(item => new BrowserNavigationSuggestion { Title = item.Title, Kind = item.Kind,
                    DisplayLocator = item.DisplayLocator, SourceLabel = item.OwnerScope.ScopeId,
                    DisabledReason = item.State == BrowserTargetState.Removed ? BrowserRefusalCode.Removed : item.Reason }).ToArray();
            return new BrowserSuggestionPage { Items = items, RequestGeneration = request.RequestGeneration };
        }
        private static bool ValidOwner(BrowserOwnerScope owner)
        { return owner != null && Enum.IsDefined(typeof(BrowserOwnerKind), owner.Kind) && !string.IsNullOrWhiteSpace(owner.ScopeId) &&
            owner.ScopeId.Length <= 256 && (owner.ChatId ?? "").Length <= 256 && (owner.Kind != BrowserOwnerKind.User || string.IsNullOrEmpty(owner.ChatId)); }
        private static bool Accessible(BrowserOwnerScope caller, BrowserTargetDescriptor target)
        { return target?.OwnerScope != null && (caller.Kind == BrowserOwnerKind.User || caller.Matches(target.OwnerScope)); }
        private static BrowserResolveResult Refuse(BrowserRefusalCode code, string message)
        { return new BrowserResolveResult { Reason = code, Message = message }; }
    }
}
