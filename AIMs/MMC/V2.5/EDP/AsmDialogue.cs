using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Edp;

// EDP IN AUDIO SCENE MANAGEMENT (the author, 2026/10/03): what a User says about an
// audio scene - "move the violin to the left", "play the scene" - becomes User
// Commands (CAE-UCM) for CAE-ASM, as in the CAV what the passenger says becomes an
// AMS-HCI Message. EDP is extended rather than a new AIM made, because EDP already
// holds the dialogue with the language model.
//
// The language model only understands. Its answer is forced to an action schema whose
// names are the Scene's members (structured output), with "unknown" for what is not
// there; the conventions are given, not left to it - left and right are the User's,
// "a bit" is 0.5 m, a move without an amount 1 m, a volume change 3 dB. What it
// understood is checked in code: "it" with nothing to refer to, or "unknown", is a
// question to the User. Then everything is composed here, not by the model: the new
// positions from the Scene and the User Point of View, the User Command, and the
// reply that says what was done.
//
// Measured on 59 requests (Test/AsmVoice): llama3.2:3b, 49 right (83%), about 1 s.
public sealed class AsmDialogue
{
    public sealed record Member(string Name, string Id, double[] Position, double YawDeg);
    public sealed record Turn(string Reply, UserCommand? Command, TextForUA? ForUA = null);

    private readonly Func<string, IEnumerable<(string Role, string Content)>, JsonNode, Task<string>> understand;
    public AsmDialogue(Func<string, IEnumerable<(string Role, string Content)>, JsonNode, Task<string>> understand) => this.understand = understand;

    // THE MEMBERS of a Basic Audio Scene: each named by the first line of its Basic
    // Audio Object's description, else by its identifier; placed by its Space-Time.
    public static List<Member> MembersOf(BasicAudioSceneDescriptors scene) =>
        scene.BasicAudioSceneDescriptorsEntries.Where(e => e.AudioObjectIDOrAudioObject is not null).Select(e =>
        {
            var o = e.AudioObjectIDOrAudioObject!;
            var name = (o.DescrMetadata ?? "").Split('\n')[0].Trim();
            var sa = e.AudioObjectSpaceTime?.SpatialAttitude1;
            var p = sa?.Position?.CartPosition is { Length: >= 3 } c ? new[] { c[0], c[1], c[2] } : new double[3];
            var yaw = sa?.Orientation?.EulerAngles is { Length: >= 3 } a ? a[2] : 0;
            return new Member(name.Length > 0 ? name.ToLowerInvariant() : o.BasicAudioObjectID, o.BasicAudioObjectID, p, yaw);
        }).ToList();

    private static readonly string[] Acts = ["play", "stop", "add", "remove", "move", "turn", "volume", "listen_from", "undo", "save", "ask"];
    private static readonly Regex Pronoun = new(@"\b(move|turn|make|bring|put|push|raise|lower|take)\s+(it|them|this)\b", RegexOptions.IgnoreCase);

