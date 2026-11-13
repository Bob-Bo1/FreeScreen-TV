using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

namespace TvAssistant {
    sealed class Surface : TableLayoutPanel {
        protected override void OnPaintBackground(PaintEventArgs e) {
            e.Graphics.Clear(Parent==null?SystemColors.Control:Parent.BackColor);
            if(Width<4 || Height<4)return;
            using(var path=RoundedPath(Width-1,Height-1,(int)(12*e.Graphics.DpiX/96f)))
            using(var fill=new SolidBrush(BackColor))
            using(var border=new Pen(Color.FromArgb(224,231,240))) {
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                e.Graphics.FillPath(fill,path);
                e.Graphics.DrawPath(border,path);
            }
        }
        internal static GraphicsPath RoundedPath(int width,int height,int radius) {
            var path=new GraphicsPath();
            int d=Math.Max(1,Math.Min(Math.Min(width,height),radius*2));
            path.AddArc(0,0,d,d,180,90); path.AddArc(width-d,0,d,d,270,90);
            path.AddArc(width-d,height-d,d,d,0,90); path.AddArc(0,height-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }
    }
    class MainWindow : Form {
        readonly Color ink = Color.FromArgb(31, 38, 48), muted = Color.FromArgb(111, 123, 139), blue = Color.FromArgb(36, 91, 184);
        TextBox ip, log, playlist, epg; Label status, serviceStatus, channelsInfo, fileM3u, fileEpg, programInfo; Button install, restore, service, copy, play;
        Button tivimate, sendUrl, homeOnly, chooseM3u;
        ListBox channelList, programList; Label emptyPreview;
        List<Channel> channels = new List<Channel>(); Dictionary<string,List<string>> programmes = new Dictionary<string,List<string>>();
        class Channel { public string Name, Id, Url; public override string ToString() { return Name; } }
        Process operation, server; readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        Button maintenance;
        bool maintenanceBusy, automaticBusy;
        int serverGeneration;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly System.Windows.Forms.Timer epgTimer = new System.Windows.Forms.Timer { Interval=60000 };
        Font FontOf(float size, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", size, style); }
        FlowLayoutPanel ActionRow() {
            return new FlowLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, WrapContents=true, Margin=new Padding(0,2,0,4) };
        }
        Label Caption(string text,float size,bool bold) {
            return new Label { Text=text, AutoSize=true, Font=FontOf(size,bold?FontStyle.Bold:FontStyle.Regular), Margin=new Padding(2,8,10,5), Padding=new Padding(5,0,5,0) };
        }
        Button ActionButton(string text,bool primary=false) {
            var button=new Button { Text=text, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Padding=new Padding(13,6,13,6), Margin=new Padding(0,3,8,3), FlatStyle=FlatStyle.Flat,
                BackColor=primary?blue:Color.FromArgb(235,241,249), ForeColor=primary?Color.White:ink, Font=FontOf(9.5f,primary?FontStyle.Bold:FontStyle.Regular) };
            button.FlatAppearance.BorderSize=0;
            button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(29,76,159):Color.FromArgb(223,234,250);
            Action shape=()=>{
                if(button.Width<2 || button.Height<2)return;
                using(var g=button.CreateGraphics())
                using(var path=Surface.RoundedPath(button.Width,button.Height,(int)(8*g.DpiX/96f))) {
                    var previous=button.Region;
                    button.Region=new Region(path);
                    if(previous!=null)previous.Dispose();
                }
            };
            button.SizeChanged+=(sender,args)=>shape();
            button.EnabledChanged+=(sender,args)=>{
                button.BackColor=button.Enabled?(primary?blue:Color.FromArgb(235,241,249)):Color.FromArgb(241,244,248);
            };
            shape();
            return button;
        }
        Panel Section(Color color) {
            return new Panel { BackColor=color, Padding=new Padding(14,8,14,8) };
        }
        static void CompactRows(Panel section,int width) {
            int innerWidth=Math.Max(1,width-section.Padding.Horizontal);
            int y=section.Padding.Top;
            section.SuspendLayout();
            foreach(Control row in section.Controls) {
                if(!row.Visible)continue;
                row.Dock=DockStyle.None;
                int height=row.GetPreferredSize(new Size(innerWidth,0)).Height;
                row.SetBounds(section.Padding.Left,y,innerWidth,height);
                y+=height+row.Margin.Bottom;
            }
            section.Size=new Size(width,y+section.Padding.Bottom);
            section.ResumeLayout(false);
        }
        public MainWindow(string packageRoot = null) {
            if(packageRoot!=null) root=Path.GetFullPath(packageRoot);
            Text="电视助手"; ClientSize=new Size(1060,720); AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
            Shown+=(s,e)=>WindowFit.Apply(this);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } FormBorderStyle=FormBorderStyle.Sizable; StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(241,245,250); Font=FontOf(9); ForeColor=ink; DoubleBuffered=true;
            var viewport=new Panel { Dock=DockStyle.Fill, AutoScroll=true };
            Controls.Add(viewport);
            var page=new TableLayoutPanel { ColumnCount=1, RowCount=2, Padding=new Padding(16), Margin=Padding.Empty };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            viewport.Controls.Add(page);

