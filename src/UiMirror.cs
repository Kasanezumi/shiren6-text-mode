using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UISystem.Scripts.Components;
using UISystem.Scripts;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace ShirenAsciiProbe;

internal sealed record UiLine(string Path,string Text,float X,float Y,float W,float H,int Size,int Align,bool Selected);
internal sealed record UiPanel(float X,float Y,float W,float H);
internal sealed record UiImage(string Name,string Sprite,string Path,float X,float Y,float W,float H);

internal static class UiMirror
{
    private static TMP_Text[] tmp=Array.Empty<TMP_Text>();
    private static Text[] legacy=Array.Empty<Text>();
    private static CustomInfiniteScrollBase[] scrolls=Array.Empty<CustomInfiniteScrollBase>();
    private static Image[] images=Array.Empty<Image>();
    private static List<UiImage> visibleImages=new();
    private static List<UiLine> bodyLines=new();
    private static bool menuVisible;
    private static readonly Dictionary<int,bool> hierarchyCache=new();
    private static readonly Dictionary<int,string> pathCache=new();
    private static Regex cursorRegex=new("cursor|focus|selectmark|selectframe",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(30));
    private static long discovered,refreshed,captured;
    private static readonly Il2CppStructArray<Vector3> corners=new(4);
    private static List<UiLine> lines=new();
    private static List<UiPanel> panels=new();
    private static List<string> selections=new();
    private static GUIStyle? label,black;
    private static bool reported;
    private static string lastError="";
    private static string cursorPattern="cursor|focus|selectmark|selectframe";
    private static float textScale=1f;
    private static readonly Dictionary<char,string> iconOverrides=new();
    private static readonly Dictionary<char,string> iconNames=new();
    private static readonly List<object> textDiagnostics=new();
    private static readonly Regex Tags=new("<[^>]+>",RegexOptions.Compiled);
    private static bool fastUiBounds=true;

