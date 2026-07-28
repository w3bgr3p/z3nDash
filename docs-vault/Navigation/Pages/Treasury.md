# Treasury

Treasury собирает и показывает Web3-балансы для адресов из таблицы `addresses`.

## Обновление

`POST /treasury/update` запускает фоновое обновление. Параметры:

- `maxId`;
- `minValue`;
- `concurrency` от 1 до 20.

Текущее состояние доступно через `GET /treasury/status`.

## Представление

Страница показывает:

- общую стоимость;
- balances по аккаунтам и chains;
- top tokens;
- распределение по chains;
- сводку портфеля.

Набор chains определяется колонками фактической таблицы `_treasury`.

## AI

Анализ портфеля использует OmniRoute. Результат хранится в AI-кэше Treasury.

## API

- `POST /treasury/update`
- `GET /treasury/status`
- `GET /treasury/data`
- `POST /treasury/ai-analyze`
- `GET /treasury/ai-cache`
- `DELETE /treasury/ai-cache`
