(function () {
  window.markdown = function (text) {
    return DOMPurify.sanitize(marked.parse(text || ""));
  };

  function renderLatex(root) {
    if (!window.renderMathInElement) {
      return;
    }

    try {
      window.renderMathInElement(root, {
        delimiters: [
          { left: "$$", right: "$$", display: true },
          { left: "\\[", right: "\\]", display: true },
          { left: "\\(", right: "\\)", display: false },
          { left: "$", right: "$", display: false }
        ],
        ignoredTags: ["script", "noscript", "style", "textarea", "pre", "code"],
        throwOnError: false
      });
    } catch (error) {
      logOnce("LaTeX render failed: " + (error.message || error));
    }
  }

  function codePreviewText(code, pre) {
    var raw = code ? code.textContent : (pre ? pre.innerText : "");
    var lines = String(raw || "").replace(/\r\n/g, "\n").split("\n");
    var useful = [];
    lines.forEach(function (line) {
      var text = line.trim();
      if (text) {
        useful.push(text);
      }
    });

    if (!useful.length) {
      return "Code block";
    }

    var preview = useful[0];
    if ((preview === "{" || preview === "[" || preview.length < 8) && useful.length > 1) {
      preview += " " + useful[1];
    }
    if (preview.length > 180) {
      preview = preview.substring(0, 177) + "...";
    }
    return preview;
  }

  function enhanceMarkdown(root) {
    Array.prototype.slice.call(root.querySelectorAll("pre code")).forEach(function (code) {
      highlightCode(code);
    });

    Array.prototype.slice.call(root.querySelectorAll("pre")).forEach(function (pre) {
      if (pre.parentNode.classList.contains("code-wrap")) {
        return;
      }
      var wrap = document.createElement("div");
      wrap.className = "code-wrap";
      var tools = document.createElement("div");
      tools.className = "block-tools";
      var code = pre.querySelector("code");
      if (code && code.dataset.language) {
        var language = document.createElement("span");
        language.className = "code-lang";
        language.textContent = code.dataset.language;
        tools.appendChild(language);
      }
      var preview = document.createElement("div");
      preview.className = "code-preview";
      preview.textContent = codePreviewText(code, pre);
      var toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "block-tool-button";
      toggle.innerHTML = iconSvg("eye") + "<span>Show</span>";
      toggle.addEventListener("click", function () {
        var hidden = pre.style.display === "none";
        pre.style.display = hidden ? "" : "none";
        preview.style.display = hidden ? "none" : "";
        toggle.innerHTML = iconSvg(hidden ? "eyeOff" : "eye") + "<span>" + (hidden ? "Hide" : "Show") + "</span>";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.className = "block-tool-button";
      copy.innerHTML = iconSvg("copy") + "<span>Copy</span>";
      copy.addEventListener("click", function () {
        copyText(code ? code.textContent : pre.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(tools);
      wrap.appendChild(preview);
      wrap.appendChild(pre);
      pre.style.display = "none";
    });

    Array.prototype.slice.call(root.querySelectorAll("table")).forEach(function (table) {
      if (table.parentNode.classList.contains("table-wrap")) {
        return;
      }
      var wrap = document.createElement("div");
      wrap.className = "table-wrap";
      var tools = document.createElement("div");
      tools.className = "block-tools";
      var toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "block-tool-button";
      toggle.innerHTML = iconSvg("eyeOff") + "<span>Hide</span>";
      toggle.addEventListener("click", function () {
        var hidden = table.style.display === "none";
        table.style.display = hidden ? "" : "none";
        toggle.innerHTML = iconSvg(hidden ? "eyeOff" : "eye") + "<span>" + (hidden ? "Hide" : "Show") + "</span>";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.className = "block-tool-button";
      copy.innerHTML = iconSvg("copy") + "<span>Copy</span>";
      copy.addEventListener("click", function () {
        copyText(table.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      table.parentNode.insertBefore(wrap, table);
      wrap.appendChild(tools);
      wrap.appendChild(table);
    });

    renderLatex(root);
  }

  function highlightCode(code) {
    var text = code.textContent || "";
    var requestedLanguage = detectCodeLanguage(code);
    var language = normalizeCodeLanguage(requestedLanguage);

    code.classList.add("hljs");
    if (!window.hljs) {
      code.dataset.language = language || "plaintext";
      scheduleHighlightRetry();
      return;
    }

    state.highlightRetryAttempts = 0;
    try {
      var result;
      if (language && window.hljs.getLanguage && window.hljs.getLanguage(language)) {
        result = window.hljs.highlight(text, { language: language, ignoreIllegals: true });
      } else if (window.hljs.highlightAuto) {
        if (language) {
          logOnce("Highlight language is not bundled: " + requestedLanguage + "; using auto-detect.");
        }
        result = window.hljs.highlightAuto(text);
        language = result.language || "plaintext";
      }

      if (result && result.value) {
        code.innerHTML = result.value;
      }
      code.dataset.language = language || "plaintext";
      code.classList.add("language-" + code.dataset.language);
      logOnce("Highlighted code as " + code.dataset.language + (requestedLanguage ? " from " + requestedLanguage : " by auto-detect") + ".");
    } catch (error) {
      code.textContent = text;
      code.dataset.language = "plaintext";
      code.classList.add("language-plaintext");
      logOnce("Highlight failed: " + (error.message || error));
    }
  }

  function highlightAllCode() {
    Array.prototype.slice.call(document.querySelectorAll(".markdown pre code")).forEach(function (code) {
      highlightCode(code);
    });
  }

  function scheduleHighlightRetry() {
    if (state.highlightRetryScheduled || state.highlightLoadLogged) {
      return;
    }

    state.highlightRetryScheduled = true;
    window.setTimeout(function () {
      state.highlightRetryScheduled = false;
      if (window.hljs) {
        highlightAllCode();
        return;
      }

      state.highlightRetryAttempts += 1;
      if (state.highlightRetryAttempts < 3) {
        scheduleHighlightRetry();
        return;
      }

      state.highlightLoadLogged = true;
      logOnce("Highlight.js was not loaded from js/vendor/highlight.min.js; code is shown without syntax colors.");
    }, 150);
  }

  window.enhanceMarkdown = enhanceMarkdown;
  window.highlightCode = highlightCode;
  window.highlightAllCode = highlightAllCode;
  window.scheduleHighlightRetry = scheduleHighlightRetry;
}());
