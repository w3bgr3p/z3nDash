using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DevDeck.Browser
{
    /// <summary>
    /// Захват трафика вкладки для ZP-шного Tab.GetTraffic.
    ///
    /// Буфер привязан к IPage, а не к обёртке: PlaywrightTab создаётся заново на
    /// каждое обращение к ActiveTab, поэтому состояние в нём не выжило бы.
    /// Подписка ставится один раз, при первом запросе трафика — держать её всегда
    /// значит платить за то, чем в большинстве задач не пользуются.
    /// </summary>
    internal static class TrafficCapture
    {
        private const int MaxItems = 500;

        private static readonly ConditionalWeakTable<IPage, ConcurrentQueue<TrafficEntry>> _buffers = new();

        internal static IList<ITrafficItem> Get(IPage page, IEnumerable<string> urlFilters)
        {
            var buffer = Subscribe(page);
            IEnumerable<TrafficEntry> items = buffer.ToArray();

            var filters = urlFilters?.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray()
                          ?? Array.Empty<string>();
            if (filters.Length > 0)
                items = items.Where(i => filters.Any(f => Matches(i.Url, f)));

            return items.Cast<ITrafficItem>().ToList();
        }

        /// <summary>ZP трактует фильтр как regex, но кривой шаблон не должен ронять сбор.</summary>
        private static bool Matches(string url, string filter)
        {
            try { return Regex.IsMatch(url, filter, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { return url.Contains(filter, StringComparison.OrdinalIgnoreCase); }
        }

        private static ConcurrentQueue<TrafficEntry> Subscribe(IPage page)
        {
            if (_buffers.TryGetValue(page, out var existing)) return existing;

            var buffer = new ConcurrentQueue<TrafficEntry>();
            _buffers.Add(page, buffer);

            page.Response += (sender, response) =>
            {
                buffer.Enqueue(new TrafficEntry(response));
                while (buffer.Count > MaxItems) buffer.TryDequeue(out var dropped);
            };

            return buffer;
        }

        private sealed class TrafficEntry : ITrafficItem
        {
            private readonly IResponse _resp;
            private byte[] _body;

            internal TrafficEntry(IResponse resp)
            {
                _resp = resp;
                Url             = resp.Url;
                Method          = resp.Request.Method;
                ResultCode      = (uint)resp.Status;
                RequestHeaders  = Flatten(resp.Request.Headers);
                ResponseHeaders = Flatten(resp.Headers);
                RequestBody     = resp.Request.PostData ?? "";
                RequestQuery    = QueryOf(resp.Url);
            }

            public string Url             { get; }
            public string Method          { get; }
            public uint   ResultCode      { get; }
            public string RequestHeaders  { get; }
            public string RequestQuery    { get; }
            public string RequestBody     { get; }
            public string ResponseHeaders { get; }
            public bool   HasResponse     => true;

            public string ResponseContentType
                => _resp.Headers.TryGetValue("content-type", out var ct) ? ct : "";

            /// <summary>
            /// Тело читается по требованию и один раз: Playwright отдаёт его только
            /// пока запрос не вытеснен, поэтому неудачу трактуем как пустое тело.
            /// </summary>
            public byte[] ResponseBody
            {
                get
                {
                    if (_body != null) return _body;
                    try   { _body = _resp.BodyAsync().GetAwaiter().GetResult(); }
                    catch { _body = Array.Empty<byte>(); }
                    return _body;
                }
            }

            private static string Flatten(IDictionary<string, string> headers)
                => headers == null
                    ? ""
                    : string.Join("\r\n", headers.Select(h => $"{h.Key}: {h.Value}"));

            private static string QueryOf(string url)
            {
                try { return new Uri(url).Query.TrimStart('?'); } catch { return ""; }
            }
        }
    }
}
