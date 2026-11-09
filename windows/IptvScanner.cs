using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TvAssistant {
    public enum IptvScanMode { Repair, OptimizeHd, DetectOnly, InspectCandidates }
    public sealed class IptvScanOptions {
        public IptvScanMode Mode = IptvScanMode.Repair;
        public string ChannelsPath, SourcesPath, ToolsDirectory;
        // 实测：CCTV-5 的 5MB 分片在本机要 9 秒左右才能下完，8 秒的旧默认值会把健康源
        // 判成“请求被取消/DEAD”，用户看到的就是“这次没连上”。这里放宽到可容纳慢而健康的源。
        public int RequestTimeoutMs = 12000, CandidateTimeoutMs = 20000, MaxCandidatesTotal = 150, MaxCandidatesPerChannel = 4;
        public int TotalBudgetSeconds = 1800, DeepTestSeconds = 30;
        public int OldProbeConcurrency = 6;
        public bool AllowCrossOriginUpgrade = false;
        public string[] TrustedDomesticHosts = new string[0];
        public string[] OnlyChannels = new string[0];
    }
    public sealed class IptvScanChange {
        public string Channel, OldUrl, NewUrl, Reason;
        public bool RequiresConfirmation;
    }
    public sealed class IptvProbeResult {
        public int Width, Height; public bool Hd, HdBySize, Decoded, QualityKnown, IsCandidate, Smooth; public double Speed;
        public string Channel, Url, Detail, Status, Source;
    }
    public sealed class IptvScanPlan {
        public string M3u, Summary;
        public bool RequiresConfirmation, Cancelled, Incomplete;
        public int CandidateListsLoaded, CandidatesFound, CandidatesTested;
        public bool CandidateSearchStarted;
        public List<IptvScanChange> Changes = new List<IptvScanChange>();
        public List<string> Warnings = new List<string>();
        public List<IptvProbeResult> Results = new List<IptvProbeResult>();
    }
    // Injection seam: tests exercise the actual Run and HLS state machine, without public traffic.
    public interface IIptvScanTransport {
        byte[] Get(Uri url, int timeoutMs, int maxBytes, CancellationToken cancellation);
        void Wait(int milliseconds, CancellationToken cancellation);
    }
    internal sealed class IptvRedirect : IOException { public readonly string Location; public IptvRedirect(string location) { Location = location; } }
    public sealed class IptvHttpTransport : IIptvScanTransport {
        public byte[] Get(Uri url, int timeoutMs, int maxBytes, CancellationToken cancellation) {
            if (url.Scheme != "http" && url.Scheme != "https") throw new IOException("仅支持HTTP(S)");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = "TvAssistant-IPTV/1.0"; request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            // A slow-but-healthy IPTV segment must not be aborted just because the whole transfer is
            // longer than one timeout window: 5.5 MB slices on these gateways legitimately need 20-60s.
            // The limit therefore applies to "no bytes arriving", bounded by an absolute safety cap.
            long idleLimit = Math.Max(1000, timeoutMs);
            long hardCap = Math.Max(idleLimit * 6, 180000);
            var watch = Stopwatch.StartNew();
            long lastData = 0;
            using (cancellation.Register(delegate { request.Abort(); }))
            using (var timer = new Timer(delegate { request.Abort(); }, null, (int)Math.Min(Int32.MaxValue, hardCap), Timeout.Infinite)) {
                try {
                    using (var response = (HttpWebResponse)request.GetResponse()) {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) { string location = response.Headers["Location"]; if (String.IsNullOrEmpty(location)) throw new IOException("重定向缺少Location"); throw new IptvRedirect(location); }
                        if (response.ResponseUri.Scheme != "http" && response.ResponseUri.Scheme != "https") throw new IOException("无效重定向");
                        if (response.ContentLength > maxBytes) throw new IOException("响应超过大小上限");
                        using (var stream = response.GetResponseStream())
                        using (var output = new MemoryStream()) {
                            byte[] buffer = new byte[8192]; int count;
                            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                                cancellation.ThrowIfCancellationRequested();
                                lastData = watch.ElapsedMilliseconds;
                                if (output.Length + count > maxBytes) throw new IOException("响应超过大小上限");
                                output.Write(buffer, 0, count);
                            }
                            return output.ToArray();
                        }
                    }
                } catch (WebException) {
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.ElapsedMilliseconds - lastData >= idleLimit) throw new IOException("超过 " + (idleLimit / 1000) + " 秒没有收到数据");
                    if (watch.ElapsedMilliseconds >= hardCap) throw new IOException("下载时间超过上限");
                    throw;
                }
            }
        }
        public void Wait(int milliseconds, CancellationToken cancellation) {
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }
    public sealed class IptvScanner {
        readonly IIptvScanTransport transport;
        public IptvScanner() : this(new IptvHttpTransport()) { }
        public IptvScanner(IIptvScanTransport transport) { if (transport == null) throw new ArgumentNullException("transport"); this.transport = transport; }
        sealed class BudgetExpired : Exception { }
        sealed class Entry { public string Name, Url, Standard, Source; public int Line; public bool Protected, Trusted; }
        sealed class Probe {
            public string Status, Detail, Sar, Dar, MediaUrl; public int Width, Height; public double DisplayWidth; public bool Decoded, QualityKnown;
            // How fast the source actually arrives compared with real time: 1.0 = just keeps up,
            // below 1.0 means the TV cannot fill its buffer fast enough and will stutter.
            public double Speed;
            public bool Healthy { get { return Status == "LIVE"; } }
            public bool Smooth { get { return Healthy && Speed > 0 && Speed >= 1.15; } }
            public bool Hd { get { return Decoded && Width >= 1280 && Height >= 720 && DisplayWidth >= 1279; } }
            public bool HdBySize { get { return QualityKnown && Width >= 1280 && Height >= 720 && DisplayWidth >= 1279; } }
        }
        sealed class Context {
            public IptvScanOptions Options; public CancellationToken Token; public Stopwatch Clock = Stopwatch.StartNew();
            public readonly SemaphoreSlim HttpSlots = new SemaphoreSlim(3);
            readonly Dictionary<string, SemaphoreSlim> origins = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            public SemaphoreSlim Origin(string url) { string origin = new Uri(url).GetLeftPart(UriPartial.Authority); lock (origins) { SemaphoreSlim gate; if (!origins.TryGetValue(origin, out gate)) origins[origin] = gate = new SemaphoreSlim(1); return gate; } }
            public void Enter(SemaphoreSlim gate) { while (true) { Check(); if (gate.Wait(50, Token)) return; } }
            public void Check() { Token.ThrowIfCancellationRequested(); if (Clock.Elapsed.TotalSeconds >= Math.Max(1, Options.TotalBudgetSeconds)) throw new BudgetExpired(); }
            public int Timeout() { Check(); return Math.Max(1, Math.Min(Math.Max(1, Options.RequestTimeoutMs), (int)Math.Max(1, Math.Max(1, Options.TotalBudgetSeconds) * 1000L - Clock.ElapsedMilliseconds))); }
        }
        static bool Http(string value) { Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && value.IndexOf('|') < 0; }
        static bool SameOrigin(string first, string second) { return Uri.Compare(new Uri(first), new Uri(second), UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0; }
        static bool TrustedHost(string url, IptvScanOptions options) {
            string host = new Uri(url).DnsSafeHost.TrimEnd('.');
            foreach (string trusted in options.TrustedDomesticHosts ?? new string[0]) if (String.Equals(host, (trusted ?? "").Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static bool UpgradeOriginAllowed(string oldUrl, string newUrl, IptvScanOptions options) {
            return SameOrigin(oldUrl, newUrl) || (options.AllowCrossOriginUpgrade && TrustedHost(oldUrl, options) && TrustedHost(newUrl, options));
        }
        static void WarnOrigin(IptvScanPlan plan, string channel) {
            string warning = channel + "：保留健康旧源，已阻止未明确可信的跨origin高清替换（不依据域名后缀或region标签推断境内）";
            if (!plan.Warnings.Contains(warning)) plan.Warnings.Add(warning);
        }
        static string Key(string text) { return Regex.Replace((text ?? "").ToUpperInvariant(), @"[\s\-－]", ""); }
        static string StandardName(string name, Dictionary<string, string> standards) {
            string standard; if (standards.TryGetValue(Key(name), out standard)) return standard;
            string clean = (name ?? "").Trim();
            // Strip only recognized trailing annotations, never channel digits or native CCTV-4K/8K names.
            for (int i = 0; i < 4; i++) {
                string next = Regex.Replace(clean, @"\s*(?:[（(\[]\s*(?:备|备用|备线|主|主线|HD|SD|FHD|UHD|高清|超清|标清|\d{3,4}[pPiI])\s*[）)\]]|\s+(?:HD|SD|FHD|UHD|高清|超清|标清|\d{3,4}[pPiI]))\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (next == clean) break; clean = next;
                if (standards.TryGetValue(Key(clean), out standard)) return standard;
            }
            return null;
        }
        static string ReadSmall(string path) {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > 2097152) throw new IOException("配置超过2MB上限");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        static Dictionary<string, object> Json(string text) {
            return new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 32 }.DeserializeObject(text) as Dictionary<string, object>;
        }
        static IEnumerable<object> ArrayValue(Dictionary<string, object> obj, string key) {
            object value; if (obj != null && obj.TryGetValue(key, out value) && value is object[]) return (object[])value;
            return new object[0];
        }
        static string Value(Dictionary<string, object> obj, string key) { object value; return obj.TryGetValue(key, out value) && value != null ? value.ToString() : ""; }
        static Dictionary<string, string> Standards(string path) {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text = ReadSmall(path); if (text == null) return result;
            foreach (object item in ArrayValue(Json(text), "channels")) {
                var row = item as Dictionary<string, object>; if (row == null) continue;
                string standard = Value(row, "std"); if (standard.Length == 0) continue;
                result[Key(standard)] = standard;
                foreach (string field in new[] { "short", "epg" }) { string alias = Value(row, field); if (alias.Length > 0) result[Key(alias)] = standard; }
                foreach (object alias in ArrayValue(row, "aliases")) if (alias is string) result[Key((string)alias)] = standard;
            }
            return result;
        }
        static List<Entry> Parse(string[] lines, Dictionary<string, string> standards) {
            var entries = new List<Entry>(); string name = null; bool protect = false;
            for (int i = 0; i < lines.Length; i++) {
                string line = lines[i].Trim();
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) {
                    // Commas in quoted attributes are not display-name delimiters.
                    bool quote = false; int comma = -1;
                    for (int p = 0; p < line.Length; p++) { if (line[p] == '"') quote = !quote; if (line[p] == ',' && !quote) { comma = p; break; } }
                    name = comma >= 0 ? line.Substring(comma + 1).Trim() : null; protect = false;
                } else if (line.StartsWith("#")) {
                    if (name != null && (line.StartsWith("#EXTVLCOPT", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#EXTHTTP", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#KODIPROP", StringComparison.OrdinalIgnoreCase))) protect = true;
                } else if (line.Length > 0) {
                    string standard = name == null ? null : StandardName(name, standards);
                    entries.Add(new Entry { Name = name, Standard = standard, Url = line, Line = i, Protected = protect || !Http(line) }); name = null; protect = false;
                }
            }
            return entries;
        }
        byte[] Fetch(string url, int limit, Context context) { return FetchResolved(ref url, limit, context); }
        byte[] FetchResolved(ref string url, int limit, Context context) {
            for (int hop = 0; hop <= 4; hop++) {
                try { return FetchSingle(url, limit, context); }
                catch (IptvRedirect redirect) { if (hop == 4) throw new IOException("重定向超过4跳"); url = Resolve(url, redirect.Location); }
            }
            throw new IOException("无效重定向");
        }
        byte[] FetchSingle(string url, int limit, Context context) {
            context.Check(); SemaphoreSlim origin = context.Origin(url); context.Enter(origin);
            try {
                context.Enter(context.HttpSlots);
                try {
                    byte[] bytes = transport.Get(new Uri(url), context.Timeout(), limit, context.Token);
                    context.Check(); if (bytes == null || bytes.Length > limit) throw new IOException("响应超过大小上限"); return bytes;
                } finally { context.HttpSlots.Release(); }
            } finally { origin.Release(); }
        }
        string Text(string url, Context context) { return Encoding.UTF8.GetString(Fetch(url, 2097152, context)).TrimStart('\uFEFF'); }
        void Wait(int ms, Context context) { context.Check(); int remaining = (int)Math.Min(Int32.MaxValue, Math.Max(1, context.Options.TotalBudgetSeconds) * 1000L - context.Clock.ElapsedMilliseconds); transport.Wait(Math.Max(1, Math.Min(ms, remaining)), context.Token); context.Check(); }
        static string Resolve(string url, string child) {
            Uri result; if (!Uri.TryCreate(new Uri(url), child, out result) || !Http(result.AbsoluteUri)) throw new IOException("不支持的HLS引用");
            return result.AbsoluteUri;
        }
        sealed class Media { public string Signature; public long Sequence = -1; public List<string> Segments = new List<string>(); public List<double> Durations = new List<double>(); public int Delay = 1000; public bool Ended; }
        Media Manifest(string url, Context context, int depth, out string mediaUrl) {
            if (depth > 3) throw new IOException("HLS嵌套过深");
            string text = Encoding.UTF8.GetString(FetchResolved(ref url, 2097152, context)).TrimStart('\uFEFF'); if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new IOException("非HLS内容");
            string[] lines = Regex.Split(text, "\r\n|\n|\r"); bool variant = false; var media = new Media(); string sequence = ""; double duration = 0;
            foreach (string raw in lines) {
                string line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:")) variant = true;
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) { sequence = line; long n; if (Int64.TryParse(line.Substring(22), out n)) media.Sequence = n; }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:")) { int secs; if (Int32.TryParse(line.Substring(22), out secs)) media.Delay = Math.Max(500, Math.Min(5000, secs * 1000)); }
                else if (line.StartsWith("#EXTINF:")) { double secs; if (Double.TryParse(line.Substring(8).Split(',')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs)) duration = secs; }
                else if (line == "#EXT-X-ENDLIST") media.Ended = true;
                else if (line.Length > 0 && line[0] != '#') {
                    string child = Resolve(url, line);
                    if (variant) return Manifest(child, context, depth + 1, out mediaUrl);
                    media.Segments.Add(child);
                    media.Durations.Add(duration > 0 ? duration : media.Delay / 1000.0);
                }
            }
            if (media.Segments.Count == 0) throw new IOException("没有HLS分片");
            media.Signature = sequence + "|" + media.Segments[media.Segments.Count - 1]; mediaUrl = url; return media;
        }
        sealed class ToolOutput { public int ExitCode; public string Text, Error; }
        static void Kill(Process process) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        ToolOutput Tool(string executable, string arguments, byte[] sample, Context context) {
            context.Check(); int timeout = Math.Min(15000, context.Timeout());
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var stdout = new StringBuilder(); var stderr = new StringBuilder(); bool oversized = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) return; lock (stdout) { if (stdout.Length + e.Data.Length > 1048576) { oversized = true; Kill(process); } else stdout.AppendLine(e.Data); } };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) return; lock (stderr) { if (stderr.Length + e.Data.Length > 1048576) { oversized = true; Kill(process); } else stderr.AppendLine(e.Data); } };
                if (!process.Start()) throw new IOException("无法启动媒体工具");
                bool expired = false;
                using (context.Token.Register(delegate { Kill(process); }))
                using (var timer = new Timer(delegate { expired = true; Kill(process); }, null, timeout, Timeout.Infinite)) {
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    try { process.StandardInput.BaseStream.Write(sample, 0, sample.Length); process.StandardInput.Close(); }
                    catch (IOException) { } // Early tool exit/timeout closes stdin; checked below.
                    if (!process.WaitForExit(timeout)) { expired = true; Kill(process); }
                    process.WaitForExit(); context.Check();
                    if (expired) throw new IOException("媒体工具超时");
                    if (oversized) throw new IOException("媒体工具输出超过上限");
                    return new ToolOutput { ExitCode = process.ExitCode, Text = stdout.ToString(), Error = stderr.ToString() };
                }
            }
        }
        static double Ratio(string value) {
            string[] fields = (value ?? "").Split(':'); double a, b;
            if (fields.Length != 2 || !Double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a) || !Double.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b) || a <= 0 || b <= 0) return 0;
            return a / b;
        }
        void Quality(Probe result, byte[] sample, Context context) {
            string directory = context.Options.ToolsDirectory;
            if (String.IsNullOrEmpty(directory)) directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            string ffprobe = Path.Combine(directory, "ffprobe.exe"), ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            if (!File.Exists(ffprobe) || !File.Exists(ffmpeg)) { result.Detail += "；画质未知（缺少包内ffprobe/ffmpeg）"; return; }
            try {
                // Feed only the already bounded sample. Tools cannot fetch remote URLs or playlist references.
                ToolOutput metadata = Tool(Path.GetFullPath(ffprobe), "-v error -protocol_whitelist pipe -select_streams v:0 -show_entries stream=width,height,sample_aspect_ratio,display_aspect_ratio -of json -i pipe:0", sample, context);
                if (metadata.ExitCode != 0) { result.Status = "UNKNOWN"; result.Detail += "；画质未知（ffprobe无法解析分片，不作为替换候选）"; return; }
                foreach (object item in ArrayValue(Json(metadata.Text), "streams")) {
                    var row = item as Dictionary<string, object>; if (row == null) continue;
                    Int32.TryParse(Value(row, "width"), out result.Width); Int32.TryParse(Value(row, "height"), out result.Height);
                    result.Sar = Value(row, "sample_aspect_ratio"); result.Dar = Value(row, "display_aspect_ratio"); break;
                }
                if (result.Width <= 0 || result.Height <= 0) { result.Detail += "；画质未知（缺视频尺寸）"; return; }
                double sar = Ratio(result.Sar), dar = Ratio(result.Dar);
                if (sar <= 0 && dar <= 0) { result.Detail += "；画质未知（SAR/DAR未提供）"; return; }
                result.DisplayWidth = dar > 0 ? result.Height * dar : result.Width * sar;
                // Size/SAR/DAR is enough to tell the user what they are watching; the strict decode below
                // only decides whether we may auto-upgrade this source.
                result.QualityKnown = true;
                string measured = result.Width + "x" + result.Height
                    + (result.HdBySize ? " 高清" : (result.DisplayWidth < result.Height * 1.5 ? " 4:3 标清" : " 16:9 标清"));
                // -xerror + 8s window falsely failed healthy live slices: a captured HLS slice starts and
                // ends mid-GOP, so the tail is always incomplete. Require 20 decoded video frames instead,
                // and tolerate transient corrupt frames the decoder can skip.
                ToolOutput decode = Tool(Path.GetFullPath(ffmpeg), "-v error -protocol_whitelist pipe -i pipe:0 -map 0:v:0 -an -frames:v 20 -f null -", sample, context);
                if (decode.ExitCode != 0 && !String.IsNullOrEmpty(decode.Text + decode.Error) && (decode.Text + decode.Error).IndexOf("frame=", StringComparison.Ordinal) < 0) {
                    // One retry without strict framing for slices whose first packet is mid-GOP.
                    decode = Tool(Path.GetFullPath(ffmpeg), "-v error -err_detect ignore_err -protocol_whitelist pipe -i pipe:0 -map 0:v:0 -an -frames:v 20 -f null -", sample, context);
                }
                if (decode.ExitCode != 0) { result.Detail += "；实测 " + measured + "（抽样解码未通过，画质仅供参考）"; return; }
                result.Decoded = true;
                result.Detail += "；实测 " + measured + " SAR=" + result.Sar + " DAR=" + result.Dar;
            } catch (OperationCanceledException) { throw; } catch (BudgetExpired) { throw; }
              catch (Exception ex) { context.Check(); result.Detail += "；画质未知（媒体工具：" + ex.Message + "）"; }
        }
        Probe ProbeOnce(string url, Context context) {
            try {
                string mediaUrl; Media media = Manifest(url, context, 0, out mediaUrl);
                if (media.Ended) return new Probe { Status = "UNKNOWN", Detail = "静态/点播HLS，保留不自动替换" };
                string last = media.Signature; long lastSequence = media.Sequence; int advances = 0, elapsed = 0;
                var observed = new HashSet<string>(StringComparer.Ordinal); observed.Add(last);
                // Download initial and every newly observed trailing segment, not just playlist headers.
                var speedWatch = new Stopwatch(); long mediaBytes = 0; double mediaSeconds = 0;
                speedWatch.Start();
                byte[] sample = Fetch(media.Segments[media.Segments.Count - 1], 16777216, context);
                speedWatch.Stop();
                if (sample.Length == 0) throw new IOException("空分片");
                mediaBytes += sample.Length; mediaSeconds += media.Durations.Count > 0 ? media.Durations[media.Durations.Count - 1] : 5;
                int duration = Math.Max(2, Math.Min(120, context.Options.DeepTestSeconds)) * 1000;
                while (elapsed < duration || advances < 2) {
                    if (elapsed >= duration + 10000) return new Probe { Status = "DEAD", Detail = "HLS未连续滚动" };
                    Wait(media.Delay, context); elapsed += media.Delay;
                    string actual; media = Manifest(mediaUrl, context, 0, out actual); mediaUrl = actual;
                    if (media.Ended) return new Probe { Status = "UNKNOWN", Detail = "直播转为静态HLS" };
                    if (media.Sequence >= 0 && lastSequence >= 0 && media.Sequence < lastSequence) throw new IOException("HLS序列倒退");
                    if (media.Signature != last) {
                        if (!observed.Add(media.Signature)) throw new IOException("HLS重复历史列表，未持续滚动");
                        speedWatch.Start();
                        byte[] next = Fetch(media.Segments[media.Segments.Count - 1], 16777216, context);
                        speedWatch.Stop();
                        if (next.Length == 0) throw new IOException("空分片");
                        mediaBytes += next.Length; mediaSeconds += media.Durations.Count > 0 ? media.Durations[media.Durations.Count - 1] : 5;
                        advances++; last = media.Signature; lastSequence = media.Sequence;
                    }
                }
                // Real throughput vs. required bitrate, from the time actually spent downloading.
                double wall = speedWatch.Elapsed.TotalSeconds;
                var result = new Probe { Status = "LIVE", Detail = "HLS连续滚动及分片下载通过", MediaUrl = mediaUrl,
                    Speed = wall > 0.05 && mediaSeconds > 0 ? mediaSeconds / wall : 0 };
                result.Detail += result.Speed > 0 ? "；速度 " + result.Speed.ToString("0.0") + "× 实时" : "；速度未知";
                Quality(result, sample, context); return result;
            } catch (OperationCanceledException) { throw; } catch (BudgetExpired) { throw; }
              catch (Exception ex) { context.Check(); return new Probe { Status = "DEAD", Detail = ex.GetType().Name + ": " + ex.Message }; }
        }
        Probe ProbeUrl(string url, Context context) {
            Probe result = ProbeOnce(url, context);
            if (result.Status == "DEAD") { Wait(1000, context); result = ProbeOnce(url, context); result.Detail = "DEAD冷启动复检后：" + result.Detail; }
            return result;
        }
        static void BuildPlan(IptvScanPlan plan, string original, string[] lines, string[] originalLines, List<Entry> entries, Dictionary<int, string> reasons) {
            var replacements = new Dictionary<int, string>();
            foreach (Entry entry in entries) {
                if (lines[entry.Line] == originalLines[entry.Line]) continue;
                string reason; reasons.TryGetValue(entry.Line, out reason);
                replacements[entry.Line] = lines[entry.Line];
                plan.Changes.Add(new IptvScanChange { Channel = entry.Standard, OldUrl = originalLines[entry.Line].Trim(), NewUrl = lines[entry.Line], Reason = reason ?? "候选调整", RequiresConfirmation = true });
                if (reason != null && reason.Contains("待修复")) plan.Warnings.Add(entry.Standard + "：" + reason);
            }
            if (replacements.Count > 0) {
                int index = 0; plan.M3u = Regex.Replace(original ?? "", @"[^\r\n]+|\r\n|\r|\n", delegate(Match match) {
                    if (match.Value == "\r\n" || match.Value == "\r" || match.Value == "\n") { index++; return match.Value; }
                    string replacement; return replacements.TryGetValue(index, out replacement) ? replacement : match.Value;
                });
                plan.RequiresConfirmation = true;
            }
            plan.Summary = "检测完成：" + plan.Results.Count + "项；候选调整" + plan.Changes.Count + "项（仅预览，未写入生产）";
        }
        void ProbeOld(List<Entry> entries, Dictionary<string, Probe> probes, Context context, Action<string> progress) {
            var targets = new List<Entry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in entries) if (entry.Standard != null && !entry.Protected && Selected(entry.Standard, context.Options) && seen.Add(entry.Url)) targets.Add(entry);
            int lanes = Math.Max(1, Math.Min(6, context.Options.OldProbeConcurrency));
            if (progress != null) progress("并行检测旧主备：" + targets.Count + "条，最多" + lanes + "路；每条约需半分钟，可随时取消");
            int done = 0, started = 0;
            try {
                Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = context.Token }, delegate(Entry entry) {
                    context.Check();
                    int slot = Interlocked.Increment(ref started);
                    if (progress != null) progress("(" + slot + "/" + targets.Count + ") 正在探测 " + Label(entry) + " …");
                    Stopwatch watch = Stopwatch.StartNew();
                    Probe probe = ProbeUrl(entry.Url, context);
                    watch.Stop();
                    lock (probes) { probes[entry.Url] = probe; }
                    int index = Interlocked.Increment(ref done);
                    if (progress != null) progress("[" + index + "/" + targets.Count + "] " + Label(entry) + " → " + Clean(probe) + " · " + watch.ElapsedMilliseconds + "毫秒");
                });
            } catch (AggregateException ex) {
                context.Token.ThrowIfCancellationRequested();
                foreach (Exception inner in ex.Flatten().InnerExceptions) if (inner is BudgetExpired) throw new BudgetExpired();
                throw;
            }
            context.Check();
        }
        static string Clean(Probe probe) {
            string detail = probe.Detail ?? "";
            if (probe.Status == "LIVE") detail = detail.Replace("DEAD冷启动复检后：", "").Replace("DEAD复检后：", "").TrimStart('D', 'E', 'A', 'D', ' ', '；');
            return probe.Status + " " + detail;
        }
        static IptvProbeResult Result(string channel, string url, Probe probe, bool candidate, string source) {
            return new IptvProbeResult { Channel = channel, Url = url, Status = probe.Status, Detail = Clean(probe),
                Width = probe.Width, Height = probe.Height, Hd = probe.Hd, HdBySize = probe.HdBySize,
                Decoded = probe.Decoded, QualityKnown = probe.QualityKnown, IsCandidate = candidate, Source = source,
                Speed = probe.Speed, Smooth = probe.Smooth };
        }
        string SourceContent(Dictionary<string, object> row, string sourcesPath, Context context) {
            string local = Value(row, "path");
            if (!String.IsNullOrWhiteSpace(local)) {
                string directory = Path.GetDirectoryName(Path.GetFullPath(sourcesPath));
                string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string resolved = Path.GetFullPath(Path.Combine(directory, local));
                if (Path.IsPathRooted(local) || !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("本地候选必须位于配置目录内");
                string content = ReadSmall(resolved);
                if (content == null || !content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new IOException("本地候选不存在或不是M3U清单");
                return content;
            }
            var urls = new List<string>(); string primary = Value(row, "url"); if (Http(primary)) urls.Add(primary);
            foreach (object mirror in ArrayValue(row, "mirrors")) {
                string url = mirror as string;
                if (urls.Count >= 4) break;
                if (Http(url) && !urls.Contains(url)) urls.Add(url);
            }
            string lastError = "未配置有效的HTTP(S)下载入口";
            foreach (string url in urls) {
                context.Check();
                try {
                    string content = Text(url, context);
                    if (!content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new IOException("下载内容不是M3U清单");
                    return content; // A mirror is fallback, not an additional independent source.
                } catch (OperationCanceledException) { throw; } catch (BudgetExpired) { throw; }
                  catch (Exception ex) { context.Check(); lastError = ex.Message; }
            }
            throw new IOException(lastError);
        }
        static bool Selected(string standard, IptvScanOptions options) {
            if (options.OnlyChannels == null || options.OnlyChannels.Length == 0) return true;
            if (standard == null) return false;
            foreach (string name in options.OnlyChannels) {
                if (String.IsNullOrEmpty(name)) continue;
                if (String.Equals(name.Trim(), standard, StringComparison.Ordinal)) return true;
                if (standard.IndexOf(name.Trim(), StringComparison.Ordinal) >= 0) return true;
                if (name.Trim().IndexOf(standard, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
        static string Label(Entry entry) {
            if (!String.IsNullOrEmpty(entry.Name)) return entry.Name.Trim();
            return entry.Standard ?? "未识别频道";
        }
        public IptvScanPlan Run(string playlistText, IptvScanOptions options, CancellationToken cancellation, Action<string> progress) {
            var plan = new IptvScanPlan { M3u = playlistText, Summary = "未修改播放列表" };
            Action buildPartial = null;
            var context = new Context { Options = options ?? new IptvScanOptions(), Token = cancellation };
            try {
                context.Check(); options = context.Options;
                string channels = options.ChannelsPath;
                if (String.IsNullOrEmpty(channels)) channels = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "channels.json");
                var standards = Standards(channels);
                if (standards.Count == 0) {
                    plan.Incomplete = true;
                    plan.Summary = "缺少可用的频道对照表（files/channels.json），请完整解压新版电视助手后重试。";
                    plan.Warnings.Add(plan.Summary);
                    return plan;
                }
                string[] lines = Regex.Split(playlistText ?? "", "\r\n|\n|\r");
                string[] originalLines = (string[])lines.Clone();
                var reasons = new Dictionary<int, string>();
                var entries = Parse(lines, standards); var probes = new Dictionary<string, Probe>(StringComparer.Ordinal);
                int pickedCounter = 0;
                foreach (Entry probeEntry in entries)
                    if (probeEntry.Standard != null && !probeEntry.Protected && Selected(probeEntry.Standard, options)) pickedCounter++;
                if (pickedCounter == 0) {
                    plan.Summary = "本轮没有可检测的频道。请确认所选频道在频道对照表中，并使用 HTTP(S) 播放地址。";
                    return plan;
                }
                if (progress != null) progress("已解析 " + entries.Count + " 条频道地址；本轮检测 " + pickedCounter + " 个频道" + (pickedCounter < entries.Count ? "（按左侧勾选）" : "（全部）") + "…");
                var broken = new List<Entry>(); var occupied = new HashSet<string>(StringComparer.Ordinal);
                foreach (Entry entry in entries) occupied.Add(entry.Url);
                buildPartial = delegate { BuildPlan(plan, playlistText, lines, originalLines, entries, reasons); };
                // Every old URL is checked before any choice. Only Probe result dictionary is shared/locked;
                // plan lists and decisions stay on this thread. Per-request gates never cover Wait/Quality.
                ProbeOld(entries, probes, context, progress);
                foreach (Entry entry in entries) {
                    context.Check(); if (entry.Standard == null || entry.Protected) continue;
                    Probe probe; if (!probes.TryGetValue(entry.Url, out probe)) continue;
                    plan.Results.Add(Result(entry.Standard, entry.Url, probe, false, "当前订阅"));
                }
                if (options.Mode == IptvScanMode.DetectOnly) {
                    int bad = 0; foreach (IptvProbeResult row in plan.Results) if (row.Detail.StartsWith("DEAD")) bad++;
                    plan.Summary = "检测完成：" + plan.Results.Count + " 个台，其中 " + bad + " 个异常；本轮只检测，未寻找替代";
                    return plan;
                }
                // Promote a tested healthy backup only by exchanging slots, never by duplicating its URL.
                var first = new HashSet<string>(StringComparer.Ordinal);
                foreach (Entry primary in entries) {
                    if (options.Mode == IptvScanMode.InspectCandidates) break;
                    Probe primaryProbe; if (!probes.TryGetValue(primary.Url, out primaryProbe) || primaryProbe.Status != "DEAD") continue;
                    if (primary.Standard == null || !first.Add(primary.Standard) || primary.Protected) continue;
                    foreach (Entry backup in entries) {
                        Probe backupProbe; if (!probes.TryGetValue(backup.Url, out backupProbe)) continue;
                    if (backup.Line <= primary.Line || backup.Standard != primary.Standard || backup.Protected || !backupProbe.Healthy || backup.Url == primary.Url) continue;
                        string dead = primary.Url; primary.Url = backup.Url; backup.Url = dead;
                        lines[primary.Line] = primary.Url; lines[backup.Line] = backup.Url;
                        reasons[primary.Line] = "旧主源复检异常；已验证健康备源升主（不复制URL）";
                        reasons[backup.Line] = "主备互换后此备槽仍异常，待修复";
                        break;
                    }
                }
                foreach (Entry entry in entries) {
                    if (entry.Standard == null || entry.Protected || !Selected(entry.Standard, options)) continue;
                    Probe probe; if (!probes.TryGetValue(entry.Url, out probe)) continue;
                    if (options.Mode == IptvScanMode.InspectCandidates || probe.Status == "DEAD" || (options.Mode == IptvScanMode.OptimizeHd && probe.Healthy && probe.QualityKnown && !probe.HdBySize)) broken.Add(entry);
                }
                plan.Warnings.Add("仅包内ffprobe/ffmpeg可验证抽样画质；缺工具或分片无法独立解码时标未知，不凭URL/频道标签升级。所有替换仅生成候选并需确认。");
                if (options.Mode == IptvScanMode.OptimizeHd) plan.Warnings.Add("高清优选：先验证旧源活性；仅已解码SD旧源可升级到已解码且尺寸不降级的HD候选。健康HD/画质未知旧源保持原位。");
                if (broken.Count == 0) { plan.Summary = "旧源检测完成；健康/未知/特殊频道保持不变"; return plan; }
                plan.CandidateSearchStarted = true;
                string sourcesPath = options.SourcesPath;
                if (String.IsNullOrEmpty(sourcesPath)) sourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "sources.json");
                string sourcesText = ReadSmall(sourcesPath);
                if (sourcesText == null) { plan.Warnings.Add("未配置sources.json，无新替代源；异常备槽仍待修复"); context.Check(); BuildPlan(plan, playlistText, lines, originalLines, entries, reasons); return plan; }
                var candidates = new List<Entry>(); var seen = new HashSet<string>(StringComparer.Ordinal); int sourceCount = 0;
                var needed = new HashSet<string>(StringComparer.Ordinal); foreach (Entry entry in broken) needed.Add(entry.Standard);
                if (progress != null) progress("开始为 " + needed.Count + " 个频道查找候选来源…");
                foreach (object item in ArrayValue(Json(sourcesText), "sources")) {
                    context.Check(); var row = item as Dictionary<string, object>; if (row == null) continue;
                    object enabled; if (!row.TryGetValue("enabled", out enabled) || !(enabled is bool) || !(bool)enabled) continue;
                    object trustedValue; bool trusted = row.TryGetValue("trusted", out trustedValue) && trustedValue is bool && (bool)trustedValue;
                    object candidateOnly;
                    // candidateOnly stays out of automatic repair unless the list is explicitly trusted
                    // (an author-verified local list). Untrusted reference lists remain inspection-only.
                    if (row.TryGetValue("candidateOnly", out candidateOnly) && candidateOnly is bool && (bool)candidateOnly && options.Mode != IptvScanMode.InspectCandidates && !trusted) continue;
                    if (++sourceCount > 32) { plan.Warnings.Add("配置源超过32个，已限制抓取"); break; }
                    try {
                        string source = SourceContent(row, sourcesPath, context);
                        plan.CandidateListsLoaded++;
                        string note = Value(row, "note");
                        if (!String.IsNullOrWhiteSpace(note)) plan.Warnings.Add("候选来源提示：" + Value(row, "name") + "：" + note);
                        foreach (Entry candidate in Parse(Regex.Split(source, "\r\n|\n|\r"), standards)) {
                            if (candidate.Standard != null && needed.Contains(candidate.Standard) && !candidate.Protected && !occupied.Contains(candidate.Url) && seen.Add(candidate.Url)) {
                                candidate.Source = Value(row, "name"); candidate.Trusted = trusted; candidates.Add(candidate);
                            }
                        }
                    } catch (OperationCanceledException) { throw; } catch (BudgetExpired) { throw; }
                      catch (Exception ex) { context.Check(); plan.Warnings.Add("配置源抓取失败：" + Value(row, "name") + " " + ex.Message); }
                    if (progress != null) progress("来源 " + sourceCount + "：" + Value(row, "name") + " · 候选累计 " + candidates.Count + " 条");
                }
                plan.CandidatesFound = candidates.Count;
                if (progress != null) progress("候选去重后 " + candidates.Count + " 条（不含现用地址）；每频道最多检测 " + Math.Max(0, options.MaxCandidatesPerChannel) + " 条");
                int tested = 0; var perChannel = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Entry old in broken) {
                    context.Check();
                    foreach (Entry candidate in candidates) {
                        if (candidate.Standard != old.Standard || occupied.Contains(candidate.Url)) continue;
                        // A healthy source may only be upgraded across origins when the candidate comes from
                        // an author-verified trusted list; otherwise the old safety rule still applies.
                        if (options.Mode != IptvScanMode.InspectCandidates && probes[old.Url].Healthy && !candidate.Trusted && !UpgradeOriginAllowed(old.Url, candidate.Url, options)) { WarnOrigin(plan, old.Standard); continue; }
                        int count; perChannel.TryGetValue(old.Standard, out count);
                        if (tested >= Math.Max(0, options.MaxCandidatesTotal) || count >= Math.Max(0, options.MaxCandidatesPerChannel)) break;
                        Probe probe;
                        if (!probes.TryGetValue(candidate.Url, out probe)) {
                            tested++; plan.CandidatesTested = tested; perChannel[old.Standard] = count + 1;
                            if (progress != null) progress("候选 " + tested + "/" + Math.Max(1, options.MaxCandidatesTotal) + "：" + old.Standard + " 正在探测替代源…");
                            Stopwatch candidateWatch = Stopwatch.StartNew();
                            int savedTimeout = context.Options.RequestTimeoutMs;
                            if (context.Options.CandidateTimeoutMs > savedTimeout) context.Options.RequestTimeoutMs = context.Options.CandidateTimeoutMs;
                            try { probe = ProbeUrl(candidate.Url, context); } finally { context.Options.RequestTimeoutMs = savedTimeout; }
                            probes[candidate.Url] = probe; candidateWatch.Stop();
                            if (progress != null) progress("候选 " + tested + "：" + old.Standard + " → " + Clean(probe) + " · " + candidateWatch.ElapsedMilliseconds + "毫秒");
                            plan.Results.Add(Result(old.Standard, candidate.Url, probe, true, candidate.Source));
                        }
                        if (options.Mode == IptvScanMode.InspectCandidates) continue;
                        if (!probe.Healthy) continue;
                        Probe previous = probes[old.Url];
                        bool upgrade = previous.Healthy;
                        if (upgrade && !candidate.Trusted && !UpgradeOriginAllowed(previous.MediaUrl ?? old.Url, probe.MediaUrl ?? candidate.Url, options)) { WarnOrigin(plan, old.Standard); continue; }
                        if (!upgrade && !SameOrigin(old.Url, probe.MediaUrl ?? candidate.Url)) plan.Warnings.Add(old.Standard + "：异常源使用跨origin修复候选，地域/网络路径未经验证，必须人工确认");
                        // A higher resolution is worthless when the link cannot keep up: measured speeds
                        // of 0.2x real time are 1080p on paper and unwatchable on the television. Unknown
                        // speed is tolerated (offline fixtures), measured-slow is never proposed.
                        bool fastEnough = probe.Speed <= 0 || probe.Smooth;
                        if (upgrade && !(previous.Decoded && probe.Hd && fastEnough && probe.Height > previous.Height && probe.DisplayWidth >= previous.DisplayWidth)) continue;
                        // Never promote a duplicate backup or move/downgrade a healthy old primary.
                        lines[old.Line] = candidate.Url; occupied.Add(candidate.Url);
                        reasons[old.Line] = upgrade ? "旧源已解码SD；候选HLS及解码通过，可信HD升级 " + previous.Width + "x" + previous.Height + " → " + probe.Width + "x" + probe.Height : "旧源复检异常；候选HLS滚动/分片通过；" + (probe.Decoded ? "已验证抽样解码" : "画质未知");
                        break;
                    }
                }
                context.Check();
                if (plan.CandidatesFound > plan.CandidatesTested) plan.Warnings.Add("部分候选未检测（数量上限、同源保护或已找到替代）；未测不代表不可用，可只勾一个频道再查看候选。");
                if (options.Mode == IptvScanMode.InspectCandidates) {
                    plan.Summary = "候选检测完成：去重后 " + plan.CandidatesFound + " 条，已检测 " + plan.CandidatesTested + " 条；未选择或替换现用源";
                    return plan;
                }
                if (progress != null) progress("核对结果并生成预览…");
                foreach (Entry entry in broken) if (lines[entry.Line] == entry.Url) plan.Warnings.Add(entry.Standard + "：未找到合格替代源，保留原槽待修/待优选");
                BuildPlan(plan, playlistText, lines, originalLines, entries, reasons);
            } catch (OperationCanceledException) {
                plan.Cancelled = true; plan.Incomplete = true; plan.Summary = "已取消，原列表保持不变";
            } catch (BudgetExpired) {
                // Keep whatever was already found: a partial replacement list is still useful.
                plan.Incomplete = true;
                if (buildPartial != null) { try { buildPartial(); } catch { } }
                plan.RequiresConfirmation = plan.Changes.Count > 0;
                plan.Summary = plan.Changes.Count > 0
                    ? "预算用完：已找到 " + plan.Changes.Count + " 个可用替代，其余频道保持原样（仍可应用）"
                    : "预算用完，未找到可用替代；原列表保持不变";
            } catch (Exception ex) {
                plan.Incomplete = true; plan.Summary = "检测未完成，原列表保持不变"; plan.Warnings.Add(ex.GetType().Name + ": " + ex.Message);
            }
            if (plan.Cancelled || (plan.Incomplete && plan.Changes.Count == 0)) { plan.M3u = playlistText; plan.Changes.Clear(); plan.RequiresConfirmation = false; }
            return plan;
        }
    }
}
