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
        var value = await send("openPlugin", { pluginId: target.pluginId, releaseId: target.releaseId });
        function close() { send("closePlugin", { instanceId: value.instanceId }).catch(function () {}); }
        if (!context.isCurrent()) { close(); return null; }
        var frame = document.createElement("iframe"); frame.title = target.title;
        frame.sandbox = "allow-scripts allow-same-origin allow-downloads"; frame.referrerPolicy = "no-referrer";
        function appearance() { if (target.pluginId === "data-templates") RNAssistantPlugins.applyAppearance(frame); }
        var theme = new MutationObserver(appearance);
        theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-accent"] });
        frame.addEventListener("load", appearance); frame.src = value.entryUrl;
        return { element: frame, frame: frame, presentation: { address: value.entryUrl }, comparison: comparison,
          dispose: function () { theme.disconnect(); frame.remove(); close(); } };
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
