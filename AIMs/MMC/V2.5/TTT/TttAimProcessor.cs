using AIF.Controller;
using Mmc.Ttt;
using Mpai.Core;

namespace Mpai.Aims.Ttt;

// MMC-TTT-V2.5 â€” self-contained IAimProcessor.
//
// TTT is the first AIM in this code base with TWO input ports of the same
// DataType: Input Text (PortNumber 1) and Recognised Text (PortNumber 2), both
// OSD-TXO-V1.5. Routing is by DataType, so the ordinal is what tells them
// apart - here, and in the Topology that feeds them.
//
// Which one gets translated:
//   only one present  -> that one, and the Media Selector is ignored
//   both present      -> the Media Selector decides
//   both present, no selector -> Input Text, on the grounds that a caller who
//                                supplied text explicitly meant it
//   neither present   -> an error; there is nothing to translate
public sealed class TttAimProcessor : IAimProcessor
{
    private const string TextType     = "OSD-BTO-V1.5";
    private const string SelectorType = "OSD-SEL-V1.5";

    private readonly string  _inputTextPort;
    private readonly string  _recognisedTextPort;
    private readonly string  _languageSelectorPort;
    private readonly string  _mediaSelectorPort;
    private readonly string  _outputPort;
    private readonly ITttAim _ttt;

    public string InstanceId { get; }

    public TttAimProcessor(
        string   instanceId,
        ITttAim  ttt,
        AimPortReader ports)
    {
        InstanceId = instanceId;
        _ttt       = ttt;
        _inputTextPort      = ports.Input(TextType, 1);
        _recognisedTextPort = ports.Input(TextType, 2);
        _languageSelectorPort   = ports.Input(SelectorType, 1);
        _mediaSelectorPort     = ports.InputOrDefault(SelectorType, 2, string.Empty);
        _outputPort         = ports.Output(TextType);
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var inputText      = Read(message, _inputTextPort);
        var recognisedText = Read(message, _recognisedTextPort);

        if (inputText is null && recognisedText is null)
        {
            return Message.Error(
                message.MessageId,
                InstanceId,
                "Neither Input Text nor Recognised Text was supplied; nothing to translate.");
        }

        var chosen =
            inputText is not null && recognisedText is not null
                ? ChooseSource(message) == TextSource.RecognisedText
                      ? recognisedText
                      : inputText
                : inputText ?? recognisedText!;

        var languages =
            ReadSelector(message, _languageSelectorPort)
            ?? new BasicSelectorObject();

        var translated =
            await _ttt.ProcessAsync(
                chosen,
                languages);

        var json = MpaiJson.ToJson(translated);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicTextObject",
            DataType    = TextType,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    // Both texts arrived: the Media Selector decides, defaulting to the
    // explicitly supplied Input Text when no selector was given.
    private TextSource ChooseSource(Message message)
    {
        var selector =
            _mediaSelectorPort.Length > 0
                ? ReadSelector(message, _mediaSelectorPort)
                : null;

        return selector?.TranslateFrom ?? TextSource.InputText;
    }

    private static BasicTextObject? Read(Message message, string port) =>
        port.Length > 0 &&
        message.Ports.TryGetValue(port, out var json) &&
        !string.IsNullOrWhiteSpace(json)
            ? NonEmpty(MpaiJson.FromJson<BasicTextObject>(json))
            : null;

    // An empty Text Object is how a caller says "I am not using this branch"
    // (the AMQ suspend/resume test does exactly that), so treat it as absent.
    private static BasicTextObject? NonEmpty(BasicTextObject text) =>
        string.IsNullOrWhiteSpace(text.GetText()) ? null : text;

    private static BasicSelectorObject? ReadSelector(Message message, string port) =>
        port.Length > 0 &&
        message.Ports.TryGetValue(port, out var json) &&
        !string.IsNullOrWhiteSpace(json)
            ? MpaiJson.FromJson<BasicSelectorObject>(json)
            : null;
}