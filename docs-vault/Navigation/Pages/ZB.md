# ZB

ZB отображает данные внешнего ZennoBoxer API.

## Подключение

Адрес берётся из `ApiConfig.ZbHost`. Если он пуст, используется:

```text
http://localhost:8160
```

Токен передаётся upstream-сервису заголовком `Api-Token`.

## Данные

Интерфейс запрашивает через proxy данные ZB, включая профили, proxy, instances и threads. Набор полей зависит от ответа внешнего API.

## Локальные операции

- получение uptime процессов по PID;
- завершение процесса целиком по PID.

## API

- `/zb/api/*` — proxy в настроенный ZB host;
- `GET /zb/process/uptime?pids=...`;
- `POST /zb/process/kill`.
