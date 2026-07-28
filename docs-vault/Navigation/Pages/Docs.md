# Docs

Docs визуализирует Markdown-файлы текущего `docs-vault`.

## Генерация

`POST /docs-graph/generate?vaultPath=...` читает Markdown-файлы и строит связи по wiki-ссылкам.

Относительный путь вычисляется от каталога приложения.

## Просмотр

`GET /docs` и `GET /docs-graph` возвращают последний граф. До первой генерации открывается пустой шаблон.

## Export

`GET /docs-graph/export` скачивает автономный HTML последнего построенного графа.

## API

- `GET /docs`
- `GET /docs-graph`
- `POST /docs-graph/generate`
- `GET /docs-graph/export`
