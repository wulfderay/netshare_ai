using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using NetShare.Core.Networking;
using NetShare.Core.Settings;

namespace NetShare.App.Dialogs
{
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _txtName = new TextBox();
        private readonly NumericUpDown _numUdp = new NumericUpDown();
        private readonly NumericUpDown _numTcp = new NumericUpDown();
        private readonly CheckBox _chkOpen = new CheckBox();
        private readonly TextBox _txtKey = new TextBox();
        private readonly TextBox _txtDownload = new TextBox();
        private readonly CheckBox _chkFileLog = new CheckBox();
        private readonly ComboBox _cmbNic = new ComboBox();

        public AppSettings Result { get; private set; }

        public SettingsForm(AppSettings current)
        {
            Text = "Settings";
            Width = 520;
            Height = 400;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(10) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

            _txtName.Text = current.DeviceName;
            _numUdp.Minimum = 1024; _numUdp.Maximum = 65535; _numUdp.Value = current.DiscoveryPort;
            _numTcp.Minimum = 1024; _numTcp.Maximum = 65535; _numTcp.Value = current.TcpPort;

            _chkOpen.Text = "Open mode (no access key)";
            _chkOpen.Checked = current.OpenMode;
            _chkOpen.CheckedChanged += (s, e) => _txtKey.Enabled = !_chkOpen.Checked;

            _txtKey.Text = current.AccessKey ?? "";
            _txtKey.PasswordChar = '●';
            _txtKey.Enabled = !current.OpenMode;

            _txtDownload.Text = current.DownloadDirectory;
            var btnBrowse = new Button { Text = "Browse..." };
            btnBrowse.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog { Description = "Select download directory" })
                {
                    if (fbd.ShowDialog(this) == DialogResult.OK) _txtDownload.Text = fbd.SelectedPath;
                }
            };

            _chkFileLog.Text = "Write event log to disk";
            _chkFileLog.Checked = current.EnableFileLogging;

            _cmbNic.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbNic.Width = 340;
            _cmbNic.Items.Add(new NicItem { Id = "", Text = "Auto (recommended)" });
            foreach (var opt in NetworkSelection.GetIPv4AdapterOptions())
            {
                _cmbNic.Items.Add(new NicItem { Id = opt.InterfaceId ?? "", Text = opt.ToString() });
            }
            SelectNicItem(current.PreferredInterfaceId);

            table.Controls.Add(new Label { Text = "Device name", AutoSize = true }, 0, 0);
            table.Controls.Add(_txtName, 1, 0);

            table.Controls.Add(new Label { Text = "UDP discovery port", AutoSize = true }, 0, 1);
            table.Controls.Add(_numUdp, 1, 1);

            table.Controls.Add(new Label { Text = "TCP service port", AutoSize = true }, 0, 2);
            table.Controls.Add(_numTcp, 1, 2);

            table.Controls.Add(_chkOpen, 1, 3);

            table.Controls.Add(new Label { Text = "Access key", AutoSize = true }, 0, 4);
            table.Controls.Add(_txtKey, 1, 4);

            var dlPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            _txtDownload.Width = 260;
            dlPanel.Controls.Add(_txtDownload);
            dlPanel.Controls.Add(btnBrowse);
            table.Controls.Add(new Label { Text = "Download directory", AutoSize = true }, 0, 5);
            table.Controls.Add(dlPanel, 1, 5);

            table.Controls.Add(_chkFileLog, 1, 6);

            table.Controls.Add(new Label { Text = "Discovery adapter", AutoSize = true }, 0, 7);
            table.Controls.Add(_cmbNic, 1, 7);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                var nic = _cmbNic.SelectedItem as NicItem;

                // Preserve configured shares across settings edits.
                var shares = new List<ConfiguredShare>();
                if (current.Shares != null)
                {
                    shares.AddRange(current.Shares.Select(x => new ConfiguredShare
                    {
                        ShareId = x.ShareId,
                        Name = x.Name,
                        LocalPath = x.LocalPath,
                        ReadOnly = x.ReadOnly
                    }));
                }

                Result = new AppSettings
                {
                    DeviceId = current.DeviceId,
                    DeviceName = _txtName.Text.Trim(),
                    DiscoveryPort = (int)_numUdp.Value,
                    TcpPort = (int)_numTcp.Value,
                    PreferredInterfaceId = nic == null ? "" : (nic.Id ?? ""),
                    OpenMode = _chkOpen.Checked,
                    AccessKey = _txtKey.Text,
                    EnableFileLogging = _chkFileLog.Checked,
                    DownloadDirectory = _txtDownload.Text,
                    Shares = shares
                };
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 1, 8);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private sealed class NicItem
        {
            public string Id;
            public string Text;
            public override string ToString() => Text ?? "";
        }

        private void SelectNicItem(string id)
        {
            if (_cmbNic.Items.Count == 0) return;

            int autoIndex = 0;
            for (int i = 0; i < _cmbNic.Items.Count; i++)
            {
                var it = _cmbNic.Items[i] as NicItem;
                if (it == null) continue;
                if (string.IsNullOrWhiteSpace(it.Id)) autoIndex = i;
                if (!string.IsNullOrWhiteSpace(id) && string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbNic.SelectedIndex = i;
                    return;
                }
            }

            _cmbNic.SelectedIndex = autoIndex;
        }
    }
}
