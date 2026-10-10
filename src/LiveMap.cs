using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using F7.Scripts.Dungeon;
using F7.Scripts.Dungeon.Base;
namespace ShirenAsciiProbe;

internal static class LiveMap
{
    private static readonly Dictionary<(IntPtr,string),int> offsets=new();
    internal static MapState? Read(IntPtr dungeon,bool capture=false,bool overview=false)
    {
        // Floor transitions temporarily remove/rebuild these objects. A null
        // result means "wait for the next sample", not an unsupported layout.
        if(dungeon==IntPtr.Zero)return null;
        var terrain=Ref(dungeon,"egbe");if(terrain==IntPtr.Zero)return null;
        var size=Address(terrain,"egxz");
        int width=Marshal.ReadInt32(size),height=Marshal.ReadInt32(size,4);
        if(width==0||height==0)return null;
        if(width<1||height<1||width>128||height>128)throw new InvalidOperationException("Unsupported map dimensions");
        var creatureManager=Ref(dungeon,"egbc");if(creatureManager==IntPtr.Zero)return null;
        var creatureList=Ref(creatureManager,"egfm");if(creatureList==IntPtr.Zero)return null;
        var creatures=new List<IntPtr>(List(creatureList));
        IntPtr playerPointer=IntPtr.Zero;int px=-1,py=-1;
        foreach(var obj in creatures)
        {
            if(TypeName(obj)!="F7.Scripts.Dungeon.Player")continue;
            playerPointer=obj;var coord=Address(obj,"eglq");
            px=Marshal.ReadInt32(coord);py=Marshal.ReadInt32(coord,4);break;
        }
        if(playerPointer==IntPtr.Zero||px<0||py<0||px>=width||py>=height)return null;
        var player=new Player(playerPointer);
        // Convert the actual game direction through its own direction table;
        // do not infer it from movement, since turning in place also matters.
        int direction=Int(playerPointer,"eglt");
        int facingX=0,facingY=0;
        if(direction>=0&&direction<8)
        {
            var delta=new Coord(new Direction(direction));
            facingX=delta.ffwv;facingY=delta.ffww;
        }
        var origin=new Coord(px,py);
        var map=new MapTile[width*height];
        Array.Fill(map,new MapTile(0,0));
        var window=AsciiModel.ReadWindow(width,height,px,py);
        int x0=(capture||overview)?0:window.Left,y0=(capture||overview)?0:window.Top;
        int x1=(capture||overview)?width-1:window.Right,y1=(capture||overview)?height-1:window.Bottom;
        var grid=Ref(terrain,"egya");
        if(!ReadyArray(grid,height))return null;
        for(int y=y0;y<=y1;y++)
        {
            var row=Marshal.ReadIntPtr(grid,32+y*8);if(!ReadyArray(row,width))return null;
            for(int x=x0;x<=x1;x++)
            {
                var cell=Marshal.ReadIntPtr(row,32+x*8);
                if(cell==IntPtr.Zero)return null;
                uint flags=Bits(Ref(cell,"egtm"));
                // qxa checks the live sight radius, room IDs and floor restrictions.
                // The final argument enables the native one-cell outer room ring
                // (all eight neighbors). Keep straight-line sight disabled.
                // Native floor restrictions and corridor sight remain authoritative.
                bool visible=(flags&(AsciiModel.Revealed|AsciiModel.Explored))!=0&&player.qxa(origin,new Coord(x,y),false,true);
                map[y*width+x]=new MapTile(Int(cell,"egtn"),flags,visible,Marshal.ReadByte(Address(cell,"egtq")));
            }
        }
        var entities=new List<MapEntity>();
        foreach(var obj in creatures)
        {
            var coord=Address(obj,"eglq");int x=Marshal.ReadInt32(coord),y=Marshal.ReadInt32(coord,4);
            string type=TypeName(obj);
            uint id=(uint)Int(obj,"eglb");
            string? name=null;
            if(type.Contains("Monster")&&x>=0&&y>=0&&x<width&&y<height&&AsciiModel.CanShowEntity(map[y*width+x],false,0))
                name=EnemyNames.Resolve(id);
            entities.Add(new MapEntity(x,y,type,id,0,false,name));
        }
        var itemManager=Ref(dungeon,"egbg");if(itemManager==IntPtr.Zero)return null;
        foreach(var obj in List(Ref(itemManager,"efwl")))
        {
            var coord=Address(obj,"egqn");
            entities.Add(new MapEntity(Marshal.ReadInt32(coord),Marshal.ReadInt32(coord,4),TypeName(obj),0,Bits(Ref(obj,"egqm")),true));
        }
        if(capture)
        {
            CaptureVisibility(map,entities,width,height,px,py,direction,facingX,facingY);
        }
        return new MapState(width,height,map,entities,px,py,facingX,facingY);
    }
    private static void CaptureVisibility(MapTile[] tiles,List<MapEntity> entities,int width,int height,int px,int py,int direction,int facingX,int facingY)
    {
        try
        {
            var entries=new List<object>();
            foreach(var e in entities)
            {
                if(e.X<0||e.Y<0||e.X>=width||e.Y>=height)continue;
                var tile=tiles[e.Y*width+e.X];
                entries.Add(new{e.X,e.Y,e.Type,tile.Kind,tile.RoomId,tile.CurrentlyVisible,discovered=(tile.Flags&80)!=0,
                    solidWall=tile.Kind is 2 or 8,tileFlags=tile.Flags,concealingWall=AsciiModel.IsConcealingWall(tile),
                    concealedByWall=(tile.Kind is 2 or 8)&&(e.IsItem||AsciiModel.IsConcealingWall(tile)),drawn=AsciiModel.CanShowEntity(tile,e.IsItem,e.ItemFlags)});
            }
            string path=System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,"diagnostics","ShirenAsciiProbe","visibility-latest.json");
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{version="0.5.16",roomOuterRing=true,capturedUtc=DateTime.UtcNow,playerX=px,playerY=py,direction,facingX,facingY,facing=AsciiModel.Facing(facingX,facingY).Name,playerRoom=tiles[py*width+px].RoomId,entities=entries}));
        }
        catch(Exception ex){Plugin.Report("Visibility capture failed: "+ex.Message);}
    }
    private static IntPtr Address(IntPtr obj,string name)
    {
        if(obj==IntPtr.Zero)throw new InvalidOperationException("Missing object for "+name);
        var klass=Native.il2cpp_object_get_class(obj);
        if(!offsets.TryGetValue((klass,name),out int offset))
        {
            IntPtr field=IntPtr.Zero;
            for(var k=klass;k!=IntPtr.Zero&&field==IntPtr.Zero;k=Native.il2cpp_class_get_parent(k))field=Native.il2cpp_class_get_field_from_name(k,name);
            if(field==IntPtr.Zero||(Native.il2cpp_field_get_flags(field)&0x10)!=0)throw new MissingFieldException(name);
            offset=(int)Native.il2cpp_field_get_offset(field);
            if(offset<16 || offset+4>Native.il2cpp_class_instance_size(klass))throw new InvalidOperationException("Unsupported field layout");
            offsets[(klass,name)]=offset;
        }
        return IntPtr.Add(obj,offset);
    }
    private static IntPtr Ref(IntPtr obj,string name)=>Marshal.ReadIntPtr(Address(obj,name));
    private static int Int(IntPtr obj,string name)=>Marshal.ReadInt32(Address(obj,name));
    private static uint Bits(IntPtr obj)
    {
        if(obj==IntPtr.Zero)return 0;
        var a=Ref(obj,"m_array");CheckArray(a,1,4);return (uint)Marshal.ReadInt32(a,32);
    }
    private static void CheckArray(IntPtr a,int minimum,int stride=8)
    {
        if(!ReadyArray(a,minimum,stride))throw new InvalidOperationException("Missing or incomplete array");
    }
    private static bool ReadyArray(IntPtr a,int minimum,int stride=8)
    {
        if(a==IntPtr.Zero)return false;
        var k=Native.il2cpp_object_get_class(a);
        if(Native.il2cpp_class_get_rank(k)!=1||Native.il2cpp_array_element_size(k)!=stride)throw new InvalidOperationException("Unsupported array layout");
        var length=Native.il2cpp_array_length(a).ToUInt64();
        if(length>16384)throw new InvalidOperationException("Unsupported array length");
        return length>=(ulong)minimum;
    }
    private static IEnumerable<IntPtr> List(IntPtr list)
    {
        if(list==IntPtr.Zero)yield break;
        int count=Int(list,"_size");if(count<0||count>1024)throw new InvalidOperationException("Unsupported list length");
        if(count==0)yield break;
        var array=Ref(list,"_items");if(!ReadyArray(array,count))yield break;
        for(int i=0;i<count;i++){var p=Marshal.ReadIntPtr(array,32+i*8);if(p!=IntPtr.Zero)yield return p;}
    }
    private static string TypeName(IntPtr obj)
    {
        var k=Native.il2cpp_object_get_class(obj);
        return Marshal.PtrToStringUTF8(Native.il2cpp_class_get_namespace(k))+"."+Marshal.PtrToStringUTF8(Native.il2cpp_class_get_name(k));
    }
}
