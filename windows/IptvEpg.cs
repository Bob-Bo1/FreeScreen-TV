using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace TvAssistant {
    public sealed class EpgChannelCoverage {
        public string Name { get; internal set; }
        public string OriginalId { get; internal set; }
        public string MatchedId { get; internal set; }
        public bool HasCurrent { get; internal set; }
        public int FutureProgrammes { get; internal set; }
        public DateTimeOffset? CoveredUntil { get; internal set; }
        public string CurrentTitle { get; internal set; }
        public bool HasCoverage { get { return HasCurrent || FutureProgrammes > 0; } }
    }
    public sealed class EpgAnalysis {
        public List<EpgChannelCoverage> Channels { get; private set; }
        public List<string> Warnings { get; private set; }
        public int TotalChannels { get { return Channels.Count; } }
        public int CoveredChannels { get { return Channels.Count(c => c.HasCoverage); } }
        public int CurrentChannels { get { return Channels.Count(c => c.HasCurrent); } }
        public int FutureChannels { get { return Channels.Count(c => c.FutureProgrammes > 0); } }
        public DateTimeOffset CheckedAt { get; internal set; }
        public bool NeedsRefresh { get { return TotalChannels > 0 && Channels.Any(c => !c.HasCurrent || c.FutureProgrammes == 0 || !c.CoveredUntil.HasValue || c.CoveredUntil.Value < CheckedAt.AddHours(24)); } }
        public string Summary { get { return "订阅 " + TotalChannels + " 个频道；当前有表 " + CurrentChannels + "，未来48小时有表 " + FutureChannels + "，无有效表 " + (TotalChannels - CoveredChannels); } }
        public EpgAnalysis() { Channels = new List<EpgChannelCoverage>(); Warnings = new List<string>(); }
    }
    public sealed class EpgPreview {
        public string PlaylistText { get; internal set; }
        public byte[] EpgBytes { get; internal set; }
        public EpgAnalysis Analysis { get; internal set; }
        public List<string> Errors { get; private set; }
        public List<string> Warnings { get; private set; }
        public bool CanApply { get { return Errors.Count == 0 && EpgBytes != null && Analysis != null; } }
        public EpgPreview() { Errors = new List<string>(); Warnings = new List<string>(); }
    }

    // Pure preview/analysis: never writes files or changes the TV. URLs are supplied
    // by the caller's configuration; there is deliberately no hardcoded provider.
    public static class IptvEpg {
        public const int MaxInputBytes = 16 * 1024 * 1024;
        public const int MaxXmlBytes = 64 * 1024 * 1024;
        public const int MaxPlaylistCharacters = 4 * 1024 * 1024;
        public const int CoverageHours = 48;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        sealed class Subscription {
            public string Name, Id, TvgName;
            public List<int> Lines = new List<int>();
        }
        sealed class Programme {
            public DateTimeOffset Start, Stop;
            public string Title;
        }
        sealed class Guide {
            public XDocument Document;
            public Dictionary<string, List<string>> Names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, List<Programme>> Programmes = new Dictionary<string, List<Programme>>(StringComparer.Ordinal);
            public int InvalidTimes;
        }
        static string Attribute(string line, string name) {
            Match match = Regex.Match(line, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }
        static int NameComma(string line) {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++) { if (line[i] == '"') quoted = !quoted; else if (line[i] == ',' && !quoted) return i; }
            return -1;
        }
        static List<Subscription> Subscriptions(string playlistText) {
            if (String.IsNullOrWhiteSpace(playlistText)) return new List<Subscription>();
            if (playlistText.Length > MaxPlaylistCharacters) throw new InvalidDataException("播放列表过大。");
            string[] lines = playlistText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var result = new List<Subscription>();
            var byKey = new Dictionary<string, Subscription>(StringComparer.OrdinalIgnoreCase);
            Subscription pending = null;
            int pendingLine = -1;
            for (int i = 0; i < lines.Length; i++) {
                string line = lines[i].Trim();
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) {
                    int comma = NameComma(line);
                    string name = comma >= 0 ? line.Substring(comma + 1).Trim() : "";
                    pending = new Subscription { Name = Regex.Replace(name, @"\s*[（(]备(?:线)?\d*[)）]\s*$", ""),
                        Id = Attribute(line, "tvg-id"), TvgName = Attribute(line, "tvg-name") };
                    pendingLine = i;
                } else if (pending != null && line.Length > 0 && !line.StartsWith("#")) {
                    Uri uri;
                    if (Uri.TryCreate(line, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https")) {
                        string key = !String.IsNullOrWhiteSpace(pending.TvgName) ? Normalize(pending.TvgName) : Normalize(pending.Name);
                        if (key.Length == 0) key = "id:" + pending.Id;
                        Subscription existing;
                        if (!byKey.TryGetValue(key, out existing)) { existing = pending; byKey.Add(key, existing); result.Add(existing); }
                        existing.Lines.Add(pendingLine);
                    }
                    pending = null;
                }
            }
            return result;
        }
        static string Normalize(string value) {
            string name = (value ?? "").Trim().ToLowerInvariant();
            name = Regex.Replace(name, @"\s*[（(]备(?:线)?\d*[)）]\s*$", "");
            name = Regex.Replace(name, @"[\s_\-]", "");
            name = Regex.Replace(name, @"(?:高清|超清|标清|频道|hd|sd)$", "", RegexOptions.IgnoreCase);
            Match cctv = Regex.Match(name, @"^cctv(\d{1,2})(\+|plus|p)?(?:[^0-9a-z+].*)?$", RegexOptions.IgnoreCase);
            if (cctv.Success) return "cctv" + Int32.Parse(cctv.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + (cctv.Groups[2].Success ? "+" : "");
            return name;
        }
        public static DateTimeOffset ParseXmlTvTime(string text) {
            Match m = Regex.Match((text ?? "").Trim(), @"^(\d{8}(?:\d{2}){0,3})(?:\s*(Z|UTC|GMT|[+-]\d{2}:?\d{2}))?$", RegexOptions.IgnoreCase);
            if (!m.Success) throw new FormatException("无效 XMLTV 时间：" + text);
            string digits = m.Groups[1].Value.PadRight(14, '0');
            DateTime date = DateTime.ParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
            string zone = m.Groups[2].Value.Replace(":", "");
            TimeSpan offset = TimeSpan.Zero; // XMLTV: omitted zone means UTC, never local machine time.
            if (zone.StartsWith("+") || zone.StartsWith("-")) {
                int hours = Int32.Parse(zone.Substring(1, 2), CultureInfo.InvariantCulture);
                int minutes = Int32.Parse(zone.Substring(3, 2), CultureInfo.InvariantCulture);
                if (minutes > 59 || hours > 14 || (hours == 14 && minutes != 0)) throw new FormatException("无效 XMLTV 时区。");
                offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (zone[0] == '-' ? -1 : 1));
            }
            return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), offset);
        }
        public static byte[] Download(Uri uri, CancellationToken token, int timeoutMilliseconds) {
            if (uri == null || !uri.IsAbsoluteUri || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("节目表地址必须是 HTTP/HTTPS。", "uri");
            if (timeoutMilliseconds < 1000 || timeoutMilliseconds > 300000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            token.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;
            request.UserAgent = "TvAssistant-EPG/1.0";
            request.AutomaticDecompression = DecompressionMethods.None;
            // Abort releases both a pending GetResponse and a blocked network read.
            using (token.Register(request.Abort))
            using (var deadline = new System.Threading.Timer(delegate { request.Abort(); }, null, timeoutMilliseconds, System.Threading.Timeout.Infinite)) {
                try {
                    using (var response = (HttpWebResponse)request.GetResponse()) {
                        if (response.ContentLength > MaxInputBytes) throw new InvalidDataException("节目表下载超过大小限制。");
                        using (Stream input = response.GetResponseStream()) return ReadLimited(input, MaxInputBytes, token);
                    }
                } catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
            }
        }
        static byte[] ReadLimited(Stream input, int limit, CancellationToken token) {
            using (var output = new MemoryStream()) {
                byte[] buffer = new byte[32768];
                while (true) {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    if (output.Length + count > limit) throw new InvalidDataException("节目表超过解压/下载大小限制。");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }
        static bool IsGzip(byte[] bytes) { return bytes != null && bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b; }
        static byte[] Decode(byte[] bytes) {
            if (bytes == null || bytes.Length == 0) throw new InvalidDataException("节目表为空。");
            if (bytes.Length > MaxInputBytes) throw new InvalidDataException("节目表输入超过大小限制。");
            if (!IsGzip(bytes)) return bytes;
            using (var memory = new MemoryStream(bytes, false))
            using (var gzip = new GZipStream(memory, CompressionMode.Decompress))
                return ReadLimited(gzip, MaxXmlBytes, CancellationToken.None);
        }
        static Guide Parse(byte[] xml, DateTimeOffset now) {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes, MaxCharactersFromEntities = 1024, IgnoreComments = true };
            XDocument document;
            using (var memory = new MemoryStream(xml, false))
            using (XmlReader reader = XmlReader.Create(memory, settings)) document = XDocument.Load(reader);
            if (document.Root == null || document.Root.Name.LocalName != "tv") throw new InvalidDataException("节目表不是 XMLTV 格式。");
            var guide = new Guide { Document = document };
            foreach (XElement channel in document.Root.Elements().Where(e => e.Name.LocalName == "channel")) {
                string id = (string)channel.Attribute("id");
                if (String.IsNullOrWhiteSpace(id)) continue;
                List<string> names;
                if (!guide.Names.TryGetValue(id, out names)) { names = new List<string>(); guide.Names.Add(id, names); }
                foreach (XElement name in channel.Elements().Where(e => e.Name.LocalName == "display-name"))
                    if (!String.IsNullOrWhiteSpace(name.Value)) names.Add(Normalize(name.Value));
            }
            DateTimeOffset horizon = now.AddHours(CoverageHours);
            foreach (XElement item in document.Root.Elements().Where(e => e.Name.LocalName == "programme")) {
                string id = (string)item.Attribute("channel");
                if (String.IsNullOrWhiteSpace(id)) continue;
                try {
                    DateTimeOffset start = ParseXmlTvTime((string)item.Attribute("start"));
                    DateTimeOffset stop = ParseXmlTvTime((string)item.Attribute("stop"));
                    if (stop <= start) { guide.InvalidTimes++; continue; }
                    // Only current/near-future programmes count. A lone programme years
                    // ahead must never make an expired subscription look healthy.
                    if (stop <= now || start > horizon) continue;
                    List<Programme> programmes;
                    if (!guide.Programmes.TryGetValue(id, out programmes)) { programmes = new List<Programme>(); guide.Programmes.Add(id, programmes); }
                    XElement title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title");
                    programmes.Add(new Programme { Start = start, Stop = stop, Title = title == null ? "" : title.Value });
                } catch (FormatException) { guide.InvalidTimes++; }
                  catch (ArgumentException) { guide.InvalidTimes++; }
            }
            return guide;
        }
        static EpgChannelCoverage Coverage(Subscription channel, string id, Guide guide, DateTimeOffset now) {
            var result = new EpgChannelCoverage { Name = channel.Name, OriginalId = channel.Id, MatchedId = id, CurrentTitle = "" };
            List<Programme> programmes;
            if (id == null || !guide.Programmes.TryGetValue(id, out programmes)) return result;
            var active = programmes.Where(p => p.Start <= now && p.Stop > now).OrderBy(p => p.Start).FirstOrDefault();
            result.HasCurrent = active != null;
            result.CurrentTitle = active == null ? "" : active.Title;
            result.FutureProgrammes = programmes.Where(p => p.Start > now).Select(p => p.Start.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + p.Stop.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)).Distinct().Count();
            DateTimeOffset cursor = now;
            foreach (var programme in programmes.OrderBy(p => p.Start)) {
                if (programme.Stop <= cursor) continue;
                if (programme.Start > cursor.AddMinutes(30)) break;
                cursor = programme.Stop;
            }
            if (cursor > now) result.CoveredUntil = cursor;
            return result;
        }
        static EpgAnalysis AnalyzeGuide(List<Subscription> subscriptions, Guide guide, DateTimeOffset now) {
            var result = new EpgAnalysis { CheckedAt = now };
            foreach (Subscription channel in subscriptions) {
                string tvg = Normalize(channel.TvgName), display = Normalize(channel.Name);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (!String.IsNullOrWhiteSpace(channel.Id) && (guide.Names.ContainsKey(channel.Id) || guide.Programmes.ContainsKey(channel.Id))) ids.Add(channel.Id);
                foreach (KeyValuePair<string, List<string>> pair in guide.Names)
                    if (pair.Value.Any(n => (tvg.Length > 0 && n == tvg) || (display.Length > 0 && n == display))) ids.Add(pair.Key);
                EpgChannelCoverage coverage = ids.Select(id => Coverage(channel, id, guide, now))
                    .OrderByDescending(c => c.HasCurrent).ThenByDescending(c => c.CoveredUntil).ThenByDescending(c => c.FutureProgrammes)
                    .ThenByDescending(c => c.MatchedId == channel.Id).FirstOrDefault();
                if (coverage == null) coverage = Coverage(channel, null, guide, now);
                result.Channels.Add(coverage);
                if (!coverage.HasCoverage) result.Warnings.Add(channel.Name + "：没有当前或未来48小时的节目表。");
                else if (!coverage.HasCurrent) result.Warnings.Add(channel.Name + "：只有未来节目，当前时段无表。");
                else if (coverage.FutureProgrammes == 0) result.Warnings.Add(channel.Name + "：只有当前节目，未来节目即将断档。");
            }
            if (guide.InvalidTimes > 0) result.Warnings.Add("已忽略 " + guide.InvalidTimes + " 条缺少结束时间或时间无效的节目。");
            return result;
        }
        public static EpgAnalysis Analyze(string playlistText, byte[] epgBytes, DateTimeOffset now) {
            List<Subscription> subscriptions = Subscriptions(playlistText);
            if (epgBytes == null || epgBytes.Length == 0) {
                var empty = new EpgAnalysis { CheckedAt = now };
                foreach (Subscription channel in subscriptions) {
                    empty.Channels.Add(new EpgChannelCoverage { Name = channel.Name, OriginalId = channel.Id, CurrentTitle = "" });
                    empty.Warnings.Add(channel.Name + "：尚未提供节目表。");
                }
                return empty;
            }
            return AnalyzeGuide(subscriptions, Parse(Decode(epgBytes), now), now);
        }
        public static EpgPreview Prepare(string playlistText, byte[] currentEpg, byte[] candidateEpg, DateTimeOffset now, bool correctKnownEpgPwUtc) {
            var preview = new EpgPreview { PlaylistText = playlistText };
            try {
                List<Subscription> subscriptions = Subscriptions(playlistText);
                if (subscriptions.Count == 0) { preview.Errors.Add("先导入包含 HTTP/HTTPS 频道的播放列表，才能验证节目表覆盖。"); return preview; }
                byte[] xml = Decode(candidateEpg);
                Guide guide = Parse(xml, now);
                if (correctKnownEpgPwUtc) {
                    int changed = 0;
                    foreach (XElement programme in guide.Document.Root.Elements().Where(e => e.Name.LocalName == "programme")) {
                        foreach (string attribute in new[] { "start", "stop" }) {
                            XAttribute value = programme.Attribute(attribute);
                            if (value != null && Regex.IsMatch(value.Value, @"\s\+0000\s*$")) {
                                value.Value = Regex.Replace(value.Value, @"\s\+0000\s*$", " +0800"); changed++;
                            }
                        }
                    }
                    xml = Serialize(guide.Document);
                    guide = Parse(xml, now);
                    preview.Warnings.Add("已按显式选项修正已知 epg.pw 错标时区：" + changed + " 处 +0000 改为 +0800；仅适用于已确认内容为北京时间的来源。");
                }
                preview.Analysis = AnalyzeGuide(subscriptions, guide, now);
                preview.Warnings.AddRange(preview.Analysis.Warnings);
                if (preview.Analysis.CoveredChannels == 0) preview.Errors.Add("候选节目表已过期、时段不适用或没有匹配频道，禁止替换。");
                else if (preview.Analysis.CoveredChannels * 2 < preview.Analysis.TotalChannels) preview.Errors.Add("候选节目表的有效频道覆盖不足一半，禁止替换。");
                EpgAnalysis baseline = null;
                try { baseline = Analyze(playlistText, currentEpg, now); }
                catch (Exception e) {
                    if (!IsDataError(e)) throw;
                    preview.Warnings.Add("现有节目表无法解析，将按首次导入验证；原始文件仍应保留备份。");
                }
                if (baseline != null && baseline.CoveredChannels > 0 && preview.Analysis.CoveredChannels < Math.Ceiling(baseline.CoveredChannels * 0.8))
                    preview.Errors.Add("候选节目表有效频道覆盖较现有版本下降超过20%，禁止替换。");
                if (baseline != null && baseline.FutureChannels > 0 && preview.Analysis.FutureChannels < Math.Ceiling(baseline.FutureChannels * 0.8))
                    preview.Errors.Add("候选节目表未来节目覆盖明显退步，禁止替换。");
                preview.PlaylistText = PatchPlaylist(playlistText, subscriptions, preview.Analysis, preview.Warnings);
                preview.EpgBytes = Gzip(xml);
            } catch (Exception e) {
                if (!IsDataError(e)) throw;
                preview.Errors.Add("节目表无效：" + e.Message);
            }
            return preview;
        }
        static bool IsDataError(Exception error) {
            return error is XmlException || error is InvalidDataException || error is IOException || error is ArgumentException || error is FormatException;
        }
        static string PatchPlaylist(string text, List<Subscription> subscriptions, EpgAnalysis analysis, List<string> warnings) {
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < subscriptions.Count; i++) {
                EpgChannelCoverage row = analysis.Channels[i];
                if (!row.HasCoverage || String.IsNullOrEmpty(row.MatchedId)) continue;
                if (row.MatchedId.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new InvalidDataException("节目表频道 ID 含非法字符。");
                foreach (int line in subscriptions[i].Lines) {
                    string id = Attribute(lines[line], "tvg-id");
                    if (id == row.MatchedId) continue;
                    if (Regex.IsMatch(lines[line], "(?:^|\\s)tvg-id\\s*=\\s*\"[^\"]*\"", RegexOptions.IgnoreCase))
                        lines[line] = Regex.Replace(lines[line], "(?<=\\s)tvg-id\\s*=\\s*\"[^\"]*\"", m => "tvg-id=\"" + row.MatchedId + "\"", RegexOptions.IgnoreCase);
                    else {
                        int comma = NameComma(lines[line]);
                        if (comma >= 0) lines[line] = lines[line].Insert(comma, " tvg-id=\"" + row.MatchedId + "\"");
                    }
                    warnings.Add(row.Name + "：tvg-id " + (id.Length == 0 ? "(空)" : id) + " → " + row.MatchedId + "（确认后生效）。");
                }
            }
            return String.Join(newline, lines);
        }
        static byte[] Serialize(XDocument document) {
            using (var output = new MemoryStream()) {
                var settings = new XmlWriterSettings { Encoding = Utf8, Indent = false, CloseOutput = false };
                using (XmlWriter writer = XmlWriter.Create(output, settings)) document.Save(writer);
                return output.ToArray();
            }
        }
        static byte[] Gzip(byte[] bytes) {
            using (var output = new MemoryStream()) {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(bytes, 0, bytes.Length);
                return output.ToArray();
            }
        }
    }
}
