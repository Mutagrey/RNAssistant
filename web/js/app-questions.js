// Transient form drafts only. The runtime validates and accepts every answer/skip.
(function () {
  "use strict";
  var drafts = new Map(), sequence = 0;
  function element(tag, className, text) {
    var node = document.createElement(tag); node.className = className || "";
    if (text != null) node.textContent = text;
    return node;
  }
  function iconButton(icon, label, className) {
    var button = element("button", "icon-btn " + className);
    button.type = "button"; button.title = label; button.setAttribute("aria-label", label);
    button.innerHTML = window.RNAssistantIcons.render(icon);
    return button;
  }
  function render(container, questions, options) {
    var key = options.runId + ":" + questions.questionSetId, entry = drafts.get(options.scope);
    if (!entry || entry.key !== key) {
      entry = { key: key, page: 0, busy: false, accepted: false, error: "" };
      drafts.set(options.scope, entry);
      create(entry, questions, options.runId);
    }
    entry.submit = options.submit; entry.container = container;
    if (entry.accepted) { container.replaceChildren(); container.hidden = true; return; }
    if (entry.form.parentNode !== container) container.replaceChildren(entry.form);
    container.hidden = false;
  }
  function create(entry, data, runId) {
    var form = element("form", "plan-question-cards"), prefix = "question-form-" + (++sequence) + "-";
    entry.form = form;
    form.setAttribute("aria-label", "Ответы на вопросы");
    var header = element("div", "question-header"), title = element("span", "question-title");
    title.innerHTML = window.RNAssistantIcons.render("chat"); title.appendChild(element("span", "", "Вопрос"));
    var navigation = element("div", "question-navigation");
    var previous = iconButton("left", "Предыдущий вопрос", "question-previous");
    var next = iconButton("right", "Следующий вопрос", "question-next");
    var counter = element("span", "question-counter"); counter.setAttribute("aria-live", "polite");
    navigation.append(previous, counter, next);
    var skipAll = iconButton("close", "Пропустить всё", "question-close");
    header.append(title, navigation, skipAll); form.appendChild(header);
    var fields = data.questions.map(function (question, index) {
      var group = element("fieldset", "question-card"); group.tabIndex = -1;
      group.appendChild(element("legend", "question-prompt", question.prompt));
      if (question.selection === "multiple") {
        var hint = element("p", "question-hint", "Можно выбрать несколько");
        hint.id = prefix + index + "-hint"; group.setAttribute("aria-describedby", hint.id);
        group.appendChild(hint);
      }
      var optionList = element("div", "question-options"); group.appendChild(optionList);
      var choices = (question.options || []).map(function (option) {
        var label = element("label", "question-option"), input = document.createElement("input");
        input.type = question.selection === "multiple" ? "checkbox" : "radio";
        input.name = prefix + index; input.value = option.id;
        var copy = element("span", "question-option-copy"), optionTitle = element("span", "question-option-title");
        optionTitle.appendChild(element("strong", "", option.label));
        if (option.recommended) optionTitle.appendChild(element("small", "question-recommended", "Рекомендуется"));
        copy.appendChild(optionTitle);
        if (option.description) copy.appendChild(element("small", "question-description", option.description));
        label.append(input, copy); optionList.appendChild(label);
        input.addEventListener("click", function () {
          item.skipped = false; changed();
          if (question.selection !== "multiple" && !item.arrowKey) advance();
        });
        return input;
      });
      var free = null;
      if (question.allowFreeText !== false) {
        free = element("textarea", "question-free"); free.id = prefix + index + "-free";
        free.rows = 1; free.maxLength = 4000; free.placeholder = "Ответ…";
        free.setAttribute("aria-label", "Свой ответ"); group.appendChild(free);
        free.addEventListener("input", function () { item.skipped = false; changed(); });
      }
      form.appendChild(group);
      var item = { question: question, group: group, choices: choices, free: free, skipped: false, arrowKey: false };
      group.addEventListener("keydown", function (event) { item.arrowKey = /^Arrow/.test(event.key); });
      group.addEventListener("keyup", function () { item.arrowKey = false; });
      return item;
    });
    var status = element("p", "question-status"); status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite"); status.hidden = true;
    var actions = element("div", "question-actions");
    var skip = element("button", "button compact question-skip", "Пропустить"); skip.type = "button";
    var submit = element("button", "button primary compact"); submit.type = "submit";
    actions.append(skip, submit); form.append(status, actions);
    function update() {
      var locked = entry.busy || entry.accepted;
      form.setAttribute("aria-busy", String(entry.busy));
      fields.forEach(function (item, index) {
        item.group.hidden = index !== entry.page; item.group.disabled = locked;
        item.group.classList.toggle("is-skipped", item.skipped);
        item.choices.forEach(function (input) {
          input.closest("label").classList.toggle("is-selected", input.checked && !item.skipped);
        });
      });
      previous.disabled = locked || entry.page === 0; next.disabled = locked || entry.page === fields.length - 1;
      counter.textContent = (entry.page + 1) + " / " + fields.length;
      counter.setAttribute("aria-label", "Вопрос " + (entry.page + 1) + " из " + fields.length);
      skipAll.disabled = skip.disabled = submit.disabled = locked;
      submit.textContent = entry.busy ? "Отправляем…" : entry.page === fields.length - 1 ? "Готово" : "Далее";
      status.textContent = entry.error || (fields[entry.page].skipped ? "Вопрос пропущен. Можно выбрать ответ заново." : "");
      status.classList.toggle("is-error", !!entry.error); status.hidden = !status.textContent;
      if (entry.accepted && form.parentNode === entry.container) {
        entry.container.replaceChildren(); entry.container.hidden = true;
      }
    }
    function changed() {
      entry.error = "";
      fields.forEach(function (item) { item.group.removeAttribute("aria-invalid"); });
      update();
    }
    function goTo(index) {
      if (entry.busy || entry.accepted || index < 0 || index >= fields.length) return;
      entry.page = index; changed(); fields[index].group.focus({ preventScroll: true });
    }
    function answerFor(item, allSkipped) {
      var skipped = allSkipped || item.skipped;
      var answer = { questionId: item.question.id,
        optionIds: skipped ? [] : item.choices.filter(function (input) { return input.checked; }).map(function (input) { return input.value; }),
        freeText: !skipped && item.free ? item.free.value.trim() : "" };
      if (skipped) answer.skipped = true;
      return answer;
    }
    function isAnswered(answer) { return answer.skipped || answer.optionIds.length || answer.freeText; }
    function showMissing(index) {
      entry.page = index; entry.error = "Ответьте на вопрос или нажмите «Пропустить».";
      fields[index].group.setAttribute("aria-invalid", "true"); update();
      (fields[index].choices[0] || fields[index].free || skip).focus();
    }
    function advance() {
      if (entry.busy || entry.accepted) return;
      if (!isAnswered(answerFor(fields[entry.page], false))) { showMissing(entry.page); return; }
      if (entry.page < fields.length - 1) goTo(entry.page + 1);
      else sendAnswers(false);
    }
    async function sendAnswers(allSkipped) {
      if (entry.busy || entry.accepted) return;
      var missing = -1;
      var answers = fields.map(function (item, index) {
        item.group.removeAttribute("aria-invalid");
        var answer = answerFor(item, allSkipped);
        if (!isAnswered(answer) && missing === -1) missing = index;
        return answer;
      });
      if (missing !== -1) { showMissing(missing); return; }
      var fingerprint = JSON.stringify(answers);
      if (entry.fingerprint !== fingerprint) {
        entry.fingerprint = fingerprint; entry.requestId = inboxOperationId();
      }
      entry.busy = true; entry.error = ""; update();
      try {
        var accepted = await entry.submit({ runId: runId, questionSetId: data.questionSetId, answers: answers }, entry.requestId);
        if (accepted !== true) throw new Error("Не удалось подтвердить отправку. Ответ сохранён — попробуйте ещё раз.");
        entry.accepted = true;
      } catch (error) {
        entry.error = error.detail || error.message || "Не удалось отправить ответ. Попробуйте ещё раз.";
      } finally { entry.busy = false; update(); }
    }
    previous.addEventListener("click", function () { goTo(entry.page - 1); });
    next.addEventListener("click", function () { goTo(entry.page + 1); });
    form.addEventListener("submit", function (event) { event.preventDefault(); advance(); });
    form.addEventListener("keydown", function (event) {
      if (event.key === "Enter" && !event.isComposing && (event.ctrlKey || event.metaKey)) {
        event.preventDefault(); advance();
      }
    });
    skipAll.addEventListener("click", function () { sendAnswers(true); });
    skip.addEventListener("click", function () { fields[entry.page].skipped = true; changed(); advance(); });
    update();
  }
  window.RNAssistantQuestions = { render: render };
}());
