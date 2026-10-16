(function (root) {
  "use strict";

  // Presentation only: the caller owns the operation, counters, clock and cancellation.
  // The component never advances a stage, starts a timer or interprets an operation result.
  function create(container, actions) {
    const part = name => container.querySelector('[data-operation-part="' + name + '"]');
    const cancel = part("cancel"), dismiss = part("dismiss");
    const cancelClick = () => actions.cancel();
    const dismissClick = () => actions.dismiss();
    cancel.addEventListener("click", cancelClick);
    dismiss.addEventListener("click", dismissClick);
    function render(snapshot) {
      if (!snapshot) { container.hidden = true; return; }
      const measure = snapshot.measure;
      if (!["running", "completed", "cancelled", "failed"].includes(snapshot.state) ||
          !measure || !Number.isSafeInteger(measure.completed) || measure.completed < 0 ||
          measure.total !== null && (!Number.isSafeInteger(measure.total) || measure.total < measure.completed) ||
          !Number.isFinite(snapshot.elapsedMs) || snapshot.elapsedMs < 0)
        throw new TypeError("Invalid operation-view snapshot");
      container.hidden = false;
      container.dataset.state = snapshot.state;
      const running = snapshot.state === "running";
      part("stage").textContent = snapshot.stage;
      part("title").textContent = snapshot.title;
      part("current").textContent = snapshot.currentItem || "";
      part("current").title = snapshot.currentItem || "";
      part("current").hidden = !running || !snapshot.currentItem;
      const seconds = Math.floor(snapshot.elapsedMs / 1000);
      part("elapsed").textContent = seconds < 60 ? seconds + " с" : Math.floor(seconds / 60) + " мин " + seconds % 60 + " с";
      part("elapsed").title = "Прошло времени";
      part("count").textContent = measure.label + ": " + measure.completed + (measure.total === null ? "" : " из " + measure.total);
      part("detail").textContent = snapshot.detail;
      part("note").textContent = snapshot.note;
      part("meter").hidden = !running;
      const progress = part("progress");
      if (measure.total !== null && measure.total > 0) {
        progress.max = measure.total;
        progress.value = measure.completed;
        part("percent").textContent = Math.floor(measure.completed / measure.total * 100) + "%";
        progress.setAttribute("aria-valuetext", part("count").textContent);
      } else {
        progress.removeAttribute("value");
        progress.removeAttribute("aria-valuetext");
        part("percent").textContent = "Всего пока неизвестно";
      }
      cancel.hidden = !running;
      dismiss.hidden = running;
    }
    return Object.freeze({ render: render, dispose: () => {
      cancel.removeEventListener("click", cancelClick);
      dismiss.removeEventListener("click", dismissClick);
      container.hidden = true;
    } });
  }
  root.RNAssistantDataOperationView = Object.freeze({ create: create });
}(window));
