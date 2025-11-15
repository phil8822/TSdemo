using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;

namespace TSdemo
{
    public partial class Form1 : Form
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private TextBox? txtBoxDiag; // keep this field

        public Form1()
        {
            InitializeComponent();

            Shown += Form1_Shown;

            GoButton.Click += GoButton_Click;
            FormClosed += Form1_FormClosed;
        }

        // Thread-safe append helper (add inside Form1)
        private void AppendDiag(string text)
        {
            if (txtBoxDiag == null || txtBoxDiag.IsDisposed) return;
            if (txtBoxDiag.InvokeRequired)
                txtBoxDiag.BeginInvoke(new Action(() => txtBoxDiag.AppendText(text)));
            else
                txtBoxDiag.AppendText(text);
        }

        private void Form1_Shown(object? sender, EventArgs e)
        {
            txtBoxDiag = FindControlRecursive(this, "txtBoxDiag") as TextBox;

            if (txtBoxDiag != null)
            {
                AppendDiag("txtBoxDiag found and initialized\r\n");
                AppendDiag("DiagnosticWriter removed; using AppendDiag helper\r\n");
                txtBoxDiag.Text = "Ready.\r\n";
            }
        }

        private void Form1_FormClosed(object? sender, FormClosedEventArgs e) => _http.Dispose();

