(function () {
  "use strict";

  // Addresses include the shell's navigation generation. A late read from an
  // earlier visit cannot replace a newer visit to the same conversation.
  function create(options) {
    var inFlight = new Map(), refreshAfter = new Set(), timer = null;
    var interval = options.interval || 1200;
    function key(target) { return target ? JSON.stringify(target) : ""; }
    function currentKey() { return key(options.current()); }
    function readNow() {
      var target = options.current();
      if (!target) return Promise.resolve(null);
      var id = key(target), existing = inFlight.get(id);
      if (existing) return existing;
      var request = Promise.resolve().then(function () { return options.read(target); })
        .then(function (value) {
          if (currentKey() === id) options.changed(value, target);
          return value;
        }).catch(function (error) {
          if (currentKey() === id && options.failed) options.failed(error, target);
          return null;
        }).finally(function () {
          if (inFlight.get(id) === request) inFlight.delete(id);
          if (refreshAfter.delete(id) && currentKey() === id) readNow();
        });
      inFlight.set(id, request);
      return request;
    }
    function refresh() {
      var target = options.current();
      if (!target) return Promise.resolve(null);
      var id = key(target), existing = inFlight.get(id);
      if (!existing) return readNow();
      refreshAfter.add(id);
      return existing;
    }
    function start() {
      if (timer) return;
      timer = window.setInterval(function () {
        var target = options.current();
        if (target && (!options.shouldPoll || options.shouldPoll(target))) readNow();
      }, interval);
    }
    function stop() { if (timer) window.clearInterval(timer); timer = null; }
    if (options.autoStart) start();
    return { readNow: readNow, refresh: refresh, start: start, stop: stop };
  }

  window.RNAssistantConversationObserver = { create: create };
})();
