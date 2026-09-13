# Treasury

Treasury collects and displays Web3 balances for the addresses in the `addresses` table.

## Refresh

`POST /treasury/update` starts a background refresh. Parameters:

- `maxId`;
- `minValue`;
- `concurrency` between 1 and 20.

Current progress is available from `GET /treasury/status`.

## Presentation

The page shows:

- total value;
- balances by account and by chain;
- top tokens;
- distribution across chains;
- a portfolio summary.

Which chains appear is decided by the columns of the actual `_treasury` table.

## AI

Portfolio analysis goes through OmniRoute. The result is kept in the Treasury AI cache.

## API

- `POST /treasury/update`
- `GET /treasury/status`
- `GET /treasury/data`
- `POST /treasury/ai-analyze`
- `GET /treasury/ai-cache`
- `DELETE /treasury/ai-cache`
