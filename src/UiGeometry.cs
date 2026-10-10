using System;
namespace ShirenAsciiProbe;
internal static class UiGeometry
{
    // Conservative: include a margin for glyph-bound rounding and never reject
    // unknown geometry. The original exact layout still runs for candidates.
    internal static bool MayOverlap(float x0,float y0,float x1,float y1,int width,int height)
    {
        if(!float.IsFinite(x0)||!float.IsFinite(y0)||!float.IsFinite(x1)||!float.IsFinite(y1)||x1<x0||y1<y0)return true;
        const float margin=8;
        return x1>=-margin&&x0<=width+margin&&y1>=-margin&&y0<=height+margin;
    }
}