    internal static void Update()
    {
        long now=Environment.TickCount64;
        if(now-refreshed<80)return;
        refreshed=now;
        long updateStart=Performance.Mark(),step=updateStart;
        try
        {
            // Scope these caches to a single UI sample: menu reparenting,
            // fades and selection changes remain visible on the next sample.
            hierarchyCache.Clear();pathCache.Clear();
            if(now-captured>2000){captured=now;ReadSettings();}
            if(now-discovered>500)
            {
                step=Performance.Mark();
                discovered=now;
                tmp=Resources.FindObjectsOfTypeAll<TMP_Text>().ToArray();
                legacy=Resources.FindObjectsOfTypeAll<Text>().ToArray();
                scrolls=Resources.FindObjectsOfTypeAll<CustomInfiniteScrollBase>().ToArray();
                images=Resources.FindObjectsOfTypeAll<Image>().ToArray();
                Performance.Record("uiDiscovery",step);
                Performance.Count("tmpObjects",tmp.Length);Performance.Count("legacyObjects",legacy.Length);
                Performance.Count("scrollObjects",scrolls.Length);Performance.Count("imageObjects",images.Length);
            }
            step=Performance.Mark();
            var next=new List<UiLine>();var nextPanels=new List<UiPanel>();
            var selectedPaths=new List<string>();
            foreach(var scroll in scrolls)
            {
                if(scroll==null||!scroll.isActiveAndEnabled||!VisibleHierarchy(scroll.transform))continue;
                var element=scroll.InfiniteScrollElement;
                if(element==null)continue;
                var instance=element.Cast<IUIObjectSystemBase>().Instance;
                if(instance==null||!instance.activeInHierarchy)continue;
                selectedPaths.Add(Path(instance.transform));
                var rt=scroll.transform.TryCast<RectTransform>();
                var canvas=scroll.GetComponentInParent<Canvas>();
                if(rt!=null&&canvas!=null&&Bounds(rt,canvas,out var box))nextPanels.Add(box);
            }
            var eventSystem=EventSystem.current;
            if(eventSystem!=null&&eventSystem.currentSelectedGameObject!=null)
                selectedPaths.Add(Path(eventSystem.currentSelectedGameObject.transform));
            Performance.Record("uiSelection",step);step=Performance.Mark();
            int geometrySkipped=0,converted=0;
            foreach(var source in tmp)
            {
                if(source==null||!source.isActiveAndEnabled)continue;
                // Most of the game's dormant menus remain active. Test their
                // geometry before parsing sprites or traversing text paths.
                if(!TextLayout(source,out var box,out bool glyphFallback)){geometrySkipped++;continue;}
                if(!VisibleText(source))continue;
                var text=ReadText(source);
                converted++;
                // maxVisibleCharacters counts source glyphs, not the expanded ASCII tokens.
                Add(next,source,text,source.fontSize,Alignment((int)source.alignment),selectedPaths,box,glyphFallback);
            }
            Performance.Count("geometrySkipped",geometrySkipped);Performance.Count("convertedTexts",converted);
            Performance.Record("uiTmpRead",step);step=Performance.Mark();
            foreach(var source in legacy)
            {
                if(!Visible(source))continue;
                Add(next,source,Plain(source.text),source.fontSize,(int)source.alignment,selectedPaths);
            }
            Performance.Record("uiLegacyRead",step);step=Performance.Mark();
            var imageList=new List<UiImage>();
            var equippedRows=new HashSet<string>(StringComparer.Ordinal);
            foreach(var source in images)
            {
                // Measure only cursors and the native equipped check. Reuse the
                // existing image scan rather than adding another global search.
                if(source==null||!source.isActiveAndEnabled)continue;
                string imageName=source.name,spriteName=source.sprite==null?"":source.sprite.name;
                bool cursor=cursorRegex.IsMatch(imageName+" "+spriteName);
                bool equipped=EquipmentLabels.IsMarker(imageName,spriteName);
                if(!cursor&&!equipped)continue;
                if(!Visible(source)||source.canvas==null||!Bounds(source.rectTransform,source.canvas,out var box))continue;
                string path=Path(source.transform);
                if(cursor)imageList.Add(new(imageName,spriteName,path,box.X,box.Y,box.W,box.H));
                if(equipped){string? row=EquipmentLabels.Row(path);if(row!=null)equippedRows.Add(row);}
            }
            visibleImages=imageList;
            Performance.Record("uiImageRead",step);
            for(int i=0;i<next.Count;i++)
            {
                var line=next[i];string text=EquipmentLabels.Apply(line.Path,line.Text,equippedRows);
                if(text!=line.Text)next[i]=line with {Text=text};
            }
            Performance.Count("equippedRows",equippedRows.Count);
            // The bottom Y-menu status shares CommonBaseUi with the rebuilt top HUD.
            // Use only already-visible text and one background for the whole panel.
            var simpleStatus=next.Where(l=>l.Path.Contains("/CommonBaseUi[")&&l.Path.Contains("/Ui_SimpleStatus[")).ToList();
            if(simpleStatus.Count>0)
            {
                float x=simpleStatus.Min(l=>l.X),y=simpleStatus.Min(l=>l.Y);
                nextPanels.Add(new(x,y,simpleStatus.Max(l=>l.X+l.W)-x,simpleStatus.Max(l=>l.Y+l.H)-y));
            }
            lines=next;panels=nextPanels;selections=selectedPaths;
            menuVisible=lines.Any(l=>l.Path.Contains("/ItemSelect[")||l.Path.Contains("/XMenu[")||l.Path.Contains("/CommandMenu["));
            bodyLines=lines.Where(l=>!l.Path.Contains("/CommonBaseUi[")||l.Path.Contains("/Ui_SimpleStatus[")).Select(l=>l with {Selected=l.Selected||visibleImages.Any(icon=>l.X>=icon.X-2&&l.X<icon.X+icon.W+24&&Math.Abs((l.Y+l.H/2)-(icon.Y+icon.H/2))<l.H*.45f)}).ToList();
            Performance.Count("drawnLines",lines.Count);Performance.Count("cursorImages",visibleImages.Count);
        }
        catch(Exception ex)
        {
            lines=new();panels=new();bodyLines=new();visibleImages=new();menuVisible=false;
            if(lastError!=ex.Message){lastError=ex.Message;Plugin.Report("UI mirror read failed: "+ex);}
        }
        finally{Performance.Record("uiUpdate",updateStart);}
    }

