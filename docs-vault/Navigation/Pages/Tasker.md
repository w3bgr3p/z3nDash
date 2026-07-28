# Tasker

Tasker — страница `/scheduler.html` для хранения расписаний и запуска локальных задач.

## Список

Задачи читаются из таблицы `schedules`. В списке отображаются имя, состояние и основные параметры запуска.

## Редактирование

Для задачи задаются:

- имя;
- executor;
- путь к скрипту, файлу или задаче;
- аргументы;
- состояние `enabled`;
- расписание;
- политика overlap;
- payload.

Поддерживаемые executor:

`python`, `node`, `ts-node`, `npm`, `exe`, `cmd`, `bat`, `bash`, `ps1`, `internal`.

## Расписание

UI умеет формировать:

- запуск по требованию;
- ежедневный запуск;
- запуск по дням недели;
- запуск по дню месяца;
- интервал в минутах.

Итоговые значения сохраняются в `cron`, `interval_minutes` и `fixed_time`.

## Overlap

- `skip` — не запускать новый экземпляр, пока активен предыдущий;
- `parallel` — разрешить несколько экземпляров до `max_threads`;
- `kill_restart` — остановить активные экземпляры и запустить новый.

## Управление

Доступны:

- Run;
- Stop;
- Restart;
- включение/отключение расписания;
- удаление и дублирование;
- открытие файла и каталога;
- открытие терминала;
- проверка/установка зависимостей Python и Node.

## Output

Вывод активного запуска передаётся через SSE. Для нескольких параллельных экземпляров доступны отдельные `runId`.

## Payload

Payload состоит из schema и values. Его можно редактировать, импортировать, экспортировать и передавать executor при запуске.

## API

Основные маршруты:

- `GET /scheduler/list`
- `POST /scheduler/save`
- `POST /scheduler/delete`
- `POST /scheduler/run`
- `POST /scheduler/stop`
- `GET /scheduler/instances`
- `POST /scheduler/kill-instance`
- `GET|POST /scheduler/payload`
- `GET /scheduler/output/stream`
- `GET /scheduler/queue`
- `POST /scheduler/clear-queue`
