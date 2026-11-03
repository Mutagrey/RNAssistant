(function () {
  "use strict";
  function accepts(target) {
    return target && target.view === "plugin-release" && [target.pluginId, target.releaseId, target.expectedActiveReleaseId].every(function (id) {
      return typeof id === "string" && id.length > 0 && id.length <= 256;
    }) && typeof target.title === "string" && target.title.length <= 1024;
  }
  function key(target) { return "plugin-release:" + encodeURIComponent(JSON.stringify([target.pluginId, target.releaseId])); }
  function create(target) {
    return RNAssistantBrowserObjectView.create({ label: { title: target.title, path: target.pluginId + " · " + target.releaseId },
      note: "Сохранённая версия · пробная копия данных. Изменения здесь не публикуются.",
      load: async function (context) {
        var value = await send("openPlugin", { pluginId: target.pluginId, releaseId: target.releaseId });
        function close() { send("closePlugin", { instanceId: value.instanceId }).catch(function () {}); }
        if (!context.isCurrent()) { close(); return null; }
        var frame = document.createElement("iframe"); frame.title = target.title;
        frame.sandbox = "allow-scripts allow-same-origin allow-downloads"; frame.referrerPolicy = "no-referrer";
        function appearance() { if (target.pluginId === "data-templates") RNAssistantPlugins.applyAppearance(frame); }
        var theme = new MutationObserver(appearance);
        theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-accent"] });
        frame.addEventListener("load", appearance); frame.src = value.entryUrl;
        return { element: frame, frame: frame, dispose: function () { theme.disconnect(); frame.remove(); close(); } };
      },
      actions: function (root, context) {
        var more = RNAssistantActionMenu.createTrigger("Действия с версией"); root.appendChild(more);
        var restoring = false;
        more.onclick = function () { RNAssistantActionMenu.open(more, [{ label: "Копировать адрес версии", icon: "copy", onSelect: function () {
          return copyTextResult(target.pluginId + " / " + target.releaseId).then(function () { context.status("Адрес скопирован"); }).catch(context.error);
        } }, { label: "Восстановить эту версию", icon: "rotateLeft", disabled: restoring || target.releaseId === target.expectedActiveReleaseId,
          onSelect: async function () { if (restoring) return; restoring = true; context.status("Восстановление версии…");
            try { await RNAssistantPlugins.restoreVersion(target); context.status("Версия восстановлена. Исходники не изменены; здесь остаётся пробная копия."); }
            catch (error) { context.error(error); }
            finally { restoring = false; }
          } }]); };
      }
    });
  }
  window.RNAssistantPluginReleaseView = { key: key,
    register: function (dock) { dock.registerView("plugin-release", { perObject: true, accepts: accepts, key: key, create: create,
      maximumInstances: 4, renderer: "onlyWhenVisible", titleFor: function (target) { return target.title; } }); },
    open: function (plugin, release) {
      return RNAssistantDockWorkspace.openTarget({ view: "plugin-release", pluginId: plugin.pluginId, releaseId: release.releaseId,
        expectedActiveReleaseId: plugin.releaseId, title: plugin.name + " · v" + release.version }, { referencePanel: RNAssistantPluginView.key(plugin) });
    }
  };
}());
