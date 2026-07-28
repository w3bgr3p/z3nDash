# System Snapshot

System Snapshot собирает диагностический снимок текущей Windows-системы.

## Capture

`Capture Now` запускает сбор на машине, где работает DevDeck. Снимок включает секции, которые фактически смог собрать backend: процессы, сеть и системные данные.

## Файлы

Страница может:

- загрузить снимок из файла;
- сохранить снимок в БД;
- открыть сохранённый снимок;
- удалить сохранённый снимок.

Снимки хранятся в таблице `system_snapshots`.

## AI Audit

Опциональный аудит отправляет снимок в OmniRoute. Кэш хранится в `system_snapshot_ai_cache`.

## API

- `POST /system-snapshot/capture`
- `POST /system-snapshot/read-file`
- `POST /system-snapshot/save`
- `GET /system-snapshot/list`
- `GET /system-snapshot/get`
- `DELETE /system-snapshot/delete`
- `POST /system-snapshot/ai-audit`
- `GET /system-snapshot/ai-cache`
- `DELETE /system-snapshot/ai-cache`
