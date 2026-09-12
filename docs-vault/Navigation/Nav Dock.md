# Nav Dock

Dock подключается страницами через `wwwroot/js/nav.js`. Состав пунктов и хоткеи находятся в `wwwroot/js/navpath.js`.

## Пункты

| ID | Страница | Хоткей |
|---|---|---|
| `tasker` | [[Tasker]] | `Alt+1` |
| `zp7` | [[ZP7]] | `Alt+2` |
| `zb` | [[ZB]] | `Alt+3` |
| `har` | [[HAR]] | — |
| `json` | [[JSON]] | `Alt+6` |
| `text` | [[Text Tools]] | `Alt+7` |
| `treasury` | [[Treasury]] | `Alt+8` |
| `config` | [[Config]] | `Alt+0` |
| `clips` | [[Clips]] | `Alt+C` |
| `system` | [[System Snapshot]] | `Alt+S` |
| `dllGraph` | [[dllGraph]] | `Alt+G` |
| `docsVault` | [[docsVault]] | `Alt+H` |
| `sql` | [[SQLite]] | — |

## Хоткеи без пункта в доке

Открывают модальные окна на странице [[ZP7]]:

| Хоткей | Окно | Ссылка |
|---|---|---|
| `Alt+4` | [[Logs\|allLogs]] | `/?page=zp7#allLogs` |
| `Alt+5` | [[Traffic\|traffic]] | `/?page=zp7#traffic` |

## Дополнительно

Dock содержит OTP-генератор. Секрет остаётся в текущей странице и используется только для вычисления одноразового кода.