    public static JsonNode Schema(IEnumerable<string> members, IEnumerable<string> library)
    {
        var targets = new JsonArray(new[] { "scene" }.Concat(members).Concat(library).Append("unknown").Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        JsonArray Enum(params string[] v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        return new JsonObject
        {
            ["type"] = "object", ["required"] = new JsonArray("actions"), ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["actions"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 3,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["required"] = new JsonArray("act"), ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["act"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(Acts) },
                            ["target"] = new JsonObject { ["type"] = "string", ["enum"] = targets },
                            ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = Enum("left", "right", "front", "back", "closer", "farther", "up", "down") },
                            ["amount_m"] = new JsonObject { ["type"] = "number", ["minimum"] = 0.1, ["maximum"] = 20 },
                            ["towards"] = new JsonObject { ["type"] = "string", ["enum"] = Enum("user", "away", "left", "right") },
                            ["change_db"] = new JsonObject { ["type"] = "number", ["minimum"] = -30, ["maximum"] = 30 },
                            ["question"] = new JsonObject { ["type"] = "string" }
                        }
                    }
                }
            }
        };
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Where(Member m, double[] user, double userYaw)
    {
        var (r, a) = InUserFrame(m.Position, user, userYaw);
        var parts = new List<string>();
        if (Math.Abs(a) > 0.3) parts.Add($"{F(Math.Abs(a))} m {(a > 0 ? "ahead" : "behind")}");
        if (Math.Abs(r) > 0.3) parts.Add($"{F(Math.Abs(r))} m to the {(r > 0 ? "right" : "left")}");
        return parts.Count == 0 ? "where the user is" : string.Join(" and ", parts);
    }

    // A position as the User sees it: to the right, ahead.
    private static (double Right, double Ahead) InUserFrame(double[] p, double[] user, double userYaw)
    {
        var y = userYaw * Math.PI / 180;
        double dx = p[0] - user[0], dy = p[1] - user[1];
        return (dx * Math.Cos(y) + dy * Math.Sin(y), -dx * Math.Sin(y) + dy * Math.Cos(y));
    }

    public static string SystemPrompt(IReadOnlyList<Member> members, IEnumerable<string> library, double[] user, double userYaw) =>
        "You turn what a user says into actions on an audio scene. Answer only with the JSON actions.\n\n" +
        "The user is listening to the scene. Its members, from where the user is:\n" +
        string.Join("\n", members.Select(m => $"- {m.Name}: {Where(m, user, userYaw)}")) + "\n" +
        (library.Any() ? $"Stored sounds that can be added: {string.Join(", ", library)}.\n" : "No stored sounds can be added.\n") + @"
Actions:
- play (target: ""scene"" or a member), stop
- add (target: a stored sound; optional direction, amount_m), remove (target: a member)
- move (target: a member; direction: left, right, front, back, closer, farther, up, down; amount_m)
- turn (target: a member; towards: user, away, left, right)
- volume (target: a member or ""scene""; change_db: positive louder, negative quieter)
- listen_from (the user moves: target, a member to sit next to; or direction and amount_m)
- undo, save
- ask (question): when it is not clear which member is meant, when a sound named is neither a member nor stored, or when the request is not about the scene.

Conventions: left and right are the user's. Without an amount, move 1 m; ""a bit"", ""a little"", ""slightly"" is 0.5 m; ""much"", ""a lot"" is 2 m. Without an amount, louder or quieter is 3 dB; ""a little"" is 1.5 dB; ""a lot"", ""much"" is 6 dB. Recognised speech may contain errors: read words as the member or action they most likely stand for. Several requests in one sentence are several actions, in order.
""Everything"", ""all"", ""the whole thing"" is the target ""scene"", in ONE action. Raise or lower a member is move up or down, not volume. ""Me"", ""I"" is the user: moving the user is listen_from. When a sound named is neither a member nor a stored sound, or a word could mean several members, or ""it"" has nothing to refer to, use target ""unknown"".