        private async void GoButton_Click(object? sender, EventArgs e)
        {
            GoButton.Enabled = false;
            textBox1.Text = "Fetching TSLA price...";
            if (textBox2 != null) textBox2.Text = "";

            try
            {
                var connStr = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING1");
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    MessageBox.Show("Environment variable DB_CONNECTION_STRING1 is not set.", "Configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                else
                {
                    MessageBox.Show("DB_CONNECTION_STRING1 is   " + connStr, "Configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // DEV WORKAROUND for untrusted SQL cert (only for dev). Remove for production.
                if (!connStr.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
                {
                    if (!connStr.TrimEnd().EndsWith(";")) connStr += ";";
                    connStr += "TrustServerCertificate=True;";
                }

                // Query the TokenData row with Id = 4 (live system Authority)
                const string sql = @"
SELECT [Id], [Authority], [AuthToken], [RefreshToken], [TokenExpiration]
FROM [Trading].[dbo].[TokenData]
WHERE [Id] = @id";

                string? authority = null;
                string? accessToken = null;
                DateTimeOffset? tokenExpiration = null;

                await using (var cn = new SqlConnection(connStr))
                {
                    await cn.OpenAsync();

                    await using var cmd = new SqlCommand(sql, cn);
                    // <-- Id = 4 (live)
                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 4 });

                    await using var rdr = await cmd.ExecuteReaderAsync();
                    if (!await rdr.ReadAsync())
                    {
                        MessageBox.Show("No token row found (Id = 4).", "Token", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    authority = rdr.IsDBNull(rdr.GetOrdinal("Authority")) ? null : rdr.GetString(rdr.GetOrdinal("Authority"));
                    accessToken = rdr.IsDBNull(rdr.GetOrdinal("AuthToken")) ? null : rdr.GetString(rdr.GetOrdinal("AuthToken"));

                    var idx = rdr.GetOrdinal("TokenExpiration");
                    if (!rdr.IsDBNull(idx))
                    {
                        var raw = rdr.GetValue(idx);
                        if (raw is DateTimeOffset dto)
                        {
                            tokenExpiration = dto;
                        }
                        else if (raw is DateTime dt)
                        {
                            tokenExpiration = new DateTimeOffset(dt);
                        }
                        else if (raw is string s && DateTimeOffset.TryParse(s, out var parsed))
                        {
                            tokenExpiration = parsed;
                        }
                        else
                        {
                            try
                            {
                                tokenExpiration = Convert.ToDateTime(raw);
                            }
                            catch
                            {
                                tokenExpiration = null;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(authority))
                {
                    MessageBox.Show("Authority (API base URL) is missing in the token row.", "Configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    MessageBox.Show("AuthToken is missing in the token row.", "Token", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Diagnostic: show token expiration & length (do not display token value)
                var expText = tokenExpiration.HasValue ? tokenExpiration.Value.ToString("u") : "unknown";
                MessageBox.Show($"Token expiration: {expText}\nToken length: {accessToken.Length} chars", "Token diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Normalize authority and build baseUri
                var authorityCandidate = authority.Trim();
                if (!authorityCandidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !authorityCandidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    authorityCandidate = "https://" + authorityCandidate;
                }

                if (!Uri.TryCreate(authorityCandidate, UriKind.Absolute, out var baseUri))
                {
                    MessageBox.Show($"Authority value from DB is not a valid absolute URI:\n{authority}", "Invalid Authority", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // If stored Authority is just the host/root, try common API base-path candidates.
                var baseCandidates = new System.Collections.Generic.List<Uri> { baseUri };

                // Only add candidates if baseUri doesn't already include a path beyond "/"
                if (string.IsNullOrEmpty(baseUri.AbsolutePath) || baseUri.AbsolutePath == "/")
                {
                    // Include v3 candidates per TradeStation docs
                    baseCandidates.Add(new Uri(baseUri, "/v3/"));
                    baseCandidates.Add(new Uri(baseUri, "/v3"));
                    baseCandidates.Add(new Uri(baseUri, "/v3/marketdata/"));
                    baseCandidates.Add(new Uri(baseUri, "/v2/"));
                    baseCandidates.Add(new Uri(baseUri, "/v2"));
                    baseCandidates.Add(new Uri(baseUri, "/v2/marketdata/"));
                }

                // Try each base candidate until one returns success (or 401)
                var symbol = "TSLA";
                HttpStatusCode finalStatus = HttpStatusCode.NotFound;
                string finalContent = "";
                Uri finalUri = new Uri(baseUri, $"marketdata/quotes/{Uri.EscapeDataString(symbol)}");

                // Use the dedicated client implementation (moved to TradeStationClient.cs)
                var tsClient = new TradeStationClient(_http);

                foreach (var candidateBase in baseCandidates)
                {
                    var (status, content, triedUri) = await tsClient.TryGetQuoteContentAsync(candidateBase, symbol, accessToken);
                    finalStatus = status;
                    finalContent = content;
                    finalUri = triedUri;

                    // Stop on success or unauthorized (no refresh here)
                    if (status == HttpStatusCode.Unauthorized || ((int)status >= 200 && (int)status <= 299))
                        break;
                }

                // Put raw JSON into textBox1
                textBox1.Text = finalContent;

                if (finalStatus == HttpStatusCode.Unauthorized)
                {
                    MessageBox.Show($"Unauthorized (401) for request:\n{finalUri}\n\nToken refresh is handled externally; check DB and ensure token has marketdata access/scopes.", "Unauthorized", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (finalStatus == HttpStatusCode.NotFound)
                {
                    MessageBox.Show($"404 Not Found. Last attempted URI:\n{finalUri}\n\nResponse body shown in the window. Verify the API base URL (`Authority`) and endpoint path.", "404 Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!((int)finalStatus >= 200 && (int)finalStatus <= 299))
                {
                    MessageBox.Show($"API returned {(int)finalStatus} for request:\n{finalUri}\n\nResponse body is shown in the window.", "API Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Parse the successful response content for a best-effort last price and structured output
                var quotes = ParseTradeStationQuotes(finalContent);

                if (quotes.Count == 0)
                {
                    MessageBox.Show("Could not parse any quotes from the response.", "Parsing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (textBox2 != null) textBox2.Text = "Could not parse JSON into quotes.";
                    return;
                }

                // Build readable details for textBox2 (parsed)
                var sb = new StringBuilder();
                foreach (var quote in quotes)
                {
                    sb.AppendLine("----- Quote -----");
                    sb.AppendLine($"Symbol: {quote.Symbol}");
                    sb.AppendLine($"Open: {FormatNullableDecimal(quote.Open)}  High: {FormatNullableDecimal(quote.High)}  Low: {FormatNullableDecimal(quote.Low)}");
                    sb.AppendLine($"PreviousClose: {FormatNullableDecimal(quote.PreviousClose)}  Close: {FormatNullableDecimal(quote.Close)}");
                    sb.AppendLine($"Last: {FormatNullableDecimal(quote.Last)}  LastSize: {FormatNullableLong(quote.LastSize)}  LastVenue: {quote.LastVenue}");
                    sb.AppendLine($"Bid: {FormatNullableDecimal(quote.Bid)} (Size: {FormatNullableLong(quote.BidSize)})  Ask: {FormatNullableDecimal(quote.Ask)} (Size: {FormatNullableLong(quote.AskSize)})");
                    sb.AppendLine($"NetChange: {FormatNullableDecimal(quote.NetChange)}  NetChangePct: {FormatNullableDecimal(quote.NetChangePct)}");
                    sb.AppendLine($"VWAP: {FormatNullableDecimal(quote.VWAP)}  Volume: {FormatNullableLong(quote.Volume)}  PreviousVolume: {FormatNullableLong(quote.PreviousVolume)}");
                    sb.AppendLine($"52-Week High: {FormatNullableDecimal(quote.High52Week)} ({FormatNullableDate(quote.High52WeekTimestamp)})");
                    sb.AppendLine($"52-Week Low : {FormatNullableDecimal(quote.Low2Week)} ({FormatNullableDate(quote.Low52WeekTimestamp)})");
                    sb.AppendLine($"TradeTime: {FormatNullableDate(quote.TradeTime)}");
                    sb.AppendLine($"MarketFlags: Delayed={quote.MarketFlags?.IsDelayed}, Halted={quote.MarketFlags?.IsHalted}, HardToBorrow={quote.MarketFlags?.IsHardToBorrow}");
                    sb.AppendLine();
                }

                if (textBox2 != null) textBox2.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error fetching/parsing TSLA price: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                GoButton.Enabled = true;
            }
        }

        private static List<TsQuote> ParseTradeStationQuotes(string json)
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            try
            {
                var root = JsonSerializer.Deserialize<TsQuotesRoot>(json, opts);
                if (root?.Quotes == null) return new List<TsQuote>();

                var list = new List<TsQuote>(root.Quotes.Count);
                foreach (var dto in root.Quotes)
                {
                    list.Add(MapDtoToQuote(dto));
                }
                return list;
            }
            catch (JsonException)
            {
                return new List<TsQuote>();
            }
        }

        private static TsQuote MapDtoToQuote(TsQuoteDto dto)
        {
            return new TsQuote
            {
                Symbol = dto.Symbol ?? "",
                Open = ParseDecimalNullable(dto.Open),
                High = ParseDecimalNullable(dto.High),
                Low = ParseDecimalNullable(dto.Low),
                PreviousClose = ParseDecimalNullable(dto.PreviousClose),
                Last = ParseDecimalNullable(dto.Last),
                Ask = ParseDecimalNullable(dto.Ask),
                AskSize = ParseLongNullable(dto.AskSize),
                Bid = ParseDecimalNullable(dto.Bid),
                BidSize = ParseLongNullable(dto.BidSize),
                NetChange = ParseDecimalNullable(dto.NetChange),
                NetChangePct = ParseDecimalNullable(dto.NetChangePct),
                High52Week = ParseDecimalNullable(dto.High52Week),
                High52WeekTimestamp = ParseDateNullable(dto.High52WeekTimestamp),
                Low2Week = ParseDecimalNullable(dto.Low52Week),
                Low52WeekTimestamp = ParseDateNullable(dto.Low52WeekTimestamp),
                Volume = ParseLongNullable(dto.Volume),
                PreviousVolume = ParseLongNullable(dto.PreviousVolume),
                Close = ParseDecimalNullable(dto.Close),
                DailyOpenInterest = ParseLongNullable(dto.DailyOpenInterest),
                TradeTime = ParseDateNullable(dto.TradeTime),
                TickSizeTier = ParseIntNullable(dto.TickSizeTier),
                LastSize = ParseLongNullable(dto.LastSize),
                LastVenue = dto.LastVenue,
                VWAP = ParseDecimalNullable(dto.VWAP),
                MarketFlags = dto.MarketFlags
            };
        }

        private static decimal? ParseDecimalNullable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        private static long? ParseLongNullable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl)) return (long)dbl;
            return null;
        }

        private static int? ParseIntNullable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i)) return i;
            return null;
        }

        private static DateTimeOffset? ParseDateNullable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto;
            // try parsing as unix epoch number
            if (long.TryParse(s, out var unix))
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(unix); } catch { try { return DateTimeOffset.FromUnixTimeSeconds(unix); } catch { } }
            }
            return null;
        }

        private static string FormatNullableDecimal(decimal? d) => d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "n/a";
        private static string FormatNullableLong(long? l) => l.HasValue ? l.Value.ToString(CultureInfo.InvariantCulture) : "n/a";
        private static string FormatNullableDate(DateTimeOffset? dto) => dto.HasValue ? dto.Value.ToString("u") : "n/a";

        // ---- DTOs matching the JSON you pasted ----

        private sealed class TsQuotesRoot
        {
            [JsonPropertyName("Quotes")]
            public List<TsQuoteDto>? Quotes { get; set; }
        }

        private sealed class TsQuoteDto
        {
            [JsonPropertyName("Symbol")] public string? Symbol { get; set; }
            [JsonPropertyName("Open")] public string? Open { get; set; }
            [JsonPropertyName("High")] public string? High { get; set; }
            [JsonPropertyName("Low")] public string? Low { get; set; }
            [JsonPropertyName("PreviousClose")] public string? PreviousClose { get; set; }
            [JsonPropertyName("Last")] public string? Last { get; set; }
            [JsonPropertyName("Ask")] public string? Ask { get; set; }
            [JsonPropertyName("AskSize")] public string? AskSize { get; set; }
            [JsonPropertyName("Bid")] public string? Bid { get; set; }
            [JsonPropertyName("BidSize")] public string? BidSize { get; set; }
            [JsonPropertyName("NetChange")] public string? NetChange { get; set; }
            [JsonPropertyName("NetChangePct")] public string? NetChangePct { get; set; }
            [JsonPropertyName("High52Week")] public string? High52Week { get; set; }
            [JsonPropertyName("High52WeekTimestamp")] public string? High52WeekTimestamp { get; set; }
            [JsonPropertyName("Low52Week")] public string? Low52Week { get; set; }
            [JsonPropertyName("Low52WeekTimestamp")] public string? Low52WeekTimestamp { get; set; }
            [JsonPropertyName("Volume")] public string? Volume { get; set; }
            [JsonPropertyName("PreviousVolume")] public string? PreviousVolume { get; set; }
            [JsonPropertyName("Close")] public string? Close { get; set; }
            [JsonPropertyName("DailyOpenInterest")] public string? DailyOpenInterest { get; set; }
            [JsonPropertyName("TradeTime")] public string? TradeTime { get; set; }
            [JsonPropertyName("TickSizeTier")] public string? TickSizeTier { get; set; }
            [JsonPropertyName("MarketFlags")] public MarketFlags? MarketFlags { get; set; }
            [JsonPropertyName("LastSize")] public string? LastSize { get; set; }
            [JsonPropertyName("LastVenue")] public string? LastVenue { get; set; }
            [JsonPropertyName("VWAP")] public string? VWAP { get; set; }
        }

        private sealed class MarketFlags
        {
            [JsonPropertyName("IsDelayed")] public bool IsDelayed { get; set; }
            [JsonPropertyName("IsHardToBorrow")] public bool IsHardToBorrow { get; set; }
            [JsonPropertyName("IsBats")] public bool IsBats { get; set; }
            [JsonPropertyName("IsHalted")] public bool IsHalted { get; set; }
        }

        private sealed class TsQuote
        {
            public string Symbol { get; init; } = "";
            public decimal? Open { get; init; }
            public decimal? High { get; init; }
            public decimal? Low { get; init; }
            public decimal? PreviousClose { get; init; }
            public decimal? Last { get; init; }
            public decimal? Ask { get; init; }
            public long? AskSize { get; init; }
            public decimal? Bid { get; init; }
            public long? BidSize { get; init; }
            public decimal? NetChange { get; init; }
            public decimal? NetChangePct { get; init; }
            public decimal? High52Week { get; init; }
            public DateTimeOffset? High52WeekTimestamp { get; init; }
            public decimal? Low2Week { get; init; }
            public DateTimeOffset? Low52WeekTimestamp { get; init; }
            public long? Volume { get; init; }
            public long? PreviousVolume { get; init; }
            public decimal? Close { get; init; }
            public long? DailyOpenInterest { get; init; }
            public DateTimeOffset? TradeTime { get; init; }
            public int? TickSizeTier { get; init; }
            public long? LastSize { get; init; }
            public string? LastVenue { get; init; }
            public decimal? VWAP { get; init; }
            public MarketFlags? MarketFlags { get; init; }
        }


        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            /* F1 key for help */
            if (keyData == Keys.F1)
            {
                MessageBox.Show("F1 button was pressed", "Keyboard feedback...", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            /* close form on escape key */
            try
            {
                if (msg.WParam.ToInt32() == (int)Keys.Escape) this.Close();
                /*    else return base.ProcessCmdKey(ref msg, keyData); is this line needed???? */
            }
            catch (Exception Ex)
            {
                MessageBox.Show("Key Overrided Events Error:" + Ex.Message);
            }

            /* Call the base class for normal key processing */
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Add this helper method inside the Form1 class (anywhere in the class body)
        private static Control? FindControlRecursive(Control parent, string name)
        {
            foreach (Control child in parent.Controls)
            {
                if (child.Name == name)
                    return child;
                var found = FindControlRecursive(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

    }  // end of class


} // end of namespace