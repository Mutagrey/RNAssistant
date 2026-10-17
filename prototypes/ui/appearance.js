(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon, key = "rnassistant.prototype.appearance.v1";
  var accents = [["green", "Зелёный", "#35684b"], ["blue", "Синий", "#355fba"], ["violet", "Фиолетовый", "#7550ad"], ["rose", "Розовый", "#aa4564"], ["amber", "Янтарный", "#94611b"]];
  var allowed = { theme: ["light", "dark", "system"], accent: accents.map(function (a) { return a[0]; }), density: ["compact", "relaxed"], textSize: ["normal", "large"], motion: ["system", "reduced"] };
  P.initAppearance = function (state) {
    var saved = {}; try { saved = JSON.parse(localStorage.getItem(key)) || {}; } catch (_) {}
    Object.keys(allowed).forEach(function (name) { state[name] = allowed[name].includes(saved[name]) ? saved[name] : allowed[name][0]; });
  };
  P.applyAppearance = function () {
    var s = P.state, root = document.documentElement;
    s.effectiveTheme = s.theme === "system" ? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light") : s.theme;
    root.dataset.theme = s.effectiveTheme; root.dataset.accent = s.accent; root.dataset.density = s.density; root.dataset.textSize = s.textSize; root.dataset.motion = s.motion;
  };
  P.setAppearance = function (name, value) {
    if (!allowed[name] || !allowed[name].includes(value)) return;
    P.state[name] = value;
    var values = {}; Object.keys(allowed).forEach(function (k) { values[k] = P.state[k]; });
    try { localStorage.setItem(key, JSON.stringify(values)); } catch (_) {}
    P.applyAppearance();
  };
  function select(name, label, choices) { return '<label class="field">' + label + '<select data-appearance="' + name + '">' + choices.map(function (c) { return '<option value="' + c[0] + '"' + (P.state[name] === c[0] ? ' selected' : '') + '>' + c[1] + '</option>'; }).join("") + '</select></label>'; }
  P.renderAppearance = function () {
    var s = P.state;
    return '<div class="settings-group"><h3>Тема</h3><div class="theme-choices">' + [["light", "Светлая", "sun"], ["dark", "Тёмная", "moon"], ["system", "Системная", "panel"]].map(function (t) {
      return '<button class="theme-choice" data-action="appearance:theme:' + t[0] + '" aria-pressed="' + (s.theme === t[0]) + '"><span class="theme-preview is-' + t[0] + '"><i></i><b></b><em></em></span><span>' + I(t[2]) + t[1] + '</span></button>';
    }).join("") + '</div></div><div class="settings-group"><h3>Акцент</h3><p>Цвет кнопок, выбранных элементов и ваших сообщений.</p><div class="accent-choices">' + accents.map(function (a) {
      return '<button class="accent-choice" data-action="appearance:accent:' + a[0] + '" aria-label="Акцент: ' + a[1] + '" aria-pressed="' + (s.accent === a[0]) + '"><span style="background:' + a[2] + '">' + (s.accent === a[0] ? I("check") : '') + '</span>' + E(a[1]) + '</button>';
    }).join("") + '</div><div class="appearance-sample"><span class="sample-bubble">Так выглядит ваше сообщение</span><button type="button" class="button primary" data-action="appearance-example">Продолжить</button></div></div><div class="settings-group"><h3>Размеры и движение</h3><div class="grid-2">' + select("density", "Плотность", [["compact", "Компактная"], ["relaxed", "Свободная"]]) + select("textSize", "Размер текста", [["normal", "Стандартный"], ["large", "Крупнее"]]) + select("motion", "Анимация", [["system", "Как в системе"], ["reduced", "Уменьшить движение"]]) + '</div><p class="appearance-footnote">Изменения применяются сразу и запоминаются в этом браузере.</p></div>';
  };
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function () { if (P.state && P.state.theme === "system") { P.applyAppearance(); if (P.refresh) P.refresh(); } });
}());
