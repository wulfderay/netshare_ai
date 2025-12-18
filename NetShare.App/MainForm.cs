using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using NetShare.App.Dialogs;
using System.Windows.Forms;
using NetShare.Core.Discovery;
using NetShare.Core.Logging;
using NetShare.Core.Networking;
using NetShare.Core.Protocol;
using NetShare.Core.Settings;
using NetShare.Core.Sharing;
using NetShare.Core.Transfers;

namespace NetShare.App
{
    public sealed class MainForm : Form
    {
        private readonly SettingsStore _settingsStore = new SettingsStore();
        private AppSettings _settings;

        private readonly ShareManager _shareManager = new ShareManager();
        private PeerServer _server;
        private DiscoveryService _discovery;

        private readonly Dictionary<string, PeerInfo> _peersById = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _peerTimer = new Timer();
        private readonly Timer _uiTimer = new Timer();
        private readonly Timer _shareTimer = new Timer();

        private ListView _lvPeers;
        private ListView _lvShares;
        private TreeView _tvRemote;
        private ListView _lvRemote;
        private ListView _lvTransfers;
        private Button _btnDownload;
        private Button _btnUpload;
        private Button _btnPause;
        private Button _btnCancel;

        private StatusStrip _status;
        private ToolStripStatusLabel _statusIp;
        private ToolStripStatusLabel _statusPeer;
        private ToolStripStatusLabel _statusTransfers;

        private string _lastPeerName;
        private string _lastPeerIp;

        private readonly List<TransferRow> _transfers = new List<TransferRow>();
        private readonly Dictionary<string, ListViewItem> _transferItemsById = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        private bool _refreshingTransfers;

        private LogViewerForm _logViewer;

        public MainForm()
        {
            Text = "NetShare";
            Width = 1200;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();

            _settings = _settingsStore.LoadOrCreate();

            Logger.ConfigureFileLogging(_settings.EnableFileLogging);
            Logger.Info("UI", "App started. DeviceName=" + _settings.DeviceName + " DeviceId=" + _settings.DeviceId + " UDP=" + _settings.DiscoveryPort + " TCP=" + _settings.TcpPort + " OpenMode=" + _settings.OpenMode + " FileLog=" + _settings.EnableFileLogging);

            RestoreSharesFromSettings();
            RefreshSharesView();

            StartServices();

            UpdateStatusIp();
            UpdateStatusPeer();
            UpdateStatusTransfers();

            _peerTimer.Interval = 1000;
            _peerTimer.Tick += (s, e) => RefreshPeerOnlineStates();
            _peerTimer.Start();

            _uiTimer.Interval = 500;
            _uiTimer.Tick += (s, e) => RefreshTransfersView();
            _uiTimer.Start();

            _shareTimer.Interval = 2000;
            _shareTimer.Tick += (s, e) => ReconcileShares();
            _shareTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { _peerTimer.Stop(); } catch { }
            try { _uiTimer.Stop(); } catch { }
            try { _shareTimer.Stop(); } catch { }
            try { _discovery?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }

            Logger.Info("UI", "App shutting down.");
            Logger.Shutdown();
        }

        private void StartServices()
        {
            _server = new PeerServer(_shareManager, _settings);
            _server.Start(_settings.TcpPort);

            _discovery = new DiscoveryService();
            _discovery.OnMessage += Discovery_OnMessage;

            IPAddress bind;
            IPAddress broadcast;
            if (NetworkSelection.TryResolve(_settings.PreferredInterfaceId, out bind, out broadcast))
            {
                Logger.Info("UI", "Discovery adapter selected. Bind=" + bind + " Broadcast=" + broadcast);
                _discovery.Start(_settings.DiscoveryPort,
                    () => DiscoveryMessage.CreateAnnounce(NetShareProtocol.ProtocolVersion, _settings.DeviceId, _settings.DeviceName, _settings.TcpPort, _settings.DiscoveryPort),
                    enableAnnounce: true,
                    bindAddress: bind,
                    broadcastAddress: broadcast);
            }
            else
            {
                _discovery.Start(_settings.DiscoveryPort,
                    () => DiscoveryMessage.CreateAnnounce(NetShareProtocol.ProtocolVersion, _settings.DeviceId, _settings.DeviceName, _settings.TcpPort, _settings.DiscoveryPort));
            }
            _discovery.SendQuery();

            Logger.Info("UI", "Services started.");
        }

        private void RestartDiscovery()
        {
            try { _discovery?.Dispose(); } catch { }

            _discovery = new DiscoveryService();
            _discovery.OnMessage += Discovery_OnMessage;

            IPAddress bind;
            IPAddress broadcast;
            if (NetworkSelection.TryResolve(_settings.PreferredInterfaceId, out bind, out broadcast))
            {
                Logger.Info("UI", "Discovery restart. Bind=" + bind + " Broadcast=" + broadcast + " Port=" + _settings.DiscoveryPort);
                _discovery.Start(_settings.DiscoveryPort,
                    () => DiscoveryMessage.CreateAnnounce(NetShareProtocol.ProtocolVersion, _settings.DeviceId, _settings.DeviceName, _settings.TcpPort, _settings.DiscoveryPort),
                    enableAnnounce: true,
                    bindAddress: bind,
                    broadcastAddress: broadcast);
            }
            else
            {
                Logger.Info("UI", "Discovery restart. Auto bind. Port=" + _settings.DiscoveryPort);
                _discovery.Start(_settings.DiscoveryPort,
                    () => DiscoveryMessage.CreateAnnounce(NetShareProtocol.ProtocolVersion, _settings.DeviceId, _settings.DeviceName, _settings.TcpPort, _settings.DiscoveryPort));
            }

            _discovery.SendQuery();
        }

