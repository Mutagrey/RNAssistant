// Only the trusted settings view can author a policy. Native code owns signature and admission.
var RNAssistantDeploymentConfig = (function () {
  "use strict";
  var active = false, bound = false, generation = 0, session = null, busy = false, expiryTimer = null, root = null;
  function el(name) { return (root || document).querySelector("#deploymentConfig" + name); }
  function controls() {
    el("Login").hidden = !!session;
    el("Editor").hidden = !session;
    ["Password", "Unlock", "Save", "Sites", "Manual", "KeyPath", "OpenDirectory"].forEach(function (name) { el(name).disabled = busy; });
  }
  function status(value) {
    el("Status").textContent = value.state === "Applied" ? "Конфигурация загружена" :
      value.state === "Absent" ? "Конфигурация не загружена" : "Конфигурация не применена";
  }
  async function refresh() {
    var revision = generation;
    try {
      var result = await send("getDeploymentConfigStatus", {});
      if (active && revision === generation) status(result);
    } catch (error) {
      if (active && revision === generation) el("Status").textContent = "Конфигурация не применена";
    }
  }
  async function nativeLock() {
    try { await send("lockDeveloperMode", {}); }
    catch (error) { if (active) el("EditorStatus").textContent = "Не удалось подтвердить закрытие редактора: " + error.message; }
  }
  function lock() {
    var hadAuthority = !!session || busy;
    generation++; session = null;
    clearTimeout(expiryTimer); expiryTimer = null;
    ["Password", "Sites", "KeyPath", "Path"].forEach(function (name) { el(name).value = ""; });
    el("Manual").checked = false; el("Version").textContent = ""; el("EditorStatus").textContent = "";
    controls();
    if (hadAuthority) void nativeLock();
  }
  function editor(value) {
    session = value.sessionToken;
    el("Sites").value = value.sites.join("\n");
    el("Manual").checked = value.allowAnyManualSite;
    el("KeyPath").value = value.privateKeyPath || "";
    el("Path").value = value.filePath;
    el("Version").textContent = "Версия политики: " + value.policyVersion;
    status(value.status);
    clearTimeout(expiryTimer);
    expiryTimer = setTimeout(lock, Math.max(0, Date.parse(value.expiresUtc) - Date.now()));
  }
  async function submit(unlock) {
    if (busy || !active) return;
    var revision = ++generation;
    var payload = unlock ? { password: el("Password").value } : {
      sessionToken: session,
      sites: el("Sites").value.split(/\r?\n/).map(function (line) { return line.trim(); }).filter(Boolean),
      allowAnyManualSite: el("Manual").checked, privateKeyPath: el("KeyPath").value.trim()
    };
    el("Password").value = "";
    busy = true; controls(); el("EditorStatus").textContent = unlock ? "Проверка кода…" : "Подпись и сохранение…";
    try {
      var result = await send(unlock ? "unlockDeveloperMode" : "saveDeploymentConfig", payload);
      if (revision !== generation || !active) { await nativeLock(); return; }
      editor(result);
      el("EditorStatus").textContent = unlock ? "Режим разработчика открыт на 10 минут." : "Конфигурация сохранена и проверена. Можно передать deployment-config.bin пользователям.";
    } catch (error) {
      if (revision === generation && active) el("EditorStatus").textContent = error.message;
      // A timed-out unlock may still finish natively. Lock before enabling another attempt.
      if (unlock) await nativeLock();
    } finally {
      payload.password = null; busy = false; controls();
    }
  }
  function bind() {
    if (bound) return; bound = true;
    root = el("Status").closest("[data-deployment-config]"); // Dockview can detach the retained form before deactivate.
    el("Unlock").addEventListener("click", function () { void submit(true); });
    el("Password").addEventListener("keydown", function (event) {
      if (event.key === "Enter") { event.preventDefault(); void submit(true); }
    });
    el("Save").addEventListener("click", function () { void submit(false); });
    el("Lock").addEventListener("click", lock);
    el("OpenDirectory").addEventListener("click", async function () {
      var revision = generation;
      try { await send("openDeploymentConfigDirectory", { sessionToken: session }); }
      catch (error) { if (active && revision === generation) el("EditorStatus").textContent = error.message; }
    });
    el("Developer").addEventListener("toggle", function () { if (!this.open) lock(); });
  }
  return {
    bind: bind, lock: lock,
    activate: function () { active = true; void refresh(); },
    deactivate: function () { active = false; lock(); }
  };
})();
