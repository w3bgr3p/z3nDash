# HTTP

Страница HTTP показывает запросы, принятые `/http-log` или `/traffic`.

## Список

Доступны фильтры по:

- методу;
- статусу;
- URL и тексту;
- задаче.

Детальная панель показывает только реально присутствующие данные запроса и ответа: URL, method, headers, body, status и timing.

## Replay

Replay повторяет выбранный запрос через отдельный replay listener. Перед отправкой запрос можно проверить и изменить в модальном окне.

## Код

Для выбранной записи интерфейс может сформировать пример HTTP-вызова или API-заготовку.

## API

- `POST /http-log`
- `GET /http-logs`
- `GET /http-stats`
- `GET /http-logs/stream`
- `POST /clear-http`
- `POST /clear-http-logs-by-task`

Для маршрутов HTTP также доступны варианты `/traffic-logs`, `/traffic-stats` и `/traffic-logs/stream`.
