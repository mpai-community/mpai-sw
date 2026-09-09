using System;
using System.Threading.Tasks;

using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

using Mpai.Core;

namespace Mpai.Aims.Visual;

// Live camera Visual Object Acquisition using the NATIVE Windows Media Capture
// API (Windows.Media.Capture) - no OpenCV. Grabs a single JPEG photo from the
// default camera and returns it as a Basic Visual Object, satisfying the same
// IVisualAcquisitionAim contract as the other acquisitions, so it drops straight
// into the VOA slot in a provider.
//
// Windows Media Capture initialises the device, lets it settle, and captures one
// still to a JPEG-encoded in-memory stream via LowLagPhotoCapture. The device is
// initialised and disposed per capture so back-to-back grabs each start clean.
//
// Diagnostics (content trace) are written to the user's Downloads folder, never
// to the working tree.
public sealed class WebcamVisualAcquisition : IVisualAcquisitionAim
{
    private const string DiagLog = @"C:\Users\Leonardo\Downloads\cam-diag.log";

    private readonly int _settleMs;

    // settleMs: time to let the sensor's auto-exposure / auto-white-balance
    // settle after the device starts, before the still we keep.
    public WebcamVisualAcquisition(int cameraIndex = 0, int settleMs = 800)
    {
        _settleMs = Math.Max(0, settleMs);
    }

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(DiagLog, s + System.Environment.NewLine); } catch { }
    }

    public async Task<BasicVisualObject> AcquireAsync(VisualAcquisitionRequest request)
    {
        byte[] jpeg = await CaptureJpegAsync();
        Diag("cam: jpeg bytes=" + jpeg.Length);
        AimLog.Write("CVE-VOA-V1.0", $"acquired webcam frame: {jpeg.Length:N0} bytes JPEG (Windows Media Capture)");
        return BasicVisualObject.FromFile("webcam.jpg", jpeg, request.VisualObjectType);
    }

    private async Task<byte[]> CaptureJpegAsync()
    {
        MediaCapture? capture = null;
        try
        {
            capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video
            };
            await capture.InitializeAsync(settings);
            Diag("cam: MediaCapture initialised");

            // Let auto-exposure / white-balance settle before the still.
            await Task.Delay(_settleMs);

            var format = ImageEncodingProperties.CreateJpeg();
            var lowlag = await capture.PrepareLowLagPhotoCaptureAsync(format);

            var photo = await lowlag.CaptureAsync();
            using (var frame = photo.Frame)   // CapturedFrame : IRandomAccessStreamWithContentType (an IInputStream)
            {
                var size = (uint)frame.Size;
                using var reader = new DataReader(frame);
                reader.InputStreamOptions = InputStreamOptions.None;
                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                await lowlag.FinishAsync();
                Diag("cam: captured frame bytes=" + bytes.Length + " size=" + size);
                return bytes;
            }
        }
        catch (Exception ex)
        {
            Diag("cam: ERROR " + ex.GetType().Name + " " + ex.Message);
            throw new InvalidOperationException("Windows Media Capture failed: " + ex.Message, ex);
        }
        finally
        {
            capture?.Dispose();
        }
    }
}
