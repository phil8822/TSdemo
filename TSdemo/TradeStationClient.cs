using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace TSdemo
{
    public sealed class TradeStationClient
    {
        private readonly HttpClient _http;

        public TradeStationClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<(HttpStatusCode status, string content, Uri triedUri)> TryGetQuoteContentAsync(Uri baseUri, string symbol, string accessToken)
        {
            var candidates = new[]
            {
                $"v3/marketdata/quotes/{Uri.EscapeDataString(symbol)}",
                $"v3/marketdata/quotes/{Uri.EscapeDataString(symbol)}?symbols={Uri.EscapeDataString(symbol)}",
                $"marketdata/quotes/{Uri.EscapeDataString(symbol)}",
                $"marketdata/quotes?symbols={Uri.EscapeDataString(symbol)}",
                $"marketdata/quotes?symbol={Uri.EscapeDataString(symbol)}",
                $"v2/marketdata/quotes/{Uri.EscapeDataString(symbol)}",
                $"quotes/{Uri.EscapeDataString(symbol)}",
                $"quotes?symbols={Uri.EscapeDataString(symbol)}"
            };

            string lastContent = "";
            Uri lastUri = new Uri(baseUri, candidates[0]);

            foreach (var rel in candidates)
            {
                var uri = new Uri(baseUri, rel);
                lastUri = uri;

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                HttpResponseMessage res;
                try
                {
                    res = await _http.SendAsync(req).ConfigureAwait(false);
                }
                catch (HttpRequestException hre)
                {
                    lastContent = hre.Message;
                    continue;
                }

                lastContent = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (res.IsSuccessStatusCode) return (res.StatusCode, lastContent, uri);
                if (res.StatusCode == HttpStatusCode.Unauthorized) return (res.StatusCode, lastContent, uri);
                if (res.StatusCode == HttpStatusCode.NotFound) continue;
                return (res.StatusCode, lastContent, uri);
            }

            return (HttpStatusCode.NotFound, lastContent, lastUri);
        }
    }
}