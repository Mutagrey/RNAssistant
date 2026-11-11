(function () {
  "use strict";
  if (window.top !== window || globalThis.__rnaBrowserControl) return;
  var manual = false, epoch = 0;
  function invalidate() {
    if (globalThis.__rnaBrowser) globalThis.__rnaBrowser.invalidate();
    if (globalThis.__rnaBrowserVisual) globalThis.__rnaBrowserVisual.hide();
  }
  ['pointerdown', 'keydown', 'input', 'change', 'wheel'].forEach(function (type) {
    document.addEventListener(type, function (event) {
      if (!event.isTrusted) return;
      manual = true; epoch++; invalidate();
      if (typeof globalThis.__rnaUserControl === 'function') globalThis.__rnaUserControl(JSON.stringify({ epoch: epoch }));
    }, { capture: true, passive: true });
  });
  globalThis.__rnaBrowserControl = {
    snapshot: function () { return { manual: manual, epoch: epoch }; },
    hold: function () { manual = true; invalidate(); return true; },
    resume: function (expectedEpoch) {
      if (epoch !== expectedEpoch) return false;
      invalidate(); manual = false; return true;
    }
  };
}());
