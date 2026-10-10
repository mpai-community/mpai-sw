using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mpai.Mmc.Edp;

// EDP IN THE CAV (M3243 3.2): the passenger's dialogue with the CAV about where to go.
//
// The places are the way points the Offline Map names. The language model only
// understands: told the places and what the CAV is doing, it says what the passenger
// wants - a Destination (the place as said), yes, a choice of Route, no, suspend,
// resume, stop, or nothing of these. Everything it says is checked before anything
// is sent: a place is matched against the map's names - none: the passenger is told
// which places the CAV knows; more than one: asked which; one: its way point is
// requested of the AMS. What the AMS answers - the Routes, the Route's state - is
// told the passenger in words composed here, not by the model: a time or a place
// must be the one the AMS gave.
//
// THE STATE of the dialogue - the places, what was asked, the Routes proposed, the
// Route's state - is kept in EDP's Private Storage: the dialogue of one CAV's cabin,
// reachable by this AIM alone.
public sealed class CavDialogue
{
    public sealed record Place(string Name, string WayPointId);

    public sealed class State
    {
        public string? MapId { get; set; }
        public List<Place> Places { get; set; } = [];
        public List<Place> Asking { get; set; } = [];           // an ambiguous name: which of these
        public Place? Destination { get; set; }
        public List<(string RouteId, double Seconds)> Routes { get; set; } = [];
        public string? RouteId { get; set; }
        public string? Status { get; set; }
        public int Sent { get; set; }
    }

    private readonly Func<string, string, Task<string>> understand;
    public CavDialogue(Func<string, string, Task<string>> understand) => this.understand = understand;

    // The places of an Offline Map: its way points that have a Name.
    public static List<Place> PlacesOf(JsonNode offlineMap, out string mapId)
    {
        var geo = JsonNode.Parse((string)offlineMap["BasicOfflineMapData"]![0]!["Data"]!)!;
        mapId = (string?)geo["MapID"] ?? "";
        return geo["features"]!.AsArray()
            .Where(f => (string?)f!["geometry"]?["type"] == "Point" && f["properties"]?["Name"] is not null)
            .Select(f => new Place((string)f!["properties"]!["Name"]!, (string)f["properties"]!["WayPointID"]!)).ToList();
    }

    // What the passenger said: the reply, and the AMS-HCI Message to send, if any.
    public async Task<(string Reply, JsonObject? ToAms)> HeardAsync(string text, State s)
    {
        var raw = await understand(System(s), $"The passenger says: \"{text}\"");
        var (intent, place, choice) = Parse(raw);
        place = Grounded(place, text);

        // A place named is a Destination - "no, take me to the station" too - unless
        // what was said is a command.
        if (intent == "destination" || (place.Length > 0 && (s.Asking.Count > 0 || intent is "no" or "other")))
        {
            var among = s.Asking.Count > 0 ? s.Asking : s.Places;
            var matches = Match(place, among);
            if (matches.Count == 0 && s.Asking.Count > 0) matches = Match(place, s.Places);
            if (matches.Count == 0)
                return ($"I do not know {Quoted(place)}. I can take you to {List(s.Places.Select(p => p.Name))}.", null);
            if (matches.Count > 1)
            {
                s.Asking = matches;
                return ($"There are {matches.Count}: {List(matches.Select(p => p.Name), "or")}. Which one?", null);
            }
            s.Asking = [];
            s.Destination = matches[0];
            s.Routes = [];
            return ($"Let me find the way to {matches[0].Name}.", Message(s, hci => hci["RequestedRoutes"] = new JsonArray(new JsonObject
            {
                ["Route"] = new JsonObject
                {
                    ["Header"] = "CAV-RTE-V2.0", ["RouteID"] = $"REQ-{matches[0].WayPointId}", ["OfflineMapID"] = s.MapId,
                    ["RouteSegments"] = new JsonArray(new JsonObject { ["WayPoint1ID"] = "HERE", ["WayPoint2ID"] = matches[0].WayPointId })
                }
            })));
        }
        switch (intent)
        {
            case "yes" or "choose" when s.Routes.Count > 0:
                var n = intent == "choose" && choice >= 1 && choice <= s.Routes.Count ? choice : 1;
                var chosen = s.Routes[n - 1];
                s.Routes = [];
                return ($"Very well: to {s.Destination!.Name}, about {Duration(chosen.Seconds)}.", Command(s, "Execute", chosen.RouteId));
            case "no" when s.Routes.Count > 0:
                s.Routes = [];
                return (s.Status == "Executing" ? $"All right: we keep going to the place we were going." : "All right: we stay here. Where would you like to go?", null);
            case "suspend" when s.Status == "Executing":
                return ("I will stop at the next junction.", Command(s, "Suspend"));
            case "resume" when s.Status == "Suspended":
                return ($"We go on to {s.Destination?.Name}.", Command(s, "Resume"));
            case "stop" when s.Status is "Executing" or "Suspended":
                return ("I will stop at the next junction, and end the trip there.", Command(s, "Stop"));
        }
        return (s.Destination is null || s.Status is null or "Arrived" or "Stopped"
            ? $"Where would you like to go? I can take you to {List(s.Places.Select(p => p.Name))}."
            : $"We are going to {s.Destination.Name}.", null);
    }

