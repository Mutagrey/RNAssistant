(function () {
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
    title.textContent = artifactValue(artifact, "Title", "title", "HTML-компонент");
    var badge = document.createElement("span");
    badge.className = "html-artifact-badge";
    badge.title = "HTML sandbox iframe включен";
    badge.setAttribute("aria-label", badge.title);
    header.appendChild(title);
    header.appendChild(badge);
    node.appendChild(header);

    var iframe = document.createElement("iframe");
    iframe.className = "html-artifact-frame";
    iframe.title = title.textContent;
    iframe.setAttribute("sandbox", "allow-scripts allow-forms allow-modals allow-popups");
    iframe.referrerPolicy = "no-referrer";
    iframe.style.height = Math.max(180, Math.min(900, Number(artifactValue(artifact, "Height", "height", 360) || 360))) + "px";
    iframe.srcdoc = String(artifactValue(artifact, "Html", "html", ""));
    node.appendChild(iframe);
    return node;
  }

  window.tryRenderHtmlArtifact = renderHtmlArtifact;
}());
