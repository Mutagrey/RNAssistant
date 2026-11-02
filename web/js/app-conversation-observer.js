(function () {
  "use strict";
  // One observer belongs to one view binding. Transport completion is independent
  // of UI lifetime; an obsolete response cannot resume or update that view.
  function create(options) {
    var inFlight = new Map(), refreshAfter = new Set(), timer = null;
    var interval = options.interval || 1200, epoch = 0, polling = false, suspended = false, disposed = false, needsRefresh = false;
    function key(target) { return target ? JSON.stringify(target) : ""; }
    function sameAddress(left, right) {
      return left && right && left.source === right.source && left.sessionId === right.sessionId &&
        (left.workspaceId || "") === (right.workspaceId || "");
    }
    function capture() {
      if (disposed || suspended) return null;
      var target = options.current(), binding = options.scope();
      if (!target || !binding.isCurrent() || !sameAddress(target, binding.target)) return null;
      target = Object.freeze(Object.assign({}, target));
      var stamp = epoch, id = key(target);
      return { target: target, key: JSON.stringify([stamp, binding.generation, id]),
        isCurrent: function () {
          return !disposed && !suspended && epoch === stamp && binding.isCurrent() && key(options.current()) === id;
        }
      };
    }
    function readNow() {
      var scope = capture();
      if (!scope) return Promise.resolve(null);
      var id = scope.key, existing = inFlight.get(id);
      if (existing) return existing;
      var request = Promise.resolve().then(function () {
        return scope.isCurrent() ? options.read(scope.target) : null;
      }).then(function (value) {
        if (scope.isCurrent()) options.changed(value, scope.target);
        return value;
      }).catch(function (error) {
        if (scope.isCurrent() && options.failed) options.failed(error, scope.target);
        return null;
      }).finally(function () {
        if (inFlight.get(id) === request) inFlight.delete(id);
        if (refreshAfter.delete(id) && scope.isCurrent()) readNow();
      });
      inFlight.set(id, request);
      return request;
    }
    function refresh() {
      if (suspended && !disposed) { needsRefresh = true; return Promise.resolve(null); }
      var scope = capture();
      if (!scope) return Promise.resolve(null);
      var existing = inFlight.get(scope.key);
      if (!existing) return readNow();
      refreshAfter.add(scope.key);
      return existing;
    }
    function clearTimer() { if (timer !== null) window.clearInterval(timer); timer = null; }
    function invalidate() { ++epoch; inFlight.clear(); refreshAfter.clear(); }
    function startTimer() {
      if (timer !== null || !polling || suspended || disposed) return;
      timer = window.setInterval(function () {
        var scope = capture();
        if (scope && (!options.shouldPoll || options.shouldPoll(scope.target))) readNow();
      }, interval);
    }
    function start() { if (!disposed) { polling = true; startTimer(); } }
    function stop() { polling = false; needsRefresh = false; clearTimer(); invalidate(); }
    function suspend() {
      if (suspended || disposed) return;
      needsRefresh = needsRefresh || inFlight.size > 0;
      suspended = true; clearTimer(); invalidate();
    }
    function resume() {
      if (!suspended || disposed) return;
      suspended = false; startTimer();
      var catchUp = needsRefresh, scope = polling && capture(); needsRefresh = false;
      if (catchUp || scope && (!options.shouldPoll || options.shouldPoll(scope.target))) refresh();
    }
    function dispose() {
      if (disposed) return;
      disposed = true; stop();
    }
    if (options.autoStart) start();
    return { readNow: readNow, refresh: refresh, start: start, stop: stop,
      capture: capture, suspend: suspend, resume: resume, dispose: dispose };
  }
  window.RNAssistantConversationObserver = { create: create };
})();
