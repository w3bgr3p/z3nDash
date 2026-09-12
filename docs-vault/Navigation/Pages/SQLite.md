# SQLite

SQLite — отдельный просмотрщик выбранного SQLite-файла.

## Возможности

- открыть файл базы;
- получить список таблиц;
- выполнить запрос;
- изменить значение;
- удалить строку.

Этот экран не переключает основную БД z3nDash и работает с файлом, переданным в запросе viewer API.

## API

- `POST /sqlite-viewer/tables`
- `POST /sqlite-viewer/query`
- `POST /sqlite-viewer/update`
- `POST /sqlite-viewer/delete`
