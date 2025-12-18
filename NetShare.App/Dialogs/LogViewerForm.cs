using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using NetShare.Core.Logging;

namespace NetShare.App.Dialogs
{
    public sealed class LogViewerForm : Form
    {
        private readonly ListView _lv = new ListView();
        private readonly Button _btnCopy = new Button();
        private readonly Button _btnClear = new Button();
        private readonly ComboBox _cmbMinLevel = new ComboBox();

        private long _maxSeqObserved;
        private LogLevel _minLevel = LogLevel.Info;

        private readonly Timer _pollTimer = new Timer();

        public LogViewerForm()
        {
            Text = "Event Log";
            Width = 980;
            Height = 520;
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();

            // Subscribe first; de-dupe via sequence.
            Logger.EntryAdded += Logger_EntryAdded;
            LoadSnapshot();

            // Polling ensures we never drop entries if EntryAdded callbacks arrive out-of-order.
            _pollTimer.Interval = 250;
            _pollTimer.Tick += (s, e) => FlushNewEntries();
            _pollTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { _pollTimer.Stop(); } catch { }
            Logger.EntryAdded -= Logger_EntryAdded;
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };

            buttons.Controls.Add(new Label { Text = "Min level:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });

            _cmbMinLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMinLevel.Width = 120;
            _cmbMinLevel.Items.AddRange(new object[] { "Debug", "Info", "Warn", "Error" });
            _cmbMinLevel.SelectedIndex = 1;
            _cmbMinLevel.SelectedIndexChanged += (s, e) =>
            {
                _minLevel = ParseLevel(_cmbMinLevel.SelectedItem as string);
                LoadSnapshot();
            };
            buttons.Controls.Add(_cmbMinLevel);

            _btnCopy.Text = "Copy";
            _btnCopy.AutoSize = true;
            _btnCopy.Click += (s, e) => CopyToClipboard();

            _btnClear.Text = "Clear";
            _btnClear.AutoSize = true;
            _btnClear.Click += (s, e) => ClearLog();

            buttons.Controls.Add(_btnCopy);
            buttons.Controls.Add(_btnClear);

            _lv.Dock = DockStyle.Fill;
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.HideSelection = false;

            _lv.Columns.Add("Time", 150);
            _lv.Columns.Add("Level", 70);
            _lv.Columns.Add("Source", 120);
            _lv.Columns.Add("Message", 600);

            root.Controls.Add(buttons, 0, 0);
            root.Controls.Add(_lv, 0, 1);

            Controls.Add(root);
        }

        private void LoadSnapshot()
        {
            var entries = Logger.Snapshot();
            _lv.BeginUpdate();
            try
            {
                _lv.Items.Clear();
                _maxSeqObserved = 0;
                foreach (var e in entries)
                {
                    ProcessEntry(e);
                }
            }
            finally
            {
                _lv.EndUpdate();
            }
        }

        private void Logger_EntryAdded(LogEntry entry)
        {
            if (IsDisposed) return;

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    FlushNewEntries();
                }));
            }
            catch
            {
                // ignore shutdown races
            }
        }

        private void FlushNewEntries()
        {
            var newEntries = Logger.GetSince(_maxSeqObserved);
            if (newEntries == null || newEntries.Count == 0) return;

            var list = new List<LogEntry>(newEntries);
            list.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            _lv.BeginUpdate();
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    ProcessEntry(list[i]);
                }
            }
            finally
            {
                _lv.EndUpdate();
            }
        }

        private void ProcessEntry(LogEntry entry)
        {
            if (entry == null) return;

            // Always advance observed sequence even if filtered out.
            if (entry.Sequence <= _maxSeqObserved) return;
            _maxSeqObserved = entry.Sequence;

            if (entry.Level < _minLevel) return;

            var it = new ListViewItem(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            it.SubItems.Add(entry.Level.ToString());
            it.SubItems.Add(string.IsNullOrWhiteSpace(entry.Source) ? "" : entry.Source);

            var msg = entry.Message ?? "";
            if (!string.IsNullOrWhiteSpace(entry.ExceptionText))
            {
                msg = msg + " | " + entry.ExceptionText;
            }
            it.SubItems.Add(msg);

            it.Tag = entry;
            _lv.Items.Add(it);

            // Keep newest visible.
            if (_lv.Items.Count > 0)
            {
                _lv.EnsureVisible(_lv.Items.Count - 1);
            }
        }

        private void ClearLog()
        {
            Logger.Clear();
            _lv.Items.Clear();
            _maxSeqObserved = 0;
        }

        private static LogLevel ParseLevel(string text)
        {
            if (string.Equals(text, "Info", StringComparison.OrdinalIgnoreCase)) return LogLevel.Info;
            if (string.Equals(text, "Warn", StringComparison.OrdinalIgnoreCase)) return LogLevel.Warn;
            if (string.Equals(text, "Error", StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
            return LogLevel.Debug;
        }

        private void CopyToClipboard()
        {
            try
            {
                var items = new List<ListViewItem>();
                if (_lv.SelectedItems.Count > 0)
                {
                    foreach (ListViewItem it in _lv.SelectedItems) items.Add(it);
                }
                else
                {
                    foreach (ListViewItem it in _lv.Items) items.Add(it);
                }

                var sb = new StringBuilder();
                sb.AppendLine("Time\tLevel\tSource\tMessage");
                foreach (var it in items)
                {
                    var time = it.SubItems.Count > 0 ? it.SubItems[0].Text : "";
                    var level = it.SubItems.Count > 1 ? it.SubItems[1].Text : "";
                    var src = it.SubItems.Count > 2 ? it.SubItems[2].Text : "";
                    var msg = it.SubItems.Count > 3 ? it.SubItems[3].Text : "";
                    sb.AppendLine(time + "\t" + level + "\t" + src + "\t" + msg);
                }

                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Copy failed: " + ex.Message, "Copy failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
