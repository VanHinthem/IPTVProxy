using System.Text;
using System.Text.RegularExpressions;

namespace IPTVProxy.Providers;

// Quality values are inferred labels, not measured video resolutions.
public sealed record ChannelMetadata(string Name, string Country, int Quality, string Codec, string MatchKey, string Package = "", string Tags = "", bool Separator = false);
public static class ChannelName
{
    private static readonly Regex Prefix = new(@"^\s*[\[(]?(NL|BE|DE|UK|US|FR|ES|IT|PT|PL|TR|GR|SE|NO|DK|FI|AT|CH|CA|AU|IE)[\]) ]*\s*[:|›»\-]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CategoryPrefix = new(@"^\s*[\[(]?(NL|BE|DE|UK|US|FR|ES|IT|PT|PL|TR|GR|SE|NO|DK|FI|AT|CH|CA|AU|IE)(?:[\])\s:|›»\-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Suffix = new(@"(?:\s+|[|:\-]\s*|\s*[\[(])(4K|UHD|2160P|FHD|1080P|HD|720P|SD|HEVC|H\.?265|H\.?264|AVC|50FPS|60FPS)[\])\s]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Npo = new(@"^NPO\s*([123])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static ChannelMetadata Parse(string original, string category)
    {
        var tags = new List<string>();
        var raw = original.Trim();
        var superscript = Regex.Match(raw, @"(?:[ᴿᴬᵂᴳᴼᴸᴰⱽᴵᴾʳᵉᶜˢᵘᵖᵉʳ]+\s*)+$");
        if (superscript.Success)
        {
            var label = superscript.Value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
            if (label.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(x => x is "RAW" or "GOLD" or "VIP" or "REC" or "SUPER"))
            { tags.Add(label); raw = raw[..superscript.Index].TrimEnd(); }
        }
        var name = raw.Normalize(NormalizationForm.FormKC).Trim();
        var cat = category.Normalize(NormalizationForm.FormKC);
        var package = "";
        // Match only observed package markers within their corresponding category.
        if (cat.Contains("TELE2", StringComparison.OrdinalIgnoreCase) && name.StartsWith("T2:", StringComparison.OrdinalIgnoreCase))
        { package = "Tele2"; name = name[3..].Trim(); }
        if (cat.Contains("DELTA", StringComparison.OrdinalIgnoreCase) && name.StartsWith("[Delta]", StringComparison.OrdinalIgnoreCase))
        { package = "Delta"; name = name[7..].Trim(); }
        if (cat.Contains("ODIDO", StringComparison.OrdinalIgnoreCase)) package = "Odido";
        if (cat.Contains("HD NEDERLAND", StringComparison.OrdinalIgnoreCase)) package = "HD Nederland";
        var separator = original.TrimStart().StartsWith("###", StringComparison.Ordinal);
        if (package == "Tele2" && name.EndsWith(" RAW", StringComparison.OrdinalIgnoreCase))
        { tags.Add("RAW"); name = name[..^4].TrimEnd(); }
        var prefix = Prefix.Match(name);
        var categoryPrefix = CategoryPrefix.Match(category.Normalize(NormalizationForm.FormKC));
        var country = prefix.Success ? prefix.Groups[1].Value.ToUpperInvariant() :
            categoryPrefix.Success ? categoryPrefix.Groups[1].Value.ToUpperInvariant() : "";
        var conflict = prefix.Success && categoryPrefix.Success &&
            !prefix.Groups[1].Value.Equals(categoryPrefix.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        if (prefix.Success) name = name[prefix.Length..];
        int quality = 0;
        string codec = "";
        bool qualityConflict = false;
        while (true)
        {
            var suffix = Suffix.Match(name);
            if (!suffix.Success) break;
            var token = suffix.Groups[1].Value.ToUpperInvariant().Replace(".", "");
            var q = token switch { "4K" or "UHD" or "2160P" => 2160, "FHD" or "1080P" => 1080, "HD" or "720P" => 720, "SD" => 480, _ => 0 };
            if (q != 0) { if (quality != 0 && quality != q) qualityConflict = true; quality = q; }
            if (token is "HEVC" or "H265") codec = "HEVC";
            if (token is "AVC" or "H264") codec = "AVC";
            // Preserve frame rate as an identity discriminator until it has its own metadata.
            if (token is "50FPS" or "60FPS") break;
            name = name[..suffix.Index].TrimEnd();
        }
        name = Spaces.Replace(name.Trim(), " ");
        if (quality == 0 && !qualityConflict && package == "HD Nederland") quality = 720;
        // Exact aliases only; never remove all spaces or punctuation globally.
        var aliases = name.ToUpperInvariant() switch
        {
            "192TV" or "192 TV" => "192 TV",
            "24KITCHEN" or "24 KITCHEN" => "24Kitchen",
            "BABYTV" or "BABY TV" => "Baby TV",
            "BBC FOUR / CBEEBIES" or "BBC FOUR - CBEEBIES" => "BBC Four - CBeebies",
            _ => name
        };
        name = aliases;
        var npo = Npo.Match(name);
        if (npo.Success && (country is "" or "NL") && !conflict) { country = "NL"; name = "NPO " + npo.Groups[1].Value; }
        if (name.Length == 0) name = original;
        // Unknown geography, contradictory labels and generic event feeds are never auto-merged.
        var generic = Regex.IsMatch(name, @"\b(EVENT|PPV|BACKUP|TEST|VIP|FEED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        generic = generic || Regex.IsMatch(cat, @"\b(PPV|EVENT|EVENTS)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var key = separator || country.Length == 0 || conflict || qualityConflict || generic ? "" : country + ":" + name.ToUpperInvariant();
        return new(name, conflict ? "" : country, qualityConflict ? 0 : quality, codec, key, package, string.Join(" ", tags.Distinct()), separator);
    }
}
