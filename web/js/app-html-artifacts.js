(function () {
  function htmlArtifactsEnabled() {
    var settings = state.settings || {};
    return !!(settings.AllowUnsafeHtmlArtifacts || settings.allowUnsafeHtmlArtifacts);
  }

  function artifactValue(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
  }

  function htmlArtifactFromActivity(activity) {
    var parsed = tryParseJson(activityDataJson(activity));
    if (!parsed.ok || !parsed.value || typeof parsed.value !== "object") {
      return null;
    }
    var type = artifactValue(parsed.value, "Type", "type", "");
    return type === "rnassistant.html" ? parsed.value : null;
  }

  function renderHtmlArtifact(activity) {
    var artifact = htmlArtifactFromActivity(activity);
    if (!artifact) {
      return null;
    }

    var node = document.createElement("section");
    node.className = "html-artifact";

    var header = document.createElement("div");
    header.className = "html-artifact-header";
    var title = document.createElement("div");
    title.className = "html-artifact-title";
    title.textContent = artifactValue(artifact, "Title", "title", "HTML component");
    var badge = document.createElement("span");
    badge.className = "html-artifact-badge";
    badge.textContent = htmlArtifactsEnabled() ? "sandboxed scripts" : "blocked";
    header.appendChild(title);
    header.appendChild(badge);
    node.appendChild(header);

    if (!htmlArtifactsEnabled()) {
      var blocked = document.createElement("div");
      blocked.className = "html-artifact-blocked";
      blocked.textContent = "HTML artifacts are disabled in Settings > Interface.";
      node.appendChild(blocked);
      return node;
    }

    var iframe = document.createElement("iframe");
    iframe.className = "html-artifact-frame";
    iframe.setAttribute("sandbox", "allow-scripts allow-forms allow-modals allow-popups");
    iframe.referrerPolicy = "no-referrer";
    iframe.style.height = Math.max(180, Math.min(900, Number(artifactValue(artifact, "Height", "height", 360) || 360))) + "px";
    var url = htmlBlobUrl(String(artifactValue(artifact, "Html", "html", "")));
    iframe.dataset.objectUrl = url;
    iframe.src = url;
    iframe.addEventListener("load", function () {
      var url = iframe.dataset.objectUrl;
      if (url) {
        URL.revokeObjectURL(url);
        iframe.removeAttribute("data-object-url");
      }
    }, { once: true });
    node.appendChild(iframe);
    return node;
  }

  function htmlBlobUrl(html) {
    var blob = new Blob([html || ""], { type: "text/html" });
    var url = URL.createObjectURL(blob);
    return url;
  }

  window.tryRenderHtmlArtifact = renderHtmlArtifact;
}());
