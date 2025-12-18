using System;
using System.Text;
using System.Windows.Forms;
using NetShare.Core.Logging;

namespace NetShare.App.Dialogs
{
    internal sealed class ErrorDialog : Form
    {
        private readonly TextBox _txt;
        private readonly Button _btnCopy;
        private readonly Button _btnClose;

        private ErrorDialog(string title, string message, string details)
        {
            Text = title ?? "Error";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Width = 760;
            Height = 420;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = string.IsNullOrWhiteSpace(message) ? "An error occurred." : message
            };

            _txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                HideSelection = false,
                Text = details ?? ""
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            _btnClose = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            _btnCopy = new Button { Text = "Copy", AutoSize = true };
            _btnCopy.Click += (s, e) => CopyDetailsToClipboard();

            buttons.Controls.Add(_btnClose);
            buttons.Controls.Add(_btnCopy);

            layout.Controls.Add(lbl, 0, 0);
            layout.Controls.Add(_txt, 0, 1);
            layout.Controls.Add(buttons, 0, 2);

            Controls.Add(layout);

            AcceptButton = _btnClose;
            CancelButton = _btnClose;
        }

        public static void ShowException(IWin32Window owner, string title, Exception ex)
        {
            var details = ex == null ? "" : ex.ToString();
            var message = ex == null ? "" : ex.Message;

            try
            {
                Logger.Error("UI", (string.IsNullOrWhiteSpace(title) ? "Error" : title) + ": " + message, ex);
            }
            catch
            {
                // avoid any logging-related crashes when showing errors
            }

            using (var dlg = new ErrorDialog(title, message, details))
            {
                dlg.ShowDialog(owner);
            }
        }

        private void CopyDetailsToClipboard()
        {
            try
            {
                var text = _txt.Text ?? "";
                Clipboard.SetText(text);
                MessageBox.Show(this, "Copied to clipboard.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Failed to copy to clipboard.");
                sb.AppendLine();
                sb.AppendLine(ex.Message);
                MessageBox.Show(this, sb.ToString(), "Copy failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
