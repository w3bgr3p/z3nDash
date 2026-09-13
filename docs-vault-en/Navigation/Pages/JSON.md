# JSON

JSON is a local JSON viewer and editor.

## Working with the document

- paste JSON;
- format it;
- walk the value tree;
- search;
- collapse branches;
- copy a value or its path;
- filter fields.

## Security badges

The interface marks values that look like secrets or sensitive data. This is a heuristic highlight, not a security check.

## Replay

The document can be used as the body when preparing an HTTP replay.

## AI analysis

Optional analysis is sent to OmniRoute. The cache lives in the `ai_json_cache` table.

API:

- `POST /json-analyzer/ai-analyze`
- `GET /json-analyzer/ai-cache`
- `DELETE /json-analyzer/ai-cache`
- `GET /config/ai-models`
