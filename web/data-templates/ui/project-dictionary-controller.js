(function (root) {
  "use strict";
  const fail = (code, message) => { throw Object.assign(new Error(message), { code }); };
  function create(projectView, processing) {
    const store = root.DataTemplatesBrowserProjectDictionary;
    let project = null, snapshot = null, ready = false, blocked = false, disposed = false, ticket = 0;
    const observers = new Set();
    const emit = () => observers.forEach(observer => observer(ready ? project : null));
    function requireReady(expected) {
      if (disposed || !ready || blocked || !project || expected && expected !== project)
        fail("DICTIONARY_UNAVAILABLE", "Общий справочник недоступен. Откройте проект заново.");
      return snapshot;
    }
    async function assertCurrent(expected) {
      const observed = requireReady(expected), stored = await store.current(project);
      if ((stored && stored.head || null) !== (observed && observed.head || null) ||
          stored && stored.text !== observed.text) {
        blocked = true; processing.invalidateDraft("Общий справочник изменился вне приложения. Откройте проект заново.");
        fail("DICTIONARY_CHANGED", "Общий справочник изменился вне приложения. Откройте проект заново.");
      }
      return observed;
    }
    async function writeUnderLock(expected, dictionary, signal) {
      const observed = await assertCurrent(expected);
      try {
        const saved = await store.save(expected, dictionary, observed && observed.head || null,
          signal, observed && observed.text);
        snapshot = saved; return saved;
      } catch (error) {
        if (error.code === "effect_unknown" || error.code === "DICTIONARY_CHANGED") {
          blocked = true; processing.invalidateDraft("Эффект записи справочника неизвестен. Проверьте указанный файл и откройте проект заново.");
        }
        throw error;
      }
    }
    async function absorb(expected, incoming, signal) {
      if (processing.hasPendingEdit())
        fail("DICTIONARY_EDIT_PENDING", "Сначала примените или отмените текущую правку.");
      processing.setProjectDictionaryBusy(true);
      try {
        const observed = await assertCurrent(expected);
        const merged = root.DataTemplatesProjectTemplate.merge(observed && observed.dictionary, incoming);
        if (observed && root.DataTemplatesProjectTemplate.equal(merged, observed.dictionary)) return observed;
        const saved = await writeUnderLock(expected, merged, signal);
        await processing.rebaseDictionary(saved.dictionary);
        return saved;
      } finally { processing.setProjectDictionaryBusy(false); }
    }
    async function saveEdit(dictionary, signal) {
      const selected = project;
      return projectView.withProject(async current => {
        requireReady(selected);
        if (current !== selected) fail("DICTIONARY_UNAVAILABLE", "Проект переключился во время правки.");
        return writeUnderLock(current, dictionary, signal);
      });
    }
    projectView.subscribe(value => {
      const currentTicket = ++ticket;
      project = value; snapshot = null; ready = !value; blocked = false;
      processing.invalidateDraft(value ? "Проект изменился. Откройте шаблон проекта заново." :
        "Проект закрыт. Выберите шаблон заново.");
      emit();
      if (!value) return;
      projectView.withProject(current => store.current(current)).then(found => {
        if (disposed || currentTicket !== ticket) return;
        snapshot = found; ready = true; emit();
      }).catch(error => {
        if (disposed || currentTicket !== ticket) return;
        blocked = true; processing.invalidateDraft("Не удалось открыть общий справочник: " + error.message);
        emit();
      });
    });
    return Object.freeze({ active: () => !!project, ready: () => ready && !blocked,
      current: () => requireReady(), assertCurrent, absorb, saveEdit,
      subscribe: observer => { observers.add(observer); observer(ready ? project : null);
        return () => observers.delete(observer); },
      dispose: () => { disposed = true; ticket++; observers.clear(); } });
  }
  root.DataTemplatesProjectDictionaryController = Object.freeze({ create });
}(window));
