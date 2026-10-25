(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon;
  var labels = { complete: "Готово", running: "Выполняется", warning: "С замечанием", error: "Ошибка", stopped: "Остановлено" };

  // Adjacent actions form stages; an explicit final answer closes the whole run.
  function project(message) {
    var items = [], group;
    (message.blocks || []).forEach(function (block, index) {
      if (block.type === "work") {
        if (!group) { group = { type: "activity", id: "activity-" + (block.id || index), blocks: [] }; items.push(group); }
        group.blocks.push(block);
      } else { group = null; items.push({ type: block.type, id: "content-" + index, block: block }); }
    });
    return items;
  }
  function disclosure(key, kind, body, defaultOpen) {
    var c = P.state.chats[P.state.chatId], expanded = c.activityExpanded && c.activityExpanded[key];
    return '<details class="' + kind + '" data-disclosure="' + E(key) + '"' + ((expanded == null ? defaultOpen : expanded) ? ' open' : '') + '>' + body + '</details>';
  }
  function symbol(state, icon) {
    return '<span class="work-symbol is-' + state + '" aria-hidden="true">' + (state === "running" ? '<span></span>' : I(state === "complete" ? (icon || "check") : state === "stopped" ? "stop" : "warning")) + '</span>';
  }
  function countLabel(count) {
    var tail = count % 100, last = count % 10;
    return count + " " + (tail >= 11 && tail <= 14 ? "действий" : last === 1 ? "действие" : last >= 2 && last <= 4 ? "действия" : "действий");
  }
  function action(item, key) {
    var state = item.resolved ? "complete" : item.state;
    var caption = item.resolved ? "Повтор помог" : item.result || (state === "error" || state === "warning" ? labels[state] : "");
    return disclosure(key, "timeline-action is-" + state,
      '<summary class="rna-history-summary" tabindex="0">' + symbol(state, item.icon || (item.target ? 'file' : 'search')) + '<span class="action-title' + (state === 'running' ? ' status-shimmer' : '') + '">' + E(item.title) + '</span><span class="action-result">' + E(caption) + '</span>' + I("right") + '</summary>' +
      '<div class="action-detail"><p>' + E(item.detail) + '</p>' + (item.target ? '<code>' + E(item.target) + '</code>' : '') + (item.resolved ? '<p class="resolved-note">Первая попытка не удалась. Повторное чтение выполнено успешно.</p>' : '') + '</div>', false);
  }
  function activity(item, key) {
    var work = item.blocks, steps = work.reduce(function (all, block) { return all.concat(block.steps); }, []);
    var running = work.find(function (block) { return block.state === "running"; });
    var issues = steps.filter(function (s) { return !s.resolved && (s.state === "error" || s.state === "warning"); });
    var stopped = work.some(function (block) { return block.state === "stopped"; });
    var state = running ? "running" : stopped ? "stopped" : issues.some(function (s) { return s.state === "error"; }) ? "error" : issues.length ? "warning" : "complete";
    var last = work[work.length - 1], title = running ? running.title : stopped ? "Работа остановлена" : state === "error" ? "Действие не выполнено" : last.summary || last.title;
    var body = work.map(function (block) { return block.steps.map(function (step, index) { return action(step, key + "/" + block.id + "/" + (step.id || index)); }).join(""); }).join("");
    var meta = countLabel(steps.length) + (issues.length ? " · " + issues.length + " замеч." : "") + (last.duration ? " · " + last.duration : "");
    return disclosure(key, "timeline-group is-" + state,
      '<summary class="rna-history-summary" tabindex="0">' + symbol(state === "running" ? "complete" : state, "layers") + '<span class="work-label' + (running ? ' status-shimmer' : '') + '">' + E(title) + '</span><span class="work-count">' + E(meta) + '</span>' + I("right") + '</summary><div class="timeline-actions">' + body + '</div>', !!running);
  }
  function renderItems(message, items) {
    return items.map(function (item, index) {
      var block = item.block, key = message.id + "/" + item.id, html;
      if (item.type === "activity") html = activity(item, key);
      else if (item.type === "text") html = '<p class="' + (block.phase === 'commentary' ? 'agent-comment' : 'agent-answer') + '">' + E(block.text) + '</p>';
      else if (item.type === "notice") html = disclosure(key, "timeline-notice is-" + (block.tone || "warning"), '<summary class="rna-history-summary" tabindex="0">' + I("warning") + '<span>' + E(block.title) + '</span>' + I("right") + '</summary><p>' + E(block.text) + '</p>', false);
      else if (item.type === "table") html = P.table(block.headers.map(E), block.rows.map(function (row) { return row.map(E); }));
      else if (item.type === "artifact") html = '<button class="artifact-link" data-go="materials/' + E(block.materialId) + '">' + I("file") + '<span class="row-copy"><strong>' + E(block.name) + '</strong><small>' + E(block.description) + '</small></span>' + I("right") + '</button>';
      else html = '';
      return '<div class="timeline-item" data-scroll-key="' + E(key) + '">' + html + '</div>';
    }).join("");
  }
  function completedHistory(message, items) {
    if (!items.length) return "";
    var steps = items.reduce(function (all, item) {
      return item.type === "activity" ? all.concat(item.blocks.flatMap(function (block) { return block.steps; })) : all;
    }, []);
    var issues = steps.some(function (step) { return !step.resolved && ["error", "warning"].includes(step.state); }) || items.some(function (item) { return item.type === "notice"; });
    var title = message.duration ? "Работа заняла " + message.duration : "Ход работы";
    return disclosure(message.id + "/run", "run-history",
      '<summary><span>' + E(title) + '</span>' + (steps.length ? '<span class="work-count">' + countLabel(steps.length) + '</span>' : '') +
      (issues ? '<span class="run-history-issues">Есть замечания</span>' : '') + I("right") + '</summary><div class="run-history-body">' + renderItems(message, items) + '</div>', false);
  }
  P.chatTimeline = { project: project };
  P.renderRunHistory = function (message) { return completedHistory(message, project(message)); };
  P.renderAgentMessage = function (message) {
    var items = project(message);
    var finalIndex = message.done ? items.findIndex(function (item) { return item.block && item.block.type === "text" && item.block.phase === "final"; }) : -1;
    if (finalIndex <= 0) return renderItems(message, items);
    var preceding = items.slice(0, finalIndex);
    // Limitations still affect the answer after the work history has collapsed.
    return completedHistory(message, preceding.filter(function (item) { return item.type !== "notice"; })) +
      renderItems(message, preceding.filter(function (item) { return item.type === "notice"; })) +
      renderItems(message, items.slice(finalIndex));
  };
}());
