using System.Text;
using System.Text.RegularExpressions;

namespace IPTVProxy.Providers;
// All resolution and frame rate values are declared hints, never measured.
public sealed record ChannelMetadata(string Name,string Country,int Quality,string Codec,string MatchKey,string Package="",string Tags="",bool Separator=false,string ChannelType="Unknown",string QualityOrigin="",int FrameRate=0,bool CatchupHint=false);
public static class ChannelName
{
    private const string Regions="NL|BE|DE|UK|GB|USA|US|FR|ES|IT|PT|PL|TR|GR|SE|NO|DK|FI|AT|CH|CA|AU|IE|AR|RU|MX|AL|BR|BG|CZ|RO|IL|HU|HR|MK|CY|UA|PH|ID|MA|TH|AM|IR|AF|ISL|ASIA|AFR|LA|EX-YU|EXYU|IND";
    private const RegexOptions Options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
    private static readonly Regex Prefix=new(@"^\s*[\[(]?(?<region>"+Regions+@")[\]) ]*\s*[:|›»\-]\s*",Options);
    private static readonly Regex CategoryPrefix=new(@"^\s*[\[(]?(?<region>"+Regions+@")(?:[\])\s:|›»\-]|$)",Options);
    private static readonly Regex Superscripts=new(@"[\u1D2C-\u1D6A\u1D9B-\u1DBF\u02B0-\u02FF\u2070-\u209F]+",RegexOptions.CultureInvariant);
    private static readonly Regex Suffix=new(@"(?:\s+|[|:\-]\s*|\s*[\[(])(4K|UHD|2160P|FHD|1080P|HD|720P|SD|HEVC|H\.?265|H\.?264|AVC|50FPS|60FPS|25FPS|30FPS|RAW|REC|GOLD|VIP|SUPER|ULTRA|8K|16K)[\])\s]*$",Options);
    private static readonly Regex QualityPrefix=new(@"^(4K|8K|16K|UHD|FHD|HD|SD)\s*[|:]\s*",Options);
    private static readonly Regex Spaces=new(@"\s+",RegexOptions.CultureInvariant);
    private static readonly Regex Npo=new(@"^NPO\s*([123])$",Options);
    private static readonly (string Category,string Prefix,string Package)[] Packages=[
        ("TELE2","T2:","Tele2"),("DELTA","[Delta]","Delta"),
        ("CANAL+ ONLINE","PLAY+:","Canal+ Online"),("TELENET","TEL |","Telenet"),
        ("TELESAT","TS:","Telesat"),("VOO+","[Voo+]","Voo+"),
        ("PLAY TV","PLAY:","Play TV"),("TV-VLAANDEREN","SolcoTV |","TV Vlaanderen"),
        ("PRIME","PRIME:","Prime"),("BELGIUM VIP","BE-VIP:","Belgium VIP")
    ];
    private static string Normalize(string value)=>Spaces.Replace(Superscripts.Replace(value,m=>" "+m.Value.Normalize(NormalizationForm.FormKC)+" ").Normalize(NormalizationForm.FormKC).Trim()," ");
    private static string Region(string value)=>value.ToUpperInvariant() switch {"USA"=>"US","GB"=>"UK","EXYU"=>"EX-YU", "ISL"=>"IS","IND"=>"IN",var r=>r};
    private static int Resolution(string token)=>token.ToUpperInvariant() switch {"4K" or "UHD" or "2160P"=>2160,"FHD" or "1080P"=>1080,"HD" or "720P"=>720,"SD"=>480,_=>0};
    public static ChannelMetadata Parse(string original,string category)
    {
        var name=Normalize(original);var cat=Normalize(category);
        var tags=new HashSet<string>(StringComparer.Ordinal);
        var separator=original.TrimStart().StartsWith("###",StringComparison.Ordinal);
        var package="";
        foreach(var rule in Packages)
        {
            if(!cat.Contains(rule.Category,StringComparison.OrdinalIgnoreCase))continue;
            if(name.StartsWith(rule.Prefix,StringComparison.OrdinalIgnoreCase)){package=rule.Package;name=name[rule.Prefix.Length..].Trim();break;}
        }
        if(package=="" && cat.Contains("ODIDO",StringComparison.OrdinalIgnoreCase))package="Odido";
        if(package=="" && cat.Contains("CAIWAY DELTA",StringComparison.OrdinalIgnoreCase))package="Caiway Delta";
        if(package=="" && cat.Contains("HD NEDERLAND",StringComparison.OrdinalIgnoreCase))package="HD Nederland";
        var prefix=Prefix.Match(name);var categoryPrefix=CategoryPrefix.Match(cat);
        var country=prefix.Success?Region(prefix.Groups["region"].Value):categoryPrefix.Success?Region(categoryPrefix.Groups["region"].Value):"";
        var conflict=prefix.Success&&categoryPrefix.Success&&Region(prefix.Groups["region"].Value)!=Region(categoryPrefix.Groups["region"].Value);
        if(prefix.Success)name=name[prefix.Length..];
        int quality=0, fps=0;string codec="",qualityOrigin="";bool qualityConflict=false,catchup=false;
        void Token(string raw)
        {
            var token=raw.ToUpperInvariant().Replace(".","");
            var q=Resolution(token);
            if(q!=0){if(quality!=0&&quality!=q)qualityConflict=true;quality=q;qualityOrigin="Name";}
            else if(token is "HEVC" or "H265")codec="HEVC";
            else if(token is "AVC" or "H264")codec="AVC";
            else if(token.EndsWith("FPS")&&int.TryParse(token[..^3],out var rate))fps=rate;
            else {tags.Add(token);if(token=="REC")catchup=true;}
        }
        var qp=QualityPrefix.Match(name);
        if(qp.Success){Token(qp.Groups[1].Value);name=name[qp.Length..];}
        // Observed bullet is a source annotation, not an identity.
        name=name.TrimEnd().TrimEnd('◉').TrimEnd();
        while(true)
        {
            var suffix=Suffix.Match(name);if(!suffix.Success)break;
            Token(suffix.Groups[1].Value);name=name[..suffix.Index].TrimEnd();
        }
        if(quality==0&&!qualityConflict)
        {
            // Only unambiguous category hints. 8K/16K marketing labels never imply 4K.
            var hints=Regex.Matches(cat,@"(?<![\p{L}\p{N}])(4K|UHD|FHD|HD|SD|8K|16K)(?![\p{L}\p{N}])",Options).Select(m=>m.Value.ToUpperInvariant()).Distinct().ToArray();
            if(hints.Length==1&&Resolution(hints[0])>0){quality=Resolution(hints[0]);qualityOrigin="Category";}
        }
        if(tags.Contains("8K")||tags.Contains("16K")||Regex.IsMatch(cat,@"\b(8K|16K)\b",Options)){quality=0;qualityOrigin=""; }
        name=Spaces.Replace(name.Trim()," ");
        name=name.ToUpperInvariant() switch {
            "192TV" or "192 TV"=>"192 TV","24KITCHEN" or "24 KITCHEN"=>"24Kitchen",
            "BABYTV" or "BABY TV"=>"Baby TV","BBC FOUR / CBEEBIES" or "BBC FOUR - CBEEBIES"=>"BBC Four - CBeebies",
            "ESPN2" or "ESPN 2"=>"ESPN 2","ESPN3" or "ESPN 3"=>"ESPN 3","ESPN4" or "ESPN 4"=>"ESPN 4",
            "RTL4" or "RTL 4"=>"RTL 4","RTL5" or "RTL 5"=>"RTL 5","RTL7" or "RTL 7"=>"RTL 7","RTL8" or "RTL 8"=>"RTL 8",
            "SBS6" or "SBS 6"=>"SBS 6","NPO3FM" or "NPO 3FM"=>"NPO 3FM",_=>name};
        var npo=Npo.Match(name);if(npo.Success&&(country is "" or "NL")&&!conflict){country="NL";name="NPO "+npo.Groups[1].Value;}
        if(name.Length==0)name=original;
        var nlbe=country is "NL" or "BE";
        var numberedEvent=nlbe && (Regex.IsMatch(name,@"\bDISCOVERY\+\s+(LIVE\s+\d+|UFC LIVE)\b",Options)
            || Regex.IsMatch(cat,@"\bVIAPLAY\s+(OVERIGE|VOETBAL|FORMULE 1)\b",Options)
            || Regex.IsMatch(name,@"\b(ONLY EVENTS|LIVE DURING EVENTS)\b",Options));
        var filmLoop=nlbe && Regex.IsMatch(name,@"^VIAPLAY\s+(AKTIE|ACTION|COMEDY|DOCU|DRAMA|HORROR|KOMEDIE|MISDAAD|ROMANTIEK|SCI-FI|THRILLER)(\s+\d+)?$",Options);
        var radio=nlbe && !Regex.IsMatch(name,@"\b(TV|TELEVISIE|INFOBOX)\b|INFOBOX$",Options) &&
            Regex.IsMatch(name,@"(?:\bWEBRADIO\b|RADIO$|^RADIONL$|FM$|^NPO\s*3FM$)",Options);
        var type=separator?"Separator":numberedEvent?"Event":filmLoop?"Loop":radio?"Radio":
            Regex.IsMatch(cat,@"\b(PPV|EVENTS?|F1 TV|MIX SPORT|MOTOGP|ESPN EXTRA)\b",Options)||Regex.IsMatch(name,@"\b(EVENT|PPV|NO EVENT STREAMING)\b",Options)?"Event":
            Regex.IsMatch(cat,@"24/7|ENJO[Y]?TV|ENJOY TV|MOVIEBOX",Options)||name.Contains("24/7",StringComparison.OrdinalIgnoreCase)?"Loop":
            Regex.IsMatch(cat,@"\bRADIO\b",Options)&&!cat.Contains("MUSIC & RADIO",StringComparison.OrdinalIgnoreCase)?"Radio":
            Regex.IsMatch(name,@"\b(RADIO|FM)\b",Options)?"Radio":"Television";
        var generic=Regex.IsMatch(name,@"\b(BACKUP|TEST|VIP|FEED)\b",Options);
        var key=separator||country.Length==0||conflict||qualityConflict||generic||type is "Event" or "Loop"?"":country+":"+ (type=="Radio"?"RADIO:":"")+name.ToUpperInvariant();
        return new(name,conflict?"":country,qualityConflict?0:quality,codec,key,package,string.Join(" ",tags.Order()),separator,type,qualityConflict?"":qualityOrigin,fps,catchup);
    }
}
