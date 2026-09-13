# docsVault

docsVault renders the Markdown files of the current `docs-vault`.

**Choose folder** opens a system dialog for picking a folder.
By default the `docs-vault` bundled with the application is selected.

## Generation

`POST /docsVault/generate?vaultPath=...` reads the Markdown files and builds links from wiki references.

A relative path is resolved against the application folder.

## Viewing

`GET /docsVault` returns the last graph and the selected folder. Before the first generation an empty template opens.

## Export

`GET /docsVault/export` downloads a standalone HTML file of the last graph built.

## API

- `GET /docsVault`
- `GET /docsVault/pick`
- `POST /docsVault/generate`
- `GET /docsVault/export`
