using System;
using System.Linq;
using RNAssistant.Core.Services;
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
            var allowed = new[] { "https://page.local.invalid/start", "https://page.local.invalid/final" };
            Func<ExternalBrowserNavigation> create = () => new ExternalBrowserNavigation(before, "https://page.local.invalid/start",
                url => { if (!allowed.Contains(url)) throw ExternalBrowserResourceProvider.Error("Local grant revoked.", "browser_site_denied"); });
            var navigation = create();
            navigation.Starting(8, "https://page.local.invalid/start", false);
            navigation.Starting(8, "https://page.local.invalid/final", true);
            navigation.Completed(7, true, after, "https://page.local.invalid/final");
            AssertTrue(!navigation.Completion.IsCompleted, "overlapping old completion cannot satisfy the operation");
            navigation.Completed(8, true, after, "https://page.local.invalid/final");
            AssertEqual(ExternalBrowserEffect.VerifiedChange, navigation.Completion.Result.Effect, "allowed redirect plus exact terminal evidence");
            AssertTrue(navigation.Completion.Result.NavigationTarget.Matches(after), "result carries the new exact document binding");
            navigation = create(); navigation.Starting(9, "https://page.local.invalid/start", false);
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(9, "https://outside.invalid/", true));
            navigation.Unknown("browser_site_denied");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "redirect refusal after dispatch is not no-effect");
            navigation = create(); navigation.Starting(10, "https://page.local.invalid/start", false);
            allowed = new string[0]; navigation.Completed(10, true, after, "https://page.local.invalid/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "permission revocation cannot report success");
            allowed = new[] { "https://page.local.invalid/start", "https://page.local.invalid/final" };
            navigation = create(); navigation.Starting(11, "https://page.local.invalid/start", false);
            navigation.Unknown("browser_user_control"); navigation.Completed(11, true, after, "https://page.local.invalid/start");
            AssertEqual("browser_user_control", navigation.Completion.Result.Code, "late completion cannot override user takeover");
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(11, "https://outside.invalid/late", true));
            navigation = create(); navigation.Starting(12, "https://page.local.invalid/start", false);
            RuntimeThrows<ResourceRequestException>(() => navigation.Starting(13, "https://page.local.invalid/other", false));
            navigation = create(); navigation.Starting(14, "https://page.local.invalid/start", false);
            navigation.Completed(14, true, new ExternalBrowserReadTarget("other-chat", "page", 3), "https://page.local.invalid/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "foreign owner is never navigation evidence");
            navigation = create(); navigation.Completed(null, true, after, "https://page.local.invalid/start");
            AssertEqual(ExternalBrowserEffect.VerifiedChange, navigation.Completion.Result.Effect, "same-document source change can complete without NavigationStarting");
            navigation = create(); navigation.Starting(15, "https://page.local.invalid/start", false);
            navigation.Completed(15, false, null, "https://page.local.invalid/start");
            AssertEqual(ExternalBrowserEffect.Unknown, navigation.Completion.Result.Effect, "failed load has no verified effect claim");

        }
    }
}
