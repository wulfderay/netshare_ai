using System;
using System.Windows.Forms;
using NetShare.Core.Protocol;

namespace NetShare.App.Dialogs
{
    public sealed class AddPeerForm : Form
    {
        private readonly TextBox _txtName = new TextBox();
        private readonly TextBox _txtIp = new TextBox();
        private readonly NumericUpDown _numTcp = new NumericUpDown();

        public string PeerName => _txtName.Text.Trim();
        public string PeerIp => _txtIp.Text.Trim();
        public int TcpPort => (int)_numTcp.Value;

        public AddPeerForm()
        {
            Text = "Add Peer by IP";
            Width = 420;
            Height = 200;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

            _numTcp.Minimum = 1024;
            _numTcp.Maximum = 65535;
            _numTcp.Value = NetShareProtocol.DefaultTcpPort;

            table.Controls.Add(new Label { Text = "Name", AutoSize = true }, 0, 0);
            table.Controls.Add(_txtName, 1, 0);

            table.Controls.Add(new Label { Text = "IP address", AutoSize = true }, 0, 1);
            table.Controls.Add(_txtIp, 1, 1);

            table.Controls.Add(new Label { Text = "TCP port", AutoSize = true }, 0, 2);
            table.Controls.Add(_numTcp, 1, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 1, 3);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
