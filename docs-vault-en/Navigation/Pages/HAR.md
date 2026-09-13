# HAR

The HAR page views HTTP archives in the HAR format and replays requests from them.

## Data source

The file is opened locally: the page reads `log.entries` and parses it in the browser. The page itself has no server routes.

Records can also be added by importing cURL, with no file at all.

## Storage

The loaded archive is kept in IndexedDB and survives a page reload. `Clear HAR` wipes both the list and the stored copy.

HAR records never reach the z3nDash database.

## List

Filters are available by:

- method;
- status;
- URL;
- project.

A limit on how many records are shown is set separately. `Refresh` re-applies the filters to data already loaded and never touches the network. `Reset` clears the filters.

## Replay

A selected request is sent through the same replay listener the [[Traffic]] window uses — `POST /http-replay`. Method and URL can be changed before sending, and the response can be copied.

## Code

For a selected record the page generates stubs:

- API Skeleton;
- API Example;
- HttpClient;
- ZP7;
- Hybrid;
- Python;
- TypeScript;
- cURL.
