# Config

Страница `/config.html` управляет локальной конфигурацией z3nDash.

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

Настраиваются адрес приёма логов, порт панели и рабочие каталоги.

Трафик отдельного адреса не требует: его пишут в файл, см. [[Traffic]].

## OmniRoute

OmniRoute — единственный AI-провайдер. Поле содержит URL сервиса, по умолчанию `http://localhost:20128`.

`Validate & Save AI` сохраняет адрес и проверяет доступность `/v1/models`. Результат показывается toast-уведомлением.

## Security

Раздел jVars сохраняет зашифрованные локальные переменные и путь к файлу jVars.

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

Очистка: `POST /clear` для логов приложения, `POST /clear-all-logs` для всего каталога.
Отдельной кнопки для трафика нет: файл ротируется сам, см. [[Traffic]].

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
