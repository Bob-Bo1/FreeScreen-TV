using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace TvAssistant {
    public sealed class IptvStoredState {
        public string PlaylistText { get; internal set; }
        public byte[] EpgBytes { get; internal set; }
        public string Version { get; internal set; }
        public bool PlaylistExists { get; internal set; }
        public bool EpgExists { get; internal set; }
    }
    public sealed class IptvSnapshot {
        public string Id { get; internal set; }
        public string Reason { get; internal set; }
        public DateTimeOffset CreatedUtc { get; internal set; }
        public bool PlaylistExists { get; internal set; }
        public bool EpgExists { get; internal set; }
    }

    // Owns exactly home_ready.m3u + epg.xml.gz. null means an absent file;
    // byte[0]/"" means a present empty file. Keep these states distinct for undo.
    // Each file replace is atomic. The pair is serialized + exception-rollback,
    // NOT a filesystem-wide atomic transaction against an OS/power failure.
    public sealed class IptvStorage {
        readonly string root, playlistPath, epgPath, snapshotsPath, lockPath, journalPath;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        const long MaxStoredBytes = 128L * 1024 * 1024;
        public IptvStorage(string dataDirectory) {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("直播源目录不能为空。", "dataDirectory");
            root = Path.GetFullPath(dataDirectory);
            playlistPath = Path.Combine(root, "home_ready.m3u");
            epgPath = Path.Combine(root, "epg.xml.gz");
            snapshotsPath = Path.Combine(root, "iptv-snapshots");
            lockPath = Path.Combine(root, ".iptv-write.lock");
            journalPath = Path.Combine(root, ".iptv-transaction.pending");
        }
        public IptvStoredState Read() {
            using (Acquire()) {
                Pair pair = ReadPair();
                return new IptvStoredState { PlaylistText = Decode(pair.Playlist), EpgBytes = Copy(pair.Epg),
                    Version = pair.Version, PlaylistExists = pair.Playlist != null, EpgExists = pair.Epg != null };
            }
        }
        public IptvSnapshot Apply(string playlistText, byte[] epgBytes, string expectedVersion, string reason, CancellationToken token) {
            byte[] playlist = playlistText == null ? null : Utf8.GetBytes(playlistText);
            ValidateSize(playlist); ValidateSize(epgBytes);
            using (Acquire()) return Commit(playlist, Copy(epgBytes), expectedVersion, reason ?? "更新", token);
        }
        public IptvSnapshot GetLatestSnapshot() {
            using (Acquire()) {
                SnapshotData latest = Latest();
                return latest == null ? null : latest.Info;
            }
        }
        public IptvSnapshot RestoreLatest(string expectedVersion, CancellationToken token) {
            using (Acquire()) {
                token.ThrowIfCancellationRequested();
                SnapshotData latest = Latest();
                if (latest == null) throw new InvalidOperationException("没有完整且校验通过的恢复快照。");
                // Commit creates a complete snapshot of CURRENT files before restoring.
                // The next RestoreLatest therefore undoes this restore.
                return Commit(latest.Playlist, latest.Epg, expectedVersion,
                    "恢复快照 " + latest.Info.Id, token);
            }
        }
        FileStream Acquire() {
            Directory.CreateDirectory(root);
            FileStream lease;
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { throw new IOException("其他导入/修复/恢复操作正在使用直播源，请稍后重试。", e); }
            try { RecoverPending(); return lease; }
            catch { lease.Dispose(); throw; }
        }
        void RecoverPending() {
            if (!File.Exists(journalPath)) return;
            try {
                if (new FileInfo(journalPath).Length > 1024) throw new InvalidDataException("事务日志过大。");
                string id = File.ReadAllText(journalPath, Utf8).Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{19}-[0-9a-f]{32}$")) throw new InvalidDataException("事务日志快照标识无效。");
                SnapshotData snapshot = LoadSnapshot(Path.Combine(snapshotsPath, id));
                if (snapshot == null) throw new InvalidDataException("事务日志指向的完整快照缺失或已损坏。");
                RestoreBytes(playlistPath, snapshot.Playlist);
                RestoreBytes(epgPath, snapshot.Epg);
                if (Hash(ReadBytes(playlistPath)) != Hash(snapshot.Playlist) || Hash(ReadBytes(epgPath)) != Hash(snapshot.Epg))
                    throw new IOException("事务恢复后校验失败。");
                File.Delete(journalPath);
            } catch (Exception e) {
                throw new IOException("检测到未完成的直播源事务，自动恢复未成功；已保留 .iptv-transaction.pending，停止分发直到恢复完成。", e);
            }
        }
        void BeginJournal(string snapshotId) {
            string temp = Stage(journalPath, Utf8.GetBytes(snapshotId));
            try { File.Move(temp, journalPath); }
            finally { DeleteTemp(temp); }
        }
        sealed class Pair {
            public byte[] Playlist, Epg;
            public string Version;
        }
        sealed class SnapshotData {
            public IptvSnapshot Info;
            public byte[] Playlist, Epg;
        }
        static byte[] Copy(byte[] bytes) { return bytes == null ? null : (byte[])bytes.Clone(); }
        static void ValidateSize(byte[] bytes) {
            if (bytes != null && bytes.LongLength > MaxStoredBytes) throw new InvalidDataException("文件超过存储大小限制。");
        }
        static byte[] ReadBytes(string path) {
            if (!File.Exists(path)) return null;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (input.Length > MaxStoredBytes) throw new InvalidDataException("文件超过存储大小限制。");
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
        static string Decode(byte[] bytes) {
            if (bytes == null) return null;
            int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            return Utf8.GetString(bytes, offset, bytes.Length - offset);
        }
        static string Hash(byte[] bytes) {
            if (bytes == null) return "missing";
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }
        static string Stamp(string path) {
            if (!File.Exists(path)) return "missing";
            var file = new FileInfo(path);
            return file.Length.ToString(CultureInfo.InvariantCulture) + ":" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        Pair ReadPair() {
            string pBefore = Stamp(playlistPath), eBefore = Stamp(epgPath);
            byte[] p = ReadBytes(playlistPath), e = ReadBytes(epgPath);
            string pAfter = Stamp(playlistPath), eAfter = Stamp(epgPath);
            if (pBefore != pAfter || eBefore != eAfter) throw new IOException("读取期间文件发生变化，请重新检查。");
            return new Pair { Playlist = p, Epg = e,
                Version = Hash(Utf8.GetBytes(pAfter + "|" + Hash(p) + "|" + eAfter + "|" + Hash(e))) };
        }
        static void Expect(string actual, string expected) {
            if (String.IsNullOrEmpty(expected)) throw new ArgumentException("必须提供读取/扫描开始时的版本，禁止无条件覆盖。", "expectedVersion");
            if (!String.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("扫描或预览后直播源文件已改变，请重新检查后再应用。");
        }
        IptvSnapshot Commit(byte[] playlist, byte[] epg, string expectedVersion, string reason, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Pair before = ReadPair(); Expect(before.Version, expectedVersion);
            // Prepare BOTH replacement files before touching either live path.
            string pTemp = Stage(playlistPath, playlist), eTemp = null;
            IptvSnapshot snapshot = null;
            bool touched = false;
            try {
                eTemp = Stage(epgPath, epg);
                token.ThrowIfCancellationRequested();
                Expect(ReadPair().Version, expectedVersion);
                snapshot = SaveSnapshot(before.Playlist, before.Epg, reason);
                // Also detects writers that changed files while a snapshot was being made.
                Expect(ReadPair().Version, expectedVersion);
                token.ThrowIfCancellationRequested();
                BeginJournal(snapshot.Id);
                touched = true;
                Replace(playlistPath, pTemp, playlist == null); pTemp = null;
                token.ThrowIfCancellationRequested();
                Replace(epgPath, eTemp, epg == null); eTemp = null;
                token.ThrowIfCancellationRequested();
                // The API only succeeds if both committed bytes exactly match.
                if (Hash(ReadBytes(playlistPath)) != Hash(playlist) || Hash(ReadBytes(epgPath)) != Hash(epg))
                    throw new IOException("提交后文件校验不一致。");
                File.Delete(journalPath);
                return snapshot;
            } catch (Exception failure) {
                if (touched) {
                    var rollbackErrors = new List<Exception>();
                    try { RestoreBytes(playlistPath, before.Playlist); } catch (Exception e) { rollbackErrors.Add(e); }
                    try { RestoreBytes(epgPath, before.Epg); } catch (Exception e) { rollbackErrors.Add(e); }
                    if (rollbackErrors.Count != 0) {
                        rollbackErrors.Insert(0, failure);
                        throw new AggregateException("应用失败且回滚未全部完成。原始两文件完整快照：" + (snapshot == null ? "未知" : snapshot.Id), rollbackErrors);
                    }
                    File.Delete(journalPath);
                }
                throw;
            } finally { DeleteTemp(pTemp); DeleteTemp(eTemp); }
        }
        static string Stage(string target, byte[] bytes) {
            if (bytes == null) return null;
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    output.Write(bytes, 0, bytes.Length); output.Flush(true);
                }
                return temp;
            } catch { DeleteTemp(temp); throw; }
        }
        static void Replace(string target, string temp, bool absent) {
            if (absent) { if (File.Exists(target)) File.Delete(target); return; }
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }
        static void RestoreBytes(string target, byte[] bytes) {
            if (Hash(ReadBytes(target)) == Hash(bytes)) return;
            string temp = Stage(target, bytes);
            try { Replace(target, temp, bytes == null); }
            finally { DeleteTemp(temp); }
        }
        static void DeleteTemp(string path) {
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* Keep original failure; harmless owned temp may remain. */ }
        }
        IptvSnapshot SaveSnapshot(byte[] playlist, byte[] epg, string reason) {
            Directory.CreateDirectory(snapshotsPath);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var info = new IptvSnapshot { Id = now.UtcTicksCompat().ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"),
                Reason = reason, CreatedUtc = now, PlaylistExists = playlist != null, EpgExists = epg != null };
            string directory = Path.Combine(snapshotsPath, info.Id);
            Directory.CreateDirectory(directory);
            if (playlist != null) WriteDurable(Path.Combine(directory, "playlist.bin"), playlist);
            if (epg != null) WriteDurable(Path.Combine(directory, "epg.bin"), epg);
            var document = new XDocument(new XElement("iptvSnapshot", new XAttribute("version", "1"),
                new XAttribute("id", info.Id), new XAttribute("created", now.ToString("o", CultureInfo.InvariantCulture)),
                new XElement("reason", reason),
                Entry("playlist", playlist), Entry("epg", epg)));
            string manifest = Path.Combine(directory, "snapshot.xml");
            string temp = manifest + ".tmp";
            WriteDurable(temp, Utf8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
            // Manifest is published LAST: a missing/incomplete manifest is never a snapshot.
            File.Move(temp, manifest);
            return info;
        }
        static XElement Entry(string name, byte[] bytes) {
            return new XElement(name, new XAttribute("exists", bytes != null), new XAttribute("sha256", Hash(bytes)));
        }
        static void WriteDurable(string path, byte[] bytes) {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
        }
        SnapshotData Latest() {
            if (!Directory.Exists(snapshotsPath)) return null;
            foreach (string directory in Directory.GetDirectories(snapshotsPath).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)) {
                SnapshotData data = LoadSnapshot(directory);
                if (data != null) return data;
            }
            return null;
        }
        static SnapshotData LoadSnapshot(string directory) {
            string manifest = Path.Combine(directory, "snapshot.xml");
            if (!File.Exists(manifest)) return null;
            try {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
                XDocument doc;
                using (XmlReader reader = XmlReader.Create(manifest, settings)) doc = XDocument.Load(reader);
                XElement root = doc.Root;
                if (root == null || root.Name != "iptvSnapshot" || (string)root.Attribute("version") != "1") return null;
                string id = (string)root.Attribute("id");
                if (id != Path.GetFileName(directory)) return null;
                byte[] p = ReadSnapshotFile(directory, root.Element("playlist"), "playlist.bin");
                byte[] e = ReadSnapshotFile(directory, root.Element("epg"), "epg.bin");
                return new SnapshotData { Playlist = p, Epg = e, Info = new IptvSnapshot { Id = id,
                    Reason = (string)root.Element("reason") ?? "", CreatedUtc = DateTimeOffset.Parse((string)root.Attribute("created"), CultureInfo.InvariantCulture),
                    PlaylistExists = p != null, EpgExists = e != null } };
            } catch (IOException) { return null; }
              catch (XmlException) { return null; }
              catch (FormatException) { return null; }
              catch (InvalidDataException) { return null; }
              catch (ArgumentException) { return null; }
        }
        static byte[] ReadSnapshotFile(string directory, XElement entry, string name) {
            if (entry == null) throw new InvalidDataException("快照缺少文件声明。");
            bool exists = Boolean.Parse((string)entry.Attribute("exists"));
            byte[] bytes = exists ? ReadBytes(Path.Combine(directory, name)) : null;
            if ((exists && bytes == null) || Hash(bytes) != (string)entry.Attribute("sha256"))
                throw new InvalidDataException("快照文件校验失败。");
            return bytes;
        }
    }
    internal static class IptvDateCompat {
        internal static long UtcTicksCompat(this DateTimeOffset value) { return value.UtcDateTime.Ticks; }
    }
}
