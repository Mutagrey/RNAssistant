using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    // Localized metadata for the shipped catalog. New/custom entries supply Display
    // themselves; the renderer never classifies their ids or argument names.
    internal static class ToolDisplayCatalog
    {
        private static readonly IReadOnlyDictionary<string, ToolDisplayMetadata> BuiltIns =
            new Dictionary<string, ToolDisplayMetadata>(StringComparer.Ordinal)
        {
            { "common.capabilities_read", new ToolDisplayMetadata("Изучение", "Изучаю", ToolDisplayOperation.Learn) },
            { "common.capabilities_search", new ToolDisplayMetadata("Поиск инструментов и навыков", "Ищу подходящие инструменты", ToolDisplayOperation.Search) },
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
            { "common.resources_read", new ToolDisplayMetadata("Чтение", "Читаю", ToolDisplayOperation.Read) },
            { "common.task_list_set", new ToolDisplayMetadata("Обновление шагов задачи", "Обновляю шаги задачи", ToolDisplayOperation.Plan) },
            { "common.vba_delete", new ToolDisplayMetadata("Удаление VBA-модуля", "Удаляю VBA-модуль", ToolDisplayOperation.Delete) },
            { "common.vba_patch", new ToolDisplayMetadata("Изменение VBA-модуля", "Изменяю VBA-модуль", ToolDisplayOperation.Write) },
            { "common.vba_rename", new ToolDisplayMetadata("Переименование VBA-модуля", "Переименовываю VBA-модуль", ToolDisplayOperation.Write) },
            { "common.vba_restore", new ToolDisplayMetadata("Восстановление VBA-модуля", "Восстанавливаю VBA-модуль", ToolDisplayOperation.Write) },
            { "common.vba_write", new ToolDisplayMetadata("Запись VBA-модуля", "Записываю VBA-модуль", ToolDisplayOperation.Write) },
            { "excel.add_sheet", new ToolDisplayMetadata("Создание листа", "Создаю лист", ToolDisplayOperation.Write) },
            { "excel.add_table", new ToolDisplayMetadata("Создание таблицы", "Создаю таблицу", ToolDisplayOperation.Write) },
            { "excel.clear_range", new ToolDisplayMetadata("Очистка диапазона", "Очищаю диапазон", ToolDisplayOperation.Delete) },
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

        internal static ToolDisplayMetadata Resolve(ToolCatalogEntry tool)
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
    }
}
