using System;
using System.Threading.Tasks;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Tod;

// 3D Model Object Delivery device that renders to a WebView-hosted 3D renderer.
// It is 3OD's device (like WinmmSpeechDelivery is SOD's): 3OD calls DeliverAsync,
// and this device hands the 3D scene - the model plus its Face/Body animation - to
// the renderer. The actual message-post to the WebView is supplied by the host
// application as a delegate, so this device carries no WebView2 dependency; the
// app owns the display surface and wires the post.
public sealed class WebView3DModelDelivery : I3DModelDeliveryAim
{
    private readonly Func<string, Task> _postToRenderer;

    // postToRenderer: given a JSON message, deliver it to the WebView renderer
    // (the app implements this via WebView2 PostWebMessageAsJson on the UI thread).
    public WebView3DModelDelivery(Func<string, Task> postToRenderer)
        => _postToRenderer = postToRenderer;

    public async Task DeliverAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation = null,
        BodyDescriptorsObject? bodyAnimation = null)
    {
        // Package the 3D scene for the renderer: the model reference + the face
        // animation timeline (FDO). The renderer already holds the avatar model
        // (bundled), so the model travels by reference; the animation is the FDO.
        var message = MpaiJson.ToJson(new RendererMessage
        {
            Kind = "render",
            FaceDescriptors = faceAnimation is null ? null : MpaiJson.ToJson(faceAnimation)
        });
        await _postToRenderer(message);
    }

    // A convenience for the app: deliver model + face animation + the speech (WAV
    // base64) together, so the renderer plays the audio and animates in sync.
    public async Task DeliverWithSpeechAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation,
        byte[] speechWav)
    {
        var message = MpaiJson.ToJson(new RendererMessage
        {
            Kind = "render",
            FaceDescriptors = faceAnimation is null ? null : MpaiJson.ToJson(faceAnimation),
            SpeechWavBase64 = speechWav is { Length: > 0 } ? Convert.ToBase64String(speechWav) : null
        });
        await _postToRenderer(message);
    }

    private sealed class RendererMessage
    {
        public string Kind { get; init; } = "render";
        public string? FaceDescriptors { get; init; }
        public string? SpeechWavBase64 { get; init; }
    }
}
