# XML Converter

Конвертер XML-формата `InputSettings` (ZennoPoster) в JSON payload для [[shift + ctrl + 1.    z3nIO (Scheduler)]] и [[shift + ctrl + 2.    ZP7]].

Путь: `/xml.html`

---

## Назначение

ZP-шаблоны хранят настройки в XML-блоке `InputSettings`. XML Converter разбирает этот блок и превращает его в три JSON-представления, которые можно использовать как payload в Scheduler или передать в PM Settings.

---

## Ввод XML

Левая панель — textarea для вставки XML.

Вставь XML-блок `InputSettings` из ZP-шаблона. Обёртка `<InputSettings>...</InputSettings>` необязательна — конвертер добавит её автоматически.

Конвертация запускается автоматически при вводе. Кнопка **↺ Convert** — принудительно.

Если XML невалиден — конвертер переключается на regex-парсер (помечается как `regex` в счётчике). Это нормально для нестандартных ZP-шаблонов с тегом `<n>` вместо `<Name>`.

Счётчики в шапке: количество нод в XML и количество полей с key.

---

## Три формата вывода

Правая панель. Переключаются вкладками.

### Schema

Список полей с метаданными — без значений. Используется чтобы описать структуру настроек задачи в Scheduler (кнопка **🔧 Build Schema**).

```json
[
  { "key": "WorkMode", "label": "Work Mode", "type": "select", "options": "Cooldown, NewRandom, UpdateToken" },
  { "key": "Threads",  "label": "Threads",   "type": "text" },
  { "key": "Enabled",  "label": "Enabled",   "type": "boolean" }
]
```

### Values

Текущие значения полей из XML.

```json
{ "WorkMode": "Cooldown", "Threads": "5", "Enabled": "True" }
```

### Combined

Schema и Values в одном объекте — готовый payload для импорта в [[shift + ctrl + 1.    z3nIO (Scheduler)]] через **📥 Import**.

```json
{
  "schema": "[{\"key\":\"WorkMode\",...}]",
  "values": "{\"WorkMode\":\"Cooldown\",...}"
}
```

---

## Preview

Вкладка **Preview** — таблица всех полей: тип, key, label, значение по умолчанию, опции или help-текст.

Используется для быстрой проверки что конвертация прошла корректно.

---

## Типы полей

|XML Type|JSON type|Отображение в Scheduler|
|---|---|---|
|`Text`, `Number`, `Label`, `FileName`|`text`|Текстовое поле|
|`Password`|`password`|Скрытое поле|
|`Boolean`|`boolean`|Чекбокс|
|`DropDown`|`select`|Выпадающий список|
|`DropDownMultiSelect`|`multiselect`|Множественный выбор|
|`Tab`|`tab`|Вкладка в форме|
|`Comment`|`section`|Разделитель-заголовок|

Опции dropdown берутся из имени поля в фигурных скобках: `WorkMode{Cooldown|NewRandom|UpdateToken}` → `options: "Cooldown, NewRandom, UpdateToken"`.

---

## Edge cases

**«No InputSetting elements found»** — XML не содержит блоков `<InputSetting>`. Убедись что вставлен правильный XML из раздела настроек ZP-шаблона.

**Счётчик показывает `(regex)`** — XML-парсер не справился, используется regex-парсинг. Результат обычно корректный, но проверь Preview на наличие всех полей.

**Поля без key пропускаются** — `Tab` и `Comment` элементы без `OutputVariable` не имеют key и не попадают в Values. Это нормально — они служат только для структуры формы.

---

## Связанные страницы

- [[shift + ctrl + 1.    z3nIO (Scheduler)]] — вставь Combined через **📥 Import** чтобы передать настройки задаче
- [[shift + ctrl + 2.    ZP7]] — кнопка **⚙ Settings** использует аналогичный механизм для редактирования InputSettings напрямую