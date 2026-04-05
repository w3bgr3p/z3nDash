# z3nIO

z3nIO — локальный веб-инструментарий для разработчика и оператора автоматизации. Запускается как отдельный .NET процесс, открывается в браузере или как borderless overlay поверх рабочего стола.

Ядро — универсальный оркестратор задач (Tasker): запуск параметризованных скриптов на любом языке из единого интерфейса. Вокруг него — инструменты мониторинга, отладки, анализа и управления.

---

## Что делает

**Tasker (оркестратор)** — централизованный планировщик для запуска скриптов на Python, Node.js/TypeScript, Bash, PowerShell, C# (csx). Cron, интервал, фиксированное время, on-demand. Параметры задачи через UI, live-вывод, трекинг PID и памяти.

**Мониторинг** — живые логи и HTTP-трафик от задач и воркеров в реальном времени через SSE. Фильтрация по машине, проекту, аккаунту, сессии.

**HTTP-инструменты** — перехват запросов/ответов, replay с редактированием, кодогенерация (C#, Python, TypeScript, cURL), агрегация API-эндпоинтов.

**JSON viewer** — интерактивный tree с виртуальным скроллом, детектирование auth/captcha артефактов, поиск, replay запросов из узла.

**Analyzer** — сканирование C#-библиотек и папок, построение графов структуры и взаимосвязей классов.

**System Snapshot** — снимок состояния машины (процессы, сеть, RAM), сохранение в БД, AI-аудит.

**Docs** — встроенный браузер документации, совместимый с Obsidian/Quartz. По умолчанию отображает собственные доки, можно подключить любой vault.

**OTP** — быстрый TOTP-генератор по хоткею, без телефона и Google Authenticator.

**Clips** — clipboard-менеджер: дерево copy-paste шаблонов, копирование одним кликом.

**Text Tools** — URL encode/decode, Base64, C# String Escaper, JSON escape.

**ZP7** — управление ZennoPoster-задачами на нескольких машинах через очередь команд в БД.

**ZB** — управление ZennoBrowser: инстансы, профили, прокси, потоки через ZB API.

**Treasury** — трекинг EVM-кошельков по нескольким сетям, AI-анализ балансов.

---

## Как запускается

При старте z3nIO читает `appsettings.secrets.json`. Если файл отсутствует — дашборд открывается на странице [[Config]] для первоначальной настройки. Если файл есть — сразу открывается [[Tasker]].

Дашборд доступен по адресу `http://localhost:10993` (порт настраивается в [[Config]]).

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

| Страница                          | Хоткей | Назначение                                         |
| --------------------------------- | ------ | -------------------------------------------------- |
| [[Config]]                        | Alt+0  | Первоначальная настройка: БД, порты, папки         |
| [[Tasker]]                        | Alt+1  | Запуск скриптов по расписанию (Scheduler)          |
| [[ZP7]]                           | Alt+2  | Управление ZP7-задачами                            |
| [[ZB]]                            | Alt+3  | Мониторинг ZennoBrowser                            |
| [[Logs]]                          | Alt+4  | Живые логи приложения                              |
| [[HTTP]]                          | Alt+5  | Перехваченный трафик, replay, кодогенерация        |
| [[JSON]]                          | Alt+6  | JSON-viewer с security-детектированием             |
| [[Text Tools]]                    | Alt+7  | URL encode/decode, Base64, C# escaper, JSON escape |
| [[Treasury]]                      | Alt+8  | Управление криптовалютными кошельками              |
| [[AI Report]]                     | Alt+9  | AI-анализ проектов через LLM                       |
| [[Clips]]                         | Alt+C  | Copy-paste шаблоны                                 |
| [[Graph]]                         | Alt+G  | Визуализация графа C# репозитория                  |
| [[Docs]]                          | Alt+H  | Встроенная справка                                 |
| [[System Snapshot]]               | Alt+S  | Мониторинг системы и AI-аудит                      |
| [[Report]]                        | —      | Сводный отчет по проектам и соцсетям               |


---
ML InputSet

---

## Глобальные элементы

- [[Nav Dock]] — плавающая панель навигации, присутствует на всех страницах
- [[Nav Dock#Горячие клавиши|Горячие клавиши]] — полная таблица хоткеев
- [[Nav Dock#OTP Generator|OTP Generator]] — генерация TOTP-кода (`Ctrl+Shift+O`)

---

## Первый запуск

- [[01. Настройка сервера]] — первичная настройка z3nIO: БД, порты, jVars, импорт данных фермы
- [[02. Добавление ZennoPoster воркера в локальной сети]] — GenerateClientBundle, set worker


---


---
## Страницы


### [[Tasker]]

Запуск скриптов по расписанию. **Хоткей:** `Alt+1`

- [[Tasker#Список задач|Список задач]] — группировка, статусы, executor-фильтр
- [[Tasker#Создание и редактирование задачи|Создание задачи]] — имя, путь, executor, args, enabled
- [[Tasker#Расписание|Расписание]] — cron, interval, fixed time, overlap policy
- [[Tasker#Execution tab|Execution tab]] — статус, PID, uptime, memory, parallel instances
- [[Tasker#Output tab|Output tab]] — вывод последнего запуска
- [[Tasker#Payload — Set Values и Build Schema|Payload]] — Set Values, Build Schema, Import, Export
- [[Tasker#Logs панель|Logs панель]] — логи задачи + SSE live
- [[Tasker#HTTP панель|HTTP панель]] — трафик задачи + SSE live

---


### [[ZP7]]

Управление ZP7-задачами. Центральная страница фермы. **Хоткей:** `Alt+2`

- [[ZP7#Список задач|Список задач]] — группировка machine → project, статусы, индикаторы
- [[ZP7#Фильтры и теги|Фильтры и теги]] — поиск по имени, статусу, GroupLabels
- [[ZP7#Task Detail|Task - Detail]] — карточка выбранной задачи
- [[ZP7#Команды|Команды]] — start, stop, interrupt, tries, threads
- [[ZP7#Execution Settings|Execution Settings]] — потоки, приоритет, счётчики, прокси, GroupLabels
- [[ZP7#Scheduler Settings|Scheduler Settings]] — period, intervals, start/end, repeat
- [[ZP7#Settings|Settings]] — редактор полей InputSettings задачи
- [[ZP7#Logs панель|Logs панель]] — логи задачи + SSE live
- [[ZP7#HTTP панель|HTTP панель]] — трафик задачи + SSE live
- [[ZP7#Commands Queue|Commands Queue]] — очередь команд, фильтр, очистка
- [[ZP7#Heatmap|Heatmap]] — визуализация успех/ошибка по проекту

---
### [[ZB]]

Мониторинг и управление ZennoBrowser через ZB API. **Хоткей:** `Alt+3`

- [[ZB#Статус подключения|Статус подключения]] — host, key, connect
- [[ZB#Instances|Instances]] — запущенные инстансы, Stop / Kill, WS Endpoint
- [[ZB#Profiles|Profiles]] — список профилей, поиск, сортировка, запуск
- [[ZB#Proxies|Proxies]] — прокси и статус проверки
- [[ZB#Threads|Threads]] — активные потоки, освобождение

---

### [[Logs]]

Живые логи приложения. **Хоткей:** `Alt+4`

- [[Logs#Таблица логов|Таблица логов]] — Time, Level, Machine, Project, Uptime, PID, Port, Account, Origin, Message
- [[Logs#Фильтры|Фильтры]] — level, machine, project, session, port, pid, account, origin, uptime
- [[Logs#Detail-панель|Detail-панель]] — полная запись, копирование сообщения
- [[Logs#Счётчики в шапке|Счётчики]] — shown, errors, warnings
- [[Logs#Edge cases|Edge cases]] — логи не появляются, uptime не считается

---

### [[HTTP]]

Перехваченный HTTP-трафик из ZP-задач. **Хоткей:** `Alt+5`

- [[HTTP#Список запросов|Список запросов]] — method, status, url, duration, account
- [[HTTP#Фильтры|Фильтры]] — method, status, URL, machine, project, limit
- [[HTTP#Detail-панель|Detail-панель]] — request headers/cookies/body + response headers/body
- [[HTTP#Replay|Replay]] — повторная отправка запроса с редактированием
- [[HTTP#Кодогенерация|Кодогенерация]] — HttpClient, ZP7, Hybrid, Python, TypeScript, cURL
- [[HTTP#Кодогенерация|API Skeleton / Example]] — агрегация endpoint'ов из текущей выборки

---

### [[JSON]]

Интерактивный JSON-viewer с security-детектированием. **Хоткей:** `Alt+6`

- [[JSON#Ввод и парсинг|Ввод и парсинг]] — вставка JSON, авто-парсинг, Ctrl+Enter
- [[JSON#Навигация по дереву|Навигация по дереву]] — collapse/expand, depth-кнопки, виртуальный скролл
- [[JSON#Security Badges|Security Badges]] — Bearer, Basic, API Key, CF, Turnstile, Captcha
- [[JSON#Field Filter|Field Filter]] — фильтр по полям массива, пресеты api / headers / status
- [[JSON#Поиск|Поиск]] — подстрока по ключам и значениям
- [[JSON#Действия на узлах|Действия на узлах]] — copy value, C# selector, replay URL, hide/show
- [[JSON#Replay-модальное окно|Replay]] — отправка запроса из JSON-структуры

---

### [[Text Tools]]

Конвертеры строк. **Хоткей:** `Alt+7`

- [[Text Tools#URL Encode / Decode|URL Encode / Decode]]
- [[Text Tools#C# String Escaper|C# String Escaper]] — обычный и verbatim (`@"..."`)
- [[Text Tools#Base64 Encode / Decode|Base64 Encode / Decode]]
- [[Text Tools#JSON String Escape|JSON String Escape]]

---

### [[Clips]]

Дерево copy-paste шаблонов. **Хоткей:** `Alt+C`

- [[Clips#Дерево|Дерево]] — папки по path, навигация, поиск
- [[Clips#Копирование|Копирование]] — клик по листу → буфер обмена
- [[Clips#Редактор|Редактор]] — создание, редактирование, удаление записи

---

### [[Config]]

Настройки сервера и подключений. **Хоткей:** `Alt+0`

- [[Config#Status|Status]] — uptime, порт, режим БД, хосты, listening ports
- [[Config#DbConfig|DbConfig]] — SQLite или PostgreSQL
- [[Config#Logs & Server|Logs & Server]] — Dashboard Port, LogHost, TrafficHost, папки, MaxFileSize
- [[Config#Security · jVars|jVars]] — PIN, путь к .dat-файлу, расшифровка кошельков
- [[Config#Storage|Storage]] — размер лог-файлов, прогресс-бар, список файлов
- [[Config#Storage|Очистка логов]] — App logs, HTTP logs, Traffic logs, ALL logs
- [[Config#Fill DB (Import)|Fill DB]] — загрузка аккаунтов, прокси и данных фермы

---



---

### [[Treasury]]

Управление криптовалютными кошельками. **Хоткей:** `Alt+8`

- [[Treasury#Heatmap Table|Heatmap Table]] — балансы по аккаунтам и блокчейнам
- [[Treasury#Sidebar|Sidebar]] — Top Tokens, Chains Distribution, Portfolio Summary
- [[Treasury#AI Report|AI Report]] — AI-анализ портфеля
- [[Treasury#Операции|Операции]] — Update Balances, Swap All, Bridge To

---

### [[AI Report]]

AI-анализ проектов через LLM. **Хоткей:** `Alt+9`

- [[AI Report#Анализ одного проекта|Анализ одного проекта]]
- [[AI Report#Анализ всех проектов|Анализ всех проектов]]
- [[AI Report#Сводный анализ|Сводный анализ]]

---

### [[Graph]]

Визуализация графа зависимостей C# репозитория. **Хоткей:** `Alt+G`

- [[Graph#Генерация графа|Генерация графа]] — парсинг C# кода
- [[Graph#Навигация по графу|Навигация по графу]] — zoom, pan, клик на узел

---

### [[System Snapshot]]

Мониторинг системы и AI-аудит. **Хоткей:** `Alt+S`

- [[System Snapshot#Capture Now|Capture Now]] — захват снимка системы
- [[System Snapshot#Content Area|Content Area]] — просмотр секций (процессы, сеть, диски)
- [[System Snapshot#AI Audit|AI Audit]] — AI-анализ конфигурации

---

### [[Report]]

Сводный отчет по проектам и социальным сетям.

- [[Report#Social Networks Section|Social Networks]] — heatmap статусов соцсетей
- [[Report#Daily Projects Section|Daily Projects]] — heatmap выполнения задач
- [[Report#Process Monitor Sidebar|Process Monitor]] — мониторинг процессов по машинам

---

### [[Docs]]

Встроенная справка. **Хоткей:** `Alt+H`

- [[Docs#Навигация|Навигация]] — дерево разделов, expand all
- [[Docs#Разделы|Разделы]] — Горячие клавиши, Scheduler, PM, Cliplates, HTTP, Config
