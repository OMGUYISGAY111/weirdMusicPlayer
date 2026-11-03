using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MusicPlayer.Host;

/// <summary>
/// 让用户挑数据目录的对话框。三种场景会用到：
///   · 第一次启动（默认的 D:\MusicPlayerData 还不存在）；
///   · 上次选的目录不可用（U 盘拔掉、盘符变了）；
///   · 启动参数 --pick-user-data 主动要求重选。
///
/// 选中且校验可写后，<see cref="SelectedPath"/> 才有值；取消时保持为空，调用方沿用默认位置。
/// </summary>
internal sealed class DataDirectoryDialog : Form
{
    private readonly TextBox _path;
    private readonly bool _firstRun;

    public DataDirectoryDialog(string suggestedPath, string reason, bool firstRun)
    {
        _firstRun = firstRun;

        Text = firstRun ? "选择数据目录" : "重新选择数据目录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        // 按 DPI 缩放而不是按字体缩放：换字体不会把整个对话框缩小一圈
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(250, 250, 251);
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(560, 208);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18, 16, 18, 14),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

        var caption = new Label
        {
            Text = reason,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 10),
            ForeColor = Color.FromArgb(32, 34, 40),
        };

        _path = new TextBox
        {
            Text = suggestedPath,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        _path.SelectAll();

        var browseRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        browseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        browseRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        browseRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var browse = new Button
        {
            Text = "浏览…",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
            Padding = new Padding(10, 2, 10, 2),
        };
        browse.Click += (_, _) => BrowseForFolder();

        browseRow.Controls.Add(_path, 0, 0);
        browseRow.Controls.Add(browse, 1, 0);

        var hint = new Label
        {
            Text = "播放列表、CEF 缓存、诊断日志和设置都存在这个目录里。\n" +
                   "以后想换地方可以用播放器里的「设置 → 数据目录」，改完重启生效；\n" +
                   "把选择结果写进 exe 同目录的 userdata.txt，删掉它就回到默认位置。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 12, 0, 0),
            ForeColor = Color.FromArgb(110, 116, 130),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0),
        };

        var ok = new Button
        {
            Text = _firstRun ? "就用这里" : "确定",
            AutoSize = true,
            Padding = new Padding(14, 3, 14, 3),
            Margin = new Padding(8, 0, 0, 0),
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Text = _firstRun ? $"用默认（{DataLocation.DefaultPath}）" : "取消",
            AutoSize = true,
            Padding = new Padding(12, 3, 12, 3),
            Margin = new Padding(8, 0, 0, 0),
        };
        cancel.Click += (_, _) =>
        {
            SelectedPath = string.Empty;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        table.Controls.Add(caption, 0, 0);
        table.Controls.Add(browseRow, 0, 1);
        table.Controls.Add(hint, 0, 2);
        table.Controls.Add(buttons, 0, 3);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>用户选中并通过可写校验的目录（绝对路径）；取消时为空字符串</summary>
    public string SelectedPath { get; private set; } = string.Empty;

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择数据目录（播放列表、缓存、日志、设置都存在这里）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var current = _path.Text.Trim();
        if (current.Length > 0)
        {
            var full = DataLocation.Normalize(current);
            if (Directory.Exists(full))
            {
                dialog.SelectedPath = full;
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.SelectedPath;
        }
    }

    private void Accept()
    {
        var raw = _path.Text.Trim();
        if (raw.Length == 0)
        {
            MessageBox.Show(this, "请填一个目录，或点「浏览…」挑一个。", "还没选目录",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var full = DataLocation.Normalize(raw);
        if (!DataLocation.TryPrepare(full, out var message))
        {
            MessageBox.Show(this, message, "这个目录不能用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SelectedPath = full;
        DialogResult = DialogResult.OK;
        Close();
    }
}