    // What the AMS told: the reply to the passenger, if any.
    public string? Told(JsonNode message, State s)
    {
        var ams = message["AMSMessage"];
        if (ams?["RouteList"] is JsonArray list)
        {
            s.Routes = list.Select(r => ((string)r!["RouteID"]!, Seconds(r))).ToList();
            var to = s.Destination?.Name ?? "there";
            if (s.Routes.Count == 0) return $"I found no way to {to}.";
            var first = $"To {to}: about {Duration(s.Routes[0].Seconds)}";
            return s.Routes.Count == 1 ? first + ". Shall we go?"
                : first + $", or about {Duration(s.Routes[1].Seconds)} by another way. Shall we take the first?";
        }
        if (ams?["RouteStatus"] is { } state)
        {
            s.RouteId = (string?)state["RouteID"];
            var was = s.Status;
            s.Status = (string?)state["Status"];
            return s.Status switch
            {
                "Executing" when was == "Suspended" => "We are on our way again.",
                "Executing" when was != "Executing" => $"We are on our way to {s.Destination?.Name}.",
                "Suspended" => "We are stopping at the next junction.",
                "Stopped" => "We are stopping at the next junction; the trip ends there.",
                "Arrived" => $"We have arrived at {s.Destination?.Name}.",
                _ => null
            };
        }
        return null;
    }

    private static string System(State s) =>
        "You understand what a passenger of an autonomous car says to it. " +
        $"The places the car can go to: {string.Join("; ", s.Places.Select(p => p.Name))}. " +
        (s.Asking.Count > 0 ? $"The car has just asked which of these the passenger means: {string.Join("; ", s.Asking.Select(p => p.Name))}. " : "") +
        (s.Routes.Count > 0 ? $"The car has just proposed {s.Routes.Count} routes, numbered from 1, and asked whether to take the first. " : "") +
        (s.Status == "Executing" ? "The car is driving. " : s.Status == "Suspended" ? "The car has paused its trip. " : "The car is standing. ") +
        "Return ONLY a JSON object with the keys \"intent\", \"place\" and \"choice\". " +
        "\"intent\" is one of: \"destination\" (the passenger names a place to go to), \"yes\" (agrees), " +
        "\"choose\" (chooses one of the routes proposed, by its number), \"no\" (declines), \"suspend\" (wants a pause), " +
        "\"resume\" (wants to continue after a pause), \"stop\" (wants to end the trip), \"other\". " +
        "\"place\" is the place the passenger names, in exactly the words they used, or \"\". \"choice\" is the number of the route chosen, or 0.";

