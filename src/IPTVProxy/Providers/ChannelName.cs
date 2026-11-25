using System.Text;
using System.Text.RegularExpressions;

namespace IPTVProxy.Providers;

// Quality values are inferred labels, not measured video resolutions.
public sealed record ChannelMetadata(string Name, string Country, int Quality, string Codec, string MatchKey);
public static class ChannelName
{
    private static readonly Regex Prefix = new(@"^\s*[\[(]?(NL|BE|DE|UK|US|FR|ES|IT|PT|PL|TR|GR|SE|NO|DK|FI|AT|CH|CA|AU|IE)[\]) ]*\s*[:|›»\-]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CategoryPrefix = new(@"^\s*[\[(]?(NL|BE|DE|UK|US|FR|ES|IT|PT|PL|TR|GR|SE|NO|DK|FI|AT|CH|CA|AU|IE)(?:[\])\s:|›»\-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Suffix = new(@"(?:\s+|[|:\-]\s*|\s*[\[(])(4K|UHD|2160P|FHD|1080P|HD|720P|SD|HEVC|H\.?265|H\.?264|AVC|50FPS|60FPS)[\])\s]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Npo = new(@"^NPO\s*([123])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static ChannelMetadata Parse(string original, string category)
    {
        var name = original.Normalize(NormalizationForm.FormKC).Trim();
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
        var npo = Npo.Match(name);
        if (npo.Success && (country is "" or "NL") && !conflict) { country = "NL"; name = "NPO " + npo.Groups[1].Value; }
        if (name.Length == 0) name = original;
        // Unknown geography, contradictory labels and generic event feeds are never auto-merged.
        var generic = Regex.IsMatch(name, @"\b(EVENT|PPV|BACKUP|TEST|VIP|FEED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var key = country.Length == 0 || conflict || qualityConflict || generic ? "" : country + ":" + name.ToUpperInvariant();
        return new(name, conflict ? "" : country, qualityConflict ? 0 : quality, codec, key);
    }
}
