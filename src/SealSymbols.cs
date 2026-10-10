using System;
using System.Collections.Generic;

namespace ShirenAsciiProbe;

internal static class SealSymbols
{
    private static Dictionary<string,string> labels=new(StringComparer.OrdinalIgnoreCase);
    internal static void ReplaceLabels(Dictionary<string,string> replacements)
    {
        var next=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var pair in replacements)
        {
            string value=pair.Value.Trim();
            if(!pair.Key.StartsWith("icon_seal_",StringComparison.OrdinalIgnoreCase)||value.Length<1||value.Length>8
                ||value.Contains('\r')||value.Contains('\n'))continue;
            next[pair.Key]=value;
        }
        labels=next;
    }
    internal static bool TryGet(string name,out string text)
    {
        string n=name.ToLowerInvariant();text="";
        if(!n.StartsWith("icon_seal_",StringComparison.Ordinal))return false;
        // Keep decorative and concealed states authoritative even if a custom
        // label was supplied for them. Never expose a silver-sealed identity.
        if(n=="icon_seal_base")return true;
        if(n=="icon_seal_blank"){text="□";return true;}
        if(n is "icon_seal_silver" or "icon_seal_silver_base"){text="銀";return true;}
        if(labels.TryGetValue(n,out var configured)){text=configured;return true;}
        text=n switch
        {
            "icon_seal_shield_ukemi"=>"受",
            "icon_seal_shield_counter"=>"反",
            "icon_seal_shield_fudou"=>"不",
            "icon_seal_shield_harazikara"=>"腹力",
            "icon_seal_shield_bousui"=>"鱗",
            "icon_seal_shield_nagemonoyoke"=>"投",
            // Do not infer a seal's label from generic weapon/shield keywords,
            // or disclose the identity behind a sealed silver sprite.
            _=>"印?"
        };
        return true;
    }
}