    private static (string Intent, string Place, int Choice) Parse(string raw)
    {
        try
        {
            var i = raw.IndexOf('{'); var j = raw.LastIndexOf('}');
            var o = JsonNode.Parse(raw[i..(j + 1)])!;
            var choice = o["choice"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : int.TryParse((string?)o["choice"]?.ToString(), out var d) ? d : 0;
            return (((string?)o["intent"] ?? "other").Trim().ToLowerInvariant(), ((string?)o["place"]?.ToString() ?? "").Trim(), choice);
        }
        catch { return ("other", "", 0); }
    }

    // The place in the passenger's own words: of what the model gives, only the words
    // the passenger said - a model told the names may give one for words that fit
    // two, or one where none was named.
    private static string Grounded(string place, string said)
    {
        var spoken = Words(said).ToHashSet();
        return string.Join(" ", Words(place).Where(spoken.Contains));
    }

    // The places whose name the words said fit: every word said that is not a
    // filler is in the name - or the whole name is in what was said.
    public static List<Place> Match(string said, IEnumerable<Place> places)
    {
        var words = Words(said);
        if (words.Count == 0) return [];
        return places.Where(p =>
        {
            var name = Words(p.Name);
            return words.All(w => name.Contains(w)) || (name.Count > 0 && name.All(w => words.Contains(w)));
        }).ToList();
    }

    // Words that name no place - "one" as speech recognition may also write it, "1".
    private static readonly HashSet<string> Fillers = ["the", "a", "an", "to", "one", "1", "please", "go", "take", "me", "at", "of"];
    private static List<string> Words(string s) =>
        s.ToLowerInvariant().Split([' ', ',', '.', '!', '?', '\'', '"'], StringSplitOptions.RemoveEmptyEntries).Where(w => !Fillers.Contains(w)).ToList();

    private static double Seconds(JsonNode? route)
    {
        var segments = route?["RouteSegments"]?.AsArray();
        var end = segments?.LastOrDefault()?["EstimatedArrDepSpaceTime"]?["SimpleTimeData"]?[0]?["StartTime"];
        var start = route?["RouteTime"]?["SimpleTimeData"]?[0]?["StartTime"];
        return end is null || start is null ? 0 : ((double)end - (double)start) / 1000;
    }

    private static string Duration(double seconds) =>
        seconds < 90 ? $"{Math.Round(seconds / 10) * 10:0} seconds" : $"{Math.Round(seconds / 60):0} minutes";

    private static string Quoted(string place) => place.Length > 0 ? $"\"{place}\"" : "that place";

    private static string List(IEnumerable<string> names, string and = "and")
    {
        var l = names.ToList();
        return l.Count <= 1 ? string.Join("", l) : string.Join(", ", l.Take(l.Count - 1)) + $" {and} " + l[^1];
    }

    private static JsonObject Command(State s, string command, string? routeId = null) => Message(s, hci =>
    {
        hci["RouteCommand"] = command;
        if (routeId is not null) hci["SelectedRouteID"] = routeId;
    });

    private static JsonObject Message(State s, Action<JsonObject> fill)
    {
        var hci = new JsonObject();
        fill(hci);
        var id = $"AHM-H{++s.Sent:D4}";
        return new JsonObject { ["Header"] = "CAV-AHM-V2.0", ["AMSHCIMessageID"] = id, ["HCIMessage"] = hci };
    }

    public static string Save(State s) => JsonSerializer.Serialize(new
    {
        s.MapId, Places = s.Places.Select(p => new[] { p.Name, p.WayPointId }), Asking = s.Asking.Select(p => new[] { p.Name, p.WayPointId }),
        Destination = s.Destination is { } d ? new[] { d.Name, d.WayPointId } : null,
        Routes = s.Routes.Select(r => new object[] { r.RouteId, r.Seconds }), s.RouteId, s.Status, s.Sent
    });

    public static State Load(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new State();
        var o = JsonNode.Parse(json)!;
        Place P(JsonNode? a) => new((string)a![0]!, (string)a[1]!);
        return new State
        {
            MapId = (string?)o["MapId"],
            Places = o["Places"]!.AsArray().Select(P).ToList(),
            Asking = o["Asking"]!.AsArray().Select(P).ToList(),
            Destination = o["Destination"] is JsonArray d ? P(d) : null,
            Routes = o["Routes"]!.AsArray().Select(r => ((string)r![0]!, (double)r[1]!)).ToList(),
            RouteId = (string?)o["RouteId"], Status = (string?)o["Status"], Sent = (int)o["Sent"]!
        };
    }
}
