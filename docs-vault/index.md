# z3nIO

z3nIO — локальный веб-дашборд для управления фермой аккаунтов на базе ZennoPoster. Запускается как отдельный .NET 8 процесс на Windows-машине оператора и открывается в браузере.

Дашборд не заменяет ZennoPoster — он работает поверх него: читает состояние задач из БД, отправляет команды в очередь, принимает логи и трафик от запущенных ZP-шаблонов.

---

## Что делает

**Управление задачами** — запуск, остановка, настройка ZP7-задач без открытия ZennoPoster. Счётчики, потоки, расписание — всё через браузер.

**Мониторинг** — живые логи и HTTP-трафик от каждой задачи в реальном времени через SSE.

**Инструменты разработки** — перехват и replay HTTP-запросов, JSON-viewer с детектированием токенов и captcha, кодогенерация (C#, Python, cURL).

**Автоматизация** — встроенный планировщик для запуска .py / .js / .exe / .bat скриптов по cron, интервалу или фиксированному времени.

**Хранилище шаблонов** — Cliplates: дерево copy-paste сниппетов, копирование одним кликом.

---

## Как запускается

При старте z3nIO читает `appsettings.secrets.json`. Если файл отсутствует — дашборд открывается на странице [[shift + ctrl + 0.    Config]] для первоначальной настройки. Если файл есть — сразу открывается [[shift + ctrl + 1.    z3nIO (Scheduler)]].

Дашборд доступен по адресу `http://localhost:10993` (порт настраивается в [[shift + ctrl + 0.    Config]]).

---

## Архитектура с точки зрения оператора

```
ZennoPoster задачи
    │  логи → LogHost
    │  трафик → TrafficHost
    │  команды ← БД (_commands)
    ▼
z3nIO (встроенный HTTP-сервер)
    │
    ▼
Браузер (дашборд)
```

ZP-шаблоны отправляют логи и трафик на адреса `LogHost` / `TrafficHost` из конфига. z3nIO принимает их и сохраняет в БД. Команды (start, stop, и др.) z3nIO кладёт в таблицу `_commands` — ZP-шаблон опрашивает её и выполняет.

---

## Страницы

|Страница|Назначение|
|---|---|
|[[shift + ctrl + 0.    Config]]|Первоначальная настройка: БД, порты, папки|
|[[shift + ctrl + 2.    ZP7]]|Управление ZP7-задачами|
|[[shift + ctrl + 4.    Logs]]|Живые логи приложения|
|[[shift + ctrl + 5.    HTTP]]|Перехваченный трафик, replay, кодогенерация|
|[[shift + ctrl + 6.    JSON]]|JSON-viewer с security-детектированием|
|[[shift + ctrl + 1.    z3nIO (Scheduler)]]|Запуск скриптов по расписанию|
|[[shift + ctrl + 8.    Clips]]|Copy-paste шаблоны|
|[[shift + ctrl + 7.    Text Tools]]|URL encode/decode, Base64, C# escaper, JSON escape|
|[[XML Converter not Implemented]]|XML InputSettings → JSON payload|
|[[shift + ctrl + H.    Docs]]|Встроенная справка|

---


---

## Глобальные элементы

- [[Nav Dock]] — плавающая панель навигации, присутствует на всех страницах
- [[Nav Dock#Горячие клавиши|Горячие клавиши]] — полная таблица хоткеев
- [[Nav Dock#OTP Generator|OTP Generator]] — генерация TOTP-кода (`Ctrl+Shift+O`)

---

## Первый запуск

- [[01. Настройка сервера]] — первичная настройка z3nIO: БД, порты, jVars, импорт данных фермы
- [[02. Добавление машины воркера]] — GenerateClientBundle, set worker


---

## Внутреннее

- [[03. SAFU — Архитектура]] — схема деривации ключей, z3n7 API, миграция _(dev)_

---
## Страницы


### [[shift + ctrl + 1.    z3nIO (Scheduler)]]

Запуск скриптов по расписанию.

- [[shift + ctrl + 1.    z3nIO (Scheduler)#Список задач|Список задач]] — группировка, статусы, executor-фильтр
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Создание и редактирование задачи|Создание задачи]] — имя, путь, executor, args, enabled
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Расписание|Расписание]] — cron, interval, fixed time, overlap policy
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Execution tab|Execution tab]] — статус, PID, uptime, memory, parallel instances
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Output tab|Output tab]] — вывод последнего запуска
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Payload — Set Values и Build Schema|Payload]] — Set Values, Build Schema, Import, Export
- [[shift + ctrl + 1.    z3nIO (Scheduler)#Logs панель|Logs панель]] — логи задачи + SSE live
- [[shift + ctrl + 1.    z3nIO (Scheduler)#HTTP панель|HTTP панель]] — трафик задачи + SSE live

---


### [[shift + ctrl + 2.    ZP7]]

Управление ZP7-задачами. Центральная страница фермы.

- [[shift + ctrl + 2.    ZP7#Список задач|Список задач]] — группировка machine → project, статусы, индикаторы
- [[shift + ctrl + 2.    ZP7#Фильтры и теги|Фильтры и теги]] — поиск по имени, статусу, GroupLabels
- [[shift + ctrl + 2.    ZP7#Task Detail|Task - Detail]] — карточка выбранной задачи
- [[shift + ctrl + 2.    ZP7#Команды|Команды]] — start, stop, interrupt, tries, threads
- [[shift + ctrl + 2.    ZP7#Execution Settings|Execution Settings]] — потоки, приоритет, счётчики, прокси, GroupLabels
- [[shift + ctrl + 2.    ZP7#Scheduler Settings|Scheduler Settings]] — period, intervals, start/end, repeat
- [[shift + ctrl + 2.    ZP7#Settings|Settings]] — редактор полей InputSettings задачи
- [[shift + ctrl + 2.    ZP7#Logs панель|Logs панель]] — логи задачи + SSE live
- [[shift + ctrl + 2.    ZP7#HTTP панель|HTTP панель]] — трафик задачи + SSE live
- [[shift + ctrl + 2.    ZP7#Commands Queue|Commands Queue]] — очередь команд, фильтр, очистка
- [[shift + ctrl + 2.    ZP7#Heatmap|Heatmap]] — визуализация успех/ошибка по проекту

---
### [[shift + ctrl + 3.    ZB]]

Мониторинг и управление ZennoBrowser через ZB API.

- [[shift + ctrl + 3.    ZB#Статус подключения|Статус подключения]] — host, key, connect
- [[shift + ctrl + 3.    ZB#Instances|Instances]] — запущенные инстансы, Stop / Kill, WS Endpoint
- [[shift + ctrl + 3.    ZB#Profiles|Profiles]] — список профилей, поиск, сортировка, запуск
- [[shift + ctrl + 3.    ZB#Proxies|Proxies]] — прокси и статус проверки
- [[shift + ctrl + 3.    ZB#Threads|Threads]] — активные потоки, освобождение

---

### [[shift + ctrl + 4.    Logs]]

Живые логи приложения.

- [[shift + ctrl + 4.    Logs#Таблица логов|Таблица логов]] — Time, Level, Machine, Project, Uptime, PID, Port, Account, Origin, Message
- [[shift + ctrl + 4.    Logs#Фильтры|Фильтры]] — level, machine, project, session, port, pid, account, origin, uptime
- [[shift + ctrl + 4.    Logs#Detail-панель|Detail-панель]] — полная запись, копирование сообщения
- [[shift + ctrl + 4.    Logs#Счётчики в шапке|Счётчики]] — shown, errors, warnings
- [[shift + ctrl + 4.    Logs#Edge cases|Edge cases]] — логи не появляются, uptime не считается

---

### [[shift + ctrl + 5.    HTTP]]

Перехваченный HTTP-трафик из ZP-задач.

- [[shift + ctrl + 5.    HTTP#Список запросов|Список запросов]] — method, status, url, duration, account
- [[shift + ctrl + 5.    HTTP#Фильтры|Фильтры]] — method, status, URL, machine, project, limit
- [[shift + ctrl + 5.    HTTP#Detail-панель|Detail-панель]] — request headers/cookies/body + response headers/body
- [[shift + ctrl + 5.    HTTP#Replay|Replay]] — повторная отправка запроса с редактированием
- [[shift + ctrl + 5.    HTTP#Кодогенерация|Кодогенерация]] — HttpClient, ZP7, Hybrid, Python, TypeScript, cURL
- [[shift + ctrl + 5.    HTTP#Кодогенерация|API Skeleton / Example]] — агрегация endpoint'ов из текущей выборки

---

### [[shift + ctrl + 6.    JSON]]

Интерактивный JSON-viewer с security-детектированием.

- [[shift + ctrl + 6.    JSON#Ввод и парсинг|Ввод и парсинг]] — вставка JSON, авто-парсинг, Ctrl+Enter
- [[shift + ctrl + 6.    JSON#Навигация по дереву|Навигация по дереву]] — collapse/expand, depth-кнопки, виртуальный скролл
- [[shift + ctrl + 6.    JSON#Security Badges|Security Badges]] — Bearer, Basic, API Key, CF, Turnstile, Captcha
- [[shift + ctrl + 6.    JSON#Field Filter|Field Filter]] — фильтр по полям массива, пресеты api / headers / status
- [[shift + ctrl + 6.    JSON#Поиск|Поиск]] — подстрока по ключам и значениям
- [[shift + ctrl + 6.    JSON#Действия на узлах|Действия на узлах]] — copy value, C# selector, replay URL, hide/show
- [[shift + ctrl + 6.    JSON#Replay-модальное окно|Replay]] — отправка запроса из JSON-структуры

---

### [[shift + ctrl + 8.    Clips]]

Дерево copy-paste шаблонов.

- [[shift + ctrl + 8.    Clips#Дерево|Дерево]] — папки по path, навигация, поиск
- [[shift + ctrl + 8.    Clips#Копирование|Копирование]] — клик по листу → буфер обмена
- [[shift + ctrl + 8.    Clips#Редактор|Редактор]] — создание, редактирование, удаление записи

---

### [[shift + ctrl + 7.    Text Tools]]

Конвертеры строк.

- [[shift + ctrl + 7.    Text Tools#URL Encode / Decode|URL Encode / Decode]]
- [[shift + ctrl + 7.    Text Tools#C# String Escaper|C# String Escaper]] — обычный и verbatim (`@"..."`)
- [[shift + ctrl + 7.    Text Tools#Base64 Encode / Decode|Base64 Encode / Decode]]
- [[shift + ctrl + 7.    Text Tools#JSON String Escape|JSON String Escape]]

---


### [[shift + ctrl + 0.    Config]]

Настройки сервера и подключений.

- [[shift + ctrl + 0.    Config#Status|Status]] — uptime, порт, режим БД, хосты, listening ports
- [[shift + ctrl + 0.    Config#DbConfig|DbConfig]] — SQLite или PostgreSQL
- [[shift + ctrl + 0.    Config#Logs & Server|Logs & Server]] — Dashboard Port, LogHost, TrafficHost, папки, MaxFileSize
- [[shift + ctrl + 0.    Config#Security · jVars|jVars]] — PIN, путь к .dat-файлу, расшифровка кошельков
- [[shift + ctrl + 0.    Config#Storage|Storage]] — размер лог-файлов, прогресс-бар, список файлов
- [[shift + ctrl + 0.    Config#Storage|Очистка логов]] — App logs, HTTP logs, Traffic logs, ALL logs
- [[shift + ctrl + 0.    Config#Fill DB (Import)|Fill DB]] — загрузка аккаунтов, прокси и данных фермы

---

### [[XML Converter not Implemented]]

Конвертер InputSettings XML (ZennoPoster) → JSON payload.

- [[XML Converter not Implemented#Ввод XML|Ввод XML]] — вставка XML-блока InputSettings
- [[XML Converter not Implemented#Три формата вывода|Schema / Values / Combined]] — три формата вывода JSON
- [[XML Converter not Implemented#Preview|Preview]] — таблица полей с типами и значениями

---

### [[shift + ctrl + H.    Docs]]

Встроенная справка.

- [[shift + ctrl + H.    Docs#Навигация|Навигация]] — дерево разделов, expand all
- [[shift + ctrl + H.    Docs#Разделы|Разделы]] — Горячие клавиши, Scheduler, PM, Cliplates, HTTP, Config