    private static string ReadText(TMP_Text source)
    {
        string parsed=source.GetParsedText()??"";
        int max=source.maxVisibleCharacters;
        if(max>=0&&max<parsed.Length)parsed=parsed.Substring(0,max);
        var chars=parsed.ToCharArray();var info=source.textInfo;
        for(int i=0;i<chars.Length;i++)
        {
            if(char.GetUnicodeCategory(chars[i])!=System.Globalization.UnicodeCategory.PrivateUse)continue;
            if(info!=null&&i<info.characterCount)
            {
                var glyph=info.characterInfo[i];
                string name=SpriteName(glyph);
                if(name.Length>0)iconNames[chars[i]]=name;
                if(!glyph.isVisible||glyph.color.a==0)chars[i]=' ';
            }
        }
        return Plain(new string(chars));
    }
    private static string SpriteName(TMP_CharacterInfo glyph)
    {
        if(glyph.spriteAsset!=null&&glyph.spriteIndex>=0&&glyph.spriteIndex<glyph.spriteAsset.spriteCharacterTable.Count)
            return glyph.spriteAsset.spriteCharacterTable[glyph.spriteIndex].name??"";
        var sprite=glyph.textElement?.TryCast<TMP_SpriteCharacter>();
        return sprite==null?"":sprite.name??"";
    }
    private static bool VisibleText(TMP_Text source)
    {
        if(source==null||!source.isActiveAndEnabled||source.canvasRenderer.cull||source.canvasRenderer.GetAlpha()<.02f||!VisibleHierarchy(source.transform))return false;
        if(source.color.a>=.02f)return true;
        var info=source.textInfo;
        // Color tags can give visible glyphs an alpha different from the base color.
        if(info!=null)for(int i=0;i<Math.Min(info.characterCount,128);i++)
        {
            var glyph=info.characterInfo[i];if(glyph.isVisible&&glyph.color.a>0)return true;
        }
        return false;
    }

