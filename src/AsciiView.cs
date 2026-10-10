using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;



namespace ShirenAsciiProbe;

public sealed class AsciiView : MonoBehaviour
{
    public AsciiView(IntPtr pointer) : base(pointer) { }
    private bool enabledView=true;
    private bool pressed, failed, reportedFrame;
    private bool waitingForFloor;
    private bool overviewFrame;
    private IntPtr activeDungeon;
    private long refreshed;
    private MapFrame? frame;
    private GUIStyle? style;
    private GUIStyle? backgroundStyle;
    private GUIStyle[]? terrainStyles;
    private GUIStyle? playerStyle;
    private int terrainFontSize,playerFontSize;
    private static readonly Dictionary<char,string> glyphText=new();
    private static readonly Dictionary<string,string> playerText=new();
    private static readonly Color[] colors={Color.black,new(.72f,.78f,.82f),new(.30f,.36f,.40f),new(.4f,1f,.63f),new(1f,.82f,.32f),new(.4f,.85f,1f),new(1f,.40f,.40f),new(.35f,.80f,1f),new(.18f,.40f,.55f)};

    public void Update()
    {
        if(failed)return;
        try
        {
            var dungeon=Plugin.GetDungeonPointer();
            bool cameraActive=Environment.TickCount64-Plugin.CameraTick<=500;
            if(dungeon==IntPtr.Zero)activeDungeon=IntPtr.Zero;
            else if(cameraActive)activeDungeon=dungeon;
            bool inDungeon=dungeon!=IntPtr.Zero&&dungeon==activeDungeon;
            bool down=(Native.GetAsyncKeyState(0x78)&0x8000)!=0; // F9
            if(down&&!pressed&&inDungeon)
            {
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out uint pid);
                if(pid==Environment.ProcessId)
                {
                    enabledView=!enabledView;
                    Plugin.Report("ASCII toggle: "+(enabledView?"ON":"OFF"));
                }
            }
            pressed=down;
            Performance.Frame(enabledView&&inDungeon);
            if(!enabledView||!inDungeon){frame=null;return;}
            UiMirror.Update();
            bool overview=UiMirror.OverviewVisible;
            bool mapModeChanged=overview!=overviewFrame;
            // A map-mode change must rebuild even when the native camera is paused.
            if(!cameraActive&&frame!=null&&!mapModeChanged)return;
            if(!mapModeChanged&&Environment.TickCount64-refreshed<(overview?500:100))return;
            refreshed=Environment.TickCount64;
            long mapStart=Performance.Mark();
            try
            {
                var state=LiveMap.Read(dungeon,overview:overview);
                frame=state==null?null:overview?AsciiModel.BuildOverview(state):AsciiModel.Build(state);
                overviewFrame=overview;
                if(state==null&&!waitingForFloor){waitingForFloor=true;Plugin.Report("ASCII waiting for floor/player initialization.");}
                else if(state!=null&&waitingForFloor){waitingForFloor=false;Plugin.Report("ASCII resumed after floor initialization.");}
            }
            finally{Performance.Record("mapReadAndBuild",mapStart);}
        }
        catch(Exception ex){Fail(ex);}
        finally{Performance.FlushIfDue();}
    }

    public void OnGUI()
    {
        // These are display-only labels with explicit rectangles. Layout and
        // input events do not need to traverse or format the map a second time.
        if(failed||!enabledView||frame==null||Event.current.type!=EventType.Repaint)return;
        Color oldColor=GUI.color;int oldDepth=GUI.depth;
        long renderStart=Performance.Mark();
        try
        {
            if(style==null)
            {
                style=new GUIStyle();
                // Reuse the game's existing font. OS font creation was stripped.
                style.font=GUI.skin.font;
                style.alignment=TextAnchor.MiddleCenter;
                style.padding=new RectOffset(0,0,0,0);
                style.margin=new RectOffset(0,0,0,0);
                style.wordWrap=false;
                backgroundStyle=new GUIStyle();
                backgroundStyle.normal.background=Texture2D.whiteTexture;
                terrainStyles=new GUIStyle[colors.Length];
                for(int i=0;i<colors.Length;i++)
                {
                    terrainStyles[i]=new GUIStyle{font=style.font,alignment=TextAnchor.MiddleCenter,padding=new RectOffset(0,0,0,0),margin=new RectOffset(0,0,0,0),wordWrap=false};
                    terrainStyles[i].normal.textColor=colors[i];
                }
                playerStyle=new GUIStyle{font=style.font,alignment=TextAnchor.MiddleCenter,padding=new RectOffset(0,0,0,0),margin=new RectOffset(0,0,0,0),wordWrap=false};playerStyle.normal.textColor=colors[3];
            }
            // Cover the entire game surface, including the former HUD/message bands.
            float height=Screen.height;
            GUI.depth=-1000;
            GUI.color=Color.black;
            // GUI.DrawTexture overloads lead to an unavailable stripped method.
            GUI.Label(new Rect(0,0,Screen.width,height),"",backgroundStyle);
            float cell=Math.Min((Screen.width-24f)/frame.Columns,(height*.68f)/frame.Rows);
            float left=(Screen.width-cell*frame.Columns)/2f;
            float top=height*.16f+(height*.68f-cell*frame.Rows)/2f;
            var facing=AsciiModel.Facing(frame.FacingX,frame.FacingY);
            int normalSize=Math.Max(1,(int)(cell*.92f));
            int facingSize=Math.Max(1,(int)(cell*(facing.Arrow.Length>0?.56f:.92f)));
            if(terrainFontSize!=normalSize){terrainFontSize=normalSize;foreach(var s in terrainStyles!)s.fontSize=normalSize;}
            if(playerFontSize!=facingSize){playerFontSize=facingSize;playerStyle!.fontSize=facingSize;}
            if(!playerText.TryGetValue(facing.Arrow,out string? playerLabel)){playerLabel="@"+facing.Arrow;playerText[facing.Arrow]=playerLabel;}
            GUI.color=Color.white;
            for(int y=0;y<frame.Rows;y++)for(int x=0;x<frame.Columns;x++)
            {
                var glyph=frame.Cells[y*frame.Columns+x];
                if(glyph.Character==' ')continue;
                bool playerCell=glyph.Character=='@'&&glyph.Palette==3;
                if(!glyphText.TryGetValue(glyph.Character,out string? text)){text=glyph.Character.ToString();glyphText[glyph.Character]=text;}
                // Keep both characters inside the player's own cell, so an
                // adjacent enemy, item or wall never gets covered by the arrow.
                GUI.Label(new Rect(left+x*cell,top+y*cell,cell,cell),playerCell?playerLabel:text,playerCell?playerStyle:terrainStyles![glyph.Palette]);
            }
            style.normal.textColor=new Color(.65f,.7f,.75f);
            style.fontSize=14;
            GUI.Label(new Rect(0,height-24,Screen.width,20),"ASCII 0.5.16 | F9: original / text view | F8: capture",style);
            Performance.Record("mapDraw",renderStart);
            long uiDrawStart=Performance.Mark();
            UiMirror.Draw("向き："+facing.Name+" "+facing.Arrow);
            Performance.Record("uiDraw",uiDrawStart);
            if(!reportedFrame && Event.current.type==EventType.Repaint)
            {
                reportedFrame=true;
                Plugin.Report("ASCII frame rendered: player="+frame.PlayerX+","+frame.PlayerY+"; viewport="+frame.Columns+"x"+frame.Rows+"; full-screen="+Screen.width+"x"+Screen.height);
            }
        }
        catch(Exception ex){Fail(ex);}
        finally{GUI.color=oldColor;GUI.depth=oldDepth;Performance.Record("allDraw",renderStart);}
    }
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void Fail(Exception ex){failed=true;enabledView=false;frame=null;Plugin.Report("ASCII view disabled: "+ex);}
}
