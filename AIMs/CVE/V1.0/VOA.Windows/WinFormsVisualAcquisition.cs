using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

using Mpai.Core;

namespace Mpai.Aims.Visual;

public sealed class WinFormsVisualAcquisition : IVisualAcquisitionAim
{
    private string? _folder;

    public WinFormsVisualAcquisition(string? initialFolder = null) =>
        _folder = initialFolder;

    public Task<BasicVisualObject> AcquireAsync(VisualAcquisitionRequest request)
    {
        // Must run on the STA thread - marshal if called from background thread.
        var mainForm = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (mainForm is not null && mainForm.InvokeRequired)
        {
            BasicVisualObject? result = null;
            Exception? error = null;
            mainForm.Invoke(() =>
            {
                try   { result = RunDialog(request); }
                catch (Exception ex) { error = ex; }
            });
            if (error is not null) throw error;
            return Task.FromResult(result!);
        }
        return Task.FromResult(RunDialog(request));
    }

    private BasicVisualObject RunDialog(VisualAcquisitionRequest request)
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "Select the folder containing images"
            };
            var hint = request.SourcePath;
            if (hint is not null && Directory.Exists(hint))
                fbd.SelectedPath = hint;
            else if (hint is not null && File.Exists(hint))
                fbd.SelectedPath = Path.GetDirectoryName(hint);

            if (fbd.ShowDialog() != DialogResult.OK)
                throw new OperationCanceledException("No image folder selected.");
            _folder = fbd.SelectedPath;
        }

        using var picker = new ImagePickerForm(_folder);
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedImage is null)
            throw new OperationCanceledException("No image selected.");

        _folder = picker.Folder;
        var chosen = picker.SelectedImage;
        var bytes  = File.ReadAllBytes(chosen);
        AimLog.Write("CVE-VOA-V1.0", $"acquired image: {chosen} ({bytes.Length:N0} bytes)");
        return BasicVisualObject.FromFile(chosen, bytes);
    }
}