    private static void Add(List<UiLine> result,Graphic source,string text,float fontSize,int alignment,List<string> selected,UiPanel? measured=null,bool glyphFallback=false)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>4096)return;
        string path=Path(source.transform);
        if(path.Contains("InfoSealWindow")&&path.Contains("/InfoTextBase["))return;
        if(path.Contains("InfoSealWindow")&&path.Contains("/InfoTextForward["))
        {
            var sealSource=source.TryCast<TMP_Text>();
            if(sealSource!=null){AddSealSlots(result,sealSource,path);return;}
        }
        var canvas=source.canvas;var rect=source.rectTransform;
        if(canvas==null)return;
        UiPanel box;
        if(measured!=null){box=measured;if(glyphFallback)alignment=(int)TextAnchor.MiddleLeft;}
        else if(!Bounds(rect,canvas,out box))
        {
            var textSource=source.TryCast<TMP_Text>();
            if(textSource==null||!GlyphBounds(textSource,canvas,out box))return;
            alignment=(int)TextAnchor.MiddleLeft;
        }
        float scale=rect.rect.height>0?box.H/rect.rect.height:canvas.scaleFactor;
        int size=Math.Clamp((int)Math.Round(fontSize*scale),12,64);
        bool focused=selected.Any(s=>path==s||path.StartsWith(s+"/",StringComparison.Ordinal));
        result.Add(new UiLine(path,text,box.X,box.Y,box.W,Math.Max(box.H,size+4),size,alignment,focused));
    }
    private static bool TextLayout(TMP_Text source,out UiPanel box,out bool glyphFallback)
    {
        box=new(0,0,0,0);glyphFallback=false;
        var canvas=source.canvas;var rect=source.rectTransform;
        if(canvas==null)return false;
        var scale=rect.lossyScale;
        if(scale.x==0&&scale.y==0)return false;
        if(Bounds(rect,canvas,out box))return true;
        // Native TMP computes an enclosing local bound without making a managed
        // interop call for each glyph. It is only an early reject, never the
        // rectangle used to place text; number and seal positioning stays exact.
        if(fastUiBounds&&canvas.renderMode==RenderMode.ScreenSpaceOverlay&&!TextMayOverlap(source))return false;
        glyphFallback=true;return GlyphBounds(source,canvas,out box);
    }
    private static bool TextMayOverlap(TMP_Text source)
    {
        var bounds=source.textBounds;var min=bounds.min;var max=bounds.max;
        // Unknown or degenerate metadata must fall back to the original scan.
        if(!float.IsFinite(min.x)||!float.IsFinite(min.y)||!float.IsFinite(max.x)||!float.IsFinite(max.y)||max.x<=min.x||max.y<=min.y)return true;
        float x0=float.MaxValue,y0=float.MaxValue,x1=float.MinValue,y1=float.MinValue;
        var transform=source.transform;
        for(int i=0;i<4;i++)
        {
            var p=RectTransformUtility.WorldToScreenPoint(null,transform.TransformPoint(new Vector3((i&1)==0?min.x:max.x,(i&2)==0?min.y:max.y,0)));
            x0=Math.Min(x0,p.x);x1=Math.Max(x1,p.x);y0=Math.Min(y0,p.y);y1=Math.Max(y1,p.y);
        }
        return UiGeometry.MayOverlap(x0,y0,x1,y1,Screen.width,Screen.height);
    }
    private static void AddSealSlots(List<UiLine> result,TMP_Text source,string path)
    {
        var info=source.textInfo;var canvas=source.canvas;
        if(info==null||canvas==null)return;
        var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
        for(int i=0;i<Math.Min(info.characterCount,source.maxVisibleCharacters);i++)
        {
            var glyph=info.characterInfo[i];
            if(!glyph.isVisible||glyph.color.a==0)continue;
            string name=SpriteName(glyph);
            string text=SealSymbols.TryGet(name,out var seal)?seal:Plain(glyph.character.ToString()).Trim();
            if(string.IsNullOrWhiteSpace(text))continue;
            var a=RectTransformUtility.WorldToScreenPoint(camera,source.transform.TransformPoint(glyph.bottomLeft));
            var b=RectTransformUtility.WorldToScreenPoint(camera,source.transform.TransformPoint(glyph.topRight));
            float x=Math.Min(a.x,b.x),y=Screen.height-Math.Max(a.y,b.y),w=Math.Abs(b.x-a.x),h=Math.Abs(b.y-a.y);
            if(w<=0||h<=0||x+w<=0||x>=Screen.width||y+h<=0||y>=Screen.height)continue;
            // Preserve the game's individual slots, including its explicit row
            // break. Expanded labels must never move their neighboring seals.
            int size=Math.Clamp((int)(h*.72f),12,48);
            result.Add(new UiLine(path+"/SealSlot["+i+"]",text,x,y,w,h,size,(int)TextAnchor.MiddleCenter,false));
        }
    }
    private static bool GlyphBounds(TMP_Text source,Canvas canvas,out UiPanel result)
    {
        result=new(0,0,0,0);var info=source.textInfo;
        if(info==null)return false;
        var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
        float x0=float.MaxValue,y0=float.MaxValue,x1=float.MinValue,y1=float.MinValue;
        for(int i=0;i<Math.Min(info.characterCount,source.maxVisibleCharacters);i++)
        {
            var glyph=info.characterInfo[i];if(!glyph.isVisible||glyph.color.a==0)continue;
            foreach(var point in new[]{glyph.bottomLeft,glyph.topRight})
            {
                var screen=RectTransformUtility.WorldToScreenPoint(camera,source.transform.TransformPoint(point));
                x0=Math.Min(x0,screen.x);x1=Math.Max(x1,screen.x);
                y0=Math.Min(y0,Screen.height-screen.y);y1=Math.Max(y1,Screen.height-screen.y);
            }
        }
        if(x1<=x0||y1<=y0||x1<=0||x0>=Screen.width||y1<=0||y0>=Screen.height)return false;
        result=new(x0-2,y0-4,x1-x0+8,y1-y0+8);return true;
    }

    private static bool Visible(Graphic graphic)
    {
        if(graphic==null||!graphic.isActiveAndEnabled||graphic.color.a<.02f)return false;
        if(graphic.canvasRenderer.cull||graphic.canvasRenderer.GetAlpha()<.02f)return false;
        return VisibleHierarchy(graphic.transform);
    }
    private static bool VisibleHierarchy(Transform transform)
    {
        int id=transform.GetInstanceID();
        if(hierarchyCache.TryGetValue(id,out bool cached))return cached;
        bool visible=transform.gameObject.activeInHierarchy;
        if(visible)
        {
            var group=transform.GetComponent<CanvasGroup>();
            if(group!=null&&group.alpha<.02f)visible=false;
            else if(group==null||!group.ignoreParentGroups)
            {
                var parent=transform.parent;
                if(parent!=null)visible=VisibleHierarchy(parent);
            }
        }
        hierarchyCache[id]=visible;return visible;
    }
    private static bool Bounds(RectTransform rt,Canvas canvas,out UiPanel result)
    {
        rt.GetWorldCorners(corners);
        var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
        float x0=float.MaxValue,y0=float.MaxValue,x1=float.MinValue,y1=float.MinValue;
        for(int i=0;i<4;i++)
        {
            var p=RectTransformUtility.WorldToScreenPoint(camera,corners[i]);
            x0=Math.Min(x0,p.x);x1=Math.Max(x1,p.x);y0=Math.Min(y0,Screen.height-p.y);y1=Math.Max(y1,Screen.height-p.y);
        }
        result=new(x0,y0,x1-x0,y1-y0);
        return x1>0&&x0<Screen.width&&y1>0&&y0<Screen.height&&x1>x0&&y1>y0;
    }
    private static int Alignment(int value)
    {
        int horizontal=(value&4)!=0?2:(value&2)!=0?1:0;
        int vertical=(value&1024)!=0?2:(value&(512|4096|8192))!=0?1:0;
        return vertical*3+horizontal;
    }
    internal static string Plain(string value)
    {
        var text=Tags.Replace(value??"","");var result=new StringBuilder();
        foreach(char c in text)
        {
            if(iconOverrides.TryGetValue(c,out string? replacement)){result.Append(replacement);continue;}
            if(iconNames.TryGetValue(c,out string? name)&&TryNamedIcon(name,out string named)){result.Append(named);continue;}
            result.Append(c switch
            {
                '\uE008'=>"腕 ",'\uE2BE'=>"杖 ",'\uE235'=>"巻 ",'\uE11F'=>"草 ",'\uE118'=>"飯 ",
                '\uE2A5'=>"盾 ",'\uE2C1'=>"剣 ",'\uE11E'=>"金 ",'\uE005'=>"Items ",
                '\uE237'=>"[ ]",'\u00a0'=>" ",'\uFFFC'=>"◇",_=>char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.PrivateUse?"◇":c.ToString()
            });
        }
        return result.ToString();
    }
    private static bool TryNamedIcon(string name,out string text)
    {
        string n=name.ToLowerInvariant();text="";
        if(SealSymbols.TryGet(n,out text))return true;
        if(Regex.IsMatch(n,"(^|_)(blank|space|dummy|none|null)($|_)"))return true;
        if(n.Contains("empty")&&n.Contains("seal")){text="[ ]";return true;}
        if(n.Contains("curse")){text="[呪]";return true;}
        if(n.Contains("bless")){text="[祝]";return true;}
        if(n.StartsWith("icon_"))
        {
            text=n.Contains("weapon")?"剣 ":n.Contains("shield")?"盾 ":n.Contains("scroll")?"巻 ":
                n.Contains("grass")?"草 ":n.Contains("bracelet")?"腕 ":n.Contains("wand")?"杖 ":
                n.Contains("arrow")?"矢 ":n.Contains("stone")?"石 ":n.Contains("gitan")?"金 ":
                n.Contains("momoman")?"桃 ":n.Contains("food")?"飯 ":n.Contains("aroma")?"香 ":
                n.Contains("pot")?"壺 ":"";
            if(text.Length>0)return true;
        }
        if(n.Contains("button")||n.Contains("key_"))
        {
            if(n.Contains("ps4")||n.Contains("ps5"))
            {
                text=n.Contains("buttoneast")?"[Circle] ":n.Contains("buttonsouth")?"[Cross] ":n.Contains("buttonnorth")?"[Triangle] ":n.Contains("buttonwest")?"[Square] ":"[Key] ";return true;
            }
            text=n.Contains("buttoneast")?"[B] ":n.Contains("buttonsouth")?"[A] ":n.Contains("buttonnorth")?"[Y] ":n.Contains("buttonwest")?"[X] ":"[Key] ";return true;
        }
        return false;
    }
    private static string Path(Transform transform)
    {
        int id=transform.GetInstanceID();
        if(pathCache.TryGetValue(id,out var cached))return cached;
        var names=new List<string>();
        for(var t=transform;t!=null&&names.Count<24;t=t.parent)names.Add(t.name+"["+t.GetInstanceID()+"]");
        names.Reverse();string path=string.Join("/",names);pathCache[id]=path;return path;
    }

    internal static void Draw(string facingText)
    {
        if(label==null)
        {
            label=new GUIStyle();label.font=GUI.skin.font;label.wordWrap=true;label.richText=false;
            label.padding=new RectOffset(0,0,0,0);label.margin=new RectOffset(0,0,0,0);
            black=new GUIStyle();black.normal.background=Texture2D.whiteTexture;
        }
        if(menuVisible)Fill(new Rect(0,Screen.height*.16f,Screen.width,Screen.height*.80f));
        foreach(var panel in panels)Fill(new Rect(panel.X-12,panel.Y-6,panel.W+24,panel.H+12));
        var body=bodyLines;
        // Paint all backgrounds before any text: overlapping source rectangles must
        // never erase characters that were already drawn.
        foreach(var line in body)Fill(new Rect(line.X,line.Y,line.W,line.H));
        foreach(var line in body)
        {
            var bounds=new Rect(line.X,line.Y,line.W,line.H);
            GUI.color=Color.white;
            label.fontSize=Math.Clamp((int)(line.Size*textScale),12,64);label.alignment=(TextAnchor)line.Align;
            // A one-glyph source field cannot hold an expanded icon label wrapped
            // over adjacent rows. Fit its replacement on one line instead.
            bool iconField=line.Path.Contains("/IconText[")||line.Path.Contains("/ActKey[")||line.Path.Contains("/SealSlot[")||line.Path.Contains("/Ui_SimpleStatus[");
            string displayText=iconField?line.Text.Trim():line.Text;
            label.wordWrap=!iconField;
            if(iconField)
            {
                var content=new GUIContent(displayText);
                float measured=label.CalcSize(content).x;
                if(measured>line.W&&measured>0)
                    label.fontSize=Math.Max(1,(int)Math.Floor(label.fontSize*line.W/measured));
            }
            bool selected=line.Selected;
            label.normal.textColor=selected?new Color(.45f,1f,.6f):new Color(.9f,.93f,.95f);
            GUI.Label(bounds,displayText,label);
            if(line.Selected&&selections.Any(s=>line.Path==s||line.Path.StartsWith(s+"/",StringComparison.Ordinal)))
            {
                label.alignment=TextAnchor.MiddleRight;
                GUI.Label(new Rect(Math.Max(0,line.X-26),line.Y,22,line.H),">",label);
            }
        }
        foreach(var icon in visibleImages)
        {
            if(icon.W>Screen.width*.7f||icon.H>Screen.height*.25f)continue;
            label.fontSize=Math.Clamp((int)(icon.H*.7f),18,36);label.alignment=TextAnchor.MiddleCenter;
            label.normal.textColor=new Color(.45f,1f,.6f);GUI.color=Color.white;
            // The original cursor can be an entire row highlight. Anchor the
            // ASCII marker to its left edge rather than its center.
            GUI.Label(new Rect(Math.Max(0,icon.X-28),icon.Y,24,icon.H),">",label);
        }
        label.wordWrap=true;
        DrawStatus(facingText);
        if(!reported&&Event.current.type==EventType.Repaint&&lines.Count>0)
        {
            reported=true;Plugin.Report("Text UI rendered: "+lines.Count+" text elements, "+panels.Count+" menu panels");
        }
    }
    private static string Value(string name)=>lines.FirstOrDefault(l=>l.Path.Contains("/CommonBaseUi[")&&l.Path.Contains("/"+name+"["))?.Text??"?";
    private static void DrawStatus(string facingText)
    {
        if(!lines.Any(l=>l.Path.Contains("/CommonBaseUi[")))return;
        Fill(new Rect(0,0,Screen.width,Screen.height*.15f));
        label!.fontSize=Math.Clamp(Screen.height/36,18,32);label.alignment=TextAnchor.MiddleLeft;
        label.normal.textColor=new Color(.9f,.93f,.95f);GUI.color=Color.white;
        GUI.Label(new Rect(24,10,Screen.width-48,44),Value("Floor_Name")+"  "+Value("Floor_Number")+"   "+Value("Lv_Number")+"   "+facingText,label);
        GUI.Label(new Rect(24,56,Screen.width-48,44),"HP "+Value("Hp_Molecule")+"/"+Value("Hp_Denominator")+"   満腹度 "+Value("Hunger_Molecule")+"/"+Value("Hunger_Denominator")+"   "+Value("Gitan_Number")+"   "+Value("GetItem_Number"),label);
    }
    private static void Fill(Rect rect){GUI.color=Color.black;GUI.Label(rect,"",black);GUI.color=Color.white;}
    private static void ReadSettings()
    {
        try
        {
            string path=System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,"diagnostics","ShirenAsciiProbe","ui-settings.json");
            if(!File.Exists(path)||new FileInfo(path).Length>65536)return;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            if(doc.RootElement.TryGetProperty("cursorPattern",out var pattern))
            {
                string value=pattern.GetString()??cursorPattern;
                if(value!=cursorPattern){cursorRegex=new Regex(value,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(30));cursorPattern=value;}
            }
            if(doc.RootElement.TryGetProperty("textScale",out var scale))textScale=Math.Clamp(scale.GetSingle(),.5f,1.5f);
            if(doc.RootElement.TryGetProperty("fastUiBounds",out var fast))fastUiBounds=fast.GetBoolean();
            if(doc.RootElement.TryGetProperty("icons",out var icons))foreach(var pair in icons.EnumerateObject())
            {
                if(ushort.TryParse(pair.Name,System.Globalization.NumberStyles.HexNumber,null,out ushort code))iconOverrides[(char)code]=pair.Value.GetString()??"";
            }
            var sealLabels=new Dictionary<string,string>();
            if(doc.RootElement.TryGetProperty("sealLabels",out var seals))foreach(var pair in seals.EnumerateObject())
                sealLabels[pair.Name]=pair.Value.GetString()??"";
            SealSymbols.ReplaceLabels(sealLabels);
        }
        catch(Exception ex){Plugin.Report("UI settings ignored: "+ex.Message);}
    }
    internal static void Capture()
    {
        try
        {
            hierarchyCache.Clear();pathCache.Clear();
            string directory=System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,"diagnostics","ShirenAsciiProbe");
            var diagnosticImages=new List<UiImage>();
            int rejectedVisible=0;
            // F8 provides a one-shot comparison with the original full scan.
            // If a native bound ever excludes a real line, fail back safely.
            foreach(var source in tmp)
            {
                if(!VisibleText(source)||TextLayout(source,out _,out _))continue;
                var original=new List<UiLine>();
                Add(original,source,ReadText(source),source.fontSize,Alignment((int)source.alignment),selections);
                rejectedVisible+=original.Count;
            }
            if(rejectedVisible>0){fastUiBounds=false;Plugin.Report("Fast UI bounds disabled: "+rejectedVisible+" visible lines differed from original scan.");}
            Performance.Count("boundsValidationMisses",rejectedVisible);
            foreach(var source in images)
            {
                if(!Visible(source)||source.canvas==null||!Bounds(source.rectTransform,source.canvas,out var box))continue;
                diagnosticImages.Add(new(source.name,source.sprite==null?"":source.sprite.name,Path(source.transform),box.X,box.Y,box.W,box.H));
            }
            textDiagnostics.Clear();
            foreach(var source in tmp)
            {
                if(source==null||!source.gameObject.activeInHierarchy)continue;
                string path=Path(source.transform);
                // Include help and operation hints too: their sprite characters
                // previously fell outside the item/message-only diagnostics.
                if(!VisibleText(source)&&!path.Contains("/ItemSelect[")&&!path.Contains("Message")&&!path.Contains("Log"))continue;
                var custom=source.TryCast<CustomTMPText>();
                var glyphs=new List<object>();var info=source.textInfo;
                if(info!=null)for(int i=0;i<Math.Min(info.characterCount,64);i++)
                {
                    var glyph=info.characterInfo[i];
                    if(char.GetUnicodeCategory(glyph.character)==System.Globalization.UnicodeCategory.PrivateUse)
                        glyphs.Add(new{code=((int)glyph.character).ToString("X4"),name=SpriteName(glyph),visible=glyph.isVisible,alpha=glyph.color.a});
                }
                textDiagnostics.Add(new{path,parsed=source.GetParsedText(),tag=custom==null?null:custom.TagText,rectWidth=source.rectTransform.rect.width,rectHeight=source.rectTransform.rect.height,active=source.isActiveAndEnabled,alpha=source.color.a,rendererAlpha=source.canvasRenderer.GetAlpha(),culled=source.canvasRenderer.cull,hierarchyVisible=VisibleHierarchy(source.transform),maxVisible=source.maxVisibleCharacters,glyphs});
            }
            File.WriteAllText(System.IO.Path.Combine(directory,"ui-latest.json"),JsonSerializer.Serialize(new{capturedUtc=DateTime.UtcNow,width=Screen.width,height=Screen.height,fastUiBounds,rejectedVisible,lines,panels,selections,images=diagnosticImages,textDiagnostics},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){Plugin.Report("UI capture failed: "+ex.Message);}
    }
}
