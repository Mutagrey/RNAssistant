(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, B = P.button, G = P.go;
  // Existing Settings DTO names. These values stay in this preview's memory, never in product storage.
  var defaults = {
    BaseUrl: "https://api.example.com/v1", ModelsConfigUrl: "/v1/models", Model: "demo-balanced",
    MaxTokens: 3072, RequestTimeoutSeconds: 1800, Temperature: 0.2, TopP: 1, StreamResponses: true,
    ContextWindowOverrideTokens: 0, AttachmentHelperMaxTokens: 0, AttachmentEvidenceMaxTokens: 0,
    TokenEstimateMultiplier: 1, AutoCalibrateTokenEstimate: true, AutoCompressContext: true,
    ReasoningRequestMode: "chat_template_kwargs", ReasoningCustomJson: "{}",
    MaxAgentIterations: 256, MaxAgentFormatRetries: 10, MaxAgentToolSteps: 4096,
    AutoConfirmToolActions: false, EnableAgentJavaScript: false, SystemPromptRole: "developer",
    AgentResponseMode: "json_object", ToolResultRole: "user", FallbackToJsonObject: true,
    UiFontScale: 100, DesktopWindowWidth: 1200, RunningMessageDelivery: "Steer", SmartChatTitles: true,
    ScreenCaptureProtectionEnabled: false, HistoryIntegrityMode: "sha256", HistoryEncryptionMode: "none", HistoryKeySource: "api_key",
    ModelCapabilities: {}, ModelImageSupportOverrides: {}, ModelAudioSupportOverrides: {}, HtmlNetworkAllowedOrigins: "", CustomHeaders: "", AttachmentModelPriority: "demo-balanced\ndemo-fast"
  };
  var bounds = { MaxTokens: [1, 1000000], RequestTimeoutSeconds: [1, 86400], Temperature: [0, 2], TopP: [0, 1], ContextWindowOverrideTokens: [0, 10000000], AttachmentHelperMaxTokens: [0, 1000000], AttachmentEvidenceMaxTokens: [0, 1000000], TokenEstimateMultiplier: [0.1, 10], MaxAgentIterations: [1, 100000], MaxAgentFormatRetries: [1, 20], MaxAgentToolSteps: [1, 1000000], UiFontScale: [85, 130], DesktopWindowWidth: [900, 3840] };
  P.initSettings = function (state) { state.settingsDraft = JSON.parse(JSON.stringify(defaults)); state.settingsSaved = JSON.parse(JSON.stringify(defaults)); state.settingsBaseline = JSON.parse(JSON.stringify(defaults)); };
  P.settingsChanged = function () { return JSON.stringify(P.state.settingsDraft) !== JSON.stringify(P.state.settingsBaseline); };
  P.updateSettingsControls = function () {
    var changed = P.settingsChanged();
    document.querySelectorAll('[data-action="save-settings"]').forEach(function (button) { button.hidden = !changed; });
    var feedback = document.querySelector('[data-settings-feedback]');
    if (feedback) feedback.textContent = changed ? "Есть неприменённые изменения" : "";
  };
  function field(key, title, hint, options) {
    var value = P.state.settingsDraft[key], input;
    if (options) input = '<select data-setting="' + key + '">' + options.map(function (v) { return '<option value="' + E(v[0]) + '"' + (v[0] === value ? ' selected' : '') + '>' + E(v[1]) + '</option>'; }).join("") + '</select>';
    else if (key === "Model") input = '<input data-setting="Model" value="' + E(value) + '" list="demo-models"><datalist id="demo-models"><option value="demo-balanced">Модель A</option><option value="demo-fast">Модель B</option></datalist>';
    else if (typeof value === "boolean") return '<label class="setting-toggle"><span><strong>' + title + '</strong>' + (hint ? '<small>' + hint + '</small>' : '') + '</span><input type="checkbox" data-setting="' + key + '"' + (value ? ' checked' : '') + '></label>';
    else if (["CustomHeaders", "HtmlNetworkAllowedOrigins", "AttachmentModelPriority", "ReasoningCustomJson"].includes(key)) input = '<textarea rows="3" spellcheck="false" data-setting="' + key + '">' + E(value) + '</textarea>';
    else input = '<input data-setting="' + key + '" type="' + (typeof value === "number" ? "number" : "text") + '" value="' + E(value) + '"' + (bounds[key] ? ' min="' + bounds[key][0] + '" max="' + bounds[key][1] + '" step="' + (["Temperature", "TopP", "TokenEstimateMultiplier"].includes(key) ? '0.1' : key === "UiFontScale" ? '5' : '1') + '"' : '') + '>';
    return '<label class="field">' + title + input + (hint ? '<small>' + hint + '</small>' : '') + '</label>';
  }
  function group(title, description, content) { return '<section class="settings-group"><h2>' + title + '</h2>' + (description ? '<p>' + description + '</p>' : '') + '<div class="form-stack">' + content + '</div></section>'; }
  function advanced(title, content) { return '<details class="settings-disclosure"><summary>' + title + '</summary><div class="form-stack">' + content + '</div></details>'; }
  function grid(content) { return '<div class="settings-grid">' + content + '</div>'; }

  function capabilities() {
    var values = P.state.settingsDraft.ModelCapabilities[P.state.settingsDraft.Model] || {};
    return '<div class="settings-grid">' + [["SupportsImages", "Изображения"], ["SupportsAudio", "Аудио"], ["SupportsReasoning", "Рассуждения"]].map(function (pair) {
      var current = values[pair[0]] == null ? "auto" : String(values[pair[0]]);
      return '<label class="field">' + pair[1] + '<select data-model-capability="' + pair[0] + '">' + [["auto", "Из каталога"], ["true", "Поддерживает"], ["false", "Не поддерживает"]].map(function (v) { return '<option value="' + v[0] + '"' + (v[0] === current ? ' selected' : '') + '>' + v[1] + '</option>'; }).join("") + '</select></label>';
    }).join("") + [["MaxContextTokens", "Контекст, токенов"], ["MaxOutputTokens", "Ответ, токенов"], ["MaxImagesPerPrompt", "Изображений в запросе"]].map(function (pair) { return '<label class="field">' + pair[1] + '<input type="number" min="1" step="1" placeholder="Из каталога" data-model-capability="' + pair[0] + '" value="' + E(values[pair[0]] == null ? "" : values[pair[0]]) + '"></label>'; }).join("") + '</div>';
  }
  P.settingsPatch = function () {
    var draft = P.state.settingsDraft, patch = JSON.parse(JSON.stringify(draft)), headers = {};
    patch.UiFontScale = draft.UiFontScale / 100;
    patch.AttachmentModelPriority = draft.AttachmentModelPriority.split(/\n/).map(function (v) { return v.trim(); }).filter(Boolean);
    patch.HtmlNetworkAllowedOrigins = draft.HtmlNetworkAllowedOrigins.split(/\n/).map(function (v) { return v.trim(); }).filter(Boolean);
    draft.CustomHeaders.split(/\n/).filter(function (v) { return v.trim(); }).forEach(function (line) { var colon = line.indexOf(":"); headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim(); });
    patch.CustomHeaders = headers;
    patch.UiAccent = { rose: "pink", amber: "orange" }[P.state.accent] || P.state.accent;
    patch.UiTheme = P.state.effectiveTheme;
    return patch;
  };
  P.renderSettings = function () {
    var s = P.state, sub = s.route.split("/")[1], titles = { connection: "Подключение", model: "Модель", context: "Контекст и вложения", agent: "Агент", interface: "Интерфейс", protection: "Защита", diagnostics: "Помощь и обслуживание", storage: "Помощь и обслуживание", trajectory: "Помощь и обслуживание" }, body = "";
    if (sub === "connection") body = group("Провайдер", "Любой совместимый API, включая локальный сервер.",
      field("BaseUrl", "Адрес API") + field("ModelsConfigUrl", "Адрес каталога моделей", "Относительный путь или полный URL.") +
      '<label class="field">API-ключ<input type="password" placeholder="Задаётся в рабочем приложении" disabled><small>Здесь соединение не настроено. Реальные ключи в прототип вводить не нужно.</small></label>' +
      B("Проверить подключение", "test-connection", "link") + '<div id="connection-result" aria-live="polite"></div>') +
      advanced("Дополнительные заголовки", field("CustomHeaders", "HTTP-заголовки", "По одному «Имя: значение» в строке. Секреты не вводите в демонстрационную форму."));
    if (sub === "model") body = group("Основная модель", "Выбор для новых чатов. В отдельном чате модель можно изменить.",
      field("Model", "Модель", "Выберите из каталога или введите идентификатор модели.") + B("Обновить каталог", "refresh-models", "refresh") +
      '<div class="model-capabilities"><span>Текст</span><span>Изображения</span><span>Рассуждения</span><span class="muted">Аудио не заявлено</span></div>') +
      group("Ответ", "", grid(field("MaxTokens", "Лимит ответа, токенов") + field("RequestTimeoutSeconds", "Таймаут запроса, секунд")) + field("StreamResponses", "Показывать ответ по мере поступления")) +
      advanced("Параметры генерации", grid(field("Temperature", "Температура") + field("TopP", "Top P")) + field("ReasoningRequestMode", "Передача параметров рассуждения", "Зависит от API провайдера.", [["auto", "Автоматически"], ["reasoning_effort", "reasoning_effort"], ["enable_thinking", "enable_thinking"], ["chat_template_kwargs", "chat_template_kwargs"], ["reasoning_enabled", "reasoning_enabled"], ["custom_json", "Свой JSON"]]) + '<div data-reasoning-json' + (s.settingsDraft.ReasoningRequestMode === "custom_json" ? '' : ' hidden') + '>' + field("ReasoningCustomJson", "Дополнительный JSON", "Не может заменять model, messages, tools и response_format.") + '</div>') +
      advanced("Возможности выбранной модели", '<p class="small muted">Уточните возможности, если каталог провайдера неполон. Настройки относятся только к выбранной модели.</p><div data-model-capabilities>' + capabilities() + '</div>');
    if (sub === "context") body = group("Контекст разговора", "История и подключённые файлы используют общее контекстное окно модели.",
      field("AutoCompressContext", "Сжимать длинную историю автоматически", "Сжатие также доступно вручную в меню поля сообщения.") + field("ContextWindowOverrideTokens", "Размер контекстного окна, токенов", "0 — использовать размер из каталога модели.")) +
      group("Обработка вложений", "Файлы передаются с учётом возможностей модели и доступного места.", field("AttachmentModelPriority", "Приоритет моделей для вложений", "Идентификаторы по порядку предпочтения, по одному в строке.") + grid(field("AttachmentHelperMaxTokens", "Ответ вспомогательной модели", "0 — автоматически, до 1 024 токенов.") + field("AttachmentEvidenceMaxTokens", "Описание медиа в контексте", "0 — автоматически, до 20% бюджета и не более 2 048 токенов."))) +
      advanced("Оценка токенов", field("AutoCalibrateTokenEstimate", "Уточнять оценку по ответам провайдера") + field("TokenEstimateMultiplier", "Поправочный коэффициент", "Используется, когда точный токенизатор недоступен."));
    if (sub === "agent") body = group("Поведение", "", G("Редактировать основную инструкцию", "library/prompt", "book") +
      field("AutoConfirmToolActions", "Подтверждать разрешённые действия автоматически", "Для новых запусков. Запреты и режим планирования сохраняют приоритет.") + field("EnableAgentJavaScript", "Включить JavaScript-инструменты", "Экспериментальная возможность; по умолчанию выключена.")) +
      advanced("Ограничения выполнения", grid(field("MaxAgentIterations", "Итерации агента") + field("MaxAgentToolSteps", "Вызовы инструментов")) + field("MaxAgentFormatRetries", "Попытки исправить формат ответа", "Отдельно от сетевых повторов. От 1 до 20.")) +
      advanced("Совместимость с провайдером", field("SystemPromptRole", "Роль инструкций", "", [["developer", "developer"], ["system", "system"], ["user", "user"]]) + grid(field("AgentResponseMode", "Формат ответа", "", [["json_object", "JSON object"], ["json_schema", "JSON schema"]]) + field("ToolResultRole", "Роль результата инструмента", "", [["user", "user"], ["developer", "developer"], ["tool", "tool"]])) + field("FallbackToJsonObject", "Разрешить JSON object, если строгая схема не поддерживается") + B("Проверить совместимость", "compatibility", "check"));
    if (sub === "interface") body = P.renderAppearance() + group("Общение", "", field("SmartChatTitles", "Создавать названия чатов автоматически") + field("RunningMessageDelivery", "Сообщение во время выполнения", "", [["Steer", "Отправлять сразу"], ["Queue", "Добавлять в очередь"]])) + advanced("Размеры окна и текста", grid(field("UiFontScale", "Размер шрифта, %", "85–130%, как в рабочем приложении.") + field("DesktopWindowWidth", "Ширина отдельного окна, px", "Не применяется к панели Office.")));
    if (sub === "protection") body = group("Экран", "", field("ScreenCaptureProtectionEnabled", "Скрывать отдельное окно при захвате экрана", "В поддерживаемом Windows-хосте. Не действует на панель Office и не гарантирует защиту от всех способов захвата.")) +
      group("История разговоров", "Проверка целостности и шифрование — независимые настройки.", grid(field("HistoryIntegrityMode", "Проверка целостности", "", [["sha256", "SHA-256 · стандартная"], ["hmac_sha256", "HMAC-SHA256 · с ключом"]]) + field("HistoryEncryptionMode", "Шифрование содержимого", "", [["none", "Выключено"], ["aes256_cbc_hmac_sha256", "AES-256 + HMAC-SHA256"]])) + field("HistoryKeySource", "Источник ключа", "", [["api_key", "API-ключ"], ["custom_secret", "Отдельный секрет"]]) +
      '<p class="small muted">Если история уже существует, рабочее приложение блокирует смену режима и ключа. Удаление истории — отдельное явное действие. Секрет настраивается только при подключении к хосту.</p>') +
      advanced("Сеть в HTML-материалах", field("HtmlNetworkAllowedOrigins", "Разрешённые адреса", "Пустой список блокирует внешние запросы. По одному origin (например, https://example.com) в строке."));
    if (["diagnostics", "trajectory", "storage"].includes(sub)) body = group("Связь и поддержка", "Сведения для обращения за помощью доступны по запросу.",
      '<div class="service-row"><span>Провайдер</span><strong>Не подключён в прототипе</strong></div><div class="service-row"><span>Office</span><strong>Демонстрационный документ</strong></div>' +
      '<div class="actions">' + B("Проверить подключение", "test-connection", "link") + B("Скачать сведения", "download-diagnostics", "download", "ghost") + '</div><div id="connection-result" aria-live="polite"></div>') +
      advanced("Хранилище", '<p class="small muted">При проблемах можно проверить целостность локальных данных. Очистка доступна в рабочем приложении после проверки.</p>' + B("Показать проверку", "audit-storage", "check") + '<div id="storage-result" aria-live="polite"></div>') +
      '<p class="small muted">Ошибки конкретного запуска остаются рядом с действием в чате. Полный журнал событий и технические запросы не входят в основной интерфейс.</p>';
    var service = ["diagnostics", "trajectory", "storage"].includes(sub);
    return P.header(titles[sub] || "Настройки", "Настройки", service ? "" : B("Применить", "save-settings", "check", "primary")) + '<div class="content settings-content"><div class="settings-feedback" data-settings-feedback role="status"></div>' + body + (service ? '' : '<p class="settings-demo-note">Настройки применяются только к демонстрационной копии в этой вкладке.</p>') + '</div>';
  };
  P.validateSettings = function () {
    var values = P.state.settingsDraft;
    for (var key of Object.keys(bounds)) { var n = values[key], b = bounds[key]; if (!Number.isFinite(n) || n < b[0] || n > b[1]) return "Проверьте числовое поле «" + key + "»: допустимо от " + b[0] + " до " + b[1] + "."; }
    try { var url = new URL(values.BaseUrl); if (!["http:", "https:"].includes(url.protocol)) throw new Error(); } catch (_) { return "Укажите корректный HTTP(S) адрес API."; }
    if (values.ReasoningRequestMode === "custom_json") {
      try { var obj = JSON.parse(values.ReasoningCustomJson); if (!obj || Array.isArray(obj) || typeof obj !== "object" || ["model", "messages", "tools", "response_format"].some(function (key) { return key in obj; })) throw new Error(); } catch (_) { return "Проверьте JSON параметров рассуждения. Служебные поля запроса заменять нельзя."; }
    }
    if (values.CustomHeaders.split(/\n/).filter(function (line) { return line.trim(); }).some(function (line) { return !/^[A-Za-z0-9!#$%&'*+.^_`|~-]+\s*:/.test(line); })) return "Укажите заголовки в формате «Имя: значение».";
    for (var model of Object.keys(values.ModelCapabilities)) { var caps = values.ModelCapabilities[model]; for (var name of ["MaxContextTokens", "MaxOutputTokens", "MaxImagesPerPrompt"]) { if (caps[name] != null && (!Number.isSafeInteger(caps[name]) || caps[name] < 1)) return "Проверьте лимиты модели «" + model + "»."; } }
    var origins = values.HtmlNetworkAllowedOrigins.trim().split(/\n/).filter(Boolean);
    if (origins.some(function (value) { try { var u = new URL(value.trim()); return !["https:", "http:"].includes(u.protocol) || u.origin !== value.trim() || !!u.username || !!u.password; } catch (_) { return true; } })) return "В списке HTML-адресов нужны HTTP(S) origins без пути, логина и пароля.";
    return "";
  };
  document.addEventListener("change", function (event) { if (event.target.dataset.setting === "Model") { var root = document.querySelector("[data-model-capabilities]"); if (root) root.innerHTML = capabilities(); } });
  document.addEventListener("input", function (event) {
    var el = event.target, key = el.dataset.setting;
    if (el.dataset.modelCapability) {
      var settings = P.state.settingsDraft, model = settings.Model, caps = settings.ModelCapabilities[model] || (settings.ModelCapabilities[model] = {}), name = el.dataset.modelCapability;
      caps[name] = el.type === "number" ? (el.value === "" ? null : Number(el.value)) : el.value === "auto" ? null : el.value === "true";
      if (name === "SupportsImages" || name === "SupportsAudio") { var overrides = settings[name === "SupportsImages" ? "ModelImageSupportOverrides" : "ModelAudioSupportOverrides"]; if (caps[name] == null) delete overrides[model]; else overrides[model] = caps[name]; }
      if (caps[name] == null) delete caps[name];
      if (!Object.keys(caps).length) delete settings.ModelCapabilities[model];
      P.updateSettingsControls();
      return;
    }
    if (!key || !(key in defaults)) return;
    P.state.settingsDraft[key] = el.type === "checkbox" ? el.checked : el.type === "number" ? (el.value === "" ? NaN : Number(el.value)) : el.value;
    if (key === "ReasoningRequestMode") document.querySelector("[data-reasoning-json]").hidden = el.value !== "custom_json";
    P.updateSettingsControls();
  });
}());
