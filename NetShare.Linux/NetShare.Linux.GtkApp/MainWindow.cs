using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Gtk;
using Path = System.IO.Path;
using NetShare.Linux.Core;
using NetShare.Linux.Core.Networking;
using NetShare.Linux.Core.Settings;
using NetShare.Linux.Core.Sharing;

namespace NetShare.Linux.GtkApp;

public sealed class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly SettingsStore _store;

    private readonly ListStore _peersStore;
    private readonly TreeView _peersView;
    private readonly Button _btnPeersRefresh;
    private readonly Button _btnPeersConnect;
    private readonly Button _btnPeersAddByIp;

    private readonly ListStore _sharesStore;
    private readonly TreeView _sharesView;
    private readonly Button _btnShareAdd;
    private readonly Button _btnShareRemove;
    private readonly Button _btnShareToggleRo;

    private readonly ListStore _remoteSharesStore;
    private readonly TreeView _remoteSharesView;
    private readonly Button _btnRemoteListShares;

    private readonly ListStore _remoteDirStore;
    private readonly TreeView _remoteDirView;
    private readonly Label _lblRemotePath;
    private readonly Button _btnRemoteUp;
    private readonly Button _btnDownload;
    private readonly Button _btnUpload;

    private readonly ListStore _transfersStore;
    private readonly TreeView _transfersView;

    private PeerClient? _client;
    private IPAddress? _connectedIp;
    private int _connectedPort;

    private string? _selectedShareId;
    private string _remotePath = "";

    private uint _refreshTimerId;

    // Peers refresh helpers (avoid losing selection / reordering)
    private string? _selectedPeerDeviceId;
    private string? _lastPeersFingerprint;

    public MainWindow(AppHost host, SettingsStore store) : base("NetShare (Linux GTK)")
    {
        _host = host;
        _store = store;

        SetDefaultSize(1100, 700);
        Resizable = true;

        // Root layout: vertical box
        var root = new VBox(false, 6);
        Add(root);

        root.PackStart(BuildMenuBar(), false, false, 0);

        // Replace the fixed 2x2 grid with draggable panes.
        // Left column: Peers (top) / Shares (bottom)
        // Right column: Remote Browse (top) / Transfers (bottom)
        var hpaned = new HPaned();
        var leftV = new VPaned();
        var rightV = new VPaned();

        // Give reasonable initial split positions.
        // Note: positions are in pixels and apply after the widgets are realized.
        hpaned.Position = 520;
        leftV.Position = 340;
        rightV.Position = 480;

        root.PackStart(hpaned, true, true, 0);

        hpaned.Pack1(leftV, resize: true, shrink: false);
        hpaned.Pack2(rightV, resize: true, shrink: false);

        // Peers
        _peersStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string));
        _peersView = new TreeView(_peersStore) { HeadersVisible = true };
        AddTextColumn(_peersView, "Name", 0);
        AddTextColumn(_peersView, "IP", 1);
        AddTextColumn(_peersView, "Status", 2);
        AddTextColumn(_peersView, "Last Seen", 3);
        AddTextColumn(_peersView, "TCP", 4);
        AddTextColumn(_peersView, "DeviceId", 5);
        _peersView.Columns[5].Visible = false;

        _btnPeersRefresh = new Button("Refresh");
        _btnPeersConnect = new Button("Connect");
        _btnPeersAddByIp = new Button("Add peer by IP…");

        _btnPeersRefresh.Clicked += (_, _) =>
        {
            Console.Error.WriteLine("[GtkApp] Discovery query sent");
            _host.Discovery.SendQuery();
            RefreshPeersModel();
        };

        _btnPeersConnect.Clicked += async (_, _) => await ConnectSelectedPeerAsync();
        _btnPeersAddByIp.Clicked += async (_, _) => await AddPeerByIpAndConnectAsync();

        leftV.Pack1(WrapPanel("Peers", _peersView, new[] { _btnPeersRefresh, _btnPeersConnect, _btnPeersAddByIp }), resize: true, shrink: false);

        // Shares
        _sharesStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(bool)); // id, name, path, ro
        _sharesView = new TreeView(_sharesStore) { HeadersVisible = true };
        AddTextColumn(_sharesView, "Name", 1);
        AddTextColumn(_sharesView, "Path", 2);
        AddTextColumn(_sharesView, "Read-only", 3);

        _btnShareAdd = new Button("Add Folder…");
        _btnShareRemove = new Button("Remove");
        _btnShareToggleRo = new Button("Toggle Read-only");

        _btnShareAdd.Clicked += (_, _) => AddShareFolder();
        _btnShareRemove.Clicked += (_, _) => RemoveSelectedShare();
        _btnShareToggleRo.Clicked += (_, _) => ToggleSelectedShareReadOnly();

        leftV.Pack2(WrapPanel("Shares", _sharesView, new[] { _btnShareAdd, _btnShareRemove, _btnShareToggleRo }), resize: true, shrink: false);

        // Remote browse
        _remoteSharesStore = new ListStore(typeof(string), typeof(string)); // shareId, name
        _remoteSharesView = new TreeView(_remoteSharesStore) { HeadersVisible = true };
        AddTextColumn(_remoteSharesView, "Share", 1);

        _btnRemoteListShares = new Button("List Shares");
        _btnRemoteListShares.Clicked += async (_, _) => await ListRemoteSharesAsync();

        _remoteDirStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool)); // name, type, size, mtime, isDir
        _remoteDirView = new TreeView(_remoteDirStore) { HeadersVisible = true };
        AddTextColumn(_remoteDirView, "Name", 0);
        AddTextColumn(_remoteDirView, "Type", 1);
        AddTextColumn(_remoteDirView, "Size", 2);
        AddTextColumn(_remoteDirView, "Modified", 3);

        _remoteDirView.RowActivated += async (_, args) =>
        {
            if (!_remoteDirStore.GetIter(out var iter, args.Path)) return;
            var isDir = (bool)_remoteDirStore.GetValue(iter, 4);
            var name = (string)_remoteDirStore.GetValue(iter, 0);
            if (!isDir) return;

            var next = string.IsNullOrEmpty(_remotePath) ? name : _remotePath.TrimEnd('/') + "/" + name;
            await RefreshRemoteDirAsync(next);
        };

        _lblRemotePath = new Label("(not connected)") { Xalign = 0 };

        _btnRemoteUp = new Button("Up");
        _btnRemoteUp.Clicked += async (_, _) =>
        {
            var up = ParentPath(_remotePath);
            await RefreshRemoteDirAsync(up);
        };

        _btnDownload = new Button("Download");
        _btnDownload.Clicked += async (_, _) => await DownloadSelectedAsync();

        _btnUpload = new Button("Upload");
        _btnUpload.Clicked += async (_, _) => await UploadAsync();

        var remoteBox = new VBox(false, 6);

        var shareRow = new HBox(false, 6);
        shareRow.PackStart(_btnRemoteListShares, false, false, 0);
        shareRow.PackStart(new Label("Remote shares:"), false, false, 0);
        remoteBox.PackStart(shareRow, false, false, 0);
        remoteBox.PackStart(WrapScroll(_remoteSharesView, 120), false, true, 0);

        var pathRow = new HBox(false, 6);
        pathRow.PackStart(new Label("Path:"), false, false, 0);
        pathRow.PackStart(_lblRemotePath, true, true, 0);
        pathRow.PackStart(_btnRemoteUp, false, false, 0);
        pathRow.PackStart(_btnDownload, false, false, 0);
        pathRow.PackStart(_btnUpload, false, false, 0);
        remoteBox.PackStart(pathRow, false, false, 0);
        remoteBox.PackStart(WrapScroll(_remoteDirView, 250), true, true, 0);

        rightV.Pack1(WrapPanel("Remote Browse", remoteBox, new[] { _btnRemoteUp, _btnDownload, _btnUpload }), resize: true, shrink: false);

        // Transfers
        _transfersStore = new ListStore(typeof(string), typeof(string), typeof(string), typeof(string)); // name, progress, status, id
        _transfersView = new TreeView(_transfersStore) { HeadersVisible = true };
        AddTextColumn(_transfersView, "Transfer", 0);
        AddTextColumn(_transfersView, "Progress", 1);
        AddTextColumn(_transfersView, "Status", 2);

        // Hidden ID column (avoid invalid syntax by building the column first)
        var idColumn = new TreeViewColumn("", new CellRendererText(), "text", 3) { Visible = false };
        _transfersView.AppendColumn(idColumn);

        rightV.Pack2(WrapPanel("Transfers", _transfersView, Array.Empty<Widget>()), resize: true, shrink: false);

        SetRemoteEnabled(false);

        // Periodic refresh (thread-safe) to compute Online/Offline and LastSeen formatting.
        _refreshTimerId = GLib.Timeout.Add(1000, () =>
        {
            RefreshPeersModel();
            RefreshSharesModel();
            return true;
        });

        // Also refresh on peer update events.
        _host.PeersChanged += () =>
        {
            // This event can be raised from any thread. Always marshal to GTK thread.
            GLib.Idle.Add(() =>
            {
                RefreshPeersModel();
                return false;
            });
        };

        RefreshPeersModel();
        RefreshSharesModel();
    }

    protected override void OnDestroyed()
    {
        try
        {
            if (_refreshTimerId != 0) GLib.Source.Remove(_refreshTimerId);
        }
        catch { }

        try { _client?.Dispose(); } catch { }

        base.OnDestroyed();
    }

    private MenuBar BuildMenuBar()
    {
        var menuBar = new MenuBar();

        // File
        var file = new MenuItem("File");
        var fileMenu = new Menu();
        var exit = new MenuItem("Exit");
        exit.Activated += (_, _) => Close();
        fileMenu.Append(exit);
        file.Submenu = fileMenu;

        // Share
        var share = new MenuItem("Share");
        var shareMenu = new Menu();
        var add = new MenuItem("Add Folder…");
        add.Activated += (_, _) => AddShareFolder();
        var rem = new MenuItem("Remove");
        rem.Activated += (_, _) => RemoveSelectedShare();
        var tog = new MenuItem("Toggle Read-Only");
        tog.Activated += (_, _) => ToggleSelectedShareReadOnly();
        shareMenu.Append(add);
        shareMenu.Append(rem);
        shareMenu.Append(tog);
        share.Submenu = shareMenu;

        // Peer
        var peer = new MenuItem("Peer");
        var peerMenu = new Menu();
        var refresh = new MenuItem("Refresh");
        refresh.Activated += (_, _) => _host.Discovery.SendQuery();
        var connect = new MenuItem("Connect");
        connect.Activated += async (_, _) => await ConnectSelectedPeerAsync();
        var addIp = new MenuItem("Add Peer by IP…");
        addIp.Activated += async (_, _) => await AddPeerByIpAndConnectAsync();
        peerMenu.Append(connect);
        peerMenu.Append(refresh);
        peerMenu.Append(addIp);
        peer.Submenu = peerMenu;

        // Settings
        var settings = new MenuItem("Settings");
        settings.Activated += (_, _) => ShowSettingsDialog();

        // Help
        var help = new MenuItem("Help");
        var helpMenu = new Menu();
        var about = new MenuItem("About");
        about.Activated += (_, _) => ShowInfo("NetShare", "NetShare Linux GTK UI\nLAN file sharing tool.");
        helpMenu.Append(about);
        help.Submenu = helpMenu;

        menuBar.Append(file);
        menuBar.Append(share);
        menuBar.Append(peer);
        menuBar.Append(settings);
        menuBar.Append(help);

        return menuBar;
    }

    private static Widget WrapScroll(Widget view, int minHeight)
    {
        var sc = new ScrolledWindow();
        sc.Add(view);
        sc.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        sc.SetSizeRequest(-1, minHeight);
        return sc;
    }

    private static Widget WrapPanel(string title, Widget body, IEnumerable<Widget> buttons)
    {
        var frame = new Gtk.Frame(title);
        var box = new VBox(false, 6) { BorderWidth = 6 };
        frame.Add(box);

        if (body is VBox || body is HBox)
        {
            box.PackStart(body, true, true, 0);
        }
        else
        {
            box.PackStart(WrapScroll(body, 150), true, true, 0);
        }

        var btnRow = new HBox(false, 6);
        foreach (var b in buttons) btnRow.PackStart(b, false, false, 0);
        if (buttons.Any()) box.PackStart(btnRow, false, false, 0);

        return frame;
    }

    private static void AddTextColumn(TreeView view, string title, int modelIndex)
    {
        var col = new TreeViewColumn { Title = title, Resizable = true };
        var cell = new CellRendererText();
        col.PackStart(cell, true);
        col.AddAttribute(cell, "text", modelIndex);
        view.AppendColumn(col);
    }

    private bool TryGetSelectedPeerDeviceId(out string? deviceId)
    {
        deviceId = null;
        if (!_peersView.Selection.GetSelected(out var model, out var iter)) return false;
        deviceId = (string)model.GetValue(iter, 5);
        return !string.IsNullOrWhiteSpace(deviceId);
    }

    private void RestorePeersSelection(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        if (!_peersStore.GetIterFirst(out var iter)) return;
        do
        {
            var id = (string)_peersStore.GetValue(iter, 5);
            if (string.Equals(id, deviceId, StringComparison.Ordinal))
            {
                _peersView.Selection.SelectIter(iter);
                _peersView.ScrollToCell(_peersStore.GetPath(iter), null, false, 0, 0);
                break;
            }
        } while (_peersStore.IterNext(ref iter));
    }

    private void RefreshPeersModel()
    {
        // Capture selection before modifying the model.
        var selectedBefore = _selectedPeerDeviceId;
        if (TryGetSelectedPeerDeviceId(out var current))
            selectedBefore = current;

        var peers = _host.GetPeersSnapshot()
            .OrderByDescending(p => p.Online)
            .ThenBy(p => p.DeviceName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Address?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.DeviceId ?? "", StringComparer.Ordinal)
            .ToList();

        // Build a small fingerprint so we don't thrash selection/focus on every timer tick.
        var fp = string.Join("|", peers.Select(p =>
            $"{p.DeviceId}:{p.Address}:{p.TcpPort}:{p.Online}:{p.LastSeenUtc.Ticks}:{p.DeviceName}"));

        if (string.Equals(fp, _lastPeersFingerprint, StringComparison.Ordinal))
        {
            // Nothing material changed; leave the model untouched.
            return;
        }

        _lastPeersFingerprint = fp;

        _peersStore.Clear();
        foreach (var p in peers)
        {
            var name = Ellipsize(p.DeviceName ?? "(unknown)", 24);
            var ip = p.Address?.ToString() ?? "";
            var status = p.Online ? "Online" : "Offline";
            var lastSeenLocal = p.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
            var tcp = p.TcpPort;
            var id = p.DeviceId ?? "";

            _peersStore.AppendValues(name, ip, status, lastSeenLocal, tcp, id);
        }

        RestorePeersSelection(selectedBefore);
    }

    private void RefreshSharesModel()
    {
        _sharesStore.Clear();
        foreach (var s in _host.Shares.GetShares())
        {
            _sharesStore.AppendValues(s.ShareId ?? "", s.Name ?? "", s.LocalPath ?? "", s.ReadOnly);
        }
    }

    private async Task ConnectSelectedPeerAsync()
    {
        if (!TryGetSelectedPeer(out var ip, out var port, out var peerName))
        {
            ShowError("Connect", "Select a peer first.");
            return;
        }

        await ConnectAsync(ip, port, peerName);
    }

    private async Task AddPeerByIpAndConnectAsync()
    {
        using var dlg = new Dialog("Add peer by IP", this, DialogFlags.Modal);
        dlg.AddButton("Cancel", ResponseType.Cancel);
        dlg.AddButton("Connect", ResponseType.Ok);

        // GtkSharp 3 exposes ContentArea (VBox was removed in newer bindings).
        var box = dlg.ContentArea;
        box.BorderWidth = 8;

        var ipEntry = new Entry { PlaceholderText = "IP address (e.g., 192.168.1.50)" };
        var portSpin = new SpinButton(NetShareProtocol.DefaultTcpPort, 65535, 1) { Value = NetShareProtocol.DefaultTcpPort };

        var row1 = new HBox(false, 6);
        row1.PackStart(new Label("IP:"), false, false, 0);
        row1.PackStart(ipEntry, true, true, 0);
        box.PackStart(row1, false, false, 0);

        var row2 = new HBox(false, 6);
        row2.PackStart(new Label("TCP port:"), false, false, 0);
        row2.PackStart(portSpin, false, false, 0);
        box.PackStart(row2, false, false, 0);

        dlg.ShowAll();
        var resp = (ResponseType)dlg.Run();
        dlg.Hide();

        if (resp != ResponseType.Ok) return;

        if (!IPAddress.TryParse(ipEntry.Text?.Trim(), out var ip))
        {
            ShowError("Add peer", "Invalid IP address.");
            return;
        }

        await ConnectAsync(ip, (int)portSpin.Value, ip.ToString());
    }

    private async Task ConnectAsync(IPAddress ip, int port, string peerName)
    {
        SetRemoteEnabled(false);

        try
        {
            Console.Error.WriteLine($"[GtkApp] Connecting to {ip}:{port}...");

            _client?.Dispose();
            _client = new PeerClient(_host.Settings);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _client.ConnectAsync(ip, port, cts.Token);
            await _client.HelloAndAuthAsync(cts.Token);

            _connectedIp = ip;
            _connectedPort = port;

            Console.Error.WriteLine($"[GtkApp] Connected to {peerName} ({ip}:{port})");

            SetRemoteEnabled(true);
            await ListRemoteSharesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GtkApp] Connect failed: {ex.Message}");
            ShowError("Connect failed", ex.Message);
            SetRemoteEnabled(false);
        }
    }

    private async Task ListRemoteSharesAsync()
    {
        if (_client is null)
        {
            ShowError("Remote", "Not connected.");
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var shares = await _client.ListSharesAsync(cts.Token);

            _remoteSharesStore.Clear();
            foreach (var s in shares)
            {
                var id = s.TryGetValue("shareId", out var idObj) ? idObj?.ToString() : "";
                var name = s.TryGetValue("name", out var nameObj) ? nameObj?.ToString() : id;
                if (string.IsNullOrWhiteSpace(id)) continue;
                _remoteSharesStore.AppendValues(id!, name ?? id!);
            }

            // auto-select first share
            if (_remoteSharesStore.GetIterFirst(out var iter))
            {
                _remoteSharesView.Selection.SelectIter(iter);
                _selectedShareId = (string)_remoteSharesStore.GetValue(iter, 0);
                await RefreshRemoteDirAsync("");
            }

            Console.Error.WriteLine($"[GtkApp] Listed remote shares: {shares.Count}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GtkApp] ListShares failed: {ex.Message}");
            ShowError("List shares", ex.Message);
        }
    }

    private async Task RefreshRemoteDirAsync(string path)
    {
        if (_client is null)
        {
            ShowError("Remote", "Not connected.");
            return;
        }

        // Update selected share
        if (_remoteSharesView.Selection.GetSelected(out var model, out var iter))
        {
            _selectedShareId = (string)model.GetValue(iter, 0);
        }

        if (string.IsNullOrWhiteSpace(_selectedShareId))
        {
            ShowError("Remote", "Select a remote share.");
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var entries = await _client.ListDirAsync(_selectedShareId!, path ?? "", cts.Token);

            _remotePath = NormalizeRemotePath(path);
            _lblRemotePath.Text = $"{_selectedShareId}:{(_remotePath == "" ? "/" : "/" + _remotePath)}";

            _remoteDirStore.Clear();
            foreach (var e in entries.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var type = e.IsDir ? "Dir" : "File";
                var size = e.IsDir ? "" : FormatBytes(e.Size);
                var mtime = e.MtimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                _remoteDirStore.AppendValues(e.Name, type, size, mtime, e.IsDir);
            }

            Console.Error.WriteLine($"[GtkApp] Listed remote dir '{_remotePath}' entries={entries.Count}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GtkApp] ListDir failed: {ex.Message}");
            ShowError("List directory", ex.Message);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (_client is null)
        {
            ShowError("Download", "Not connected.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_selectedShareId))
        {
            ShowError("Download", "Select a remote share.");
            return;
        }

        if (!_remoteDirView.Selection.GetSelected(out var model, out var iter))
        {
            ShowError("Download", "Select a file.");
            return;
        }

        var isDir = (bool)model.GetValue(iter, 4);
        if (isDir)
        {
            ShowError("Download", "Select a file (not a folder).");
            return;
        }

        var name = (string)model.GetValue(iter, 0);
        var remotePath = BuildRemotePath(_remotePath, name);

        // Save location: settings DownloadDirectory + name (flat). Keep it simple.
        var localPath = System.IO.Path.Combine(_host.Settings.DownloadDirectory, name);

        var transferRow = _transfersStore.AppendValues($"Download {name}", "0%", "Starting...", Guid.NewGuid().ToString());

        try
        {
            Console.Error.WriteLine($"[GtkApp] Download: share={_selectedShareId} path={remotePath} -> {localPath}");

            var progress = new Progress<(long done, long total)>(p =>
            {
                // Progress callback may be on worker thread.
                GLib.Idle.Add(() =>
                {
                    var pct = p.total <= 0 ? 0 : (int)Math.Min(100, (p.done * 100.0 / p.total));
                    _transfersStore.SetValue(transferRow, 1, $"{pct}% ({FormatBytes(p.done)}/{FormatBytes(p.total)})");
                    _transfersStore.SetValue(transferRow, 2, "Downloading");
                    return false;
                });
            });

            using var cts = new CancellationTokenSource();

            await Task.Run(async () =>
            {
                await _client.DownloadAsync(_selectedShareId!, remotePath, localPath, offset: 0, progress, cts.Token);
            });

            _transfersStore.SetValue(transferRow, 1, "100%");
            _transfersStore.SetValue(transferRow, 2, "Done");
        }
        catch (Exception ex)
        {
            _transfersStore.SetValue(transferRow, 2, "Error");
            Console.Error.WriteLine($"[GtkApp] Download failed: {ex.Message}");
            ShowError("Download failed", ex.Message);
        }
    }

    private async Task UploadAsync()
    {
        if (_client is null)
        {
            ShowError("Upload", "Not connected.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_selectedShareId))
        {
            ShowError("Upload", "Select a remote share.");
            return;
        }

        using var fc = new FileChooserDialog("Select file to upload", this, FileChooserAction.Open,
            "Cancel", ResponseType.Cancel,
            "Upload", ResponseType.Accept);

        if (fc.Run() != (int)ResponseType.Accept)
        {
            fc.Hide();
            return;
        }

        var localFile = fc.Filename;
        fc.Hide();

        if (string.IsNullOrWhiteSpace(localFile) || !File.Exists(localFile))
        {
            ShowError("Upload", "No file selected.");
            return;
        }

        // Destination path: upload into current remote directory (same filename).
        var name = System.IO.Path.GetFileName(localFile);
        var remotePath = BuildRemotePath(_remotePath, name);

        var transferRow = _transfersStore.AppendValues($"Upload {name}", "0%", "Starting...", Guid.NewGuid().ToString());

        try
        {
            Console.Error.WriteLine($"[GtkApp] Upload: {localFile} -> share={_selectedShareId} path={remotePath}");

            var progress = new Progress<(long done, long total)>(p =>
            {
                GLib.Idle.Add(() =>
                {
                    var pct = p.total <= 0 ? 0 : (int)Math.Min(100, (p.done * 100.0 / p.total));
                    _transfersStore.SetValue(transferRow, 1, $"{pct}% ({FormatBytes(p.done)}/{FormatBytes(p.total)})");
                    _transfersStore.SetValue(transferRow, 2, "Uploading");
                    return false;
                });
            });

            using var cts = new CancellationTokenSource();

            await Task.Run(async () =>
            {
                await _client.UploadAsync(_selectedShareId!, remotePath, localFile, progress, cts.Token);
            });

            _transfersStore.SetValue(transferRow, 1, "100%");
            _transfersStore.SetValue(transferRow, 2, "Done");
        }
        catch (Exception ex)
        {
            _transfersStore.SetValue(transferRow, 2, "Error");
            Console.Error.WriteLine($"[GtkApp] Upload failed: {ex.Message}");
            ShowError("Upload failed", ex.Message);
        }
    }

    private void AddShareFolder()
    {
        // Security constraint: only allow sharing folders explicitly selected.
        using var fc = new FileChooserDialog("Select folder to share", this, FileChooserAction.SelectFolder,
            "Cancel", ResponseType.Cancel,
            "Add", ResponseType.Accept);

        if (fc.Run() != (int)ResponseType.Accept)
        {
            fc.Hide();
            return;
        }

        var path = fc.Filename;
        fc.Hide();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowError("Add share", "Invalid folder.");
            return;
        }

        var share = new ShareInfo
        {
            ShareId = Guid.NewGuid().ToString("N"),
            Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
            LocalPath = path,
            ReadOnly = true
        };

        _host.Shares.Upsert(share);
        _host.SaveSettings();
        RefreshSharesModel();
    }

    private void RemoveSelectedShare()
    {
        if (!_sharesView.Selection.GetSelected(out var model, out var iter))
        {
            ShowError("Remove share", "Select a share.");
            return;
        }

        var id = (string)model.GetValue(iter, 0);
        if (string.IsNullOrWhiteSpace(id)) return;

        _host.Shares.Remove(id);
        _host.SaveSettings();
        RefreshSharesModel();
    }

    private void ToggleSelectedShareReadOnly()
    {
        if (!_sharesView.Selection.GetSelected(out var model, out var iter))
        {
            ShowError("Toggle read-only", "Select a share.");
            return;
        }

        var id = (string)model.GetValue(iter, 0);
        if (string.IsNullOrWhiteSpace(id)) return;

        if (_host.Shares.TryGetShare(id, out var s))
        {
            s.ReadOnly = !s.ReadOnly;
            _host.Shares.Upsert(s);
            _host.SaveSettings();
            RefreshSharesModel();
        }
    }

    private void ShowSettingsDialog()
    {
        var s = _host.Settings;

        using var dlg = new Dialog("Settings", this, DialogFlags.Modal);
        dlg.AddButton("Cancel", ResponseType.Cancel);
        dlg.AddButton("Save", ResponseType.Ok);

        // GtkSharp 3 exposes ContentArea (VBox was removed in newer bindings).
        var box = dlg.ContentArea;
        box.BorderWidth = 8;
        box.Spacing = 6;

        Entry deviceName = new() { Text = s.DeviceName ?? "" };
        SpinButton discPort = new(1, 65535, 1) { Value = s.DiscoveryPort };
        SpinButton tcpPort = new(1, 65535, 1) { Value = s.TcpPort };
        CheckButton openMode = new("Open mode") { Active = s.OpenMode };
        Entry accessKey = new() { Text = s.AccessKey ?? "" };
        Entry downloadDir = new() { Text = s.DownloadDirectory ?? "" };

        box.PackStart(FormRow("Device name", deviceName), false, false, 0);
        box.PackStart(FormRow("Discovery port", discPort), false, false, 0);
        box.PackStart(FormRow("TCP port", tcpPort), false, false, 0);
        box.PackStart(openMode, false, false, 0);
        box.PackStart(FormRow("Access key", accessKey), false, false, 0);
        box.PackStart(FormRow("Download directory", downloadDir), false, false, 0);

        var note = new Label("Note: Changing ports/device name may require restarting the app.") { Xalign = 0 };
        box.PackStart(note, false, false, 0);

        dlg.ShowAll();
        var resp = (ResponseType)dlg.Run();
        dlg.Hide();

        if (resp != ResponseType.Ok) return;

        // Persist changes
        s.DeviceName = deviceName.Text?.Trim() ?? s.DeviceName;
        s.DiscoveryPort = (int)discPort.Value;
        s.TcpPort = (int)tcpPort.Value;
        s.OpenMode = openMode.Active;
        s.AccessKey = accessKey.Text;
        s.DownloadDirectory = downloadDir.Text?.Trim() ?? s.DownloadDirectory;

        try
        {
            Directory.CreateDirectory(s.DownloadDirectory);
        }
        catch (Exception ex)
        {
            ShowError("Settings", $"Invalid download directory: {ex.Message}");
            return;
        }

        _store.Save(s);
        ShowInfo("Settings", "Saved. Restart the app to apply port changes.");
    }

    private static Widget FormRow(string label, Widget field)
    {
        var row = new HBox(false, 6);
        row.PackStart(new Label(label) { Xalign = 0, WidthRequest = 140 }, false, false, 0);
        row.PackStart(field, true, true, 0);
        return row;
    }

    private void SetRemoteEnabled(bool enabled)
    {
        _btnRemoteListShares.Sensitive = enabled;
        _remoteSharesView.Sensitive = enabled;
        _remoteDirView.Sensitive = enabled;
        _btnRemoteUp.Sensitive = enabled;
        _btnDownload.Sensitive = enabled;
        _btnUpload.Sensitive = enabled;

        if (!enabled)
        {
            _remoteSharesStore.Clear();
            _remoteDirStore.Clear();
            _selectedShareId = null;
            _remotePath = "";
            _lblRemotePath.Text = "(not connected)";
        }
    }

    private bool TryGetSelectedPeer(out IPAddress ip, out int port, out string name)
    {
        ip = IPAddress.None;
        port = 0;
        name = "";

        if (!_peersView.Selection.GetSelected(out var model, out var iter))
            return false;

        name = (string)model.GetValue(iter, 0);
        var ipStr = (string)model.GetValue(iter, 1);
        port = (int)model.GetValue(iter, 4);

        return IPAddress.TryParse(ipStr, out ip);
    }

    private static string Ellipsize(string text, int max)
    {
        if (text.Length <= max) return text;
        return text.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    private static string ParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('\\', '/').Trim('/');
        var idx = p.LastIndexOf('/');
        return idx < 0 ? "" : p.Substring(0, idx);
    }

    private static string NormalizeRemotePath(string? path)
    {
        var p = (path ?? "").Replace('\\', '/').Trim('/');
        return p;
    }

    private static string BuildRemotePath(string dir, string name)
    {
        dir = NormalizeRemotePath(dir);
        name = (name ?? "").Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(dir) ? name : dir + "/" + name;
    }

    private void ShowError(string title, string message)
    {
        GLib.Idle.Add(() =>
        {
            using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Error, ButtonsType.Ok, message);
            md.Title = title;
            md.Run();
            md.Destroy();
            return false;
        });
    }

    private void ShowInfo(string title, string message)
    {
        GLib.Idle.Add(() =>
        {
            using var md = new MessageDialog(this, DialogFlags.Modal, MessageType.Info, ButtonsType.Ok, message);
            md.Title = title;
            md.Run();
            md.Destroy();
            return false;
        });
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "";

        var b = Math.Max(0, bytes.Value);
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };

        double v = b;
        var unit = 0;
        while (v >= 1024 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }

        if (unit == 0) return $"{b} {units[unit]}";
        if (v >= 100) return $"{v:0} {units[unit]}";
        if (v >= 10) return $"{v:0.#} {units[unit]}";
        return $"{v:0.##} {units[unit]}";
    }

    private static string FormatBytes(long bytes)
    {
        return FormatBytes((long?)bytes);
    }
}
