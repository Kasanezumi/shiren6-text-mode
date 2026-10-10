using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ShirenAsciiProbe;

internal static class Performance
{
    private sealed class Stat { internal long Count,Ticks,Max; }
    private static readonly Dictionary<string,Stat> stats=new();
    private static readonly Dictionary<string,int> counts=new();
    private static long started,lastFrame,frameTicks,frameMax;
    private static int frames;
    private static bool mode,initialized;
    private static bool writeError;
    internal static long Mark()=>Stopwatch.GetTimestamp();
    internal static void Record(string name,long start)
    {
        long ticks=Mark()-start;
        if(!stats.TryGetValue(name,out var stat)){stat=new();stats[name]=stat;}
        stat.Count++;stat.Ticks+=ticks;stat.Max=Math.Max(stat.Max,ticks);
    }
    internal static void Count(string name,int value)=>counts[name]=value;
    internal static void Frame(bool ascii)
    {
        long now=Mark();
        if(!initialized||mode!=ascii)
        {
            initialized=true;mode=ascii;started=now;lastFrame=0;frameTicks=0;frameMax=0;frames=0;stats.Clear();counts.Clear();
        }
        if(lastFrame!=0){long elapsed=now-lastFrame;frameTicks+=elapsed;frameMax=Math.Max(frameMax,elapsed);frames++;}
        lastFrame=now;
    }
    internal static void FlushIfDue()
    {
        long now=Mark();
        if(!initialized||(now-started)<Stopwatch.Frequency*5)return;
        try
        {
            double factor=1000.0/Stopwatch.Frequency;
            var metrics=new Dictionary<string,object>();
            foreach(var pair in stats)
            {
                var s=pair.Value;
                metrics[pair.Key]=new {calls=s.Count,averageMs=s.Ticks*factor/Math.Max(1,s.Count),maxMs=s.Max*factor,totalMs=s.Ticks*factor};
            }
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out uint foreground);
            var sample=new {version="0.5.16",capturedUtc=DateTime.UtcNow,ascii=mode,foreground=foreground==Environment.ProcessId,
                windowMs=(now-started)*factor,observedUpdateFps=frameTicks==0?0:frames*1000.0/(frameTicks*factor),maxFrameIntervalMs=frameMax*factor,metrics,counts};
            string folder=Path.Combine(BepInEx.Paths.BepInExRootPath,"diagnostics","ShirenAsciiProbe");
            // Aggregate numbers only; no object trees, glyph dumps or map snapshots.
            File.WriteAllText(Path.Combine(folder,mode?"performance-ascii.json":"performance-original.json"),JsonSerializer.Serialize(sample,new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){if(!writeError){writeError=true;Plugin.Report("Performance sample unavailable: "+ex.Message);}}
        started=now;lastFrame=0;frameTicks=0;frameMax=0;frames=0;stats.Clear();
    }
}
