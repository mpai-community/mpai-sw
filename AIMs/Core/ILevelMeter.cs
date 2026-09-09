namespace Mpai.Core;

// A live audio level meter: the current input energy (RMS), 0..1. An acquisition
// device that implements this lets a consumer (e.g. Speech Object Acquisition doing
// voice-activity detection) watch the microphone level during capture - to wait for
// speech to start and to stop when it ends - without depending on the device type.
public interface ILevelMeter
{
    // The RMS energy of the most recent captured buffer, normalised to 0..1.
    double CurrentLevel { get; }
}
