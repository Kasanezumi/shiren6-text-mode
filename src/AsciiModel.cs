using System;
using System.Collections.Generic;

namespace ShirenAsciiProbe;

public record MapTile(int Kind, uint Flags, bool CurrentlyVisible = false, int RoomId = 0);
public record MapEntity(int X, int Y, string Type, uint Id, uint ItemFlags, bool IsItem, string? DisplayName = null);
public record MapState(int Width, int Height, MapTile[] Tiles, List<MapEntity> Entities, int PlayerX, int PlayerY, int FacingX = 0, int FacingY = 0);
public record Glyph(char Character, byte Palette);
public record MapFrame(int Columns, int Rows, Glyph[] Cells, int PlayerX, int PlayerY, int FacingX = 0, int FacingY = 0);

public static class AsciiModel
{
    public const int ViewColumns=41,ViewRows=23;
    // One extra tile is needed by BordersVisibleRoom at viewport edges.
    public static (int Left,int Top,int Right,int Bottom) ReadWindow(int width,int height,int px,int py)=>
        (Math.Max(0,px-ViewColumns/2-1),Math.Max(0,py-ViewRows/2-1),Math.Min(width-1,px+ViewColumns/2+1),Math.Min(height-1,py+ViewRows/2+1));
    // Both bits persist after visiting a tile. Neither is current line of sight.
    // Current visibility is supplied separately by Player.qxa, a read-only query.
    public const uint Revealed = 1u << 4;
    public const uint Explored = 1u << 6;
    // Tile flag 15: present on the recorded sparkling wall containing a monster;
    // cjk.cit.zpu and the native wall-effect renderer both read this flag.
    public const uint ConcealedWallContents = 1u << 15;
    public static bool IsConcealingWall(MapTile tile) =>
        (tile.Kind is 2 or 8) && (tile.Flags & ConcealedWallContents) != 0;
    public static MapFrame Build(MapState state, int columns = ViewColumns, int rows = ViewRows)
    {
        if (state.Width < 1 || state.Height < 1 || state.Tiles.Length != state.Width * state.Height)
            throw new ArgumentException("Invalid map dimensions");
        if (columns < 3 || columns > 101 || rows < 3 || rows > 101) throw new ArgumentException("Invalid viewport");
        return BuildRegion(state,state.PlayerX-columns/2,state.PlayerY-rows/2,columns,rows);
    }
    // Crop to discovered terrain, never to undiscovered entities. The player
    // can be anywhere in this rectangle, unlike the normal centered viewport.
    public static MapFrame BuildOverview(MapState state)
    {
        if(state.Width<1||state.Height<1||state.Width>128||state.Height>128||state.Tiles.Length!=state.Width*state.Height)
            throw new ArgumentException("Invalid map dimensions");
        int left=Math.Clamp(state.PlayerX,0,state.Width-1),right=left;
        int top=Math.Clamp(state.PlayerY,0,state.Height-1),bottom=top;
        for(int y=0;y<state.Height;y++)for(int x=0;x<state.Width;x++)
        {
            if((state.Tiles[y*state.Width+x].Flags&(Revealed|Explored))==0)continue;
            left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
        }
        return BuildRegion(state,left,top,right-left+1,bottom-top+1);
    }
    private static MapFrame BuildRegion(MapState state,int left,int top,int columns,int rows)
    {
        var cells = new Glyph[columns * rows];
        int room=state.PlayerX>=0&&state.PlayerY>=0&&state.PlayerX<state.Width&&state.PlayerY<state.Height
            ?state.Tiles[state.PlayerY*state.Width+state.PlayerX].RoomId:0;

        for (int sy=0; sy<rows; sy++) for(int sx=0; sx<columns; sx++)
        {
            int x=left+sx, y=top+sy, index=sy*columns+sx;
            cells[index] = new Glyph(' ', 0);
            if(x<0 || x>=state.Width || y<0 || y>=state.Height) continue;
            var tile=state.Tiles[y*state.Width+x];
            if ((tile.Flags & (Revealed|Explored)) == 0) continue;
            char c=tile.Kind switch {4=>'.',2 or 8=>'#',16=>'~',32=>'_',64=>'+',128=>'=',256=>':',_=>'?'};
            bool bright=tile.CurrentlyVisible||((tile.Kind is 2 or 8)&&BordersVisibleRoom(state,x,y,room));
            // Water retains its hue in explored areas outside current sight.
            byte palette=tile.Kind==16?(byte)(bright?7:8):(byte)(bright?1:2);
            cells[index]=new Glyph(c,palette);
        }
        // Items first; living entities remain visible when sharing a tile.
        foreach(bool itemPass in new[]{true,false}) foreach(var entity in state.Entities)
        {
            if(entity.IsItem!=itemPass) continue;
            int x=entity.X,y=entity.Y,sx=x-left,sy=y-top;
            if(x<0||y<0||x>=state.Width||y>=state.Height||sx<0||sy<0||sx>=columns||sy>=rows)continue;
            var tile=state.Tiles[y*state.Width+x];
            if(!CanShowEntity(tile,entity.IsItem,entity.ItemFlags))continue;
            if(entity.IsItem)
            {
                cells[sy*columns+sx]=new Glyph(ItemGlyph(entity.Type),4);
            }
            else
            {
                bool player=entity.Type.EndsWith(".Player"),monster=entity.Type.Contains("Monster");
                char c=player?'@':monster?EnemyGlyph(entity.DisplayName):'&';
                cells[sy*columns+sx]=new Glyph(c,(byte)(player?3:monster?6:5));
            }
        }
        int playerColumn=state.PlayerX-left,playerRow=state.PlayerY-top;
        if(playerColumn>=0&&playerRow>=0&&playerColumn<columns&&playerRow<rows)
            cells[playerRow*columns+playerColumn]=new Glyph('@',3);
        return new MapFrame(columns,rows,cells,state.PlayerX,state.PlayerY,state.FacingX,state.FacingY);
    }
    public static bool CanShowEntity(MapTile tile,bool isItem,uint itemFlags)
    {
        if(!tile.CurrentlyVisible||(tile.Flags&(Revealed|Explored))==0)return false;
        // Normal walls can contain moving, wall-passing creatures. Only a wall
        // with concealed contents suppresses those creatures. Buried items
        // remain hidden in any solid wall until the terrain opens.
        if((tile.Kind is 2 or 8)&&(isItem||IsConcealingWall(tile)))return false;
        // Hidden bit 3 and discovered bit 20 still protect unrevealed traps.
        return !isItem||((itemFlags&8)==0&&(itemFlags&(1u<<20))!=0);
    }
    public static (string Arrow,string Name) Facing(int dx,int dy)=>(dx,dy) switch
    {
        (0,-1)=>("↑","上"),(1,-1)=>("↗","右上"),(1,0)=>("→","右"),(1,1)=>("↘","右下"),
        (0,1)=>("↓","下"),(-1,1)=>("↙","左下"),(-1,0)=>("←","左"),(-1,-1)=>("↖","左上"),
        _=>("","不明")
    };
    // Room perimeter walls usually have room ID 0. Illuminate their terrain
    // glyphs (including corners) from an adjacent visible tile of the current
    // room. Never alter CurrentlyVisible: entities must retain native sight.
    private static bool BordersVisibleRoom(MapState state,int x,int y,int room)
    {
        if(room<=0)return false;
        for(int ny=Math.Max(0,y-1);ny<=Math.Min(state.Height-1,y+1);ny++)
        for(int nx=Math.Max(0,x-1);nx<=Math.Min(state.Width-1,x+1);nx++)
        {
            var neighbor=state.Tiles[ny*state.Width+nx];
            if(neighbor.RoomId==room&&neighbor.CurrentlyVisible&&(neighbor.Kind is not (2 or 8))
                &&(neighbor.Flags&(Revealed|Explored))!=0)return true;
        }
        return false;
    }
    public static char ItemGlyph(string name) => name.Split('.')[^1] switch
    {
        "ItemWeapon"=>'剣',"ItemShield"=>'盾',"ItemGrass"=>'草',"ItemScroll"=>'巻',
        "ItemPot"=>'壺',"ItemAroma"=>'香',"ItemWand"=>'杖',"ItemArrow"=>'矢',"ItemStone"=>'石',
        "ItemGitan"=>'金',"ItemBracelet"=>'腕',"ItemFood"=>'飯',"ItemMomoman"=>'桃',
        "ItemStairs" or "ItemStairsShrine"=>'階',"ItemTrap"=>'罠',
        "ItemGimmick" or "ItemGimmickShrine"=>'仕',"ItemTile" or "ItemTileStrongMonsterZone"=>'地',_=>'物'
    };
    public static char EnemyGlyph(string? name)
    {
        if(string.IsNullOrWhiteSpace(name))return '敵';
        // Ignore formatting tags, but preserve the first actual name character.
        string plain=System.Text.RegularExpressions.Regex.Replace(name,"<[^>]*>","").Trim();
        foreach(char c in plain)
        {
            if(char.IsWhiteSpace(c)||char.IsControl(c)||char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.PrivateUse)continue;
            return char.IsSurrogate(c)?'敵':c;
        }
        return '敵';
    }
}
