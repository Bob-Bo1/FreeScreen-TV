using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TvAssistant {
    public sealed class MaintenanceSettings {
        public string TvIp = "192.168.";
        public string LastPlaylistUrl = "";
        public string EpgUrl = "https://epg.pw/xmltv/epg_CN.xml.gz";
        public bool CorrectKnownEpgPwUtc = true;
        public bool AutoEpg = false;
        public string LastEpgAttemptUtc = "";
        public static MaintenanceSettings Load(string root) {
            string path = Path.Combine(root,"直播源","助手设置.json");
            if (!File.Exists(path)) return new MaintenanceSettings();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("助手设置文件过大，请检查文件。");
            return new JavaScriptSerializer().Deserialize<MaintenanceSettings>(File.ReadAllText(path,Encoding.UTF8)) ?? new MaintenanceSettings();
        }
        public void Save(string root) {
            string directory = Path.Combine(root,"直播源"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,"助手设置.json"), temp = path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class MaintenanceForm : Form {
        readonly string root;
        readonly IptvStorage storage;
        readonly IptvScanner scanner;
        readonly TextBox output = new TextBox();
        readonly CheckedListBox channels = new CheckedListBox();
        readonly TextBox epgUrl = new TextBox();
        readonly CheckBox autoEpg = new CheckBox(), correctUtc = new CheckBox();
        readonly Label state = new Label();
        readonly ListBox options = new ListBox();
        readonly Label pickHint = new Label();
        sealed class OptionRow {
            public string Channel, Url, CurrentUrl, Text;
            public bool IsCurrent, Smooth, IsHd;
            public double Speed;
            public override string ToString() { return Text; }
        }
        readonly List<OptionRow> optionRows = new List<OptionRow>();
        IptvScanPlan lastPlan;
        readonly List<Button> commands = new List<Button>();
        Button apply, cancel;
        CancellationTokenSource cancellation;
        IptvStoredState captured;
        string stagedPlaylist, reason;
        byte[] stagedEpg;
        bool busy;
        public bool DataChanged { get; private set; }
        public MaintenanceForm(string packageRoot) : this(packageRoot, new IptvHttpTransport()) { }
        // The same UI/RunScan path can be tested with deterministic HLS without public traffic.
        public MaintenanceForm(string packageRoot, IIptvScanTransport transport) {
            root=packageRoot; storage=new IptvStorage(Path.Combine(root,"直播源")); scanner=new IptvScanner(transport);
            Text="直播维护 · 先预览，再应用"; ClientSize=new Size(924,661); AutoScaleMode=AutoScaleMode.None;
            ticker.Tick += (s,e) => {
                if(!busy)return; int sec=(int)(DateTime.UtcNow-startedAt).TotalSeconds;
                string head=String.IsNullOrEmpty(lastStatus)?"检测中":lastStatus;
                string count=scanTotal>0?"已完成 "+scanDone+"/"+scanTotal+" · ":"";
                state.Text=head+" · "+count+"已用 "+sec+" 秒";
            };
            StartPosition=FormStartPosition.CenterParent; Font=new Font("Microsoft YaHei UI",9);
            BackColor=Color.FromArgb(245,247,250);
            var layout=new TableLayoutPanel { ColumnCount=1,RowCount=8 };
            for(int i=0;i<layout.RowCount;i++)
                layout.RowStyles.Add(i==4?new RowStyle(SizeType.Percent,100):new RowStyle(SizeType.Absolute));
            var viewport=new Panel { Dock=DockStyle.Fill,AutoScroll=true };
            viewport.Controls.Add(layout);
            Controls.Add(viewport);
            var intro=new Label {Text="勾选频道后，点①检测现用源和候选源，选中候选再应用。② 可检测更多候选。\n不勾选时，① 体检全部频道并为需要换源的频道查找候选。",Dock=DockStyle.Fill};
            layout.Controls.Add(intro,0,0);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true}; layout.Controls.Add(actions,0,1);
            AddCommand(actions,"① 一键体检并换源",()=>DetectProblems());
            AddCommand(actions,"② 查看更多候选源",()=>InspectCandidates());
            AddCommand(actions,"更新节目表",()=>RunEpg());
            AddCommand(actions,"恢复上一版",()=>Restore());
            AddCommand(actions,"验证电视刷新",()=>PreviewRefreshTest());
            var urlRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1};
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,95)); urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,88));
            urlRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            urlRow.Controls.Add(new Label {Text="节目表网址",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft});
            epgUrl.Anchor=AnchorStyles.Left|AnchorStyles.Right;
            urlRow.Controls.Add(epgUrl);
            var save=new Button {Text="保存设置",Dock=DockStyle.Fill}; save.Click+=(s,e)=>SaveSettings(true); commands.Add(save); urlRow.Controls.Add(save); layout.Controls.Add(urlRow,0,2);
            var settingsRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};
            autoEpg.Text="助手运行时按需自动更新节目表"; autoEpg.AutoSize=true;
            correctUtc.Text="按北京时间校正（epg.pw 来源自动开启）"; correctUtc.AutoSize=true;
            settingsRow.Controls.Add(autoEpg); settingsRow.Controls.Add(correctUtc); layout.Controls.Add(settingsRow,0,3);
            var splitPane=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2};
            splitPane.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210)); splitPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            channels.Dock=DockStyle.Fill; channels.CheckOnClick=true; channels.IntegralHeight=false; channels.HorizontalScrollbar=true; channels.BackColor=Color.White;
            splitPane.Controls.Add(channels,0,0);
            output.Multiline=true; output.ReadOnly=true; output.ScrollBars=ScrollBars.Vertical; output.Dock=DockStyle.Fill; output.BackColor=Color.White;
            splitPane.Controls.Add(output,1,0); layout.Controls.Add(splitPane,0,4);
            var pick=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
            pick.RowStyles.Add(new RowStyle(SizeType.Absolute,22));
            pick.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            pickHint.Dock=DockStyle.Fill; pickHint.AutoEllipsis=true; pickHint.TextAlign=ContentAlignment.MiddleLeft; pickHint.Text="可选源：先点【①】或【②】检测，再在这里选一个，然后点【应用预览结果】。";
            pick.Controls.Add(pickHint,0,0);
            options.Dock=DockStyle.Fill; options.IntegralHeight=false; options.BackColor=Color.White;
            options.SelectedIndexChanged+=(s,e)=>OnOptionPicked();
            pick.Controls.Add(options,0,1);
            layout.Controls.Add(pick,0,5);
            state.Dock=DockStyle.Fill; state.AutoEllipsis=true; state.TextAlign=ContentAlignment.MiddleLeft; layout.Controls.Add(state,0,6);
            var footer=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
            var close=new Button {Text="关闭",Width=90,Height=32}; close.Click+=(s,e)=>Close();
            cancel=new Button {Text="取消检测",Width=100,Height=32,Enabled=false}; cancel.Click+=(s,e)=>{ if(cancellation!=null)cancellation.Cancel(); state.Text="正在取消；不会应用未完成结果。"; };
            apply=new Button {Text="应用预览结果",Width=140,Height=32,Enabled=false}; apply.Click+=(s,e)=>Apply();
            footer.Controls.Add(close); footer.Controls.Add(cancel); footer.Controls.Add(apply); layout.Controls.Add(footer,0,7);
            // Fonts already follow the window DPI. Size the layout once from logical pixels
            // and measure wrapping at the available width after the bottom menu is docked.
            double dpiScale=1;
            Func<int,int> pixels=logical=>(int)Math.Round(logical*dpiScale);
            Func<Label,int,int> labelHeight=(label,width)=>label.GetPreferredSize(new Size(width,0)).Height+pixels(6);
            Action arrange=()=>{
                int width=Math.Max(viewport.ClientSize.Width,pixels(480));
                int rowWidth=width-pixels(36)-12;
                layout.SuspendLayout();
                layout.Padding=new Padding(pixels(18));
                foreach(Control button in actions.Controls)button.Size=new Size(pixels(155),pixels(33));
                close.Size=new Size(pixels(90),pixels(32));
                cancel.Size=new Size(pixels(100),pixels(32));
                apply.Size=new Size(pixels(140),pixels(32));
                urlRow.ColumnStyles[0].Width=pixels(95);
                urlRow.ColumnStyles[2].Width=pixels(88);
                splitPane.ColumnStyles[0].Width=pixels(210);
                pick.RowStyles[0].Height=Math.Max(labelHeight(pickHint,rowWidth),pickHint.Font.Height*2+pixels(6));
                layout.RowStyles[0].Height=labelHeight(intro,rowWidth);
                layout.RowStyles[1].Height=actions.GetPreferredSize(new Size(rowWidth,0)).Height+pixels(6);
                // The button fills this row, so its current height must never determine the row height.
                layout.RowStyles[2].Height=Math.Max(pixels(40),epgUrl.PreferredHeight+urlRow.Margin.Vertical+epgUrl.Margin.Vertical);
                layout.RowStyles[3].Height=settingsRow.GetPreferredSize(new Size(rowWidth,0)).Height+pixels(6);
                layout.RowStyles[5].Height=pick.RowStyles[0].Height+pixels(92);
                layout.RowStyles[6].Height=state.Font.Height*2+pixels(6);
                layout.RowStyles[7].Height=pixels(44);
                int minimumHeight=layout.Padding.Vertical+pixels(140)+
                    (int)layout.RowStyles.Cast<RowStyle>().Where(row=>row.SizeType==SizeType.Absolute).Sum(row=>row.Height);
                layout.Size=new Size(width,Math.Max(viewport.ClientSize.Height,minimumHeight));
                layout.ResumeLayout(true);
            };
            viewport.Resize+=(s,e)=>arrange();
            Load+=(s,e)=>{
                using(var graphics=CreateGraphics())dpiScale=graphics.DpiX/96.0;
                ClientSize=new Size(pixels(924),pixels(661));
                WindowFit.Apply(this);
                arrange();
            };
            DpiChanged+=(s,e)=>{
                dpiScale=e.DeviceDpiNew/96.0;
                BeginInvoke(arrange);
            };
            try { var config=MaintenanceSettings.Load(root); epgUrl.Text=config.EpgUrl; autoEpg.Checked=config.AutoEpg; }
            catch(Exception e) { Append("设置读取失败："+e.Message); epgUrl.Text="https://epg.pw/xmltv/epg_CN.xml.gz"; }
            // 时区校正跟随来源自动决定：epg.pw 始终按北京时间校正，其他来源一律不校正，不再让用户勾选。
            correctUtc.Enabled=false;
            SyncUtcOption();
            epgUrl.TextChanged+=(s,e)=>SyncUtcOption();
            FormClosing+=(s,e)=>{ if(busy) { e.Cancel=true; if(cancellation!=null)cancellation.Cancel(); state.Text="正在停止任务，请待任务结束后关闭。"; } };
            Shown+=(s,e)=>{ ShowCurrentState(); LoadChannels(); };
        }
        void LoadChannels() {
            try {
                var current=storage.Read(); if(String.IsNullOrWhiteSpace(current.PlaylistText))return;
                channels.Items.Clear();
                foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(current.PlaylistText,@"(?m)^#EXTINF:[^\r\n]*,(.+)$")) {
                    string name=match.Groups[1].Value.Trim(); if(name.Length>0 && !channels.Items.Contains(name)) channels.Items.Add(name,false);
                }
            } catch { }
        }
        string[] PickedChannels() {
            var picked=new List<string>();
            for(int i=0;i<channels.Items.Count;i++) if(channels.GetItemChecked(i)) picked.Add(channels.Items[i].ToString());
            return picked.Count==0 ? null : picked.ToArray();
        }
        void DetectProblems() {
            if(busy)return;
            var picked=PickedChannels();
            RunScan(picked==null?IptvScanMode.OptimizeHd:IptvScanMode.InspectCandidates,picked);
        }
        void InspectCandidates() {
            if(busy)return;
            var picked=PickedChannels();
            if(picked==null) { state.Text="先在左侧勾选要查看候选的频道（建议一次一个）。只检测，不替换现用源。"; return; }
            RunScan(IptvScanMode.InspectCandidates,picked,true);
        }
        void RepairSelected() {
            if(busy)return;
            var picked=PickedChannels();
            if(picked==null) { state.Text="先在左侧勾选要换源的频道；可先点“① 检测问题”自动勾出异常的台。"; return; }
            RunScan(IptvScanMode.Repair,picked);
        }
        void MarkProblemChannels(IptvScanPlan plan) {
            if(plan==null||plan.Results==null)return;
            for(int i=0;i<channels.Items.Count;i++) channels.SetItemChecked(i,false);
            foreach(var row in plan.Results) {
                if(!row.Detail.StartsWith("DEAD")) continue;
                for(int i=0;i<channels.Items.Count;i++) {
                    string name=channels.Items[i].ToString();
                    if(name==row.Channel || name.StartsWith(row.Channel) || row.Channel.StartsWith(name)) { channels.SetItemChecked(i,true); break; }
                }
            }
        }
        void AddCommand(Control parent,string text,Action run) { var button=new Button {Text=text,Width=155,Height=33}; button.Click+=(s,e)=>run(); commands.Add(button); parent.Controls.Add(button); }
        readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer { Interval = 1000 };
        DateTime startedAt; string lastStatus = ""; int scanDone, scanTotal;
        static readonly System.Text.RegularExpressions.Regex CounterPattern = new System.Text.RegularExpressions.Regex(@"^\[(\d+)/(\d+)\]");
        void Report(string message) {
            if(IsDisposed)return;
            if(InvokeRequired) { try { if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action<string>(Report),message); } catch(ObjectDisposedException){} catch(InvalidOperationException){} return; }
            if(state==null||state.IsDisposed)return;
            Append(message);
            var counter=CounterPattern.Match(message);
            if(counter.Success) { int.TryParse(counter.Groups[1].Value,out scanDone); int.TryParse(counter.Groups[2].Value,out scanTotal); }
            if(message.StartsWith("[")||message.StartsWith("候选")||message.StartsWith("并行")||message.StartsWith("开始为")||message.StartsWith("核对")||message.StartsWith("来源")||message.StartsWith("已解析")||message.StartsWith("(")) lastStatus=message;
        }
        void Append(string message) {
            if(IsDisposed||output==null||output.IsDisposed)return;
            if(InvokeRequired) { try { if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action<string>(Append),message); } catch(ObjectDisposedException){} catch(InvalidOperationException){} return; }
            try {
                output.AppendText(message+Environment.NewLine);
                output.SelectionStart=output.TextLength; output.ScrollToCaret();
            } catch(ObjectDisposedException) { } catch(InvalidOperationException) { }
        }
        void ShowCurrentState() {
            try {
                var current=storage.Read();
                if(String.IsNullOrWhiteSpace(current.PlaylistText)) { state.Text="请先返回主窗口导入播放列表。"; return; }
                var info=IptvEpg.Analyze(current.PlaylistText,current.EpgBytes,DateTimeOffset.Now);
                state.Text=info.Summary;
                if(info.NeedsRefresh)Append("节目表部分频道缺少当前或未来节目，建议更新。");
                foreach(string warning in info.Warnings.Take(12))Append(warning);
                bool hasProbe=File.Exists(Path.Combine(root,"files","tools","ffprobe.exe"));
                bool hasDecode=File.Exists(Path.Combine(root,"files","tools","ffmpeg.exe"));
                Append(hasProbe && hasDecode ? "已检测到深度检测工具。测试结果仍不等于电视端实际播放验收。" : "当前为轻量检测：不保证解码/画幅正确，不会把未知画质称为高清。高清检测需可信来源的 ffprobe.exe 和 ffmpeg.exe 放入 files\\tools。");
                Append("电脑端检测仅代表本机网络；先保留电视现有配置，勿清除应用数据。");
            } catch(Exception e) { state.Text="读取失败"; Append(e.Message); }
        }
        static bool IsKnownEpgPwSource(string url) {
            Uri uri; if(!Uri.TryCreate((url??"").Trim(),UriKind.Absolute,out uri)) return false;
            return String.Equals(uri.Host,"epg.pw",StringComparison.OrdinalIgnoreCase);
        }
        void SyncUtcOption() {
            bool known=IsKnownEpgPwSource(epgUrl.Text);
            correctUtc.Checked=known;
            correctUtc.Text=known ? "按北京时间校正（epg.pw 来源自动开启）" : "按北京时间校正（仅 epg.pw 来源启用）";
        }
        bool SaveSettings(bool notify) {
            try {
                Uri uri; if(!Uri.TryCreate(epgUrl.Text.Trim(),UriKind.Absolute,out uri) || (uri.Scheme!="http" && uri.Scheme!="https"))throw new InvalidDataException("请输入有效的 HTTP/HTTPS 节目表网址。");
                if(correctUtc.Checked && !String.Equals(uri.Host,"epg.pw",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("北京时间错标校正仅允许用于已确认的 epg.pw 来源。其他来源保留原始时区。");
                var settings=MaintenanceSettings.Load(root); settings.EpgUrl=uri.AbsoluteUri; settings.AutoEpg=autoEpg.Checked; settings.CorrectKnownEpgPwUtc=correctUtc.Checked; settings.Save(root);
                if(notify)Append("设置已保存。自动更新仅在助手运行时进行；不会安装计划任务或后台常驻。"); return true;
            } catch(Exception e) { MessageBox.Show(this,e.Message,"设置未保存",MessageBoxButtons.OK,MessageBoxIcon.Warning); return false; }
        }
        void Start(string label, string hint = null) {
            if(IsDisposed)return;
            busy=true; apply.Enabled=false; stagedPlaylist=null; stagedEpg=null; reason=null; captured=null;
            output.Clear(); foreach(var button in commands)button.Enabled=false;
            options.SelectedIndexChanged-=OptionChanged; options.Items.Clear(); optionRows.Clear(); options.SelectedIndexChanged+=OptionChanged;
            pickHint.Text=hint ?? "检测中，稍后会在这里列出可选的源。";
            epgUrl.Enabled=autoEpg.Enabled=correctUtc.Enabled=false; cancel.Enabled=true;
            cancellation=new CancellationTokenSource(); state.Text=label;
            lastStatus=label; startedAt=DateTime.UtcNow; scanDone=0; scanTotal=0; ticker.Start();
        }
        void End() {
            busy=false; ticker.Stop();
            if(IsDisposed)return;
            foreach(var button in commands)button.Enabled=true;
            epgUrl.Enabled=autoEpg.Enabled=correctUtc.Enabled=true; cancel.Enabled=false; ticker.Stop();
            if(cancellation!=null) { cancellation.Dispose(); cancellation=null; }
        }
        public static string BuildRefreshTest(string text) {
            if(String.IsNullOrWhiteSpace(text))throw new InvalidDataException("请先导入播放列表。");
            if(text.Contains("[刷新验证]"))throw new InvalidDataException("列表已有刷新验证标记，请先恢复上一版。");
            var match=Regex.Match(text,@"(?m)^#EXTINF:[^\r\n]*");
            if(!match.Success)throw new InvalidDataException("没有可用于验证的频道。");
            bool quoted=false; int comma=-1;
            for(int i=0;i<match.Value.Length;i++) {if(match.Value[i]=='\"')quoted=!quoted;else if(match.Value[i]==',' && !quoted){comma=i;break;}}
            if(comma<0)throw new InvalidDataException("频道名称格式不完整。");
            return text.Substring(0,match.Index)+match.Value+" [刷新验证]"+text.Substring(match.Index+match.Length);
        }
        void PreviewRefreshTest() {
            Start("准备订阅刷新验证预览。","正在准备“验证电视刷新”预览；本操作不检测候选源。");
            try {
                captured=storage.Read(); stagedPlaylist=BuildRefreshTest(captured.PlaylistText); stagedEpg=captured.EpgBytes; reason="验证电视订阅刷新"; apply.Enabled=true;
                Append("只给第一个频道的显示名加上 [刷新验证]，播放地址、频道ID和节目表均不变。");
                Append("点击应用后，在电视刷新原订阅。如果能看到标记，就证明该入口会重新读取同一网址。");
                Append("请保持本窗口打开，验证后立即点击恢复上一版并再次刷新电视。此操作不会自动连接电视。");
                state.Text="仅预览，文件尚未修改。应用时将自动备份。";
                pickHint.Text="预览已生成：只改第一个频道的显示名；应用后去电视刷新验证，再点【恢复上一版】还原。";
            } catch(Exception e) {state.Text="无法准备验证";pickHint.Text="无法准备验证，未生成预览。";Append(e.Message);}
            finally {End();}
        }
        static string ProbeStatus(IptvProbeResult row) {
            if(!String.IsNullOrEmpty(row.Status))return row.Status;
            string detail=row.Detail ?? "";
            if(detail.StartsWith("LIVE"))return "LIVE";
            if(detail.StartsWith("DEAD"))return "DEAD";
            return "UNKNOWN";
        }
        static string PlainGrade(IptvProbeResult row) {
            if(row.Width<=0 || row.Height<=0)return "清晰度未知";
            string size=row.Width+"×"+row.Height;
            return size+" "+(row.HdBySize?"高清":"标清");
        }
        static string PlainState(IptvProbeResult row) {
            if(ProbeStatus(row)!="LIVE")return "这次没连上";
            if(!row.Decoded)return "能连上";
            if(row.Speed<=0)return "可用";
            return row.Smooth?"流畅":"会卡";
        }
        static string PlainSpeed(IptvProbeResult row) {
            if(row.Speed<=0)return "";
            return " · 速度 "+row.Speed.ToString("0.0")+"×";
        }
        static string HostOf(string url) {
            try { var uri=new Uri(url); return uri.IsDefaultPort?uri.Host:(uri.Host+":"+uri.Port); } catch { return url; }
        }
        // The output pane only summarises. The actual choices live in the picker list below, so the
        // user always knows which source "应用" would use.
        void ShowScanResults(IptvScanPlan plan) {
            int usable=plan.Results.Count(row=>ProbeStatus(row)=="LIVE");
            int total=plan.Results.Count;
            if(total==0) { Append(plan.Summary); return; }
            Append((plan.Cancelled?"已取消":(plan.Incomplete?"未全部测完":"体检完成"))+"：共 "+total+" 路源，可用 "+usable+" 路。");
            foreach(string channel in plan.Results.Select(row=>row.Channel).Distinct()) {
                var rows=plan.Results.Where(row=>row.Channel==channel).ToList();
                int channelUsable=rows.Count(row=>ProbeStatus(row)=="LIVE");
                Append("  "+channel+"：可用 "+channelUsable+" / "+rows.Count+" 路");
            }
            Append("");
            Append("电脑端检测，不等于电视一定能播；电视上看得顺就保留现用。");
            int candidateLive=plan.Results.Count(row=>row.IsCandidate && ProbeStatus(row)=="LIVE");
            if(plan.Cancelled || plan.Incomplete) { Append(plan.Summary); return; }
            if(!plan.CandidateSearchStarted) {
                Append("已检查现用源。本轮没有需要自动换源的频道，可勾选频道后点②比较候选源。");
            } else if(plan.CandidateListsLoaded==0) {
                Append("未能读取候选清单，请检查网络及 files/sources.json。");
            } else if(plan.CandidatesFound==0) {
                Append("已读取 "+plan.CandidateListsLoaded+" 份候选清单，没有找到与待修频道匹配的新地址。");
            } else {
                Append("找到 "+plan.CandidatesFound+" 路候选，已检测 "+plan.CandidatesTested+" 路，可用 "+candidateLive+" 路。");
            }
        }
        // Every usable source becomes a selectable row. Nothing is applied until the user picks one.
        void FillOptions(IptvScanPlan plan) {
            options.SelectedIndexChanged-=OptionChanged;
            options.Items.Clear(); optionRows.Clear();
            foreach(string channel in plan.Results.Select(row=>row.Channel).Distinct()) {
                // The current line is always listed (even when it failed) so its URL can be replaced.
                var current=plan.Results.FirstOrDefault(row=>!row.IsCandidate && row.Channel==channel);
                var candidates=plan.Results.Where(row=>row.IsCandidate && row.Channel==channel && ProbeStatus(row)=="LIVE").ToList();
                if(current==null && candidates.Count==0)continue;
                if(current!=null) {
                    optionRows.Add(new OptionRow { Channel=channel, Url=current.Url, CurrentUrl=current.Url, IsCurrent=true,
                        Smooth=current.Smooth, IsHd=current.HdBySize, Speed=current.Speed,
                        Text=channel+"　·　现用　"+PlainGrade(current)+"　·　"+PlainState(current)+PlainSpeed(current) });
                }
                foreach(var candidate in candidates) {
                    if(current!=null && candidate.Url==current.Url)continue;
                    optionRows.Add(new OptionRow { Channel=channel, Url=candidate.Url, CurrentUrl=current==null?null:current.Url, IsCurrent=false,
                        Smooth=candidate.Smooth, IsHd=candidate.HdBySize, Speed=candidate.Speed,
                        Text=channel+"　·　"+HostOf(candidate.Url)+"　"+PlainGrade(candidate)+"　·　"+PlainState(candidate)+PlainSpeed(candidate) });
                }
            }
            foreach(var row in optionRows)options.Items.Add(row);
            options.SelectedIndexChanged+=OptionChanged;
            if(optionRows.Count==0) {
                pickHint.Text="没有可用源可选；现用保持不变。";
                apply.Enabled=false;
                return;
            }
            apply.Enabled=false;
            int smoothCount=optionRows.Count(row=>row.Smooth && !row.IsCurrent);
            pickHint.Text="可选源 "+optionRows.Count+" 路（流畅 "+smoothCount+" 路）：选一个，再点【应用预览结果】。";
            // Preselect the fastest HD option: a source that cannot keep up with real time will stutter
            // even though it is genuinely 1080p, so smoothness outranks resolution.
            var best=optionRows.Where(row=>!row.IsCurrent)
                .OrderByDescending(row=>row.Smooth).ThenByDescending(row=>row.IsHd).ThenByDescending(row=>row.Speed)
                .FirstOrDefault();
            options.SelectedItem=best ?? optionRows[0];
        }
        void OptionChanged(object sender,EventArgs e) { OnOptionPicked(); }
        void OnOptionPicked() {
            var row=options.SelectedItem as OptionRow;
            if(row==null || captured==null) { apply.Enabled=false; return; }
            if(row.IsCurrent) {
                stagedPlaylist=captured.PlaylistText; stagedEpg=captured.EpgBytes;
                apply.Enabled=false;
                pickHint.Text="你选的是现用源，不需要应用。";
                state.Text="现用保持不变。";
                return;
            }
            if(String.IsNullOrEmpty(row.CurrentUrl)) {
                apply.Enabled=false;
                pickHint.Text=row.Channel+"：现用源已失效，需先在列表里另选；暂不支持自动补位。";
                return;
            }
            string updated=ReplaceFirstUrl(captured.PlaylistText,row.CurrentUrl,row.Url);
            if(updated==captured.PlaylistText) { apply.Enabled=false; pickHint.Text="没找到现用地址，未生成变更。"; return; }
            stagedPlaylist=updated; stagedEpg=captured.EpgBytes; reason="换成 "+row.Text;
            apply.Enabled=true;
            if(row.Speed>0 && !row.Smooth) {
                pickHint.Text="已选择："+row.Text+"　⚠ 这路实测只有 "+row.Speed.ToString("0.0")+"× 实时速度，电视上会卡；建议换一路流畅的。";
                state.Text="注意：所选源速度不足，可能卡顿。";
                return;
            }
            pickHint.Text="已选择："+row.Text+"　→ 点【应用预览结果】生效。";
            state.Text="已选择 1 路源，尚未写入文件。";
        }
        static string ReplaceFirstUrl(string playlist,string oldUrl,string newUrl) {
            if(String.IsNullOrEmpty(playlist) || String.IsNullOrEmpty(oldUrl))return playlist;
            int at=playlist.IndexOf(oldUrl,StringComparison.Ordinal);
            if(at<0)return playlist;
            return playlist.Substring(0,at)+newUrl+playlist.Substring(at+oldUrl.Length);
        }
        async void RunScan(IptvScanMode mode, string[] onlyChannels = null, bool moreCandidates = false) {
            Start("检测中，可取消。源站可能冷启动，请耐心等待。");
            try {
                captured=storage.Read(); if(String.IsNullOrWhiteSpace(captured.PlaylistText))throw new InvalidDataException("没有播放列表，请先导入。");
                var options=new IptvScanOptions {Mode=mode,ChannelsPath=Path.Combine(root,"files","channels.json"),SourcesPath=Path.Combine(root,"files","sources.json"),ToolsDirectory=Path.Combine(root,"files","tools"),OnlyChannels=onlyChannels ?? new string[0]};
                options.MaxCandidatesPerChannel=moreCandidates?12:8;
                options.MaxCandidatesTotal=moreCandidates?48:40;
                if(mode==IptvScanMode.InspectCandidates)options.TotalBudgetSeconds=600;
                var token=cancellation.Token;
                var rawLog=new StringBuilder();
                Action<string> onProgress=message => {
                    lock(rawLog) rawLog.AppendLine(message);
                    var counter=CounterPattern.Match(message);
                    if(counter.Success) { int done,total; int.TryParse(counter.Groups[1].Value,out done); int.TryParse(counter.Groups[2].Value,out total); scanDone=done; scanTotal=total; }
                    if(message.StartsWith("来源")||message.StartsWith("已解析")||message.StartsWith("并行")
                        ||message.StartsWith("开始为")||message.StartsWith("候选")) { lastStatus=message; }
                };
                var plan=await Task.Run(()=>scanner.Run(captured.PlaylistText,options,token,onProgress));
                ShowScanResults(plan);
                FillOptions(plan);
                if(mode==IptvScanMode.DetectOnly) MarkProblemChannels(plan);
                try {
                    string detailName="检测详情-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".txt";
                    var technical=new StringBuilder();
                    technical.AppendLine("电脑端检测记录；不等于电视实际播放验收。以下为技术细节，供排查用。");
                    technical.AppendLine();
                    foreach(var row in plan.Results) {
                        technical.AppendLine((row.IsCandidate?"[候选] ":"[现用] ")+row.Channel+"  "+row.Width+"x"+row.Height+"  "+row.Detail);
                        technical.AppendLine("        "+row.Url);
                        technical.AppendLine("        来源："+(String.IsNullOrEmpty(row.Source)?"当前播放列表":row.Source));
                    }
                    foreach(string warning in plan.Warnings)technical.AppendLine("[提示] "+warning);
                    technical.AppendLine();
                    technical.AppendLine("=== 检测过程 ===");
                    technical.Append(rawLog);
                    File.WriteAllText(Path.Combine(root,"直播源",detailName),output.Text+"\r\n\r\n"+technical,new UTF8Encoding(false));
                    Append("详细记录："+detailName+"（技术细节在里面）");
                } catch { }
                if(plan.Cancelled || token.IsCancellationRequested) { state.Text="已取消，旧配置保持不变。"; return; }
                if(plan.Results.Count==0) { state.Text=plan.Summary; return; }
                if(mode==IptvScanMode.InspectCandidates && !optionRows.Any(row=>!row.IsCurrent)) {
                    state.Text="本轮没有通过检测的新候选。";
                    return;
                }
                // Nothing is staged automatically: the user picks the exact source first.
                if(optionRows.Count>0 && this.options.SelectedItem!=null) OnOptionPicked();
                else { apply.Enabled=false; state.Text="没有可用源；现用保持不变。"; }
            } catch(OperationCanceledException) { state.Text="已取消；旧配置保持不变。"; }
            catch(Exception e) { state.Text="检测未完成；旧配置保持不变。"; Append(e.Message); }
            finally { End(); }
        }
        async void RunEpg() {
            if(!SaveSettings(false))return;
            Start("下载和校验整份节目表中（全部频道，与左侧勾选无关）；旧表保持可用。","正在更新整份节目表（全部频道，与左侧勾选无关），不做换源检测。");
            try {
                captured=storage.Read(); var settings=MaintenanceSettings.Load(root); var token=cancellation.Token;
                var preview=await Task.Run(()=>{
                    byte[] data=IptvEpg.Download(new Uri(settings.EpgUrl),token,30000); token.ThrowIfCancellationRequested();
                    return IptvEpg.Prepare(captured.PlaylistText,captured.EpgBytes,data,DateTimeOffset.Now,IsKnownEpgPwSource(settings.EpgUrl));
                });
                token.ThrowIfCancellationRequested();
                foreach(string error in preview.Errors)Append("不能应用："+error);
                foreach(string warning in preview.Warnings)Append("注意："+warning);
                if(preview.Analysis!=null)Append(preview.Analysis.Summary);
                if(!preview.CanApply) { state.Text="新节目表未通过校验，旧文件未被替换。"; pickHint.Text="节目表未通过校验，旧表保留；可稍后重试，或直接关闭。"; return; }
                stagedPlaylist=preview.PlaylistText; stagedEpg=preview.EpgBytes; reason="更新节目表"; apply.Enabled=true;
                state.Text="新节目表已校验，点击应用后生效。";
                pickHint.Text="整份节目表已处理完（与候选源无关）；确认无误后点【应用预览结果】。";
            } catch(OperationCanceledException) { state.Text="已取消，旧节目表未变。"; pickHint.Text="已取消，节目表未变。"; }
            catch(Exception e) { state.Text="更新失败，旧节目表未变。"; pickHint.Text="更新失败，节目表未变；可重试。"; Append(e.Message); }
            finally { End(); }
        }
        async void Apply() {
            if(busy || captured==null || stagedPlaylist==null)return;
            if(MessageBox.Show(this,"将要应用的更改：\n\n"+(reason??"（无）")+"\n\n会先备份当前播放列表和节目表；不会自动操作电视。继续？","确认应用",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            string text=stagedPlaylist, version=captured.Version, label=reason; byte[] bytes=stagedEpg;
            Start("正在备份并应用；请勿关闭助手。","正在备份并写入文件；不涉及候选源。"); cancel.Enabled=false;
            try {
                var snap=await Task.Run(()=>storage.Apply(text,bytes,version,label,CancellationToken.None));
                DataChanged=true; Append("应用成功。备份："+snap.Id);
                try {
                    string exportName="新列表-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".m3u";
                    File.WriteAllText(Path.Combine(root,"直播源",exportName),text,new UTF8Encoding(false));
                    Append("已生成新列表："+exportName+"（发给别人或自行导入都行，网址仍是同一个）");
                } catch(Exception exportError) { Append("新列表导出失败："+exportError.Message); }
 Append("无需更改订阅网址。在电视里更新原播放列表/节目表，再确认实际画面。");
                state.Text="文件已更新；尚未确认电视已刷新或播放成功。";
                pickHint.Text="已写入文件；请在电视上刷新/重新导入原订阅后再确认画面。";
            } catch(Exception e) { state.Text="应用未成功，请查看记录。"; pickHint.Text="应用未成功，文件已回退；请查看记录。"; Append(e.Message); }
            finally { End(); }
        }
        async void Restore() {
            if(busy)return;
            try {
                var latest=storage.GetLatestSnapshot(); if(latest==null) { MessageBox.Show(this,"还没有完整备份。首次更新前会自动创建。","恢复上一版"); return; }
                if(MessageBox.Show(this,"恢复到："+latest.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+"\n说明："+latest.Reason+"\n当前文件也会先备份。不会操作电视。","恢复上一版",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                var current=storage.Read(); Start("正在恢复上一版；请勿关闭助手。","正在恢复上一版；不涉及候选源。"); cancel.Enabled=false;
                try { await Task.Run(()=>storage.RestoreLatest(current.Version,CancellationToken.None)); DataChanged=true; state.Text="已恢复。请在电视刷新原订阅。"; pickHint.Text="已恢复；请在电视上刷新/重新导入原订阅。"; Append("再次恢复上一版，可以撤销此次恢复。"); }
                finally { End(); }
            } catch(Exception e) { state.Text="恢复未成功"; pickHint.Text="恢复未成功，文件未变；请查看记录后重试。"; Append(e.Message); }
        }
        public static string AutoRefreshEpg(string root,CancellationToken token) {
            var settings=MaintenanceSettings.Load(root); if(!settings.AutoEpg)return "";
            DateTimeOffset last; if(DateTimeOffset.TryParse(settings.LastEpgAttemptUtc,out last) && DateTimeOffset.UtcNow-last<TimeSpan.FromHours(24))return "";
            settings.LastEpgAttemptUtc=DateTimeOffset.UtcNow.ToString("o"); settings.Save(root);
            var store=new IptvStorage(Path.Combine(root,"直播源")); var current=store.Read();
            var analysis=IptvEpg.Analyze(current.PlaylistText,current.EpgBytes,DateTimeOffset.Now);
            if(!analysis.NeedsRefresh)return "节目表仍有覆盖，已跳过自动下载。";
            var uri=new Uri(settings.EpgUrl);
            bool correct=String.Equals(uri.Host,"epg.pw",StringComparison.OrdinalIgnoreCase);
            byte[] data=IptvEpg.Download(uri,token,30000); token.ThrowIfCancellationRequested();
            var preview=IptvEpg.Prepare(current.PlaylistText,current.EpgBytes,data,DateTimeOffset.Now,correct);
            if(!preview.CanApply)return "自动节目表更新未通过校验，旧文件保留："+String.Join("；",preview.Errors);
            // Automatic mode is stricter than manual preview: never reduce the number
            // of covered/current/future subscribed channels, even by one.
            if(preview.Analysis.CoveredChannels<analysis.CoveredChannels || preview.Analysis.CurrentChannels<analysis.CurrentChannels || preview.Analysis.FutureChannels<analysis.FutureChannels)
                return "自动节目表更新出现覆盖下降，已保留旧表；请到维护窗口查看。";
            store.Apply(preview.PlaylistText,preview.EpgBytes,current.Version,"自动更新节目表",token);
            return "节目表已自动更新并备份。"+preview.Analysis.Summary+"；电视端刷新待确认。";
        }
    }
}
