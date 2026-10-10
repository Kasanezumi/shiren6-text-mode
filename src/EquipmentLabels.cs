using System;
using System.Collections.Generic;
namespace ShirenAsciiProbe;
internal static class EquipmentLabels
{
    // The native green check observed on the equipped sword and shield.
    // Do not infer equipment from item names, selection or throwable A/B keys.
    internal static bool IsMarker(string name,string sprite)=>name=="EquipAndResonance"&&sprite=="Btn_EquipItemEquip";
    internal static string? Row(string path)
    {
        int leaf=path.LastIndexOf('/');
        if(leaf<0||!path.Contains("/ItemSelect[",StringComparison.Ordinal)||!path.Contains("/Ui_ItemList[",StringComparison.Ordinal))return null;
        string row=path.Substring(0,leaf);
        int parent=row.LastIndexOf('/');
        return parent>=0&&row.Substring(parent+1).StartsWith("Ui_ItemName(Clone)[",StringComparison.Ordinal)?row:null;
    }
    internal static string Apply(string path,string text,ISet<string> equippedRows)
    {
        int leaf=path.LastIndexOf('/');
        if(leaf<0||!path.Substring(leaf+1).StartsWith("ItemName[",StringComparison.Ordinal))return text;
        string? row=Row(path);
        return row!=null&&equippedRows.Contains(row)?"[装備] "+text:text;
    }
}
