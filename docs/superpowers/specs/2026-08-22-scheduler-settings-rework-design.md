# Переработка страницы Scheduler: Settings и расписание

Дата: 2026-08-22
Статус: утверждено, готово к планированию

## Задача

Таб Settings показывает поля независимо от экзекутора, из-за чего половина
формы для конкретной задачи не имеет смысла. Расписание задаётся тремя
конкурирующими полями и не отключается. Настройка терминала избыточна.

Цели:

1. Поля Settings зависят от выбранного экзекутора.
2. Расписание — опциональное и переключаемое: Disabled / ZP style / Cron.
3. ZP style воспроизводит планировщик ZennoPoster 7 (без «По сигналу»).
4. Настройка терминала убирается: Windows — PowerShell, Linux — системный.

## Что сломано сегодня (обнаружено при анализе)

1. `venv_path`, `terminal_override`, `terminal_init_cmd` отсутствуют в
   `DbSchema.Schedules` и в белом списке `SchedulerHandler.Columns`, поэтому
   `Save` их молча отбрасывает. Ни venv, ни per-task терминал никогда не
   сохранялись.
2. `venv_path` не используется при запуске: `BuildCommand` для python всегда
   вызывает голый `python`.
3. `_isJs` покрывает `node` и `ts-node`, но не `npm`. Экзекутор `npm` не
   получает кнопок `package.json` / `npm install` и лейбла «▶ npm run».
4. `cron`, `interval_minutes` и `fixed_time` складываются по ИЛИ в
   `ShouldFire`, что допускает взаимно противоречащие настройки.
5. `Enabled` для задачи без расписания означает лишь «спрятать Run».

## 1. Структура табов

Было: `Execution` · `Settings` · `Logs`
Стало: `Execution` · `Settings` · `Schedule` · `Logs`

Settings отвечает за «чем и как запускать», Schedule — за «когда».
`Enabled` переезжает на Schedule и становится мастер-тумблером ZP.
При `schedule_mode = off` кнопка ⏸ в тулбаре скрывается.

## 2. Settings: контекстные поля

| Поле | Показывается |
|---|---|
| Name, Executor | всегда |
| Script / Task | всегда, лейбл и пикер зависят от экзекутора |
| Arguments | всегда, кроме `npm`, `internal`, `csx-internal` |
| Use venv | только `python` |
| On overlap / Max threads | всегда |
| Enabled | убрано, переезжает на таб Schedule |
| Terminal (per-task override) | удаляется целиком |

Поле «Script / Task» по экзекутору:

| Executor | Лейбл | Пикер |
|---|---|---|
| python | Script (.py) | файл |
| node / ts-node | Script (.js / .ts) | файл |
| bash / ps1 / bat | Script (.sh / .ps1 / .bat) | файл |
| csx / csx-internal / csx-zp7 | Script (.csx) | файл |
| xml | Template (.xml) | файл |
| exe | Executable (.exe) | файл |
| npm | Project folder | только папка |
| cmd | Command | без пикера |
| internal | Task | выпадашка зарегистрированных задач |

Почему Arguments прячется у троих:

- `npm` — туда пишется служебное `__npm_run__<script>` из выпадашки в тулбаре;
  ручной ввод его затирает.
- `internal` и `csx-internal` — `args` это base64 JSON-payload, любой текст
  руками роняет запуск. Payload редактируется через модалку Values.

Для `internal` добавляется эндпоинт `GET /scheduler/internal-tasks`,
возвращающий ключи `SchedulerService._internalTasks`.

Попутно `npm` добавляется в группу `_isJs` (и в серверный `IsJs`), чтобы
получить кнопки `package.json` / `npm install` и корректный лейбл Run.

## 3. venv

Интерпретатор остаётся системным: `python -m venv` не кладёт интерпретатор
внутрь, в `pyvenv.cfg` стоит `home = <системная установка>`, стандартная
библиотека берётся оттуда. Изоляция идёт только по `site-packages`.

- Чекбокс **Use venv** в Settings, виден только для `python`.
- Новая колонка `use_venv TEXT DEFAULT 'false'` в `DbSchema.Schedules`
  и в `SchedulerHandler.Columns`. Без второго списка значение не сохранится.
- Каталог: `venv` рядом со скриптом. Если рядом уже лежит `.venv`, `venv`
  или `env` — берётся существующий.
- Каталога нет → создаётся `python -m venv venv` системным python,
  вывод команды попадает в output задачи.
- Запуск: `BuildCommand` для python берёт `<venv>/Scripts/python.exe`
  (на Linux `<venv>/bin/python`).
- Кнопка `📦 pip install` начинает работать через
  `<venv>/Scripts/python.exe -m pip install -r requirements.txt`.
- Поле пути и кнопка Detect удаляются.
- `GET /scheduler/detect-venv` заменяется на `POST /scheduler/ensure-venv`,
  создающий каталог по требованию.

## 4. Таб Schedule

