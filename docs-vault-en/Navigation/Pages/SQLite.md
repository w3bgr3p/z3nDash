# SQLite

SQLite is a standalone viewer for a chosen SQLite file.

## Capabilities

- open a database file;
- list its tables;
- run a query;
- change a value;
- delete a row.

This screen never switches the main z3nDash database. It works on the file passed in the viewer API request.

## API

- `POST /sqlite-viewer/tables`
- `POST /sqlite-viewer/query`
- `POST /sqlite-viewer/update`
- `POST /sqlite-viewer/delete`
