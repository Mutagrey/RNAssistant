(function () {
  "use strict";
  var P = window.Prototype;
  P.diffUI = {
    slot: function (message, index) {
      return message.changes && (message.done || message.kind === "report") ? '<div class="run-changes-slot" data-run-changes="' + index + '"></div>' : '';
    },
    mount: function (root) {
      root = root || document;
      root.querySelectorAll('[data-product-diff="vba"]').forEach(function (target) {
        var name = P.state.vbaModule, key = "vba:" + name;
        var initial = name === "FormatSales" ? P.fixtures.vba : "Option Explicit\n\n' " + name + "\n";
        var before = name === "FormatSales" ? initial.replace('        .Font.Bold = True\n        .Interior.Color = RGB(232, 241, 233)\n', '') : initial;
        var after = P.state.drafts[key] == null ? P.state.saved[key] == null ? initial : P.state.saved[key] : P.state.drafts[key];
        RNAssistantTextDiff.render(target, RNAssistantTextDiff.format(before, after));
      });
      root.querySelectorAll('[data-run-changes]').forEach(function (target) {
        var chat = P.state.chats[P.state.chatId], index = Number(target.dataset.runChanges), message = chat.messages[index];
        if (!message || !message.changes) return;
        RNAssistantRunChanges.render(target, message.changes);
        target.querySelectorAll('details').forEach(function (details, i) {
          var key = 'changes-' + (message.id || index) + '-' + i;
          details.dataset.disclosure = key;
          details.open = !!(chat.activityExpanded && chat.activityExpanded[key]);
        });
      });
    }
  };
}());
