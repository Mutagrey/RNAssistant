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
            { "core.tools.read", new ToolDisplayMetadata("Чтение описания", "Изучаю", ToolDisplayOperation.Learn, new[] { "id", "referencePath" }) },
            { "core.tools.search", new ToolDisplayMetadata("Поиск инструментов и навыков", "Ищу подходящие инструменты", ToolDisplayOperation.Search) },
            { "core.skills.load", new ToolDisplayMetadata("Настройка навыка", "Настраиваю навык", ToolDisplayOperation.Plan, new[] { "id", "action" }) },
            { "skills.upsert", new ToolDisplayMetadata("Сохранение навыка", "Сохраняю навык", ToolDisplayOperation.Write, new[] { "id", "name" }) },
            { "skills.delete", new ToolDisplayMetadata("Удаление навыка", "Удаляю навык", ToolDisplayOperation.Delete, new[] { "id" }) },
            { "skills.reference.upsert", new ToolDisplayMetadata("Сохранение справки навыка", "Сохраняю справку навыка", ToolDisplayOperation.Write, new[] { "id", "referencePath" }) },
            { "skills.reference.delete", new ToolDisplayMetadata("Удаление справки навыка", "Удаляю справку навыка", ToolDisplayOperation.Delete, new[] { "id", "referencePath" }) },
            { "tools.upsert", new ToolDisplayMetadata("Сохранение инструмента", "Сохраняю инструмент", ToolDisplayOperation.Write, new[] { "id", "name" }) },
            { "tools.delete", new ToolDisplayMetadata("Удаление инструмента", "Удаляю инструмент", ToolDisplayOperation.Delete, new[] { "id" }) },
            { "file.source.create", new ToolDisplayMetadata("Создание файла", "Создаю файл", ToolDisplayOperation.Write, new[] { "name" }) },
            { "file.source.copy", new ToolDisplayMetadata("Копирование файла", "Копирую файл", ToolDisplayOperation.Write, new[] { "source", "name" }) },
            { "file.source.patch", new ToolDisplayMetadata("Изменение файла", "Изменяю файл", ToolDisplayOperation.Write, new[] { "target" }) },
            { "core.write", new ToolDisplayMetadata("Запись ресурса", "Записываю ресурс", ToolDisplayOperation.Write, new[] { "target" }) },
            { "core.patch", new ToolDisplayMetadata("Изменение текста", "Изменяю текст", ToolDisplayOperation.Write, new string[0]) },
            { "core.replace", new ToolDisplayMetadata("Замена совпадений", "Заменяю совпадения", ToolDisplayOperation.Write, new[] { "target" }) },
            { "resources.move", new ToolDisplayMetadata("Перемещение ресурса", "Перемещаю ресурс", ToolDisplayOperation.Write, new[] { "target", "destination" }) },
            { "resources.copy", new ToolDisplayMetadata("Копирование ресурса", "Копирую ресурс", ToolDisplayOperation.Write, new[] { "target", "destination" }) },
            { "resources.delete", new ToolDisplayMetadata("Удаление ресурса", "Удаляю ресурс", ToolDisplayOperation.Delete, new[] { "target" }) },
            { "resources.restore", new ToolDisplayMetadata("Восстановление ресурса", "Восстанавливаю ресурс", ToolDisplayOperation.Write, new[] { "target" }) },
            { "html.data.bind", new ToolDisplayMetadata("Подключение данных к странице", "Подключаю данные к странице", ToolDisplayOperation.Write) },
            { "html.data.freeze", new ToolDisplayMetadata("Сохранение снимка данных", "Сохраняю снимок данных", ToolDisplayOperation.Write) },
            { "html.data.refresh", new ToolDisplayMetadata("Обновление данных страницы", "Обновляю данные страницы", ToolDisplayOperation.Write) },
            { "html.data.write", new ToolDisplayMetadata("Запись данных страницы", "Записываю данные страницы", ToolDisplayOperation.Write) },
            { "html.source.patch", new ToolDisplayMetadata("Изменение файла страницы", "Изменяю файл страницы", ToolDisplayOperation.Write) },
            { "html.source.delete", new ToolDisplayMetadata("Удаление файла или данных страницы", "Удаляю файл или данные страницы", ToolDisplayOperation.Delete) },
            { "html.source.write", new ToolDisplayMetadata("Запись файла страницы", "Записываю файл страницы", ToolDisplayOperation.Write) },
            { "office.macro.run", new ToolDisplayMetadata("Выполнение макроса", "Выполняю макрос", ToolDisplayOperation.Command) },
            { "plan.document.delete", new ToolDisplayMetadata("Удаление плана", "Удаляю план", ToolDisplayOperation.Delete) },
            { "plan.document.restore", new ToolDisplayMetadata("Восстановление плана", "Восстанавливаю план", ToolDisplayOperation.Plan) },
            { "plan.document.save", new ToolDisplayMetadata("Сохранение плана", "Сохраняю план", ToolDisplayOperation.Plan) },
            { "core.ask", new ToolDisplayMetadata("Уточнение задачи", "Готовлю уточнение", ToolDisplayOperation.Question) },
            { "core.find", new ToolDisplayMetadata("Поиск", "Ищу", ToolDisplayOperation.Search) },
            { "core.read", new ToolDisplayMetadata("Чтение", "Читаю", ToolDisplayOperation.Read, new[] { "target", "procedure", "startLine", "lineCount", "startChar", "charCount" }) },
            { "core.search", new ToolDisplayMetadata("Поиск в ресурсе", "Ищу в ресурсе", ToolDisplayOperation.Search, new[] { "scope", "target", "directory", "query", "mode" }) },
            { "resources.outline", new ToolDisplayMetadata("Структура ресурса", "Строю карту ресурса", ToolDisplayOperation.Read) },
            { "task.list.set", new ToolDisplayMetadata("Обновление шагов задачи", "Обновляю шаги задачи", ToolDisplayOperation.Plan) },
            { "vba.module.delete", new ToolDisplayMetadata("Удаление VBA-модуля", "Удаляю VBA-модуль", ToolDisplayOperation.Delete, new[] { "moduleName" }) },
            { "vba.module.patch", new ToolDisplayMetadata("Изменение VBA-модуля", "Изменяю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "vba.module.rename", new ToolDisplayMetadata("Переименование VBA-модуля", "Переименовываю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "vba.module.restore", new ToolDisplayMetadata("Восстановление VBA-модуля", "Восстанавливаю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "vba.module.write", new ToolDisplayMetadata("Запись VBA-модуля", "Записываю VBA-модуль", ToolDisplayOperation.Write, new[] { "moduleName" }) },
            { "excel.sheet.add", new ToolDisplayMetadata("Создание листа", "Создаю лист", ToolDisplayOperation.Write) },
            { "excel.table.add", new ToolDisplayMetadata("Создание таблицы", "Создаю таблицу", ToolDisplayOperation.Write) },
            { "excel.range.clear", new ToolDisplayMetadata("Очистка диапазона", "Очищаю диапазон", ToolDisplayOperation.Delete) },
            { "excel.rows.columns.delete", new ToolDisplayMetadata("Удаление строк или столбцов", "Удаляю строки или столбцы", ToolDisplayOperation.Delete,
                new[] { "sheet", "scope", "table", "axis", "start", "count" }) },
            { "excel.chat.chart.create", new ToolDisplayMetadata("Создание диаграммы в чате", "Создаю диаграмму в чате", ToolDisplayOperation.Chart) },
            { "excel.chart.delete", new ToolDisplayMetadata("Удаление диаграммы", "Удаляю диаграмму", ToolDisplayOperation.Delete) },
            { "excel.range.filter", new ToolDisplayMetadata("Фильтрация диапазона", "Фильтрую диапазон", ToolDisplayOperation.Write) },
            { "excel.cells.find", new ToolDisplayMetadata("Поиск ячеек", "Ищу ячейки", ToolDisplayOperation.Search) },
            { "excel.range.format", new ToolDisplayMetadata("Форматирование диапазона", "Оформляю диапазон", ToolDisplayOperation.Write) },
            { "excel.inspect", new ToolDisplayMetadata("Проверка структуры книги", "Проверяю структуру книги", ToolDisplayOperation.Read) },
            { "excel.sheet.rename", new ToolDisplayMetadata("Переименование листа", "Переименовываю лист", ToolDisplayOperation.Write) },
            { "excel.cells.replace", new ToolDisplayMetadata("Замена содержимого ячеек", "Заменяю содержимое ячеек", ToolDisplayOperation.Write) },
            { "excel.range.sort", new ToolDisplayMetadata("Сортировка диапазона", "Сортирую диапазон", ToolDisplayOperation.Write) },
            { "excel.chart.upsert", new ToolDisplayMetadata("Обновление диаграммы", "Обновляю диаграмму", ToolDisplayOperation.Chart) },
            { "excel.range.write", new ToolDisplayMetadata("Запись диапазона", "Записываю диапазон", ToolDisplayOperation.Write) },
            { "outlook.draft.create", new ToolDisplayMetadata("Создание черновика письма", "Создаю черновик письма", ToolDisplayOperation.Write) },
            { "outlook.mail.search", new ToolDisplayMetadata("Поиск письма", "Ищу письмо", ToolDisplayOperation.Search) },
            { "outlook.archive.index", new ToolDisplayMetadata("Индексирование архива Outlook", "Читаю письма архива", ToolDisplayOperation.Read) },
            { "outlook.mail.update", new ToolDisplayMetadata("Обновление письма", "Обновляю письмо", ToolDisplayOperation.Write) },
            { "powerpoint.object.add", new ToolDisplayMetadata("Создание объекта", "Создаю объект", ToolDisplayOperation.Write) },
            { "powerpoint.slide.add", new ToolDisplayMetadata("Создание слайда", "Создаю слайд", ToolDisplayOperation.Write) },
            { "powerpoint.slide.duplicate", new ToolDisplayMetadata("Копирование слайда", "Копирую слайд", ToolDisplayOperation.Write) },
            { "powerpoint.objects.list", new ToolDisplayMetadata("Просмотр списка объектов", "Смотрю список объектов", ToolDisplayOperation.Read) },
            { "powerpoint.slide.move", new ToolDisplayMetadata("Перемещение слайда", "Перемещаю слайд", ToolDisplayOperation.Write) },
            { "powerpoint.text.replace", new ToolDisplayMetadata("Замена текста", "Заменяю текст", ToolDisplayOperation.Write) },
            { "powerpoint.text.search", new ToolDisplayMetadata("Поиск текста", "Ищу текст", ToolDisplayOperation.Search) },
            { "powerpoint.text.set", new ToolDisplayMetadata("Изменение текста", "Меняю текст", ToolDisplayOperation.Write) },
            { "word.comment.add", new ToolDisplayMetadata("Создание комментария", "Создаю комментарий", ToolDisplayOperation.Write) },
            { "word.table.add", new ToolDisplayMetadata("Создание таблицы", "Создаю таблицу", ToolDisplayOperation.Write) },
            { "word.text.find", new ToolDisplayMetadata("Поиск текста", "Ищу текст", ToolDisplayOperation.Search) },
            { "word.text.format", new ToolDisplayMetadata("Форматирование текста", "Оформляю текст", ToolDisplayOperation.Write) },
            { "word.page.break.insert", new ToolDisplayMetadata("Вставка разрыва страницы", "Вставляю разрыв страницы", ToolDisplayOperation.Write) },
            { "word.inspect", new ToolDisplayMetadata("Проверка структуры документа", "Проверяю структуру документа", ToolDisplayOperation.Read) },
            { "word.text.replace", new ToolDisplayMetadata("Замена текста", "Заменяю текст", ToolDisplayOperation.Write) },
            { "word.text.write", new ToolDisplayMetadata("Запись текста", "Записываю текст", ToolDisplayOperation.Write) },
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
            if (command.ToolId == "core.find")
                return string.Join(" · ", new[] { text("query"), ResourceCaption(text("scope")) }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (command.ToolId == "core.read")
                return string.Join(" · ", new[] { text("target"), ResourceCaption(text("representation")) }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (command.ToolId == "core.tools.read") return text("id");
            if (command.ToolId == "core.tools.search") return text("query");
            if ((command.ToolId ?? string.Empty).StartsWith("powerpoint.", StringComparison.Ordinal))
            {
                object index;
                var slide = command.Arguments.TryGetValue("slideIndex", out index) && (index is int || index is long)
                    ? "Слайд " + Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture)
                    : "Текущий слайд";
                if (command.ToolId == "powerpoint.slide.add") return string.IsNullOrWhiteSpace(text("title")) ? "Новый слайд" : text("title");
                if (command.ToolId == "powerpoint.objects.list" && text("kind") == "slides") return "Слайды презентации";
                if ((command.ToolId == "powerpoint.text.search" || command.ToolId == "powerpoint.text.replace") &&
                    (!command.Arguments.TryGetValue("slideIndex", out index) || Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture) == "0"))
                    slide = "Вся презентация";
                if (command.ToolId == "powerpoint.slide.move" && command.Arguments.TryGetValue("toIndex", out index) &&
                    (index is int || index is long))
                    return slide + " → позиция " + Convert.ToString(index, System.Globalization.CultureInfo.InvariantCulture);
                var target = text("target") == "notes" ? "Заметки" : text("shapeName");
                if (command.ToolId == "powerpoint.object.add") target = text("kind") == "table" ? "Таблица" :
                    text("kind") == "picture" ? "Изображение" : text("kind") == "textBox" ? "Текстовый блок" : target;
                return string.Join(" · ", new[] { slide, target, text("query"), text("find"), text("path") }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            if ((command.ToolId ?? string.Empty).StartsWith("word.", StringComparison.Ordinal))
            {
                var location = command.ToolId == "word.text.write" && text("mode") != "paragraph" ? "selection" : text("location");
                if (command.ToolId == "word.inspect") return "Документ";
                var scope = text("scope");
                var target = command.ToolId == "word.text.format" && text("kind") == "font" ? "selection" : text("target");
                if (command.ToolId == "word.text.format" && string.IsNullOrWhiteSpace(target)) target = "selection";
                if (string.IsNullOrWhiteSpace(scope) && (command.ToolId == "word.text.find" || command.ToolId == "word.text.replace")) scope = "main";
                var place = location == "start" ? "Начало документа" : location == "end" ? "Конец документа" :
                    location == "selection" || scope == "selection" || target == "selection" ? "Выделение" :
                    scope == "main" ? "Текст документа" : scope == "all" || target == "document" ? "Весь документ" : string.Empty;
                if (string.IsNullOrWhiteSpace(place) && (command.ToolId == "word.text.write" ||
                    command.ToolId == "word.table.add" || command.ToolId == "word.page.break.insert" ||
                    command.ToolId == "word.comment.add")) place = "Выделение";
                return string.Join(" · ", new[] { place, text("query"), text("find"), target == "selection" || target == "document" ? "" : target }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            if (command.ToolId == "outlook.draft.create")
            {
                var draftTarget = string.Join(" · ", new[] { text("subject"), text("to") }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var kind = text("kind");
                if (kind == "reply" || kind == "replyAll" || kind == "forward")
                    return (kind == "replyAll" ? "Ответ всем" : kind == "forward" ? "Пересылка" : "Ответ") +
                        " · Текущее письмо" + (string.IsNullOrWhiteSpace(draftTarget) ? "" : " · " + draftTarget);
                return string.IsNullOrWhiteSpace(draftTarget) ? "Новое письмо" : draftTarget;
            }
            if (command.ToolId == "outlook.mail.update")
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
