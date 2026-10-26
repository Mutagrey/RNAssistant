using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Tools
{
    // Localized metadata for the shipped catalog. New/custom entries supply Display
    // themselves; the renderer never classifies their ids or argument names.
    public static class ToolDisplayCatalog
    {
        private static readonly IReadOnlyDictionary<string, ToolDisplayMetadata> BuiltIns =
            new Dictionary<string, ToolDisplayMetadata>(StringComparer.Ordinal)
        {
            { "common.capabilities_read", new ToolDisplayMetadata("Чтение описания", "Изучаю", ToolDisplayOperation.Learn, new[] { "id", "referencePath" }) },
            { "common.capabilities_search", new ToolDisplayMetadata("Поиск инструментов и навыков", "Ищу подходящие инструменты", ToolDisplayOperation.Search) },
            { "common.skills_load", new ToolDisplayMetadata("Настройка навыка", "Настраиваю навык", ToolDisplayOperation.Plan, new[] { "id", "action" }) },
            { "common.skills_upsert", new ToolDisplayMetadata("Сохранение навыка", "Сохраняю навык", ToolDisplayOperation.Write, new[] { "id", "name" }) },
            { "common.skills_delete", new ToolDisplayMetadata("Удаление навыка", "Удаляю навык", ToolDisplayOperation.Delete, new[] { "id" }) },
            { "common.skills_reference_upsert", new ToolDisplayMetadata("Сохранение справки навыка", "Сохраняю справку навыка", ToolDisplayOperation.Write, new[] { "id", "referencePath" }) },
            { "common.skills_reference_delete", new ToolDisplayMetadata("Удаление справки навыка", "Удаляю справку навыка", ToolDisplayOperation.Delete, new[] { "id", "referencePath" }) },
            { "common.tools_upsert", new ToolDisplayMetadata("Сохранение инструмента", "Сохраняю инструмент", ToolDisplayOperation.Write, new[] { "id", "name" }) },
            { "common.tools_delete", new ToolDisplayMetadata("Удаление инструмента", "Удаляю инструмент", ToolDisplayOperation.Delete, new[] { "id" }) },
            { "common.file_create", new ToolDisplayMetadata("Создание файла", "Создаю файл", ToolDisplayOperation.Write, new[] { "name" }) },
            { "common.file_copy", new ToolDisplayMetadata("Копирование файла", "Копирую файл", ToolDisplayOperation.Write, new[] { "source", "name" }) },
            { "common.file_patch", new ToolDisplayMetadata("Изменение файла", "Изменяю файл", ToolDisplayOperation.Write, new[] { "target" }) },
            { "files.create", new ToolDisplayMetadata("Создание файла", "Создаю файл", ToolDisplayOperation.Write, new[] { "relativePath" }) },
            { "files.copy", new ToolDisplayMetadata("Копирование файла", "Копирую файл", ToolDisplayOperation.Write, new[] { "relativePath", "targetPath" }) },
            { "files.move", new ToolDisplayMetadata("Перемещение файла", "Перемещаю файл", ToolDisplayOperation.Write, new[] { "relativePath", "targetPath" }) },
            { "files.patch", new ToolDisplayMetadata("Изменение файла", "Изменяю файл", ToolDisplayOperation.Write, new[] { "relativePath" }) },
            { "files.replace", new ToolDisplayMetadata("Замена файла", "Заменяю файл", ToolDisplayOperation.Write, new[] { "relativePath" }) },
            { "files.delete", new ToolDisplayMetadata("Удаление файла", "Удаляю файл", ToolDisplayOperation.Delete, new[] { "relativePath" }) },
            { "files.restore", new ToolDisplayMetadata("Восстановление файла", "Восстанавливаю файл", ToolDisplayOperation.Write, new[] { "relativePath" }) },
            { "common.html_data_bind", new ToolDisplayMetadata("Подключение данных к странице", "Подключаю данные к странице", ToolDisplayOperation.Write) },
            { "common.html_data_freeze", new ToolDisplayMetadata("Сохранение снимка данных", "Сохраняю снимок данных", ToolDisplayOperation.Write) },
            { "common.html_data_refresh", new ToolDisplayMetadata("Обновление данных страницы", "Обновляю данные страницы", ToolDisplayOperation.Write) },
            { "common.html_data_write", new ToolDisplayMetadata("Запись данных страницы", "Записываю данные страницы", ToolDisplayOperation.Write) },
            { "common.html_workspace_apply_patch", new ToolDisplayMetadata("Изменение файла страницы", "Изменяю файл страницы", ToolDisplayOperation.Write) },
            { "common.html_workspace_delete", new ToolDisplayMetadata("Удаление файла или данных страницы", "Удаляю файл или данные страницы", ToolDisplayOperation.Delete) },
            { "common.html_workspace_write_file", new ToolDisplayMetadata("Запись файла страницы", "Записываю файл страницы", ToolDisplayOperation.Write) },
            { "common.office_run_macro", new ToolDisplayMetadata("Выполнение макроса", "Выполняю макрос", ToolDisplayOperation.Command) },
            { "common.plan_doc_delete", new ToolDisplayMetadata("Удаление плана", "Удаляю план", ToolDisplayOperation.Delete) },
            { "common.plan_doc_restore", new ToolDisplayMetadata("Восстановление плана", "Восстанавливаю план", ToolDisplayOperation.Plan) },
            { "common.plan_doc_save", new ToolDisplayMetadata("Сохранение плана", "Сохраняю план", ToolDisplayOperation.Plan) },
            { "common.questions_ask", new ToolDisplayMetadata("Уточнение задачи", "Готовлю уточнение", ToolDisplayOperation.Question) },
            { "common.resources_find", new ToolDisplayMetadata("Поиск", "Ищу", ToolDisplayOperation.Search) },
            { "common.resources_read", new ToolDisplayMetadata("Чтение", "Читаю", ToolDisplayOperation.Read, new[] { "target", "procedure", "startLine", "lineCount", "startChar", "charCount" }) },
            { "common.resources_search", new ToolDisplayMetadata("Поиск в ресурсе", "Ищу в ресурсе", ToolDisplayOperation.Search, new[] { "scope", "target", "directory", "query", "mode" }) },
            { "common.resources_outline", new ToolDisplayMetadata("Структура ресурса", "Строю карту ресурса", ToolDisplayOperation.Read) },
            { "common.task_list_set", new ToolDisplayMetadata("Обновление шагов задачи", "Обновляю шаги задачи", ToolDisplayOperation.Plan) },
            { "common.vba_delete_module", new ToolDisplayMetadata("Удаление VBA-модуля", "Удаляю VBA-модуль", ToolDisplayOperation.Delete, new[] { "moduleName" }) },
            { "common.vba_apply_patch", new ToolDisplayMetadata("Изменение VBA-модуля", "Изменяю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "common.vba_rename_module", new ToolDisplayMetadata("Переименование VBA-модуля", "Переименовываю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "common.vba_restore_backup", new ToolDisplayMetadata("Восстановление VBA-модуля", "Восстанавливаю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "common.vba_write_module", new ToolDisplayMetadata("Запись VBA-модуля", "Записываю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "excel.add_sheet", new ToolDisplayMetadata("Создание листа", "Создаю лист", ToolDisplayOperation.Write) },
            { "excel.add_table", new ToolDisplayMetadata("Создание таблицы", "Создаю таблицу", ToolDisplayOperation.Write) },
            { "excel.clear_range", new ToolDisplayMetadata("Очистка диапазона", "Очищаю диапазон", ToolDisplayOperation.Delete) },
            { "excel.delete_rows_columns", new ToolDisplayMetadata("Удаление строк или столбцов", "Удаляю строки или столбцы", ToolDisplayOperation.Delete,
                new[] { "sheet", "scope", "table", "axis", "start", "count" }) },
            { "excel.create_chat_chart", new ToolDisplayMetadata("Создание диаграммы в чате", "Создаю диаграмму в чате", ToolDisplayOperation.Chart) },
            { "excel.delete_chart", new ToolDisplayMetadata("Удаление диаграммы", "Удаляю диаграмму", ToolDisplayOperation.Delete) },
            { "excel.filter_range", new ToolDisplayMetadata("Фильтрация диапазона", "Фильтрую диапазон", ToolDisplayOperation.Write) },
            { "excel.find_cells", new ToolDisplayMetadata("Поиск ячеек", "Ищу ячейки", ToolDisplayOperation.Search) },
            { "excel.format_range", new ToolDisplayMetadata("Форматирование диапазона", "Оформляю диапазон", ToolDisplayOperation.Write) },
            { "excel.inspect", new ToolDisplayMetadata("Проверка структуры книги", "Проверяю структуру книги", ToolDisplayOperation.Read) },
            { "excel.rename_sheet", new ToolDisplayMetadata("Переименование листа", "Переименовываю лист", ToolDisplayOperation.Write) },
            { "excel.replace_cells", new ToolDisplayMetadata("Замена содержимого ячеек", "Заменяю содержимое ячеек", ToolDisplayOperation.Write) },
            { "excel.sort_range", new ToolDisplayMetadata("Сортировка диапазона", "Сортирую диапазон", ToolDisplayOperation.Write) },
            { "excel.upsert_chart", new ToolDisplayMetadata("Обновление диаграммы", "Обновляю диаграмму", ToolDisplayOperation.Chart) },
            { "excel.write_range", new ToolDisplayMetadata("Запись диапазона", "Записываю диапазон", ToolDisplayOperation.Write) },
            { "outlook.create_draft", new ToolDisplayMetadata("Создание черновика письма", "Создаю черновик письма", ToolDisplayOperation.Write) },
            { "outlook.search_mail", new ToolDisplayMetadata("Поиск письма", "Ищу письмо", ToolDisplayOperation.Search) },
            { "outlook.index_archive", new ToolDisplayMetadata("Индексирование архива Outlook", "Читаю письма архива", ToolDisplayOperation.Read) },
            { "outlook.update_mail", new ToolDisplayMetadata("Обновление письма", "Обновляю письмо", ToolDisplayOperation.Write) },
            { "powerpoint.add_object", new ToolDisplayMetadata("Создание объекта", "Создаю объект", ToolDisplayOperation.Write) },
            { "powerpoint.add_slide", new ToolDisplayMetadata("Создание слайда", "Создаю слайд", ToolDisplayOperation.Write) },
            { "powerpoint.duplicate_slide", new ToolDisplayMetadata("Копирование слайда", "Копирую слайд", ToolDisplayOperation.Write) },
            { "powerpoint.list_objects", new ToolDisplayMetadata("Просмотр списка объектов", "Смотрю список объектов", ToolDisplayOperation.Read) },
            { "powerpoint.move_slide", new ToolDisplayMetadata("Перемещение слайда", "Перемещаю слайд", ToolDisplayOperation.Write) },
            { "powerpoint.replace_text", new ToolDisplayMetadata("Замена текста", "Заменяю текст", ToolDisplayOperation.Write) },
            { "powerpoint.search_text", new ToolDisplayMetadata("Поиск текста", "Ищу текст", ToolDisplayOperation.Search) },
            { "powerpoint.set_text", new ToolDisplayMetadata("Изменение текста", "Меняю текст", ToolDisplayOperation.Write) },
            { "word.add_comment", new ToolDisplayMetadata("Создание комментария", "Создаю комментарий", ToolDisplayOperation.Write) },
            { "word.add_table", new ToolDisplayMetadata("Создание таблицы", "Создаю таблицу", ToolDisplayOperation.Write) },
            { "word.find_text", new ToolDisplayMetadata("Поиск текста", "Ищу текст", ToolDisplayOperation.Search) },
            { "word.format_text", new ToolDisplayMetadata("Форматирование текста", "Оформляю текст", ToolDisplayOperation.Write) },
            { "word.insert_page_break", new ToolDisplayMetadata("Вставка разрыва страницы", "Вставляю разрыв страницы", ToolDisplayOperation.Write) },
            { "word.inspect", new ToolDisplayMetadata("Проверка структуры документа", "Проверяю структуру документа", ToolDisplayOperation.Read) },
            { "word.replace_text", new ToolDisplayMetadata("Замена текста", "Заменяю текст", ToolDisplayOperation.Write) },
            { "word.write_text", new ToolDisplayMetadata("Запись текста", "Записываю текст", ToolDisplayOperation.Write) },
        };

        public static ToolDisplayMetadata Resolve(ToolCatalogEntry tool)
        {
            if (tool == null) return new ToolDisplayMetadata("Вызов инструмента", "Вызываю инструмент");
            if (tool.Display != null) return tool.Display;
            ToolDisplayMetadata builtIn;
            if (tool.BuiltIn && BuiltIns.TryGetValue(tool.Id, out builtIn)) return builtIn;
            var operation = tool.Policy == null ? ToolDisplayOperation.Command :
                tool.Policy.Effect == ToolEffect.Read ? ToolDisplayOperation.Read :
                tool.Policy.Effect == ToolEffect.Write ? ToolDisplayOperation.Write : ToolDisplayOperation.Command;
            var action = operation == ToolDisplayOperation.Read ? "Чтение" :
                operation == ToolDisplayOperation.Write ? "Изменение" : "Вызов инструмента";
            var name = string.IsNullOrWhiteSpace(tool.Name) ? tool.Id ?? string.Empty : tool.Name;
            name = new string(name.Select(value => char.IsControl(value) ? ' ' : value).ToArray());
            // Name is content, not a behavioral hint; it never selects an icon or effect.
            return new ToolDisplayMetadata(action + " · " + name.Substring(0, Math.Min(name.Length, 170)), operation: operation);
        }
        // Display only: accepted semantic arguments, never live discovery or authority.
        public static string Target(ToolInvocation command)
        {
            if (command == null || command.Arguments == null) return string.Empty;
            Func<string, string> text = key =>
            {
                object value;
                return command.Arguments.TryGetValue(key, out value) && value is string
                    ? BoundTarget((string)value) : string.Empty;
            };
            if (command.Display != null && command.Display.TargetArguments != null)
                return string.Join(" · ", command.Display.TargetArguments.Select(key =>
                {
                    object value;
                    if (!command.Arguments.TryGetValue(key, out value) || value == null) return string.Empty;
                    if (value is string) return text(key);
                    if (value is bool || value is int || value is long || value is double || value is decimal)
                        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    return string.Empty; // Never dump arrays, objects, guards or full bodies into a caption.
                }).Where(value => !string.IsNullOrWhiteSpace(value)));
            if (command.ToolId == "common.resources_find")
                return string.Join(" · ", new[] { text("query"), ResourceCaption(text("scope")) }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (command.ToolId == "common.resources_read")
                return string.Join(" · ", new[] { text("target"), ResourceCaption(text("representation")) }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (command.ToolId == "common.capabilities_read") return text("id");
            if (command.ToolId == "common.capabilities_search") return text("query");
            if ((command.ToolId ?? string.Empty).StartsWith("powerpoint.", StringComparison.Ordinal))
            {
                object index;
                var slide = command.Arguments.TryGetValue("slideIndex", out index) && (index is int || index is long)
                    ? "Слайд " + Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture)
                    : "Текущий слайд";
                if (command.ToolId == "powerpoint.add_slide") return string.IsNullOrWhiteSpace(text("title")) ? "Новый слайд" : text("title");
                if (command.ToolId == "powerpoint.list_objects" && text("kind") == "slides") return "Слайды презентации";
                if ((command.ToolId == "powerpoint.search_text" || command.ToolId == "powerpoint.replace_text") &&
                    (!command.Arguments.TryGetValue("slideIndex", out index) || Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture) == "0"))
                    slide = "Вся презентация";
                if (command.ToolId == "powerpoint.move_slide" && command.Arguments.TryGetValue("toIndex", out index) &&
                    (index is int || index is long))
                    return slide + " → позиция " + Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture);
                var target = text("target") == "notes" ? "Заметки" : text("shapeName");
                if (command.ToolId == "powerpoint.add_object") target = text("kind") == "table" ? "Таблица" :
                    text("kind") == "picture" ? "Изображение" : text("kind") == "textBox" ? "Текстовый блок" : target;
                return string.Join(" · ", new[] { slide, target, text("query"), text("find"), text("path") }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            if ((command.ToolId ?? string.Empty).StartsWith("word.", StringComparison.Ordinal))
            {
                var location = command.ToolId == "word.write_text" && text("mode") != "paragraph" ? "selection" : text("location");
                if (command.ToolId == "word.inspect") return "Документ";
                var scope = text("scope");
                var target = command.ToolId == "word.format_text" && text("kind") == "font" ? "selection" : text("target");
                if (command.ToolId == "word.format_text" && string.IsNullOrWhiteSpace(target)) target = "selection";
                if (string.IsNullOrWhiteSpace(scope) && (command.ToolId == "word.find_text" || command.ToolId == "word.replace_text")) scope = "main";
                var place = location == "start" ? "Начало документа" : location == "end" ? "Конец документа" :
                    location == "selection" || scope == "selection" || target == "selection" ? "Выделение" :
                    scope == "main" ? "Текст документа" : scope == "all" || target == "document" ? "Весь документ" : string.Empty;
                if (string.IsNullOrWhiteSpace(place) && (command.ToolId == "word.write_text" ||
                    command.ToolId == "word.add_table" || command.ToolId == "word.insert_page_break" ||
                    command.ToolId == "word.add_comment")) place = "Выделение";
                return string.Join(" · ", new[] { place, text("query"), text("find"), target == "selection" || target == "document" ? "" : target }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            if (command.ToolId == "outlook.create_draft")
            {
                var draftTarget = string.Join(" · ", new[] { text("subject"), text("to") }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var kind = text("kind");
                if (kind == "reply" || kind == "replyAll" || kind == "forward")
                    return (kind == "replyAll" ? "Ответ всем" : kind == "forward" ? "Пересылка" : "Ответ") +
                        " · Текущее письмо" + (string.IsNullOrWhiteSpace(draftTarget) ? "" : " · " + draftTarget);
                return string.IsNullOrWhiteSpace(draftTarget) ? "Новое письмо" : draftTarget;
            }
            if (command.ToolId == "outlook.update_mail")
                return "Текущее письмо" + (text("kind") == "markRead" ? " · Отметка о прочтении" :
                    text("kind") == "categories" ? " · Категории: " + text("categories") : "");
            var sheet = text("sheet");
            var address = text("address");
            if (!string.IsNullOrWhiteSpace(sheet)) return sheet + (string.IsNullOrWhiteSpace(address) ? "" : "!" + address);
            foreach (var key in new[] { "target", "moduleName", "path", "title", "name", "address", "query" })
            {
                var value = text(key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static string ResourceCaption(string value)
        {
            switch (value)
            {
                case "all": return "Все ресурсы";
                case "conversation": return "Чат";
                case "document": return "Документ";
                case "selection": return "Выделение";
                case "html": return "Страница";
                case "vba": return "VBA";
                case "backups": return "Резервные копии";
                case "catalogs": return "Каталоги";
                case "metadata": return "Сведения";
                case "text": return "Текст";
                case "structure": return "Структура";
                case "source": return "Исходный код";
                case "media": return "Медиа";
                case "formulas": return "Формулы";
                case "table": return "Таблица";
                case "records": return "Записи";
                default: return value;
            }
        }

        private static string BoundTarget(string value)
        {
            var text = new string((value ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            if (text.Length <= 240) return text;
            var length = char.IsHighSurrogate(text[239]) ? 239 : 240;
            return text.Substring(0, length) + "…";
        }

    }
}
