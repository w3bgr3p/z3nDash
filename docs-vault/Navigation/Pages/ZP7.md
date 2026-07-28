# ZP7

ZP7 показывает состояние задач, полученное напрямую от зарегистрированных ZP node-сервисов.

## Источник данных

DevDeck хранит только адреса узлов в таблице `zp_nodes`. Состояние задач запрашивается у каждого узла:

```text
GET http://HOST:PORT/state
```

## Список задач

Доступны фильтры:

- имя;
- состояние;
- режим работы;
- теги.

Выбранные фильтры сохраняются в состоянии UI.

## Управление

Команды отправляются непосредственно выбранному node:

```text
POST http://HOST:PORT/command
```

Поддерживаемые действия интерфейса:

- start;
- stop;
- interrupt;
- apply/update settings;
- add/set tries;
- set threads;
- clear done;
- clear fails;
- execution settings;
- scheduler settings.

## Панели

- Task Detail — параметры и действия выбранной задачи;
- Logs — отфильтрованные логи;
- HTTP — связанный HTTP-трафик;
- Projects heatmap — данные проектов из report API.

## API

- `GET /zp/nodes`
- `GET /zp/state`
- `GET /zp/state/all`
- `POST /zp/commands`