            var masthead=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=3, RowCount=1, Margin=new Padding(0,0,12,10) };
            masthead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            masthead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            masthead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            masthead.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            masthead.Controls.Add(Caption("电视助手",19,true),0,0);
            var help=ActionButton("使用说明");
            help.Click+=(sender,args)=>{ try { Process.Start(Path.Combine(root,"使用说明.md")); } catch { MessageBox.Show("请在工具文件夹中打开使用说明.md。","电视助手"); } };
            maintenance=ActionButton("直播维护");
            maintenance.Click+=(sender,args)=>OpenMaintenance();
            masthead.Controls.Add(help,1,0); masthead.Controls.Add(maintenance,2,0);
            page.Controls.Add(masthead,0,0);

            var connect=ActionRow(); connect.Margin=new Padding(0,0,0,8);
            connect.Controls.Add(Caption("电视 IP",11,true));
            ip=new TextBox { Width=158, Margin=new Padding(4,8,12,4), BorderStyle=BorderStyle.FixedSingle };
            connect.Controls.Add(ip);
            status=Caption("",10,false);
            connect.Controls.Add(status);


            var workspace=new Panel { Dock=DockStyle.Fill, Margin=Padding.Empty };
            page.Controls.Add(workspace,0,1);
            var setup=Section(Color.FromArgb(232,239,248));
            setup.Controls.Add(Caption("安装与恢复",11.5f,true));
            setup.Controls.Add(connect);
            install=ActionButton("更换主页并安装播放器",true);
            homeOnly=ActionButton("更换主页");
            tivimate=ActionButton("安装 TVM 播放器");
            var combinedRow=ActionRow(); combinedRow.Controls.Add(install); setup.Controls.Add(combinedRow);
            var setupActions=ActionRow(); setupActions.Controls.Add(homeOnly); setup.Controls.Add(setupActions);
            var playerRow=ActionRow(); playerRow.Controls.Add(tivimate); setup.Controls.Add(playerRow);
            var restoreRow=ActionRow();
            restore=ActionButton("恢复原主页"); restoreRow.Controls.Add(restore);
            setup.Controls.Add(restoreRow);

            var live=Section(Color.White);
            live.Controls.Add(Caption("直播订阅",11.5f,true));
            var imports=ActionRow();
            chooseM3u=ActionButton("导入播放列表");
            var chooseEpg=ActionButton("导入节目表");
            imports.Controls.AddRange(new Control[]{chooseM3u,chooseEpg});

            service=ActionButton("启动直播源",true);
            serviceStatus=Caption("未启动",9,false);
            imports.Controls.AddRange(new Control[]{service,serviceStatus});
            live.Controls.Add(imports);
            var addressRow=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=2 };
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            addressRow.Controls.Add(Caption("订阅地址",9,false),0,0);
            playlist=new TextBox { ReadOnly=true, Dock=DockStyle.Fill };
            addressRow.Controls.Add(playlist,1,0);
            live.Controls.Add(addressRow);
            var sendRow=ActionRow();
            copy=ActionButton("复制订阅地址"); copy.Enabled=false;
            sendUrl=ActionButton("填到电视"); sendUrl.Enabled=false;
            sendUrl.Click+=(sender,args)=>SendUrl();
            sendRow.Controls.AddRange(new Control[]{sendUrl,copy});
            live.Controls.Add(sendRow);
            var fileRow=ActionRow();
            fileM3u=Caption("",9,false); fileEpg=Caption("",9,false);
            fileRow.Controls.AddRange(new Control[]{fileM3u,fileEpg});
            EventHandler showFileError=(sender,args)=>fileRow.Visible=fileM3u.Text.Length>0 || fileEpg.Text.Length>0;
            fileM3u.TextChanged+=showFileError; fileEpg.TextChanged+=showFileError; fileRow.Visible=false;
            live.Controls.Add(fileRow);
            epg=new TextBox { Visible=false };
            Controls.Add(epg);
            workspace.Controls.Add(setup); workspace.Controls.Add(live);

