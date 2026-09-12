# docsVault

docsVault визуализирует Markdown-файлы текущего `docs-vault`.

Кнопка **Choose folder** открывает системный диалог выбора папки.
По умолчанию выбран внутренний `docs-vault` из каталога приложения.

## Генерация

`POST /docsVault/generate?vaultPath=...` читает Markdown-файлы и строит связи по wiki-ссылкам.

Относительный путь вычисляется от каталога приложения.

## Просмотр

`GET /docsVault` возвращает последний граф и выбранную папку. До первой генерации открывается пустой шаблон.

## Export

`GET /docsVault/export` скачивает автономный HTML последнего построенного графа.

## API

- `GET /docsVault`
- `GET /docsVault/pick`
- `POST /docsVault/generate`
- `GET /docsVault/export`
