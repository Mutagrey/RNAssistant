(function () {
  "use strict";
  // Browser composition owns no RNAssistant services, credentials or workspace grants.
  const container = document.getElementById("dataTemplates");
  const loader = DataTemplatesLoader.create();
  DataTemplatesShell.mount(container, "core/index.html");
  loader.bind(container);
  window.addEventListener("pagehide", event => { if (!event.persisted) loader.dispose(); });
}());