            var preview=new Surface { BackColor=Color.White, Padding=new Padding(12,8,12,8), Margin=Padding.Empty, ColumnCount=2, RowCount=3 };
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,62));
            preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var countRow=ActionRow();
            countRow.Controls.Add(Caption("频道预览",11,true));
            channelsInfo=Caption("",9,false); countRow.Controls.Add(channelsInfo);
            preview.Controls.Add(countRow,0,0);
            programInfo=Caption("选择频道查看节目",10,false);
            preview.Controls.Add(programInfo,1,0);
            channelList=new ListBox { Dock=DockStyle.Fill, IntegralHeight=false, BorderStyle=BorderStyle.None, BackColor=Color.FromArgb(248,250,252) };
            programList=new ListBox { Dock=DockStyle.Fill, IntegralHeight=false, HorizontalScrollbar=true, BorderStyle=BorderStyle.None, BackColor=Color.FromArgb(248,250,252) };
                        var channelArea=new Panel { Dock=DockStyle.Fill, BackColor=Color.FromArgb(246,248,252), Margin=new Padding(2,2,8,4) };
            var programArea=new Panel { Dock=DockStyle.Fill, BackColor=Color.FromArgb(246,248,252), Margin=new Padding(2,2,2,4) };
            channelArea.Controls.Add(channelList); programArea.Controls.Add(programList);
            emptyPreview=new Label { Text="导入播放列表后，在这里查看频道", Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleCenter, ForeColor=muted };
            channelArea.Controls.Add(emptyPreview);
            emptyPreview.BringToFront();
            preview.Controls.Add(channelArea,0,1); preview.Controls.Add(programArea,1,1);
            var footer=ActionRow();
            play=ActionButton("播放选中频道",true); play.Enabled=false;
            var showLog=ActionButton("操作记录");
            footer.Controls.AddRange(new Control[]{play,showLog});
            preview.Controls.Add(footer,0,2); preview.SetColumnSpan(footer,2);
            workspace.Controls.Add(preview);
            Action arrange=()=>{
                using(var g=viewport.CreateGraphics()) {
                    float scale=g.DpiX/96f;
                    int gap=(int)(10*scale);
                    page.Width=Math.Max((int)(360*scale),viewport.Width-SystemInformation.VerticalScrollBarWidth);
                    int width=workspace.ClientSize.Width;
                    if(width<1)return;
                    bool wide=width>=780*scale;
                    int railWidth=wide?(int)(276*scale):width;
                    int rightX=wide?railWidth+gap:0;
                    CompactRows(setup,railWidth);
                    CompactRows(live,width-rightX);
                    setup.Location=Point.Empty;
                    live.Location=new Point(rightX,wide?0:setup.Height+gap);
                    int previewTop=live.Bottom+gap;
                    int minHeight=Math.Max(setup.Bottom,previewTop+(int)(200*scale));
                    page.Height=Math.Max(viewport.ClientSize.Height,workspace.Top+minHeight+page.Padding.Bottom);
                    preview.SetBounds(rightX,previewTop,width-rightX,Math.Max((int)(200*scale),workspace.ClientSize.Height-previewTop));
                }
            };
            bool arrangePending=false;
            EventHandler schedule=(sender,args)=>{
                if(!IsHandleCreated || arrangePending || IsDisposed)return;
                arrangePending=true;
                BeginInvoke(new Action(()=>{try { arrange(); } finally { arrangePending=false; }}));
            };
            viewport.Resize+=schedule;
            Shown+=schedule;
            fileM3u.TextChanged+=schedule; fileEpg.TextChanged+=schedule;
            serviceStatus.TextChanged+=schedule; status.TextChanged+=schedule;
            workspace.SizeChanged+=schedule;
            log=new TextBox { Multiline=true,ScrollBars=ScrollBars.Vertical,ReadOnly=true,Font=FontOf(9) };
            chooseM3u.Click += (s,e) => ImportPlaylist(); chooseEpg.Click += (s,e) => ImportFile(false);
            showLog.Click += (s,e) => { var f=new Form { Text="操作记录",Size=new Size(680,440),StartPosition=FormStartPosition.CenterParent }; var t=new TextBox { Multiline=true,ScrollBars=ScrollBars.Both,ReadOnly=true,Dock=DockStyle.Fill,Font=FontOf(9),Text=log.Text }; f.Controls.Add(t); f.ShowDialog(this); };
            channelList.SelectedIndexChanged += (s,e) => UpdatePrograms(); channelList.DoubleClick += (s,e) => PlayChannel(); play.Click += (s,e) => PlayChannel();
            homeOnly.Click += (s,e) => BeginInstall(false); install.Click += (s,e) => BeginInstall(true); restore.Click += (s,e) => BeginRestore();
            tivimate.Click += (s,e) => BeginTiviMate();
            service.Click += (s,e) => ToggleServer(); copy.Click += (s,e) => { if (playlist.Text.Length>0) { Clipboard.SetText(playlist.Text); status.Text="地址已复制"; } };
            FormClosing += (s,e) => {
                if ((operation!=null && !operation.HasExited) || maintenanceBusy || automaticBusy) { e.Cancel=true; MessageBox.Show("后台操作正在进行，请等待完成或在维护窗口取消。","电视助手"); return; }
                epgTimer.Stop(); lifetime.Cancel(); StopServer();
            };
            try { ip.Text=MaintenanceSettings.Load(root).TvIp; } catch(Exception ex) { Append("设置读取失败："+ex.Message); }
            LoadDefaults();
            epgTimer.Tick+=(s,e)=>CheckAutomaticEpg(); epgTimer.Start();
            Shown+=(s,e)=>CheckAutomaticEpg();
        }
        string SourcePath(string name) { return Path.Combine(root,"直播源",name); }
        static List<Channel> ReadChannels(string path) {
            if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("播放列表超过4MB，请先精简列表。");
            var result=new List<Channel>(); string name=null,id="";
            foreach(string raw in File.ReadLines(path,Encoding.UTF8)) {
                string line=raw.Trim();
                if(line.StartsWith("#EXTINF:",StringComparison.OrdinalIgnoreCase)) {
                    var m=Regex.Match(line,"tvg-id\\s*=\\s*\"([^\"]*)\"",RegexOptions.IgnoreCase); id=m.Success?m.Groups[1].Value:"";
                    int comma=-1; bool quoted=false; for(int n=0;n<line.Length;n++) { if(line[n]=='"')quoted=!quoted; else if(line[n]==',' && !quoted) { comma=n; break; } } name=comma>=0?line.Substring(comma+1).Trim():"";
                } else if(line.Length>0 && !line.StartsWith("#") && name!=null) {
                    Uri uri; if(Uri.TryCreate(line,UriKind.Absolute,out uri) && (uri.Scheme=="http" || uri.Scheme=="https")) result.Add(new Channel { Name=String.IsNullOrWhiteSpace(name)?line:name, Id=id, Url=line });
                    name=null; id="";
                }
            }
            if(result.Count==0) throw new InvalidDataException("没有找到带 HTTP 或 HTTPS 地址的频道。");
            return result;
        }
        static Stream OpenXml(string path) {
            var f=File.OpenRead(path); int a=f.ReadByte(),b=f.ReadByte(); f.Position=0;
            return a==0x1f && b==0x8b ? (Stream)new GZipStream(f,CompressionMode.Decompress) : f;
        }
        static string ShowTime(string value) {
            if(String.IsNullOrWhiteSpace(value) || value.Length<12) return "时间未知";
            try {
                string t=value.Trim(); var local=DateTime.ParseExact(t.Substring(0,12),"yyyyMMddHHmm",System.Globalization.CultureInfo.InvariantCulture);
                if(t.Length>=20) { string zone=t.Substring(t.Length-5); if((zone[0]=='+' || zone[0]=='-') && zone.Skip(1).All(Char.IsDigit)) { int minutes=(Int32.Parse(zone.Substring(1,2))*60+Int32.Parse(zone.Substring(3,2)))*(zone[0]=='-'?-1:1); return new DateTimeOffset(local,TimeSpan.FromMinutes(minutes)).ToLocalTime().ToString("MM-dd HH:mm"); } }
                return local.ToString("MM-dd HH:mm");
            } catch { return "时间未知"; }
        }
        static Dictionary<string,List<string>> ReadPrograms(string path) {
            var result=new Dictionary<string,List<string>>();
            var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=64*1024*1024 };
            using(var input=OpenXml(path)) using(var reader=XmlReader.Create(input,settings)) {
                var doc=XDocument.Load(reader); if(doc.Root==null || doc.Root.Name.LocalName!="tv") throw new InvalidDataException("节目表必须是 XMLTV 格式。");
                foreach(var el in doc.Root.Elements().Where(x=>x.Name.LocalName=="programme")) {
                    string key=(string)el.Attribute("channel"); if(String.IsNullOrEmpty(key)) continue;
                    var title=el.Elements().FirstOrDefault(x=>x.Name.LocalName=="title"); string text=ShowTime((string)el.Attribute("start"))+"  "+(title==null?"未命名节目":title.Value.Trim());
                    List<string> list; if(!result.TryGetValue(key,out list)) { list=new List<string>(); result[key]=list; } list.Add(text);
                }
            }
            if(result.Count==0) throw new InvalidDataException("节目表中没有节目数据。"); return result;
        }
        void LoadDefaults() {
            try { new IptvStorage(Path.Combine(root,"直播源")).Read(); }
            catch(Exception ex) { channels.Clear(); programmes.Clear(); RefreshChannels(); status.Text="数据需要恢复"; Append(ex.Message); return; }
            try { channels=ReadChannels(SourcePath("home_ready.m3u")); fileM3u.Text=""; } catch(Exception ex) { channels.Clear(); fileM3u.Text=""; if(File.Exists(SourcePath("home_ready.m3u"))) { fileM3u.Text="播放列表读取失败"; Append(ex.Message); } }
            try { programmes=ReadPrograms(SourcePath("epg.xml.gz")); fileEpg.Text=""; } catch(Exception ex) { programmes.Clear(); fileEpg.Text=""; if(File.Exists(SourcePath("epg.xml.gz"))) { fileEpg.Text="节目表读取失败"; Append(ex.Message); } }
            RefreshChannels();
        }
        void RefreshChannels() {
            chooseM3u.Text=File.Exists(SourcePath("home_ready.m3u"))?"更换播放列表":"导入播放列表";
            channelList.BeginUpdate(); channelList.Items.Clear(); foreach(var c in channels) channelList.Items.Add(c); channelList.EndUpdate();
            emptyPreview.Visible=channels.Count==0;
            channelList.Visible=channels.Count>0; programList.Visible=channels.Count>0;
            channelsInfo.Text=channels.Select(c=>Regex.Replace(c.Name,@"\s*\(备\d*\)$","")).Distinct().Count()+" 个频道"; if(channelList.Items.Count>0) channelList.SelectedIndex=0; else UpdatePrograms();
        }
        void UpdatePrograms() {
            var c=channelList.SelectedItem as Channel; play.Enabled=c!=null; programList.Items.Clear();
            if(c==null) { programInfo.Text="节目表"; return; }
            List<string> rows; if(String.IsNullOrEmpty(c.Id) || !programmes.TryGetValue(c.Id,out rows)) { programInfo.Text="未找到匹配节目"; return; }
            programInfo.Text=c.Name; programList.BeginUpdate(); foreach(string row in rows) programList.Items.Add(row); programList.EndUpdate();
        }
        void ImportPlaylist() {
            if(File.Exists(SourcePath("home_ready.m3u"))) {
                ImportFile(true);
                return;
            }
            if(maintenanceBusy || automaticBusy || (operation!=null && !operation.HasExited)) {
                status.Text="请等待当前操作完成";
                return;
            }
            try {
                var store=new IptvStorage(Path.Combine(root,"直播源"));
                var before=store.Read();
                if(before.PlaylistText!=null) { LoadDefaults(); return; }
                string folder=Path.Combine(root,"files","default-iptv");
                string source=Path.Combine(folder,"home_ready.m3u");
                string guide=Path.Combine(folder,"epg.xml.gz");
                if(!File.Exists(source) || !File.Exists(guide))throw new FileNotFoundException("内置播放源不完整，请重新解压电视助手。");
                ReadChannels(source);
                store.Apply(File.ReadAllText(source,Encoding.UTF8),before.EpgBytes ?? File.ReadAllBytes(guide),
                    before.Version,"导入内置播放列表",CancellationToken.None);
                LoadDefaults();
                status.Text="已导入内置播放列表";
                Append("内置播放列表已导入。请点击「启动直播源」。");
            } catch(Exception ex) {
                fileM3u.Text=ex.Message;
                Append(ex.Message);
            }
        }
        void ImportFile(bool isPlaylist) {
            if(maintenanceBusy || automaticBusy || (operation!=null && !operation.HasExited)) { MessageBox.Show("请等待当前操作完成。","电视助手"); return; }
            using(var dlg=new OpenFileDialog { Title=isPlaylist?"选择播放列表":"选择 XMLTV 节目表",Filter=isPlaylist?"M3U 播放列表|*.m3u;*.m3u8|所有文件|*.*":"XMLTV 节目表|*.xml;*.xml.gz;*.gz|所有文件|*.*",CheckFileExists=true }) {
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                try {
                    var store=new IptvStorage(Path.Combine(root,"直播源")); var before=store.Read();
                    string text=before.PlaylistText; byte[] guide=before.EpgBytes;
                    if(isPlaylist) {
                        ReadChannels(dlg.FileName); text=File.ReadAllText(dlg.FileName,Encoding.UTF8);
                        if(!Regex.IsMatch(text,@"(?m)^#EXTM3U(?:\s|$)"))throw new InvalidDataException("缺少 #EXTM3U 头，不能作为标准订阅分发。");
                    } else {
                        if(new FileInfo(dlg.FileName).Length>IptvEpg.MaxInputBytes)throw new InvalidDataException("节目表文件超过16MB。");
                        var preview=IptvEpg.Prepare(text,guide,File.ReadAllBytes(dlg.FileName),DateTimeOffset.Now,false);
                        if(!preview.CanApply)throw new InvalidDataException(String.Join("；",preview.Errors));
                        if(preview.Warnings.Count>0 && MessageBox.Show(this,String.Join("\n",preview.Warnings.Take(8))+"\n仍要导入并备份现有文件？","节目表提示",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
                        text=preview.PlaylistText; guide=preview.EpgBytes;
                    }
                    store.Apply(text,guide,before.Version,isPlaylist?"导入播放列表":"导入节目表",CancellationToken.None);
                    LoadDefaults(); status.Text="文件已导入并备份";
                    Append("已导入："+Path.GetFileName(dlg.FileName)+"。原订阅网址不变；电视需要刷新原列表/节目表。可在直播维护中恢复上一版。");
                } catch(Exception ex) { MessageBox.Show(this,"文件导入未完成："+ex.Message,"电视助手",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            }
        }
        void PlayChannel() {
            var c=channelList.SelectedItem as Channel; if(c==null) return;
            string[] candidates={ Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VideoLAN","VLC","vlc.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"VideoLAN","VLC","vlc.exe"), @"D:\VLC\vlc.exe" };
            string player=candidates.FirstOrDefault(File.Exists);
            if(player==null) using(var dlg=new OpenFileDialog { Title="选择 VLC 播放器",Filter="播放器|vlc.exe;*.exe",CheckFileExists=true }) { if(dlg.ShowDialog(this)!=DialogResult.OK) return; player=dlg.FileName; }
            try {
                Process.Start(new ProcessStartInfo {
                    FileName=player,
                    Arguments="--no-one-instance --no-qt-updates-notif \""+c.Url.Replace("\"","")+"\"",
                    UseShellExecute=false
                });
                status.Text="正在打开播放器";
                Append("已在电脑上打开："+c.Name+"。播放是否成功请查看播放器画面。");
            }
            catch(Exception ex) { MessageBox.Show("无法打开播放器："+ex.Message,"电视助手"); }
        }
        bool ValidIp() { IPAddress parsed; if (!IPAddress.TryParse(ip.Text.Trim().Normalize(NormalizationForm.FormKC).Replace('。','.'),out parsed) || parsed.AddressFamily!=AddressFamily.InterNetwork) { MessageBox.Show("请先输入电视的 IPv4 地址。","电视助手"); ip.Focus(); return false; } ip.Text=parsed.ToString(); return true; }
        void Append(string line) { if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(new Action<string>(Append),line); return; } if (!String.IsNullOrWhiteSpace(line)) log.AppendText(line+Environment.NewLine); }
        void Ui(Action a) { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
        Process StartScript(string file, string arguments, string input, Action<string> line, Action<int> finished) {
            string path=Path.Combine(root,"files",file);
            if(!File.Exists(path))throw new FileNotFoundException("分享包不完整，缺少脚本："+file,path);
            var p=new Process(); p.StartInfo=new ProcessStartInfo { FileName="powershell.exe",Arguments="-NoProfile -ExecutionPolicy Bypass -File \""+path+"\" "+arguments,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
            p.OutputDataReceived += (s,e) => { if(e.Data!=null)line(e.Data); }; p.ErrorDataReceived += (s,e) => { if(e.Data!=null)line(e.Data); };
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.StandardInput.Write(input??""); p.StandardInput.Close();
            Task.Run(()=>{p.WaitForExit();int code=p.ExitCode; if(finished!=null)Ui(()=>finished(code));}); return p;
        }
        void Busy(bool value) { homeOnly.Enabled=!value; install.Enabled=!value; restore.Enabled=!value; tivimate.Enabled=!value; maintenance.Enabled=!value; sendUrl.Enabled=!value && copy.Enabled; ip.ReadOnly=value; }
        void OpenMaintenance() {
            if(automaticBusy || (operation!=null && !operation.HasExited)) { MessageBox.Show(this,"请等待后台操作完成。","电视助手"); return; }
            maintenanceBusy=true;
            try { using(var form=new MaintenanceForm(root)) {
                var details=new ToolStripMenuItem("订阅详情");
                details.Click+=(sender,args)=>MessageBox.Show(form, !copy.Enabled?"请先启动直播源。":epg.Text.Length==0?"未导入本机节目表。":epg.Text,"节目表地址");
                var menu=new MenuStrip { Dock=DockStyle.Bottom }; menu.Items.Add(details); form.Controls.Add(menu); menu.SendToBack();
                form.ShowDialog(this);
            } LoadDefaults(); }
            finally { maintenanceBusy=false; }
            CheckAutomaticEpg();
        }
        async void CheckAutomaticEpg() {
            if(automaticBusy || maintenanceBusy || (operation!=null && !operation.HasExited))return;
            try { if(!MaintenanceSettings.Load(root).AutoEpg)return; } catch(Exception ex) { Append(ex.Message); return; }
            automaticBusy=true; Busy(true);
            try { string message=await Task.Run(()=>MaintenanceForm.AutoRefreshEpg(root,lifetime.Token)); if(message.Length>0) { Append(message); LoadDefaults(); } }
            catch(OperationCanceledException) { }
            catch(Exception ex) { Append("自动更新未完成，原有文件保留："+ex.Message); }
            finally { automaticBusy=false; if(!IsDisposed)Busy(false); }
        }
        void SendUrl() {
            if(!copy.Enabled || !ValidIp())return;
            Busy(true); status.Text="正在填写订阅地址…";
            try { operation=StartScript("setup.ps1","-Mode fill -NonInteractive",ip.Text.Trim()+"\r\n"+playlist.Text+"\r\n",Append,(code)=>{Busy(false);status.Text=code==0?"地址已填写":"填写失败 · 查看记录";}); }
            catch(Exception ex) { Busy(false); status.Text="填写失败 · 查看记录"; Append(ex.Message); }
        }
        void BeginInstall(bool includePlayer) {
            if(!ValidIp()) return;
            string arguments=includePlayer?"-Mode install -NonInteractive":"-Mode home -NonInteractive";
            log.Clear(); Busy(true); status.Text=includePlayer?"正在安装…":"正在更换主页…";
            try { operation=StartScript("setup.ps1",arguments,ip.Text.Trim()+"\r\nY\r\n",Append,(code)=>{
                Busy(false);
                status.Text=code==12?"电视系统版本过低":code==10?(includePlayer?"播放器和试用主页已安装":"试用主页已安装"):code==0?(includePlayer?"安装完成":"主页已更换"):"操作失败 · 查看记录";
            }); }
            catch(Exception ex) { Busy(false); status.Text="无法启动"; Append(ex.Message); }
        }
        void BeginRestore() {
            if(!ValidIp()) return;
            if(MessageBox.Show("将根据安装时保存的记录恢复原主页。继续吗？","恢复原主页",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
            log.Clear(); Busy(true); status.Text="正在恢复…";
            try { operation=StartScript("setup.ps1","-Mode restore -NonInteractive",ip.Text.Trim()+"\r\n",Append,(code)=>{Busy(false);status.Text=code==0?"原主页已恢复":"恢复未完成，查看记录";}); }
            catch(Exception ex) { Busy(false); status.Text="无法启动"; Append(ex.Message); }
        }
        void BeginTiviMate() {
            if(!ValidIp()) return;
            log.Clear(); Busy(true); status.Text="正在安装 TiviMate…";
            try { operation=StartScript("setup.ps1","-Mode tivimate -NonInteractive",ip.Text.Trim()+"\r\n",Append,(code)=>{Busy(false); if(code==0){status.Text="TiviMate 已安装";Append("安装完成。请在电视上打开 TiviMate 完成首次启动；装好后可在电视设置中关闭 ADB 调试。");} else status.Text="安装失败 · 查看记录";}); }
            catch(Exception ex) { Busy(false); status.Text="无法启动"; Append(ex.Message); }
        }        void ToggleServer() {
            if(server!=null && !server.HasExited) { StopServer(); return; }
            if(!ValidIp())return;
            if(channels.Count==0) { status.Text="请先导入播放列表"; return; }
            playlist.Clear(); epg.Clear(); copy.Enabled=false; serviceStatus.Text="正在启动…";
            int generation=++serverGeneration;
            try {
                var settings=MaintenanceSettings.Load(root); settings.TvIp=ip.Text.Trim(); settings.Save(root);
                server=StartScript("iptv-server.ps1","-NonInteractive -TvIp "+ip.Text.Trim(),"",line=>ServerLine(line,generation),(code)=>{
                    if(generation!=serverGeneration)return;
                    serviceStatus.Text=code==0?"已停止":"启动失败"; service.Text="启动直播源"; copy.Enabled=false; sendUrl.Enabled=false;
                    if(code!=0)Append("启动失败：请查看端口占用、网卡或文件提示，不要重复开启多个助手。");
                }); service.Text="停止直播源";
            } catch(Exception ex) { serviceStatus.Text="启动失败"; Append(ex.Message); }
        }
        void ServerLine(string line,int generation) {
            Append(line);
            const string ready="TVASSISTANT_SERVER_READY|";
            if(line.StartsWith(ready,StringComparison.Ordinal))Ui(()=>VerifyServer(line.Substring(ready.Length).Trim(),generation));
        }
        async void VerifyServer(string address,int generation) {
            try {
                await Task.Run(()=>{
                    var request=(HttpWebRequest)WebRequest.Create(address+"/home_ready.m3u"); request.Proxy=null; request.Timeout=5000; request.ReadWriteTimeout=5000;
                    using(var response=(HttpWebResponse)request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())) {
                        string first=reader.ReadLine(); if(response.StatusCode!=HttpStatusCode.OK || first==null || !first.StartsWith("#EXTM3U"))throw new IOException("本机未能读取有效播放列表。");
                    }
                });
                if(generation!=serverGeneration || IsDisposed)return;
                playlist.Text=address+"/home_ready.m3u"; epg.Text=File.Exists(SourcePath("epg.xml.gz"))?address+"/epg.xml.gz":""; copy.Enabled=true; sendUrl.Enabled=install.Enabled; serviceStatus.Text="直播源已启动";
                var settings=MaintenanceSettings.Load(root);
                if(!String.IsNullOrEmpty(settings.LastPlaylistUrl) && settings.LastPlaylistUrl!=playlist.Text)Append("注意：电脑订阅地址已变化。请核对电视中保存的网址；建议路由器固定分配电脑IP。");
                settings.LastPlaylistUrl=playlist.Text; settings.Save(root);
                Append("本机HTTP检查通过。尚未确认电视网络可达、免费版刷新或实际播放；请按验收步骤测试。");
            } catch(Exception ex) { if(generation==serverGeneration && !IsDisposed) { serviceStatus.Text="检查失败"; copy.Enabled=false; sendUrl.Enabled=false; Append(ex.Message); } }
        }
        void StopServer() {
            serverGeneration++; try { if(server!=null && !server.HasExited)server.Kill(); } catch(Exception ex) { Append("停止服务失败："+ex.Message); }
            server=null; serviceStatus.Text="已停止"; service.Text="启动直播源"; copy.Enabled=false; sendUrl.Enabled=false;
        }
        [STAThread] static void Main() { Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainWindow()); }
    }

    static class WindowFit {
        public static void Apply(Form form) {
            Rectangle work=Screen.FromControl(form).WorkingArea;
            form.Bounds=Fit(form.Bounds,work);
        }
        internal static Rectangle Fit(Rectangle bounds,Rectangle work) {
            int width=Math.Min(bounds.Width,work.Width), height=Math.Min(bounds.Height,work.Height);
            int left=Math.Max(work.Left,Math.Min(bounds.Left,work.Right-width));
            int top=Math.Max(work.Top,Math.Min(bounds.Top,work.Bottom-height));
            return new Rectangle(left,top,width,height);
        }
    }
}