        private void Discovery_OnMessage(IPEndPoint ep, DiscoveryMessage msg)
        {
            if (msg == null) return;
            if (string.IsNullOrWhiteSpace(msg.deviceId)) return;
            if (string.Equals(msg.deviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(msg.proto, NetShareProtocol.ProtocolVersion, StringComparison.Ordinal)) return;

            BeginInvoke((Action)(() =>
            {
                if (!_peersById.TryGetValue(msg.deviceId, out var peer))
                {
                    peer = new PeerInfo { DeviceId = msg.deviceId };
                    _peersById[msg.deviceId] = peer;
                }

                peer.DeviceName = msg.deviceName;
                peer.Address = ep.Address;
                peer.TcpPort = msg.tcpPort;
                peer.LastSeenUtc = DateTime.UtcNow;
                peer.Online = true;
                UpsertPeerRow(peer);
            }));
        }

        private void BuildUi()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            var exit = new ToolStripMenuItem("E&xit", null, (s, e) => Close());
            file.DropDownItems.Add(exit);

            var share = new ToolStripMenuItem("&Share");
            var addFolder = new ToolStripMenuItem("&Add Folder...", null, (s, e) => AddShareFolder()) { ShortcutKeys = Keys.Control | Keys.O };
            var removeShare = new ToolStripMenuItem("&Remove", null, (s, e) => RemoveSelectedShare()) { ShortcutKeys = Keys.Delete };
            var toggleRo = new ToolStripMenuItem("Toggle &Read-Only", null, (s, e) => ToggleSelectedShareReadOnly()) { ShortcutKeys = Keys.Control | Keys.R };
            share.DropDownItems.Add(addFolder);
            share.DropDownItems.Add(removeShare);
            share.DropDownItems.Add(toggleRo);

            var peer = new ToolStripMenuItem("&Peer");
            var connect = new ToolStripMenuItem("&Connect", null, (s, e) => ConnectToSelectedPeer());
            var refresh = new ToolStripMenuItem("&Refresh", null, (s, e) => _discovery?.SendQuery()) { ShortcutKeys = Keys.F5 };
            var addByIp = new ToolStripMenuItem("Add Peer by &IP...", null, (s, e) => AddPeerByIp());
            peer.DropDownItems.Add(connect);
            peer.DropDownItems.Add(refresh);
            peer.DropDownItems.Add(addByIp);

            var help = new ToolStripMenuItem("&Help");
            var viewLog = new ToolStripMenuItem("View &Event Log", null, (s, e) => OpenEventLog());
            var about = new ToolStripMenuItem("&About", null, (s, e) => MessageBox.Show(this, "NetShare\nLAN file sharing tool\nProtocol: " + NetShareProtocol.ProtocolVersion, "About", MessageBoxButtons.OK, MessageBoxIcon.Information));
            var openProto = new ToolStripMenuItem("Open &Protocol Doc", null, (s, e) => OpenProtocolDoc());
            help.DropDownItems.Add(viewLog);
            help.DropDownItems.Add(openProto);
            help.DropDownItems.Add(about);

            var settings = new ToolStripMenuItem("&Settings", null, (s, e) => OpenSettings());

            menu.Items.Add(file);
            menu.Items.Add(share);
            menu.Items.Add(peer);
            menu.Items.Add(settings);
            menu.Items.Add(help);

            MainMenuStrip = menu;
            Controls.Add(menu);

            _status = new StatusStrip { SizingGrip = false };
            _statusIp = new ToolStripStatusLabel { Text = "IP: (loading)" };
            _statusPeer = new ToolStripStatusLabel { Text = "Peer: (none)", Spring = true };
            _statusTransfers = new ToolStripStatusLabel { Text = "Transfers: idle", TextAlign = System.Drawing.ContentAlignment.MiddleRight };
            _status.Items.Add(_statusIp);
            _status.Items.Add(_statusPeer);
            _status.Items.Add(_statusTransfers);
            Controls.Add(_status);

            var root = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 55};
            Controls.Add(root);
            root.BringToFront();
            _status.BringToFront();

            // Left: peers + shares
            var left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal};
            root.Panel1.Controls.Add(left);

            _lvPeers = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _lvPeers.Columns.Add("Name", 160);
            _lvPeers.Columns.Add("IP", 110);
            _lvPeers.Columns.Add("Status", 70);
            _lvPeers.Columns.Add("Last Seen", 120);
            _lvPeers.DoubleClick += (s, e) => ConnectToSelectedPeer();
            _lvPeers.SelectedIndexChanged += (s, e) => UpdateStatusPeer();
            left.Panel1.Controls.Add(WrapWithLabel("Peers", _lvPeers));

            _lvShares = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _lvShares.Columns.Add("Name", 100);
            _lvShares.Columns.Add("Path", 200);
            _lvShares.Columns.Add("Read-Only", 70);
            _lvShares.Columns.Add("Status", 70);
            left.Panel2.Controls.Add(WrapWithLabel("Shares", _lvShares));