Examples (another scene: members guitar, bass, flute; stored: thunder):
""raise the bass a bit"" -> [{""act"":""move"",""target"":""bass"",""direction"":""up"",""amount_m"":0.5}]
""move me forward"" -> [{""act"":""listen_from"",""direction"":""front"",""amount_m"":1}]
""make it all quieter"" -> [{""act"":""volume"",""target"":""scene"",""change_db"":-3}]
""add the saxophone"" -> [{""act"":""ask"",""target"":""unknown"",""question"":""There is no saxophone. Which sound do you mean?""}]
""move the string instrument closer"" -> [{""act"":""ask"",""target"":""unknown"",""question"":""The guitar or the bass?""}]
""how old are you"" -> [{""act"":""ask"",""question"":""I can only change the scene. What should I do with it?""}]";

    // What the language model understood: the actions, or a question.
    public async Task<List<JsonObject>> UnderstandAsync(string text, IReadOnlyList<Member> members, IEnumerable<string> library, double[] user, double userYaw)
    {
        if (Pronoun.IsMatch(text)) return [new JsonObject { ["act"] = "ask", ["question"] = "Which one do you mean?" }];
        var raw = await understand(SystemPrompt(members, library, user, userYaw), [("user", text)], Schema(members.Select(m => m.Name), library));
        var actions = (JsonNode.Parse(raw)?["actions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (actions.Count == 0 || actions.Any(a => (string?)a["target"] == "unknown"))
            return [new JsonObject { ["act"] = "ask", ["question"] = (string?)actions.FirstOrDefault(a => a["question"] is not null)?["question"] ?? "Which one do you mean?" }];
        return actions;
    }

    // What the User said, as a reply and the User Command to send, if any.
    public async Task<Turn> HeardAsync(string text, BasicAudioSceneDescriptors scene, IEnumerable<string>? library = null)
    {
        library ??= [];
        var members = MembersOf(scene);
        var user = scene.UserPoV?.CartPosition is { Length: >= 3 } u ? u : new double[3];
        var userYaw = scene.UserPoV?.Orientation is { Length: >= 3 } o ? o[2] : 0;
        var actions = await UnderstandAsync(text, members, library, user, userYaw);
        return Compose(actions, scene, members, user, userYaw);
    }

    // The actions as one User Command and the words that say what was done.
    public static Turn Compose(List<JsonObject> actions, BasicAudioSceneDescriptors scene, IReadOnlyList<Member> members, double[] user, double userYaw)
    {
        if (actions.Count > 0 && (string?)actions[0]["act"] == "ask")
            return new Turn((string?)actions[0]["question"] ?? "Which one do you mean?", null);

        var said = new List<string>();
        var added = new List<ObjectPlacement>(); var removed = new List<ObjectPlacement>();
        var moved = new List<ObjectMovement>(); var changed = new List<ObjectChange>(); var modified = new List<ObjectChange>();
        ManagedObject? delivered = null; PointOfView? userPov = null; double? lufs = null;
        var forUA = new List<string>();
        Member? M(JsonObject a) => members.FirstOrDefault(m => m.Name == (string?)a["target"]);
        double Amount(JsonObject a) => a["amount_m"] is JsonValue v ? v.GetValue<double>() : 1.0;
        var y = userYaw * Math.PI / 180;
        double[] right = [Math.Cos(y), Math.Sin(y), 0], ahead = [-Math.Sin(y), Math.Cos(y), 0];

        foreach (var a in actions)
        {
            var act = (string?)a["act"]; var m = M(a);
            switch (act)
            {
                case "play":
                    delivered = new ManagedObject { ObjectID = m?.Id ?? scene.BasicAudioSceneDescriptorsID };
                    said.Add(m is null ? "Playing the scene." : $"Playing the {m.Name}."); break;
                case "remove" when m is not null:
                    removed.Add(new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = m.Id } });
                    said.Add($"I took out the {m.Name}."); break;
                case "move" when m is not null:
                {
                    var d = (string?)a["direction"] ?? "front"; var n = Amount(a);
                    double[] step = d switch
                    {
                        "left" => [-right[0], -right[1], 0], "right" => right, "front" => ahead, "back" => [-ahead[0], -ahead[1], 0],
                        "up" => [0, 0, 1], "down" => [0, 0, -1],
                        _ => Toward(m.Position, user, d == "closer")
                    };
                    var to = new[] { m.Position[0] + step[0] * n, m.Position[1] + step[1] * n, m.Position[2] + step[2] * n };
                    moved.Add(new ObjectMovement { ObjectID = new ManagedObject { ObjectID = m.Id }, OldSpatialAttitude = Attitude(m.Position, m.YawDeg), NewSpatialAttitude = Attitude(to, m.YawDeg) });
                    said.Add($"I moved the {m.Name} {F(n)} m {Words(d)}."); break;
                }
                case "turn" when m is not null:
                {
                    var t = (string?)a["towards"] ?? "user";
                    var face = Math.Atan2(-(user[0] - m.Position[0]), user[1] - m.Position[1]) * 180 / Math.PI;
                    var yaw = t switch { "away" => face + 180, "left" => userYaw + 90, "right" => userYaw - 90, _ => face };
                    changed.Add(new ObjectChange { ObjectID = new ManagedObject { ObjectID = m.Id }, SpatialAttitude = Attitude(m.Position, yaw) });
                    said.Add(t == "user" ? $"The {m.Name} now faces you." : t == "away" ? $"The {m.Name} now faces away from you." : $"The {m.Name} now faces your {t}."); break;
                }
                case "volume":
                {
                    var db = a["change_db"] is JsonValue v ? v.GetValue<double>() : 3;
                    modified.Add(new ObjectChange { ObjectID = new ManagedObject { ObjectID = m?.Id ?? scene.BasicAudioSceneDescriptorsID } });
                    lufs = db;
                    said.Add($"{(m is null ? "The scene" : "The " + m.Name)} is {F(Math.Abs(db))} dB {(db >= 0 ? "louder" : "quieter")}."); break;
                }
                case "listen_from":
                {
                    double[] to;
                    if (m is not null) { var back = Toward(m.Position, user, false); to = [m.Position[0] - back[0], m.Position[1] - back[1], user[2]]; }
                    else
                    {
                        var d = (string?)a["direction"] ?? "front"; var n = Amount(a);
                        double[] step = d switch { "left" => [-right[0], -right[1], 0], "right" => right, "back" => [-ahead[0], -ahead[1], 0], "up" => [0, 0, 1], "down" => [0, 0, -1], _ => ahead };
                        to = [user[0] + step[0] * n, user[1] + step[1] * n, user[2] + step[2] * n];
                    }
                    userPov = new PointOfView { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = to, Orientation = [0, 0, userYaw] };
                    said.Add(m is null ? "You moved." : $"You are now next to the {m.Name}."); break;
                }
                // ONLY THE USER AGENT DOES THESE (the author): they go to it as Text For UA.
                case "stop": forUA.Add("stop"); said.Add("Stopped."); break;
                case "undo": forUA.Add("undo"); said.Add("Undone."); break;
                case "save": forUA.Add("save"); said.Add("Saved."); break;
                case "add": said.Add("I can add a stored sound once the App gives me its list."); break;
            }
        }

        var data = new UserCommandData
        {
            UserPoV = userPov, LUFS = lufs, DeliveredObject = delivered,
            AddedObjects = added.Count > 0 ? new ObjectPlacements { Objects = added } : null,
            RemovedObjects = removed.Count > 0 ? new ObjectPlacements { Objects = removed } : null,
            MovedObjects = moved.Count > 0 ? new ObjectMovements { Objects = moved } : null,
            ChangedObjects = changed.Count > 0 ? new ObjectChanges { Objects = changed } : null,
            ModifiedObjects = modified.Count > 0 ? new ObjectChanges { Objects = modified } : null
        };
        var any = userPov is not null || delivered is not null || added.Count + removed.Count + moved.Count + changed.Count + modified.Count > 0;
        var command = any ? new UserCommand { UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data } : null;
        var ua = forUA.Count > 0 ? new TextForUA { TextForUATime = SimpleTime.At(DateTimeOffset.UtcNow), Text = string.Join(" ", forUA) } : null;
        return new Turn(string.Join(" ", said), command, ua);
    }

    // The unit step from a member towards the User (closer) or away (farther).
    private static double[] Toward(double[] member, double[] user, bool closer)
    {
        double dx = user[0] - member[0], dy = user[1] - member[1], dz = user[2] - member[2];
        var n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (n < 1e-6) return [0, 0, 0];
        var s = closer ? 1 : -1;
        return [s * dx / n, s * dy / n, s * dz / n];
    }

    private static string Words(string d) => d switch
    {
        "left" => "to your left", "right" => "to your right", "front" => "forward", "back" => "back",
        "closer" => "closer to you", "farther" => "farther from you", "up" => "up", "down" => "down", _ => d
    };

    private static SpatialAttitude Attitude(double[] p, double yaw) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = p },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, yaw] }
    };
}
