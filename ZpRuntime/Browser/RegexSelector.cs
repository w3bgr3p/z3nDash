// ══════════════════════════════════════════════════════════════════════════════
// RegexSelector.cs — движок селекторов для ZP-шного SearchKind="regexp".
//
// В ZennoPoster поиск по атрибуту умеет регулярку, и шаблоны этим пользуются
// вовсю: в simroute_test так задана половина поисков, в numlex — две трети, и
// почти всегда по class.
//
// XPath 1.0, на котором строился запрос раньше, регулярок не знает, поэтому из
// шаблона брался самый длинный литеральный кусок и подставлялся в contains().
// Для "^btn-(a|b)$" это значит «любой элемент, где в class есть btn-» — то есть
// найдётся и btn-c, и xbtn-a. Само по себе это не падает: находится не тот
// элемент, а с ним сдвигается и Number, по которому ветка выбирает нужный.
// Клик уходит не туда молча.
//
// Здесь регулярка выполняется там, где ей и место — в самой странице, движком
// JS. Playwright позволяет зарегистрировать свой селектор; он живёт наравне с
// css и xpath, поэтому Locator остаётся ленивым и переживает перерисовку DOM.
// ══════════════════════════════════════════════════════════════════════════════

using Microsoft.Playwright;

namespace DevDeck.Browser;

internal static class RegexSelector
{
    /// <summary>Имя движка. Селектор пишется как "zpre=теги|атрибут|neg|шаблон".</summary>
    public const string Name = "zpre";

    // Регистрация живёт в конкретном экземпляре Playwright, а не в процессе.
    // Раньше здесь стоял один флаг на всё приложение: первый запуск
    // регистрировал движок, а второй параллельный поднимал свой Playwright,
    // видел флаг и регистрацию пропускал. Селектор zpre в нём не существовал, и
    // каждый поиск по regexp падал — то есть при многопоточности ломался ровно
    // тот вид поиска, которым в шаблонах задана половина элементов.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlaywright, object> _registered = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Собрать селектор. Разделитель — вертикальная черта; в шаблоне регулярки
    /// она встречается (альтернатива), поэтому на части режем только первые три
    /// вхождения, а остаток целиком считаем шаблоном.
    /// </summary>
    /// <summary>
    /// Селектор для движка. Режим кодируется одним полем, потому что движок
    /// нужен не только регуляркам: по «живым» значениям (innerhtml, outerhtml,
    /// value) точное совпадение тоже нельзя выразить в CSS.
    ///   rx — регулярка, eq — точное совпадение; префикс n — отрицание.
    /// </summary>
    public static string Build(string tags, string attr, string pattern, bool negate, bool regexp = true)
        => $"{Name}={tags}|{attr}|{(negate ? "n" : "")}{(regexp ? "rx" : "eq")}|{pattern}";

    /// <summary>
    /// Зарегистрировать движок в этом экземпляре Playwright. Повторная
    /// регистрация того же имени в том же экземпляре бросает, поэтому сделанное
    /// запоминается — но по экземпляру, а не по процессу.
    /// </summary>
    public static void Register(IPlaywright pw)
    {
        lock (_lock)
        {
            if (_registered.TryGetValue(pw, out _)) return;
            pw.Selectors.RegisterAsync(Name, new SelectorsRegisterOptions { Script = Script })
              .GetAwaiter().GetResult();
            _registered.Add(pw, new object());
        }
    }

    private const string Script = """
        {
            _parse(selector) {
                const i = selector.indexOf('|');
                const j = selector.indexOf('|', i + 1);
                const k = selector.indexOf('|', j + 1);
                const mode = selector.slice(j + 1, k);
                return {
                    tags:    selector.slice(0, i),
                    attr:    selector.slice(i + 1, j),
                    negate:  mode.charAt(0) === 'n',
                    regexp:  mode.indexOf('rx') >= 0,
                    pattern: selector.slice(k + 1),
                };
            },

            _value(el, attr) {
                if (attr === 'innertext' || attr === 'text') return el.innerText;
                if (attr === 'innerhtml') return el.innerHTML;
                if (attr === 'outerhtml') return el.outerHTML;
                // value у поля — живое свойство: после ввода оно расходится с
                // атрибутом, а искать шаблон будет по тому, что в поле сейчас.
                if (attr === 'value' && 'value' in el) return el.value;
                // ZP-шный «полный тег»: input:checkbox, input:text, div.
                if (attr === 'fulltag' || attr === 'fulltagname') {
                    const t = el.tagName.toLowerCase();
                    return (t === 'input' && el.type) ? t + ':' + el.type.toLowerCase() : t;
                }
                return el.getAttribute(attr);
            },

            queryAll(root, selector) {
                const q = this._parse(selector);
                let re = null;
                if (q.regexp) {
                    try { re = new RegExp(q.pattern, 'i'); }
                    catch (e) { return []; }
                }

                // ZP пишет тип поля через двоеточие: "input:text". В CSS это
                // не тег — querySelectorAll на нём бросает SyntaxError, и весь
                // поиск молча возвращал пусто.
                const tags = (q.tags || '*').split(';')
                    .map(t => t.trim()).filter(t => t.length)
                    .map(t => {
                        const i = t.indexOf(':');
                        if (i <= 0) return t;
                        const name = t.slice(0, i), type = t.slice(i + 1);
                        // text — ещё и поле без атрибута type: оно текстовое по
                        // умолчанию, и ZP его находит.
                        return type.toLowerCase() === 'text'
                            ? name + ":is([type='text' i], :not([type]))"
                            : name + "[type='" + type + "' i]";
                    });
                const css = tags.length ? tags.join(', ') : '*';

                const out = [];
                for (const el of root.querySelectorAll(css)) {
                    const v = this._value(el, q.attr);
                    const hit = v != null && (re ? re.test(v) : v === q.pattern);
                    if (hit !== q.negate) out.push(el);
                }
                return out;
            },

            query(root, selector) {
                return this.queryAll(root, selector)[0] || null;
            },
        }
        """;
}