            // Right: remote browse + transfers
            var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 420 };
            root.Panel2.Controls.Add(right);

            var browseSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            right.Panel1.Controls.Add(browseSplit);

            _tvRemote = new TreeView { Dock = DockStyle.Fill };
            _tvRemote.AfterSelect += (s, e) => LoadRemoteListing();
            browseSplit.Panel1.Controls.Add(WrapWithLabel("Remote Browse", _tvRemote));

            _lvRemote = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _lvRemote.Columns.Add("Name", 260);
            _lvRemote.Columns.Add("Type", 80);
            _lvRemote.Columns.Add("Size", 120);
            _lvRemote.DoubleClick += (s, e) => RemoteActivateSelection();

            // Add a little breathing room on the right edge.
            var remotePad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
            remotePad.Controls.Add(_lvRemote);
            browseSplit.Panel2.Controls.Add(remotePad);

            var transfersPanel = new Panel { Dock = DockStyle.Fill };
            right.Panel2.Controls.Add(transfersPanel);
            _lvTransfers = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            _lvTransfers.Columns.Add("Dir", 60);
            _lvTransfers.Columns.Add("Peer", 140);
            _lvTransfers.Columns.Add("File", 260);
            _lvTransfers.Columns.Add("Progress", 90);
            _lvTransfers.Columns.Add("Speed", 90);
            _lvTransfers.Columns.Add("ETA", 90);
            _lvTransfers.Columns.Add("Status", 110);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight };
            _btnDownload = new Button { Text = "Download", Width = 90 };
            _btnUpload = new Button { Text = "Upload", Width = 90 };
            _btnPause = new Button { Text = "Pause/Resume", Width = 100 };
            _btnCancel = new Button { Text = "Cancel", Width = 90 };
            _btnDownload.Click += (s, e) => DownloadSelectedRemoteFile();
            _btnUpload.Click += (s, e) => UploadToSelectedRemoteDir();
            _btnPause.Click += (s, e) => PauseResumeSelectedTransfer();
            _btnCancel.Click += (s, e) => CancelSelectedTransfer();
            btnRow.Controls.AddRange(new Control[] { _btnDownload, _btnUpload, _btnPause, _btnCancel });

            transfersPanel.Controls.Add(_lvTransfers);
            transfersPanel.Controls.Add(btnRow);
            transfersPanel.Controls.Add(new Label { Dock = DockStyle.Top, Text = "Transfers", Height = 18, Padding = new Padding(3, 2, 0, 0) });
        }

        private void OpenEventLog()
        {
            if (_logViewer != null && !_logViewer.IsDisposed)
            {
                _logViewer.Activate();
                return;
            }

            _logViewer = new LogViewerForm();
            _logViewer.FormClosed += (s, e) => _logViewer = null;
            _logViewer.Show(this);
        }

        private static Control WrapWithLabel(string label, Control content)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(content);
            panel.Controls.Add(new Label { Dock = DockStyle.Top, Text = label, Height = 18, Padding = new Padding(3, 2, 0, 0) });
            content.Dock = DockStyle.Fill;
            return panel;
        }

        private void UpsertPeerRow(PeerInfo p)
        {
            ListViewItem item = null;
            foreach (ListViewItem it in _lvPeers.Items)
            {
                if (string.Equals(it.Name, p.DeviceId, StringComparison.OrdinalIgnoreCase)) { item = it; break; }
            }
            if (item == null)
            {
                item = new ListViewItem();
                item.Name = p.DeviceId;
                item.SubItems.Add("");
                item.SubItems.Add("");
                item.SubItems.Add("");
                _lvPeers.Items.Add(item);
            }
            item.Text = p.DeviceName;
            item.SubItems[1].Text = p.Address?.ToString() ?? "";
            item.SubItems[2].Text = p.Online ? "Online" : "Offline";
            item.SubItems[3].Text = p.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
        }

        private void RefreshPeerOnlineStates()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _peersById.ToList())
            {
                var p = kv.Value;
                p.Online = (now - p.LastSeenUtc).TotalMilliseconds <= NetShareProtocol.PeerOfflineAfterMs;
                UpsertPeerRow(p);
            }
        }

        private PeerInfo GetSelectedPeer()
        {
            if (_lvPeers.SelectedItems.Count == 0) return null;
            var id = _lvPeers.SelectedItems[0].Name;
            if (_peersById.TryGetValue(id, out var peer)) return peer;
            return null;
        }

        private void ConnectToSelectedPeer()
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            try
            {
                using (var client = new PeerClient(_settings))
                {
                    var auth = _settings.OpenMode ? "open" : "psk-hmac-sha256";
                    Logger.Info("UI", "Connect requested. Peer=" + peer.DeviceName + " " + peer.Address + ":" + peer.TcpPort + " Auth=" + auth);
                    client.Connect(peer.Address, peer.TcpPort, auth);
                    var shares = client.ListShares();
                    PopulateRemoteTree(peer, shares);
                    _lastPeerName = peer.DeviceName;
                    _lastPeerIp = peer.Address == null ? "" : peer.Address.ToString();
                    UpdateStatusPeer();
                    Logger.Info("UI", "Connect succeeded. Shares=" + (shares?.Count ?? 0));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "Connect failed.", ex);
                ErrorDialog.ShowException(this, "Connect failed", ex);
            }
        }

        private void PopulateRemoteTree(PeerInfo peer, List<Dictionary<string, object>> shares)
        {
            _tvRemote.Nodes.Clear();
            var root = new TreeNode(peer.DeviceName) { Tag = new RemoteNodeTag { Peer = peer, ShareId = null, RemotePath = "" } };
            foreach (var s in shares)
            {
                var shareId = s["shareId"].ToString();
                var name = s["name"].ToString();
                var node = new TreeNode(name) { Tag = new RemoteNodeTag { Peer = peer, ShareId = shareId, RemotePath = "" } };
                node.Nodes.Add(new TreeNode("(loading)"));
                root.Nodes.Add(node);
            }
            _tvRemote.Nodes.Add(root);
            root.Expand();
        }

        private void LoadRemoteListing()
        {
            var node = _tvRemote.SelectedNode;
            if (node == null) return;
            if (!(node.Tag is RemoteNodeTag tag)) return;
            if (string.IsNullOrWhiteSpace(tag.ShareId)) return;

            try
            {
                using (var client = new PeerClient(_settings))
                {
                    var auth = _settings.OpenMode ? "open" : "psk-hmac-sha256";
                    client.Connect(tag.Peer.Address, tag.Peer.TcpPort, auth);
                    Logger.Debug("UI", "Browse requested. Peer=" + tag.Peer.DeviceName + " ShareId=" + tag.ShareId + " Path=" + (tag.RemotePath ?? ""));
                    var entries = client.ListDir(tag.ShareId, tag.RemotePath);
                    PopulateRemoteList(entries);
                    PopulateChildDirs(node, entries, tag);

                    // Browsing is the most common peer interaction; treat as "last successful" peer.
                    _lastPeerName = tag.Peer.DeviceName;
                    _lastPeerIp = tag.Peer.Address == null ? "" : tag.Peer.Address.ToString();
                    UpdateStatusPeer();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UI", "Browse failed.", ex);
                ErrorDialog.ShowException(this, "Browse failed", ex);
            }
        }

        private void PopulateRemoteList(List<Dictionary<string, object>> entries)
        {
            _lvRemote.Items.Clear();
            foreach (var e in entries)
            {
                var name = e["name"].ToString();
                var isDir = Convert.ToBoolean(e["isDir"]);
                var item = new ListViewItem(name);
                item.SubItems.Add(isDir ? "Folder" : "File");
                item.SubItems.Add(isDir ? "" : (e.ContainsKey("size") ? e["size"].ToString() : ""));
                item.Tag = e;
                _lvRemote.Items.Add(item);
            }
        }

        private void PopulateChildDirs(TreeNode node, List<Dictionary<string, object>> entries, RemoteNodeTag tag)
        {
            node.Nodes.Clear();
            foreach (var e in entries)
            {
                var isDir = Convert.ToBoolean(e["isDir"]);
                if (!isDir) continue;
                var name = e["name"].ToString();
                var childPath = string.IsNullOrEmpty(tag.RemotePath) ? name : (tag.RemotePath.TrimEnd('/') + "/" + name);
                var child = new TreeNode(name) { Tag = new RemoteNodeTag { Peer = tag.Peer, ShareId = tag.ShareId, RemotePath = childPath } };
                child.Nodes.Add(new TreeNode("(loading)"));
                node.Nodes.Add(child);
            }
        }

        private void RemoteActivateSelection()
        {
            if (_lvRemote.SelectedItems.Count == 0) return;
            var entry = (Dictionary<string, object>)_lvRemote.SelectedItems[0].Tag;
            var isDir = Convert.ToBoolean(entry["isDir"]);
            if (!isDir) return;

            var node = _tvRemote.SelectedNode;
            if (node == null) return;
            foreach (TreeNode child in node.Nodes)
            {
                if (string.Equals(child.Text, entry["name"].ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _tvRemote.SelectedNode = child;
                    child.Expand();
                    return;
                }
            }
        }

        private void DownloadSelectedRemoteFile()
        {
            if (_lvRemote.SelectedItems.Count == 0) return;
            var node = _tvRemote.SelectedNode;
            if (!(node?.Tag is RemoteNodeTag tag) || string.IsNullOrWhiteSpace(tag.ShareId)) return;

            var entry = (Dictionary<string, object>)_lvRemote.SelectedItems[0].Tag;
            if (Convert.ToBoolean(entry["isDir"])) return;

            var name = entry["name"].ToString();
            var remotePath = string.IsNullOrEmpty(tag.RemotePath) ? name : (tag.RemotePath.TrimEnd('/') + "/" + name);
            var localPath = Path.Combine(_settings.DownloadDirectory, tag.Peer.DeviceName, name);

            long resume = 0;
            if (File.Exists(localPath)) resume = new FileInfo(localPath).Length;

            var transfer = new TransferRow
            {
                Info = new TransferInfo { TransferId = Guid.NewGuid().ToString(), Direction = TransferDirection.Download, Peer = tag.Peer.DeviceName, RemotePath = remotePath, LocalPath = localPath, State = TransferState.Running, StartedUtc = DateTime.UtcNow },
                Rate = new RateCalculator(),
                Cts = new System.Threading.CancellationTokenSource(),
                PeerAddress = tag.Peer.Address,
                PeerTcpPort = tag.Peer.TcpPort,
                ShareId = tag.ShareId,
                AuthMode = _settings.OpenMode ? "open" : "psk-hmac-sha256"
            };
            transfer.Rate.Reset(resume);
            _transfers.Add(transfer);

            var client = new TransferClient(_settings);

            Logger.Info("UI", "Download start. TransferId=" + transfer.Info.TransferId + " ShareId=" + transfer.ShareId + " RemotePath=" + remotePath + " Offset=" + resume + " LocalPath=" + localPath);

            transfer.Worker = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    client.Download(transfer.PeerAddress, transfer.PeerTcpPort, transfer.AuthMode, transfer.ShareId, remotePath, localPath, resume, transfer.Info.TransferId, (cur, total) =>
                    {
                        transfer.Info.TotalBytes = total;
                        transfer.Info.TransferredBytes = cur;
                    }, transfer.Cts.Token);
                    transfer.Info.State = TransferState.Completed;
                }
                catch (OperationCanceledException)
                {
                    if (transfer.Info.State == TransferState.Paused) return;
                    transfer.Info.State = TransferState.Canceled;
                }
                catch (Exception ex)
                {
                    transfer.Info.State = TransferState.Failed;
                    transfer.Info.Error = ex.Message;
                }
            });
        }

        private void UploadToSelectedRemoteDir()
        {
            var node = _tvRemote.SelectedNode;
            if (!(node?.Tag is RemoteNodeTag tag) || string.IsNullOrWhiteSpace(tag.ShareId)) return;

            using (var ofd = new OpenFileDialog { Title = "Select a file to upload" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                var name = Path.GetFileName(ofd.FileName);
                var remotePath = string.IsNullOrEmpty(tag.RemotePath) ? name : (tag.RemotePath.TrimEnd('/') + "/" + name);

                var transfer = new TransferRow
                {
                    Info = new TransferInfo { TransferId = Guid.NewGuid().ToString(), Direction = TransferDirection.Upload, Peer = tag.Peer.DeviceName, RemotePath = remotePath, LocalPath = ofd.FileName, State = TransferState.Running, StartedUtc = DateTime.UtcNow },
                    Rate = new RateCalculator(),
                    Cts = new System.Threading.CancellationTokenSource(),
                    PeerAddress = tag.Peer.Address,
                    PeerTcpPort = tag.Peer.TcpPort,
                    ShareId = tag.ShareId,
                    AuthMode = _settings.OpenMode ? "open" : "psk-hmac-sha256"
                };
                transfer.Rate.Reset(0);
                _transfers.Add(transfer);

                var client = new TransferClient(_settings);

                Logger.Info("UI", "Upload start. TransferId=" + transfer.Info.TransferId + " ShareId=" + transfer.ShareId + " RemotePath=" + remotePath + " LocalPath=" + ofd.FileName);

                transfer.Worker = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var total = new FileInfo(ofd.FileName).Length;
                        transfer.Info.TotalBytes = total;
                        client.Upload(transfer.PeerAddress, transfer.PeerTcpPort, transfer.AuthMode, transfer.ShareId, remotePath, ofd.FileName, transfer.Info.TransferId, (cur, tot) =>
                        {
                            transfer.Info.TotalBytes = tot;
                            transfer.Info.TransferredBytes = cur;
                        }, transfer.Cts.Token);
                        transfer.Info.State = TransferState.Completed;
                    }
                    catch (OperationCanceledException)
                    {
                        if (transfer.Info.State == TransferState.Paused) return;
                        transfer.Info.State = TransferState.Canceled;
                    }
                    catch (Exception ex)
                    {
                        transfer.Info.State = TransferState.Failed;
                        transfer.Info.Error = ex.Message;
                    }
                });
            }
        }

        private void PauseResumeSelectedTransfer()
        {
            if (_lvTransfers.SelectedItems.Count == 0) return;
            var id = _lvTransfers.SelectedItems[0].Name;
            var t = _transfers.FirstOrDefault(x => x.Info.TransferId == id);
            if (t == null) return;

            if (t.Info.State == TransferState.Running)
            {
                t.Info.State = TransferState.Paused;
                try { t.Cts.Cancel(); } catch { }
                Logger.Info("UI", "Transfer paused. TransferId=" + t.Info.TransferId);
                return;
            }

            if (t.Info.State == TransferState.Paused)
            {
                Logger.Info("UI", "Transfer resume requested. TransferId=" + t.Info.TransferId);
                ResumeTransfer(t);
            }
        }

        private void ResumeTransfer(TransferRow t)
        {
            if (t.Info.Direction == TransferDirection.Download)
            {
                long resume = 0;
                if (File.Exists(t.Info.LocalPath)) resume = new FileInfo(t.Info.LocalPath).Length;
                t.Rate.Reset(resume);
                t.Info.State = TransferState.Running;
                t.Cts = new System.Threading.CancellationTokenSource();

                var client = new TransferClient(_settings);
                Logger.Info("UI", "Download resume. TransferId=" + t.Info.TransferId + " ShareId=" + t.ShareId + " RemotePath=" + t.Info.RemotePath + " Offset=" + resume + " LocalPath=" + t.Info.LocalPath);
                t.Worker = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        client.Download(t.PeerAddress, t.PeerTcpPort, t.AuthMode, t.ShareId, t.Info.RemotePath, t.Info.LocalPath, resume, t.Info.TransferId, (cur, total) =>
                        {
                            t.Info.TotalBytes = total;
                            t.Info.TransferredBytes = cur;
                        }, t.Cts.Token);
                        t.Info.State = TransferState.Completed;
                    }
                    catch (OperationCanceledException)
                    {
                        if (t.Info.State == TransferState.Paused) return;
                        t.Info.State = TransferState.Canceled;
                    }
                    catch (Exception ex)
                    {
                        t.Info.State = TransferState.Failed;
                        t.Info.Error = ex.Message;
                    }
                });
                return;
            }

            if (t.Info.Direction == TransferDirection.Upload)
            {
                t.Rate.Reset(0);
                t.Info.State = TransferState.Running;
                t.Cts = new System.Threading.CancellationTokenSource();

                var client = new TransferClient(_settings);
                Logger.Info("UI", "Upload resume requested (restarts). TransferId=" + t.Info.TransferId + " ShareId=" + t.ShareId + " RemotePath=" + t.Info.RemotePath + " LocalPath=" + t.Info.LocalPath);
                t.Worker = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var total = new FileInfo(t.Info.LocalPath).Length;
                        t.Info.TotalBytes = total;
                        client.Upload(t.PeerAddress, t.PeerTcpPort, t.AuthMode, t.ShareId, t.Info.RemotePath, t.Info.LocalPath, t.Info.TransferId, (cur, tot) =>
                        {
                            t.Info.TotalBytes = tot;
                            t.Info.TransferredBytes = cur;
                        }, t.Cts.Token);
                        t.Info.State = TransferState.Completed;
                    }
                    catch (OperationCanceledException)
                    {
                        if (t.Info.State == TransferState.Paused) return;
                        t.Info.State = TransferState.Canceled;
                    }
                    catch (Exception ex)
                    {
                        t.Info.State = TransferState.Failed;
                        t.Info.Error = ex.Message;
                    }
                });
            }
        }

        private void CancelSelectedTransfer()
        {
            if (_lvTransfers.SelectedItems.Count == 0) return;
            var id = _lvTransfers.SelectedItems[0].Name;
            var t = _transfers.FirstOrDefault(x => x.Info.TransferId == id);
            if (t == null) return;
            t.Info.State = TransferState.Canceled;
            try { t.Cts.Cancel(); } catch { }
            Logger.Info("UI", "Transfer canceled. TransferId=" + t.Info.TransferId);
        }

        private void RefreshTransfersView()
        {
            if (_refreshingTransfers) return;
            _refreshingTransfers = true;
            try
            {
                string selectedId = null;
                if (_lvTransfers.SelectedItems.Count > 0) selectedId = _lvTransfers.SelectedItems[0].Name;

                _lvTransfers.BeginUpdate();
                try
                {
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in _transfers.ToList())
                    {
                        if (t?.Info == null) continue;
                        var id = t.Info.TransferId;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        ids.Add(id);

                        // Update rate samples before rendering.
                        t.Rate?.Sample(t.Info.TransferredBytes);
                        var eta = t.Rate == null ? (TimeSpan?)null : t.Rate.EstimateEta(t.Info.TransferredBytes, t.Info.TotalBytes);

                        if (!_transferItemsById.TryGetValue(id, out var item) || item == null || item.ListView != _lvTransfers)
                        {
                            item = new ListViewItem();
                            item.Name = id;

                            // Pre-create all subitems so we can update in place without reallocating.
                            item.SubItems.Add(""); // Peer
                            item.SubItems.Add(""); // File
                            item.SubItems.Add(""); // Progress
                            item.SubItems.Add(""); // Speed
                            item.SubItems.Add(""); // ETA
                            item.SubItems.Add(""); // Status

                            _transferItemsById[id] = item;
                            _lvTransfers.Items.Add(item);
                        }

                        item.Text = (t.Info.Direction == TransferDirection.Download) ? "Down" : "Up";
                        item.SubItems[1].Text = t.Info.Peer ?? "";
                        item.SubItems[2].Text = string.IsNullOrWhiteSpace(t.Info.RemotePath) ? "" : Path.GetFileName(t.Info.RemotePath);

                        var pct = t.Info.TotalBytes > 0 ? (int)(100.0 * t.Info.TransferredBytes / t.Info.TotalBytes) : 0;
                        item.SubItems[3].Text = pct + "%";
                        item.SubItems[4].Text = t.Rate == null ? "" : FormatRate(t.Rate.BytesPerSecond);
                        item.SubItems[5].Text = eta.HasValue ? eta.Value.ToString(@"hh\:mm\:ss") : "";
                        item.SubItems[6].Text = t.Info.State.ToString();
                    }

                    // Remove items that no longer exist in the backing list.
                    // (Completed transfers stay in _transfers, so they'll remain.)
                    foreach (var kv in _transferItemsById.ToList())
                    {
                        if (ids.Contains(kv.Key)) continue;
                        try { kv.Value?.Remove(); } catch { }
                        _transferItemsById.Remove(kv.Key);
                    }

                    if (!string.IsNullOrWhiteSpace(selectedId) && _transferItemsById.TryGetValue(selectedId, out var sel) && sel != null)
                    {
                        sel.Selected = true;
                        sel.Focused = true;
                    }
                }
                finally
                {
                    _lvTransfers.EndUpdate();
                }

                UpdateStatusTransfers();
            }
            finally
            {
                _refreshingTransfers = false;
            }
        }

        private static string FormatRate(double bps)
        {
            if (bps <= 0) return "";
            if (bps < 1024) return ((int)bps) + " B/s";
            if (bps < 1024 * 1024) return (bps / 1024.0).ToString("0.0") + " KB/s";
            return (bps / (1024.0 * 1024.0)).ToString("0.0") + " MB/s";
        }

        private void AddShareFolder()
        {
            using (var fbd = new FolderBrowserDialog { Description = "Select a folder to share" })
            {
                if (fbd.ShowDialog(this) != DialogResult.OK) return;

                var ro = MessageBox.Show(this, "Share as read-only?", "Read-only", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                try
                {
                    var fullPath = Path.GetFullPath(fbd.SelectedPath);
                    var existing = FindConfiguredShareByPath(fullPath);

                    ShareInfo share;
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.ShareId))
                    {
                        // Reuse stable ShareId if the user is re-adding a previously configured share.
                        share = _shareManager.AddShare(fullPath, ro, existing.ShareId, existing.Name);
                    }
                    else
                    {
                        share = _shareManager.AddShare(fullPath, ro);
                    }

                    UpsertConfiguredShare(share.ShareId, share.Name, share.LocalPath, share.ReadOnly);
                    PersistShares();
                    Logger.Info("UI", "Share added. ShareId=" + share.ShareId + " Name=" + share.Name + " ReadOnly=" + share.ReadOnly + " Path=" + share.LocalPath);
                    RefreshSharesView();
                }
                catch (Exception ex)
                {
                    Logger.Error("UI", "Share add failed.", ex);
                    ErrorDialog.ShowException(this, "Share failed", ex);
                }
            }
        }

        private void RefreshSharesView()
        {
            _lvShares.Items.Clear();
            EnsureSharesListInitialized();
            foreach (var s in _settings.Shares)
            {
                var isMissing = string.IsNullOrWhiteSpace(s.LocalPath) || !Directory.Exists(s.LocalPath);
                var it = new ListViewItem(string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name) { Name = s.ShareId ?? "" };
                it.SubItems.Add(s.LocalPath ?? "");
                it.SubItems.Add(s.ReadOnly ? "Yes" : "No");
                it.SubItems.Add(isMissing ? "Missing" : "OK");
                _lvShares.Items.Add(it);
            }
        }

        private void RemoveSelectedShare()
        {
            if (_lvShares.SelectedItems.Count == 0) return;
            var id = _lvShares.SelectedItems[0].Name;
            Logger.Info("UI", "Share remove requested. ShareId=" + id);
            _shareManager.RemoveShare(id);
            RemoveConfiguredShare(id);
            PersistShares();
            RefreshSharesView();
        }

        private void ToggleSelectedShareReadOnly()
        {
            if (_lvShares.SelectedItems.Count == 0) return;
            var id = _lvShares.SelectedItems[0].Name;
            // If it's active, toggle in manager; otherwise toggle in settings only.
            if (!_shareManager.ToggleReadOnly(id))
            {
                ToggleConfiguredShareReadOnly(id);
            }
            else
            {
                var live = _shareManager.GetShares().FirstOrDefault(x => string.Equals(x.ShareId, id, StringComparison.OrdinalIgnoreCase));
                if (live != null) UpsertConfiguredShare(live.ShareId, live.Name, live.LocalPath, live.ReadOnly);
            }

            PersistShares();
            Logger.Info("UI", "Share read-only toggled. ShareId=" + id);
            RefreshSharesView();
        }

        private void EnsureSharesListInitialized()
        {
            if (_settings.Shares == null) _settings.Shares = new List<ConfiguredShare>();
        }

        private ConfiguredShare FindConfiguredShareByPath(string fullPath)
        {
            EnsureSharesListInitialized();
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            return _settings.Shares.FirstOrDefault(x => string.Equals(x.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        private void UpsertConfiguredShare(string shareId, string name, string localPath, bool readOnly)
        {
            EnsureSharesListInitialized();
            if (string.IsNullOrWhiteSpace(shareId)) return;

            var s = _settings.Shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
            if (s == null)
            {
                s = new ConfiguredShare();
                _settings.Shares.Add(s);
            }

            s.ShareId = shareId;
            s.Name = name;
            s.LocalPath = localPath;
            s.ReadOnly = readOnly;
        }

        private void RemoveConfiguredShare(string shareId)
        {
            EnsureSharesListInitialized();
            if (string.IsNullOrWhiteSpace(shareId)) return;
            _settings.Shares.RemoveAll(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
        }

        private void ToggleConfiguredShareReadOnly(string shareId)
        {
            EnsureSharesListInitialized();
            if (string.IsNullOrWhiteSpace(shareId)) return;
            var s = _settings.Shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
            if (s == null) return;
            s.ReadOnly = !s.ReadOnly;
        }

        private void PersistShares()
        {
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Logger.Warn("UI", "Failed to save shares to settings.", ex);
            }
        }

        private void RestoreSharesFromSettings()
        {
            EnsureSharesListInitialized();

            // Ensure every entry has a stable ShareId.
            var changed = false;
            foreach (var cfg in _settings.Shares)
            {
                if (string.IsNullOrWhiteSpace(cfg.ShareId))
                {
                    cfg.ShareId = Guid.NewGuid().ToString();
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(cfg.LocalPath) && Directory.Exists(cfg.LocalPath))
                {
                    try
                    {
                        _shareManager.AddShare(cfg.LocalPath, cfg.ReadOnly, cfg.ShareId, cfg.Name);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("UI", "Configured share could not be activated. ShareId=" + cfg.ShareId + " Path=" + (cfg.LocalPath ?? ""), ex);
                    }
                }
                else
                {
                    Logger.Warn("UI", "Configured share is missing and will not be served. ShareId=" + cfg.ShareId + " Path=" + (cfg.LocalPath ?? ""));
                }
            }

            if (changed) PersistShares();
        }

        private void ReconcileShares()
        {
            EnsureSharesListInitialized();
            var needsRefresh = false;

            foreach (var cfg in _settings.Shares.ToList())
            {
                if (string.IsNullOrWhiteSpace(cfg.ShareId)) continue;
                var exists = !string.IsNullOrWhiteSpace(cfg.LocalPath) && Directory.Exists(cfg.LocalPath);

                if (exists)
                {
                    // If it became available, activate it.
                    if (!_shareManager.TryGetShare(cfg.ShareId, out _))
                    {
                        try
                        {
                            var live = _shareManager.AddShare(cfg.LocalPath, cfg.ReadOnly, cfg.ShareId, cfg.Name);
                            // Keep settings in sync with any normalization.
                            cfg.Name = live.Name;
                            cfg.LocalPath = live.LocalPath;
                            cfg.ReadOnly = live.ReadOnly;
                            PersistShares();

                            Logger.Info("UI", "Configured share is now available and will be served. ShareId=" + cfg.ShareId + " Path=" + cfg.LocalPath);
                            needsRefresh = true;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("UI", "Configured share exists but could not be activated. ShareId=" + cfg.ShareId + " Path=" + (cfg.LocalPath ?? ""), ex);
                        }
                    }
                }
                else
                {
                    // If it became missing, ensure it is not served.
                    if (_shareManager.RemoveShare(cfg.ShareId))
                    {
                        Logger.Warn("UI", "Share path became missing; share disabled. ShareId=" + cfg.ShareId + " Path=" + (cfg.LocalPath ?? ""));
                        needsRefresh = true;
                    }
                }
            }

            if (needsRefresh)
            {
                BeginInvoke((Action)(() => RefreshSharesView()));
            }
        }

        private void AddPeerByIp()
        {
            using (var dlg = new Dialogs.AddPeerForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!IPAddress.TryParse(dlg.PeerIp, out var ip))
                {
                    MessageBox.Show(this, "Invalid IP.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var id = "manual-" + ip;
                var peer = new PeerInfo { DeviceId = id, DeviceName = dlg.PeerName ?? ip.ToString(), Address = ip, TcpPort = dlg.TcpPort, LastSeenUtc = DateTime.UtcNow, Online = true };
                _peersById[id] = peer;
                UpsertPeerRow(peer);
                Logger.Info("UI", "Peer added manually. Peer=" + peer.DeviceName + " " + peer.Address + ":" + peer.TcpPort);
            }
        }

        private void OpenSettings()
        {
            using (var dlg = new Dialogs.SettingsForm(_settings))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _settings = dlg.Result;
                _settingsStore.Save(_settings);
                Logger.ConfigureFileLogging(_settings.EnableFileLogging);
                Logger.Info("UI", "Settings saved. UDP=" + _settings.DiscoveryPort + " TCP=" + _settings.TcpPort + " OpenMode=" + _settings.OpenMode + " FileLog=" + _settings.EnableFileLogging);

                // Apply discovery binding immediately (ports may still require restart for full effect).
                try { RestartDiscovery(); } catch { }

                UpdateStatusIp();

                MessageBox.Show(this, "Settings saved. Discovery adapter and logging apply immediately; restart the app for port changes to take full effect.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UpdateStatusIp()
        {
            if (_statusIp == null) return;
            if (InvokeRequired) { BeginInvoke((Action)UpdateStatusIp); return; }

            try
            {
                IPAddress bind;
                IPAddress broadcast;
                if (NetworkSelection.TryResolve(_settings?.PreferredInterfaceId, out bind, out broadcast) && bind != null)
                {
                    _statusIp.Text = "IP: " + bind;
                    return;
                }

                // Auto: pick a reasonable non-loopback IPv4.
                var opt = NetworkSelection.GetIPv4AdapterOptions()
                    .FirstOrDefault(o => o != null && o.IPv4Address != null && !IPAddress.IsLoopback(o.IPv4Address) && !IsApipa(o.IPv4Address))
                    ?? NetworkSelection.GetIPv4AdapterOptions().FirstOrDefault(o => o != null && o.IPv4Address != null && !IPAddress.IsLoopback(o.IPv4Address));

                _statusIp.Text = opt != null && opt.IPv4Address != null ? ("IP: " + opt.IPv4Address) : "IP: (none)";
            }
            catch
            {
                _statusIp.Text = "IP: (none)";
            }
        }

        private void UpdateStatusPeer()
        {
            if (_statusPeer == null) return;
            if (InvokeRequired) { BeginInvoke((Action)UpdateStatusPeer); return; }

            var selected = GetSelectedPeer();
            string selectedText;
            if (selected == null)
            {
                selectedText = "Peer: (none)";
            }
            else
            {
                var ip = selected.Address == null ? "" : selected.Address.ToString();
                selectedText = "Peer: " + selected.DeviceName + (string.IsNullOrWhiteSpace(ip) ? "" : (" (" + ip + ")"));
            }

            string lastText = "";
            if (!string.IsNullOrWhiteSpace(_lastPeerName))
            {
                lastText = "  Last: " + _lastPeerName + (string.IsNullOrWhiteSpace(_lastPeerIp) ? "" : (" (" + _lastPeerIp + ")"));
            }

            _statusPeer.Text = selectedText + lastText;
        }

        private void UpdateStatusTransfers()
        {
            if (_statusTransfers == null) return;
            if (InvokeRequired) { BeginInvoke((Action)UpdateStatusTransfers); return; }

            var active = _transfers.Count(t => t.Info != null && (t.Info.State == TransferState.Running || t.Info.State == TransferState.Paused));
            if (active <= 0)
            {
                _statusTransfers.Text = "Transfers: idle";
                return;
            }

            if (active == 1)
            {
                var t = _transfers.FirstOrDefault(x => x.Info != null && (x.Info.State == TransferState.Running || x.Info.State == TransferState.Paused));
                if (t != null)
                {
                    var pct = t.Info.TotalBytes > 0 ? (int)(100.0 * t.Info.TransferredBytes / t.Info.TotalBytes) : 0;
                    var eta = t.Rate == null ? (TimeSpan?)null : t.Rate.EstimateEta(t.Info.TransferredBytes, t.Info.TotalBytes);
                    var dir = t.Info.Direction == TransferDirection.Download ? "Down" : "Up";
                    var rate = t.Rate == null ? "" : FormatRate(t.Rate.BytesPerSecond);
                    _statusTransfers.Text = "Transfers: " + dir + " " + pct + "%" + (string.IsNullOrWhiteSpace(rate) ? "" : (" @ " + rate)) + (eta.HasValue ? (" ETA " + eta.Value.ToString(@"hh\:mm\:ss")) : "");
                    return;
                }
            }

            _statusTransfers.Text = "Transfers: " + active + " active";
        }

        private static bool IsApipa(IPAddress ipv4)
        {
            if (ipv4 == null) return false;
            var b = ipv4.GetAddressBytes();
            return b != null && b.Length == 4 && b[0] == 169 && b[1] == 254;
        }

        private void OpenProtocolDoc()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PROTOCOL.md");
                if (!File.Exists(path))
                {
                    // for running from bin, walk up a bit
                    var up = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "PROTOCOL.md"));
                    if (File.Exists(up)) path = up;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, "Open failed", ex);
            }
        }

        private sealed class RemoteNodeTag
        {
            public PeerInfo Peer;
            public string ShareId;
            public string RemotePath;
        }

        private sealed class TransferRow
        {
            public TransferInfo Info;
            public RateCalculator Rate;
            public System.Threading.Tasks.Task Worker;
            public System.Threading.CancellationTokenSource Cts;

            public IPAddress PeerAddress;
            public int PeerTcpPort;
            public string ShareId;
            public string AuthMode;
        }
    }
}
