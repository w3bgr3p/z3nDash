# Nav Dock

Dock подключается страницами через `wwwroot/js/nav.js`. Состав пунктов и хоткеи находятся в `wwwroot/js/navpath.js`.

Какие из пунктов показывать, выбирается в [[Config]] → Interface → Dock pages.
Там же выбираются тема и край экрана; кнопки темы в доке нет. `config` скрыть нельзя.

## Пункты

| ID | Страница | Хоткей |
|---|---|---|
| `tasker` | [[Tasker]] | `Alt+1` |
| `zp7` | [[ZP7]] | `Alt+2` |
| `browser` | [[Browser]] | `Alt+3` |
| `har` | [[HAR]] | — |
| `zpXml` | [[zpXml]] | — |
| `xml` | [[XML]] | `Alt+9` |
| `json` | [[JSON]] | `Alt+6` |
| `text` | [[Text Tools]] | `Alt+7` |
| `config` | [[Config]] | `Alt+0` |
| `clips` | [[Clips]] | `Alt+C` |
| `system` | [[System Snapshot]] | — |
| `dllGraph` | [[dllGraph]] | `Alt+G` |
| `docsVault` | [[docsVault]] | `Alt+H` |
| `sql` | [[SQLite]] | — |

## Хоткеи без пункта в доке

Открывают модальные окна на странице [[ZP7]]:

| Хоткей | Окно | Ссылка |
|---|---|---|
| `Alt+4` | [[Logs\|allLogs]] | `/?page=zp7#allLogs` |
| `Alt+5` | [[Traffic\|traffic]] | `/?page=zp7#traffic` |

## Хоткеи самого дока

Привязаны в `nav.js`, а не в списке элементов, и никуда не ведут:

| Хоткей | Что делает |
|---|---|
| `Alt+X` | генератор OTP |
| `Alt+T` | переключает темы Dark, Light, Hyper и Graphite |
| `Alt+P` | переносит док к следующему краю |

## Дополнительно

Dock содержит OTP-генератор. Секрет остаётся в текущей странице и используется только для вычисления одноразового кода.