Верх таба — переключатель режима: **Disabled** · **ZP style** · **Cron**.

### Disabled

Тело пустое. Задача запускается только кнопкой Run. `Enabled` и ⏸ скрыты.

### Cron

Одно поле cron, подсказка `min h dom mon dow`, кнопка Preview.
Старый Period-билдер, `interval_minutes` и `fixed_time` из UI уходят —
их роль полностью покрывает ZP-режим.

### ZP style

Шесть блоков ровно как в планировщике ZennoPoster 7:

| Блок | Содержимое |
|---|---|
| Как выполнять | Один раз · Каждый день · Каждую неделю (чекбоксы дней) · Каждый месяц (`1-5, 10, 20`) |
| Начать | Сразу · По дате (дата и время) |
| Сколько делать | Число попыток или диапазон (случайно) + «Сбрасывать успехи» |
| Когда повторять | Список интервалов времени, неограниченно. Пусто = круглосуточно. Скрыт при «Один раз» |
| Как повторять | Подряд · Подряд с паузой (мин или диапазон) · Регулярно · Распределить по интервалу. Скрыт при «Один раз» |
| Завершить | По дате · После N повторений (число или диапазон) · Без конца. Скрыт при «Один раз» |

Как в ZP: пока в блоках есть ошибки, поля подсвечиваются красной рамкой,
а мастер-тумблер неактивен.

Кнопка **Preview** показывает 20 ближайших расчётных запусков — это
упрощённый аналог отладчика расписания ZennoPoster.

### Хранение

Новые колонки в `DbSchema.Schedules` и в `SchedulerHandler.Columns`:

| Колонка | Назначение |
|---|---|
| `schedule_mode` | `off` \| `zp` \| `cron`, по умолчанию `off` |
| `schedule_json` | настройки ZP-режима одним JSON-объектом |
| `sched_runs` | сколько повторений сделано текущим расписанием |
| `sched_started_at` | когда расписание было включено |

JSON в колонке — уже принятый в этой таблице приём (`payload_schema`,
`payload_values`). Модель со списками интервалов и диапазонами в плоские
колонки не ложится. Разделитель строк в хранилище — `¦`, в JSON он не
встречается.

`sched_runs` нужен отдельно от `runs_total`: последний считает и ручные
запуски, для условия «Завершить после N повторений» он не годится.

Форма `schedule_json`:

```json
{
  "how":       "once|daily|weekly|monthly",
  "weekdays":  [1, 2, 3],
  "monthdays": "1-5,10,20",
  "start":     { "mode": "now|date", "at": "2026-08-22T12:30" },
  "attempts":  { "min": 1, "max": 1, "resetSuccess": false },
  "windows":   [ { "from": "08:00", "to": "12:00" } ],
  "repeat":    { "mode": "back_to_back|pause|regular|spread", "min": 10, "max": 10 },
  "end":       { "mode": "date|count|never", "at": "", "min": 50, "max": 50 }
}
```

### Движок

`SchedulerService.ShouldFire` заменяется на отдельный класс `ZpSchedule`
в своём файле. Чистая функция: на вход состояние задачи и `now`, на выход
решение «запускать» и время следующего запуска. Вся арифметика окон, пауз
и лимитов живёт там и тестируется без базы и процессов.

Режим `cron` продолжает считаться через `CronExpression` как сейчас.
Колонки `interval_minutes` и `fixed_time` остаются в таблице, но движком
больше не читаются. Миграции нет: данные тестовые.

## 5. Терминал

Настройка терминала — и глобальная, и per-task — убирается.

Удаляется из `wwwroot/js/scheduler.js`: секция Terminal в `renderSettings`,
функции `_globalTerminalLabel`, `onTerminalOverrideChange`,
`_checkTerminalOverrideNote`, `loadTerminalConfig`, `_updateTerminalUI`,
`saveTerminalConfig`, поля `terminal_override` и `terminal_init_cmd`
из payload `saveSchedule`.

Удаляется с сервера: эндпоинт `GET /scheduler/terminal-config`, метод
`TerminalConfig`, `FindGitBash` в `SchedulerHandler`, свойства
`Config.Terminal` и `Config.TerminalPath` (используются только здесь).

`LaunchTerminal(cwd)` сжимается до:

- **Windows** — `powershell.exe -NoExit -Command "Set-Location '<cwd>'"`
- **Linux** — `$TERMINAL`, затем `x-terminal-emulator`, `gnome-terminal`,
  `konsole`, `xterm`; берётся первый найденный

`SchedulerService.ResolveGitBash` остаётся: это запуск `.sh`-скриптов
экзекутором `bash`, к настройке терминала отношения не имеет.

## Вне объёма

- Режим «По сигналу» (файловый триггер) планировщика ZennoPoster.
- Миграция задач со старыми `cron` / `interval_minutes` / `fixed_time`.
- Полноценный отладчик расписания ZennoPoster: делается только Preview
  на 20 ближайших запусков.
