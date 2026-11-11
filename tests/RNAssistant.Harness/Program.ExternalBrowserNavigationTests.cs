using System;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExternalBrowserNavigationEvidence()
        {
            var before = new ExternalBrowserReadTarget("chat", "page", 1);
            var after = new ExternalBrowserReadTarget("chat", "page", 3);
            var allowed = new[] { "https://*.example.com" };
            Func<ExternalBrowserNavigation> create = () => new ExternalBrowserNavigation(before, "https://example.com/start",
                url => ExternalBrowserPolicy.RequireAgentAccess(url, allowed));
            var navigation = create();
            navigation.Starting(8, "https://example.com/start", false);
            navigation.Starting(8, "https://sub.example.com/final", true);
            navigation.Completed(7, true, after, "https://sub.example.com/final");
            AssertTrue(!navigation.Completion.IsCompleted, "overlapping old completion cannot satisfy the operation");
            navigation.Completed(8, true, after, "https://sub.example.com/final");
            AssertEqual(ExternalBrowserEffect.VerifiedChange, navigation.Completion.Result.Effect, "allowed redirect plus exact terminal evidence");
            AssertTrue(navigation.Completion.Result.NavigationTarget.Matches(after), "result carries the new exact document binding");
            navigation = create(); navigation.Starting(9, "https://example.com/start", false);
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(9, "https://outside.invalid/", true));
            navigation.Unknown("browser_site_denied");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "redirect refusal after dispatch is not no-effect");
            navigation = create(); navigation.Starting(10, "https://example.com/start", false);
            allowed = new string[0]; navigation.Completed(10, true, after, "https://example.com/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "permission revocation cannot report success");
            allowed = new[] { "https://*.example.com" };
            navigation = create(); navigation.Starting(11, "https://example.com/start", false);
            navigation.Unknown("browser_user_control"); navigation.Completed(11, true, after, "https://example.com/start");
            AssertEqual("browser_user_control", navigation.Completion.Result.Code, "late completion cannot override user takeover");
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(11, "https://outside.invalid/late", true));
            navigation = create(); navigation.Starting(12, "https://example.com/start", false);
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(13, "https://example.com/other", false));
            navigation = create(); navigation.Starting(14, "https://example.com/start", false);
            navigation.Completed(14, true, new ExternalBrowserReadTarget("other-chat", "page", 3), "https://example.com/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "foreign owner is never navigation evidence");
            navigation = create(); navigation.Completed(null, true, after, "https://example.com/start");
            AssertEqual(ExternalBrowserEffect.VerifiedChange, navigation.Completion.Result.Effect, "same-document source change can complete without NavigationStarting");
            navigation = create(); navigation.Starting(15, "https://example.com/start", false);
            navigation.Completed(15, false, null, "https://example.com/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "failed load has no verified effect claim");
            navigation = new ExternalBrowserNavigation(before, "https://пример.рф/path", url =>
                ExternalBrowserPolicy.RequireAgentAccess(url, new[] { "https://пример.рф" }));
            navigation.Starting(16, "https://xn--e1afmkfd.xn--p1ai/path", false);
            navigation.Completed(16, true, after, "https://xn--e1afmkfd.xn--p1ai/path");
            AssertEqual(ExternalBrowserEffect.VerifiedChange, navigation.Completion.Result.Effect, "IDN spelling and Chromium punycode identify the same destination");
        }
    }
}
