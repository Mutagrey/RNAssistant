(function () {
  // Swatches are sampled from the supplied palette screenshot. Light accents are
  // adjusted for readable text and controls; dark accents retain the swatch color.
  var choices = [
    { id: "blue", label: "Синий", swatch: "#3A63F2", light: "#3A63F2", dark: "#3A63F2" },
    { id: "turquoise", label: "Бирюзовый", swatch: "#03B0FD", light: "#007AAE", dark: "#03B0FD" },
    { id: "green", label: "Зеленый", swatch: "#05BAA1", light: "#007B6B", dark: "#05BAA1" },
    { id: "lime", label: "Лаймовый", swatch: "#B6D905", light: "#687A00", dark: "#B6D905" },
    { id: "yellow", label: "Желтый", swatch: "#F9CF37", light: "#916900", dark: "#F9CF37" },
    { id: "orange", label: "Оранжевый", swatch: "#F98367", light: "#B64B35", dark: "#F98367" },
    { id: "pink", label: "Розовый", swatch: "#FB4DAF", light: "#AF2870", dark: "#FB4DAF" },
    { id: "magenta", label: "Пурпурный", swatch: "#D96CD5", light: "#92349A", dark: "#D96CD5" },
    { id: "violet", label: "Фиолетовый", swatch: "#A45CFD", light: "#7435C4", dark: "#A45CFD" },
    { id: "white", label: "Белый", lightLabel: "Черный", swatch: "#FFFFFF", light: "#30343B", dark: "#FFFFFF" }
  ];

  function normalize(value) {
    value = String(value || "").toLowerCase();
    return choices.some(function (choice) { return choice.id === value; }) ? value : "blue";
  }

  function luminance(hex) {
    var channels = [1, 3, 5].map(function (offset) {
      var value = parseInt(hex.substring(offset, offset + 2), 16) / 255;
      return value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
    });
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  }

  function foregroundFor(hex) {
    var brightness = luminance(hex);
    return (1.05 / (brightness + 0.05)) >= ((brightness + 0.05) / (luminance("#101216") + 0.05))
      ? "#FFFFFF" : "#101216";
  }

  function mixedHex(first, second, firstWeight) {
    return "#" + [1, 3, 5].map(function (offset) {
      var value = Math.round(parseInt(first.substring(offset, offset + 2), 16) * firstWeight +
        parseInt(second.substring(offset, offset + 2), 16) * (1 - firstWeight));
      return ("0" + value.toString(16)).slice(-2);
    }).join("");
  }

  function apply(value, persist) {
    var id = normalize(value);
    var root = document.documentElement;
    var theme = root.getAttribute("data-theme") === "dark" ? "dark" : "light";
    var choice = choices.filter(function (item) { return item.id === id; })[0];
    root.setAttribute("data-accent", id);
    var accent = choice[theme];
    root.style.setProperty("--accent", accent);
    root.style.setProperty("--accent-on", foregroundFor(accent));
    root.style.setProperty("--button-primary-text", foregroundFor(mixedHex(accent, theme === "dark" ? "#10243A" : "#162C44", 0.72)));
    if (persist !== false) {
      try { localStorage.setItem("rnassistant.ui.accent", id); } catch (error) { /* WebView profile reset. */ }
    }
    return id;
  }

  window.RNAssistantAccents = { choices: choices, normalize: normalize, apply: apply };
  var saved = "blue";
  try { saved = localStorage.getItem("rnassistant.ui.accent") || saved; } catch (error) { /* WebView profile reset. */ }
  apply(saved, false);
}());
