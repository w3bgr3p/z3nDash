# Config

Страница `/config.html` управляет локальной конфигурацией z3nDash.

Разделы Overview, Database, Logs & Server, Browsers API, OmniRoute, Services,
Security, Maintenance и Interface переключаются в боковой панели. Последний открытый раздел
восстанавливается при следующем посещении страницы.

## Server Status

Показывает:

- состояние конфигурации;
- подключение к базе;
- dashboard port;
- каталоги логов и отчётов;
- выбранный режим БД.

Данные загружаются через `GET /config/status`.

## Database

Поддерживаются два режима:

- PostgreSQL — одно поле connection string;
- SQLite — путь к файлу.

Для PostgreSQL схема берётся из `Search Path`. Connection string можно скопировать кнопкой рядом с полем.

## Logs & Server

Настраиваются порт панели и рабочие каталоги.

Сервер слушает только `localhost`. Менять это не следует: маршруты не требуют
аутентификации и позволяют запускать процессы и читать секреты.

## Browsers API

Адрес и токен локального API для двух браузеров:

- **ZennoBrowser** — по умолчанию `http://localhost:8160`, токен уходит заголовком `Api-Token`;
- **ShardX** — по умолчанию `http://127.0.0.1:40325`, токен (постоянный JWT из настроек
  лаунчера) уходит как `Authorization: Bearer`.

Эти настройки использует страница [[Browser]] и xml-задачи с режимом браузера `shardx`.

## OmniRoute

OmniRoute — единственный AI-провайдер. Поле содержит URL сервиса, по умолчанию `http://localhost:20128`.

`Validate & Save AI` сохраняет адрес и проверяет доступность `/v1/models`. Результат показывается toast-уведомлением.

## Security

Раздел jVars сохраняет зашифрованные локальные переменные и путь к файлу jVars.

## Interface

- тема: Graphite, Aluminium, Hyper или Paper (`Alt+T` переключает их);
- край экрана для [[Nav Dock]] (`Alt+P` — следующий край);
- **Dock pages** — какие страницы показывает док. Config из дока не убирается:
  иначе в настройки из дока не попасть.

Изменения применяются сразу, без кнопки сохранения, и действуют на всех страницах.
Хранятся в `ui-state.json` рядом с исполняемым файлом: `theme`, `dockPosition` и
`dockHidden` — список скрытых страниц. Страница, добавленная в `navpath.js`
позже, появляется в доке сама.

`POST /config/ui` сливает присланные поля с сохранёнными: можно слать только
изменившееся поле.

Проверка инструментов машины (Environment) перенесена на страницу [[System Snapshot]].

## Memory Watchdog

Настраиваются:

- имя корневого процесса;
- включение watchdog;
- лимит памяти в MB;
- интервал проверки.

Watchdog считает память корневого процесса и его дочернего дерева. При превышении ненулевого лимита дерево процессов завершается.

## Storage

Показывает размер и количество файлов в каталоге логов — вместе с `trafficLog.jsonl`
и его ротированными копиями.

Очистка одна — `POST /clear-all-logs`, весь каталог целиком. Отдельных кнопок
для логов и трафика нет.

## Import

Доступны только:

- proxy;
- EVM addresses;
- SOL addresses.

Используются `POST /import/proxy` и `POST /import/addresses`.

## API

- `GET /config`
- `POST /config`
- `GET /config/status`
- `GET /config/storage`
- `POST /config/jvars`
- `POST /config/ai-validate`
- `GET /config/ai-models`
- `GET /config/ui`
- `POST /config/ui`
