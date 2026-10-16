using System.Text;
using System.Text.Json;

namespace AirLens.Core.Protocol;

public sealed record LensInfo(string Key, string Label, string Kind);

/// <summary>One quality choice for the active lens: a resolution with its best settings built in.</summary>
public sealed record QualityPresetInfo(
    string Id,
    string Label,
    string Tag,
    int Width,
    int Height,
    int Fps,
    VideoCodec Codec,
    int BitrateBps,
    bool Direct)
{
    /// <summary>"4K · Best picture".</summary>
    public string Title => $"{Label} · {Tag}";

    /// <summary>"30 fps · H.265 · 80 Mbps", with "4:3" for 4:3 sizes.</summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder().Append(Fps).Append(" fps");
            if (Width * 3 == Height * 4) sb.Append(" · 4:3");
            sb.Append(Codec == VideoCodec.Hevc ? " · H.265" : " · H.264");
            if (BitrateBps > 0) sb.Append(" · ").Append(Math.Round(BitrateBps / 1e6)).Append(" Mbps");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Payload of DEVICE_STATE: the phone's lenses and the quality presets of the active lens, so the PC
/// can offer the same choices as the phone (docs/PROTOCOL.md, "Remote control").
/// </summary>
/// <summary>Manual focus: <see cref="Value"/> 0 (far) .. 1 (near), null while autofocus.</summary>
public sealed record FocusInfo(bool Supported, double? Value);

/// <summary>The phone's battery temperature and heat level; <see cref="Lowered"/> = stepped down to that preset.</summary>
public sealed record HeatInfo(double? TempC, string Level, string? Lowered)
{
    public bool IsHot => Level is "hot" or "critical";
}

public sealed record DeviceState(
    string Device,
    /// <summary>The phone's stable id, the same one discovery announces. Null from older phones.</summary>
    string? Id,
    string? LensKey,
    IReadOnlyList<LensInfo> Lenses,
    string Quality,
    IReadOnlyList<QualityPresetInfo> Presets,
    FocusInfo? Focus = null,
    HeatInfo? Heat = null)
{
    public const string Custom = "custom";

    public LensInfo? ActiveLens => Lenses.FirstOrDefault(l => l.Key == LensKey);
    public QualityPresetInfo? ActivePreset => Presets.FirstOrDefault(p => p.Id == Quality);

    /// <summary>Lenient: unknown fields are ignored and missing ones get defaults. Null if not JSON.</summary>
    public static DeviceState? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var lenses = new List<LensInfo>();
            if (root.TryGetProperty("lenses", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in ls.EnumerateArray())
                    lenses.Add(new LensInfo(Str(l, "key") ?? "", Str(l, "label") ?? "", Str(l, "kind") ?? ""));
            }
            var presets = new List<QualityPresetInfo>();
            if (root.TryGetProperty("presets", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray())
                {
                    presets.Add(new QualityPresetInfo(
                        Str(p, "id") ?? "",
                        Str(p, "label") ?? "",
                        Str(p, "tag") ?? "",
                        Int(p, "width"),
                        Int(p, "height"),
                        Int(p, "fps"),
                        Str(p, "codec") == "hevc" ? VideoCodec.Hevc : VideoCodec.H264,
                        Int(p, "bitrateBps"),
                        p.TryGetProperty("direct", out var d) && d.ValueKind == JsonValueKind.True));
                }
            }
            FocusInfo? focus = null;
            if (root.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                bool auto = !f.TryGetProperty("auto", out var a) || a.ValueKind != JsonValueKind.False;
                double? value = !auto && f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                focus = new FocusInfo(f.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.True, value);
            }
            HeatInfo? heat = null;
            if (root.TryGetProperty("heat", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                double? temp = h.TryGetProperty("tempC", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetDouble() : null;
                heat = new HeatInfo(temp, Str(h, "level") ?? "normal", Str(h, "lowered"));
            }
            return new DeviceState(Str(root, "device") ?? "Phone", Str(root, "id"), Str(root, "lens"), lenses, Str(root, "quality") ?? Custom, presets, focus, heat);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
}
