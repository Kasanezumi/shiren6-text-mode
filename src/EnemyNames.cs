using System;
using System.Collections.Generic;
using F7.Lib.Localization.Runtime;
using F7.Scripts.UI.Parts.ListMenu.ListMenuBase.Interface;

namespace ShirenAsciiProbe;

internal static class EnemyNames
{
    private static readonly Dictionary<uint,string> names=new();
    private static readonly Dictionary<uint,long> retryAfter=new();
    private static readonly HashSet<uint> reportedFailure=new();
    internal static string? Resolve(uint id)
    {
        if(names.TryGetValue(id,out var cached))return cached;
        if(retryAfter.TryGetValue(id,out long next)&&Environment.TickCount64<next)return null;
        retryAfter[id]=Environment.TickCount64+5000;
        try
        {
            if(!Localizables.IsReady())return null;
            string key=new LocalizeKeyCreatureNameCommonParam(unchecked((int)id)).fhup;
            if(!string.IsNullOrEmpty(key)&&Localizables.TryGet(key,out string name)&&!string.IsNullOrWhiteSpace(name))
            {
                names[id]=name;
                Plugin.Report("Enemy glyph: id="+id+"; name="+name+"; glyph="+AsciiModel.EnemyGlyph(name));
                return name;
            }
            if(reportedFailure.Add(id))Plugin.Report("Enemy name unavailable: id="+id+"; key="+key);
        }
        catch(Exception ex){if(reportedFailure.Add(id))Plugin.Report("Enemy name lookup failed: "+id+"; "+ex.Message);}
        return null;
    }
}
