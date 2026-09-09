using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Mpai.Aims.Visual;

internal sealed class ImagePickerForm : Form
{
    private static readonly string[] Ext =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    private string _folder;
    private readonly ListBox    _list    = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _preview = new()
    {
        Dock        = DockStyle.Fill,
        SizeMode    = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle
    };

    public string  Folder        => _folder;
    public string? SelectedImage { get; private set; }

    public ImagePickerForm(string folder)
    {
        _folder = folder;
        Width   = 840;
        Height  = 560;
        StartPosition = FormStartPosition.CenterScreen;

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_preview);

        var bottom = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            Height        = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(8)
        };
        var ok     = new Button { Text = "Select",          Width = 90,  DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel",          Width = 90,  DialogResult = DialogResult.Cancel };
        var change = new Button { Text = "Change Folder\u2026", Width = 130 };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(change);

        Controls.Add(split);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        _list.SelectedIndexChanged += (_, _) => ShowPreview();
        ok.Click += (_, _) =>
        {
            SelectedImage = _list.SelectedItem is string s
                ? Path.Combine(_folder, s) : null;
        };
        change.Click += (_, _) => ChangeFolder();

        LoadImages();
    }

    private void LoadImages()
    {
        _list.Items.Clear();
        var names = Directory.EnumerateFiles(_folder)
            .Where(f => Ext.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray();

        foreach (var n in names)
            _list.Items.Add(n!);

        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;

        Text = $"CVE-VOA  \u2014  {_folder}  ({_list.Items.Count} images)";
    }

    private void ShowPreview()
    {
        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();

        if (_list.SelectedItem is string s)
        {
            var path = Path.Combine(_folder, s);
            try
            {
                // Load via Bitmap directly to avoid GDI+ retaining the stream.
                _preview.Image = new Bitmap(path);
            }
            catch { }
        }
    }

    private void ChangeFolder()
    {
        using var fbd = new FolderBrowserDialog { SelectedPath = _folder };
        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            _folder = fbd.SelectedPath;
            LoadImages();
        }
    }
}
