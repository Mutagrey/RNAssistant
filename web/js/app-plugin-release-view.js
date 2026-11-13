(function () {
  "use strict";
  function accepts(target) {
    return target && target.view === "plugin-release" && [target.pluginId, target.releaseId].every(function (id) {
      return typeof id === "string" && id.length > 0 && id.length <= 256;
    }) && typeof target.title === "string" && target.title.length <= 1024;
  }
  function key(target) { return "plugin-release:" + encodeURIComponent(JSON.stringify([target.pluginId, target.releaseId])); }
  function create(target) {
    var comparison = { kind: "plugin", afterId: target.releaseId, pluginId: target.pluginId };
    return RNAssistantBrowserObjectView.create({ label: { title: target.title, path: target.pluginId + " · " + target.releaseId },
      storage: { kind: "managed", detail: "CAS · опубликованная версия плагина" },
      comparison: comparison,
      details: [{ label: "Просмотр версии", status: "Пробная копия данных; публикация не меняется", icon: "clock", disabled: true }],
      annotationUnavailable: "Аннотации доступны в живом предпросмотре плагина.",
      discuss: function (context) {
        var plugin = RNAssistantPlugins.item(target.pluginId);
        if (!plugin) throw new Error("Проект плагина недоступен.");
        return RNAssistantPlugins.openSourceChat(null, plugin, { referencePanel: key(target), beside: true, isCurrent: context.isCurrent });
      },
      load: async function (context) {
        var source = document.createElement("div"); source.className = "empty-state";
        source.textContent = RNAssistantBrowser.unavailable;
        return { element: source, presentation: { address: "/plugins/" + encodeURIComponent(target.pluginId) + "/versions/" + encodeURIComponent(target.releaseId), supportedModes: ["source"], mode: "source" }, comparison: comparison,
          dispose: function () { source.remove(); } };
      },
      actions: function (root, context) {
        var more = RNAssistantActionMenu.createTrigger("Действия с версией"); more.classList.add("compact"); root.appendChild(more);
        var restoring = false;
        function entries(plugin, note) { return [{ label: "Копировать адрес версии", icon: "copy", onSelect: function () {
          return copyTextResult(target.pluginId + " / " + target.releaseId).then(function () { context.status("Адрес скопирован"); }).catch(context.error);
        } }, { label: "Восстановить эту версию", icon: "rotateLeft", status: note, disabled: restoring || !plugin || target.releaseId === plugin.releaseId,
          onSelect: async function () { if (restoring || !plugin || !context.isCurrent()) return; restoring = true; context.status("Восстановление версии…");
            try { await RNAssistantPlugins.restoreVersion({ pluginId: target.pluginId, releaseId: target.releaseId, expectedActiveReleaseId: plugin.releaseId });
              context.status("Версия восстановлена. Исходники не изменены; здесь остаётся пробная копия."); }
            catch (error) { context.error(error); }
            finally { restoring = false; }
          } }]; }
        more.onclick = async function () {
          var current = context.captureCurrent();
          var menu = RNAssistantActionMenu.open(more, entries(null, "Проверяю активную версию…"));
          if (!menu) return;
          try {
            var catalog = await send("getPlugins", {});
            if (!current() || !menu.isOpen()) return;
            if (catalog.error) throw new Error(catalog.error);
            var plugin = (catalog.items || []).find(function (item) { return item.pluginId === target.pluginId; });
            if (!plugin || !plugin.releaseId) throw new Error("Активная версия плагина недоступна.");
            menu.update(entries(plugin));
          } catch (error) { if (current() && menu.isOpen()) menu.update(entries(null, error.detail || error.message)); }
        };
      }
    });
  }
  window.RNAssistantPluginReleaseView = { key: key,
    register: function (dock) { dock.registerView("plugin-release", { perObject: true, accepts: accepts, key: key, create: create,
      maximumInstances: 4, renderer: "onlyWhenVisible", titleFor: function (target) { return target.title; } }); },
    open: function (plugin, release) {
      return RNAssistantDockWorkspace.openTarget({ view: "plugin-release", pluginId: plugin.pluginId, releaseId: release.releaseId,
        title: plugin.name + " · v" + release.version }, { referencePanel: RNAssistantPluginView.key(plugin) });
    }
  };
}());
