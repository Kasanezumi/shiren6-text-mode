using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ShirenAsciiProbe;

[BepInPlugin(Id, "Shiren ASCII Preview", "0.5.16")]
public sealed class Plugin : BasePlugin
{
    public const string Id = "local.shiren6.ascii.probe";
    private static ManualLogSource log = null!;
    private static string folder = "";
    private static Harmony? harmony;
    private static IntPtr singleton;
    private static bool pressed, disabled;
    private static long lastCapture;
    private static int captures;
    internal static long CameraTick;
    internal static IntPtr GetDungeonPointer()
    {
        if(disabled || singleton==IntPtr.Zero)return IntPtr.Zero;
        Native.il2cpp_field_static_get_value(singleton,out IntPtr dungeon);
        return dungeon;
    }
    internal static void Report(string message) => log.LogWarning(message);
    private const string GameHash = "8E50947F6E0B47FAA652E75A8DC65B10B209DC14FFEE0D8FB21B220C0CAB36A2";
    private const string MetadataHash = "7EE9883EFF203C1CCC030629D417356E5732C921AA5D8A4F10AB2DA80C29AFEF";

    public override void Load()
    {
        log = Log;
        folder = Path.Combine(Paths.BepInExRootPath, "diagnostics", "ShirenAsciiProbe");
        Directory.CreateDirectory(folder);
        try
        {
            if (IntPtr.Size != 8) throw new NotSupportedException("Expected Windows x64.");
            Verify(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"), GameHash);
            Verify(Path.Combine(Paths.GameRootPath, "ShirenTheWanderer6_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), MetadataHash);
            var domain = Native.il2cpp_domain_get();
            var assembly = Native.il2cpp_domain_assembly_open(domain, "Assembly-CSharp");
            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Game assembly unavailable.");
            var image = Native.il2cpp_assembly_get_image(assembly);
            var dungeon = Native.il2cpp_class_from_name(image, "F7.Scripts.Dungeon", "Dungeon");
            if (dungeon == IntPtr.Zero) throw new InvalidOperationException("Dungeon type unavailable.");
            singleton = Native.il2cpp_class_get_field_from_name(dungeon, "ecji");
            if (singleton == IntPtr.Zero || (Native.il2cpp_field_get_flags(singleton) & 0x10) == 0)
                throw new InvalidOperationException("Dungeon singleton not recognized.");
            var wrapper = Assembly.Load("Assembly-CSharp").GetType("F7.Scripts.Cameras.DungeonMapCamera", true)!;
            var update = AccessTools.DeclaredMethod(wrapper, "Update", Type.EmptyTypes);
            if (update == null) throw new MissingMethodException(wrapper.FullName, "Update");
            harmony = new Harmony(Id);
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnCameraUpdate)));
            AddComponent<AsciiView>();
            Status("READY: ASCII view defaults ON in dungeons; F9 toggles original / ASCII view; F8 captures diagnostic data.");
        }
        catch (Exception ex)
        {
            harmony?.UnpatchSelf();
            disabled = true;
            Status("DISABLED: " + ex);
        }
    }

    private static void Verify(string path, string expected)
    {
        using var file = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        if (Convert.ToHexString(algorithm.ComputeHash(file)) != expected)
            throw new InvalidOperationException("File changed since inspection: " + Path.GetFileName(path));
    }

    // Called after the game's own camera update, on the Unity main thread.
    public static void OnCameraUpdate()
    {
        if (disabled) return;
        CameraTick=Environment.TickCount64;
        try
        {
            bool down = (Native.GetAsyncKeyState(0x77) & 0x8000) != 0;
            bool trigger = down && !pressed;
            pressed = down;
            if (!trigger || Environment.TickCount64 - lastCapture < 1500) return;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
            if (foreground != Environment.ProcessId) return;
            lastCapture = Environment.TickCount64;
            UiMirror.Update();
            UiMirror.Capture();
            if (captures >= 30) { Status("Session capture limit (30) reached. Restart to capture more."); return; }
            Native.il2cpp_field_static_get_value(singleton, out IntPtr dungeon);
            if (dungeon == IntPtr.Zero) { Status("NO_DUNGEON: enter a dungeon and try F8 again."); return; }
            if(LiveMap.Read(dungeon,capture:true)==null){Status("FLOOR_LOADING: wait for the player to appear, then try F8 again.");return;}
            var timer = Stopwatch.StartNew();
            var snapshot = Reader.Capture(dungeon);
            snapshot["capturedUtc"] = DateTime.UtcNow.ToString("O");
            snapshot["gameAssemblySha256"] = GameHash;
            snapshot["metadataSha256"] = MetadataHash;
            snapshot["notice"] = "Raw diagnostic data, not a validated visibility map. Do not treat all entities as visible.";
            snapshot["readMilliseconds"] = timer.ElapsedMilliseconds;
            string name = "snapshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            captures++;
            Status("SAVED: " + name + " (" + timer.ElapsedMilliseconds + " ms)");
        }
        catch (Exception ex)
        {
            // Never repeat an unsupported native layout read every frame.
            disabled = true;
            Status("CAPTURE_DISABLED: " + ex);
        }
    }

    private static void Status(string text)
    {
        log.LogInfo(text);
        try { File.WriteAllText(Path.Combine(folder, "status.txt"), DateTime.Now.ToString("O") + Environment.NewLine + text); }
        catch (Exception ex) { log.LogWarning("Cannot write status file: " + ex.Message); }
    }

    public override bool Unload()
    {
        disabled = true;
        harmony?.UnpatchSelf();
        return true;
    }
}

internal static class Reader
{
    // Supported IL2CPP x64 array header: object (16), bounds (8), length (8).
    // The plugin verifies both native binary and metadata hashes before using it.
    private const int ArrayData = 32;
    private static readonly Dictionary<IntPtr, List<IntPtr>> fieldCache = new();

    internal static Dictionary<string, object?> Capture(IntPtr dungeon)
    {
        var result = new Dictionary<string, object?> { ["schema"] = 1, ["dungeon"] = Scalars(dungeon) };
        var terrain = Ref(dungeon, "egbe");
        result["terrain"] = Scalars(terrain);
        result["terrainAuxiliary"] = BitArrays(terrain, "ebsv", "ebsx");
        var manager = Ref(dungeon, "egbc");
        result["creatureManager"] = Scalars(manager);
        result["creatures"] = ReadList(Ref(manager, "egfm"), Entity);
        result["secondaryCreatures"] = ReadList(Ref(manager, "egfn"), Entity);
        var items = Ref(dungeon, "egbg");
        result["floorItems"] = ReadList(Ref(items, "efwl"), Entity);
        var grid = Ref(terrain, "egya");
        var rows = new List<object?>();
        foreach (var row in References(grid, 128))
        {
            var cells = new List<object?>();
            foreach (var cell in References(row, 128))
            {
                var data = Scalars(cell);
                if (cell != IntPtr.Zero)
                {
                    data["egto_bytes"] = PrimitiveArray(Ref(cell, "egto"), 512);
                    data["egtm_bits"] = Bits(Ref(cell, "egtm"));
                    var item = Ref(cell, "egtl");
                    if (item != IntPtr.Zero) data["tileItem"] = Entity(item);
                }
                cells.Add(data);
            }
            rows.Add(cells);
        }
        result["cellsOuterThenInner"] = rows;
        return result;
    }

    private static object Entity(IntPtr obj)
    {
        var values = Scalars(obj);
        // Capture direct per-entity bit arrays only, not inventory or save objects.
        if (obj != IntPtr.Zero)
            foreach (var field in Fields(Native.il2cpp_object_get_class(obj)))
            {
                var type = Native.il2cpp_class_from_type(Native.il2cpp_field_get_type(field));
                if (ClassName(type) == "System.Collections.BitArray")
                    values[Name(field) + "_bits"] = Bits(Ref(obj, Name(field)));
            }
        return values;
    }

    private static object BitArrays(IntPtr obj, params string[] names)
    {
        var data = new Dictionary<string, object?>();
        if (obj == IntPtr.Zero) return data;
        foreach (string name in names)
        {
            var field = Find(obj, name, false);
            if (field == IntPtr.Zero) continue;
            var val = Ref(obj, name);
            if (val == IntPtr.Zero) { data[name] = null; continue; }
            var klass = Native.il2cpp_object_get_class(val);
            data[name] = Native.il2cpp_class_get_rank(klass) == 1
                ? References(val, 256).Select(Bits).ToArray() : Bits(val);
        }
        return data;
    }

    private static object? Bits(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return null;
        if (ClassName(Native.il2cpp_object_get_class(obj)) != "System.Collections.BitArray")
            throw new InvalidOperationException("Expected BitArray.");
        return new { length = IntField(obj, "m_length"), words = PrimitiveArray(Ref(obj, "m_array"), 4096) };
    }

    private static object? PrimitiveArray(IntPtr array, int limit)
    {
        if (array == IntPtr.Zero) return null;
        var klass = Native.il2cpp_object_get_class(array);
        if (Native.il2cpp_class_get_rank(klass) != 1) throw new InvalidOperationException("Expected rank-one array.");
        var element = Native.il2cpp_class_get_element_class(klass);
        var type = Native.il2cpp_type_get_type(Native.il2cpp_class_get_type(element));
        if (type != 0x05 && type != 0x08 && type != 0x09) throw new InvalidOperationException("Expected byte/int array.");
        int count = Count(array, limit);
        int size = Native.il2cpp_array_element_size(klass);
        if (size != (type == 0x05 ? 1 : 4)) throw new InvalidOperationException("Unexpected array stride.");
        var values = new long[count];
        for (int i = 0; i < count; i++)
            values[i] = size == 1 ? Marshal.ReadByte(array, ArrayData + i) : (uint)Marshal.ReadInt32(array, ArrayData + i * 4);
        return values;
    }

    private static List<object> ReadList(IntPtr list, Func<IntPtr, object> read)
    {
        var values = new List<object>();
        if (list == IntPtr.Zero) return values;
        int size = IntField(list, "_size");
        if (size < 0 || size > 1024) throw new InvalidOperationException("List exceeds diagnostic limit.");
        var entries = References(Ref(list, "_items"), 8192);
        if (size > entries.Count) throw new InvalidOperationException("Invalid list size.");
        for (int i = 0; i < size; i++) values.Add(read(entries[i]));
        return values;
    }

    private static List<IntPtr> References(IntPtr array, int limit)
    {
        var result = new List<IntPtr>();
        if (array == IntPtr.Zero) return result;
        var klass = Native.il2cpp_object_get_class(array);
        if (Native.il2cpp_class_get_rank(klass) != 1 || Native.il2cpp_array_element_size(klass) != IntPtr.Size
            || Native.il2cpp_class_is_valuetype(Native.il2cpp_class_get_element_class(klass)))
            throw new InvalidOperationException("Expected rank-one reference array.");
        int length = Count(array, limit);
        for (int i = 0; i < length; i++) result.Add(Marshal.ReadIntPtr(array, ArrayData + i * IntPtr.Size));
        return result;
    }

    private static int Count(IntPtr array, int limit)
    {
        ulong count = Native.il2cpp_array_length(array).ToUInt64();
        if (count > (ulong)limit) throw new InvalidOperationException("Array exceeds diagnostic limit: " + count);
        return (int)count;
    }

    private static Dictionary<string, object?> Scalars(IntPtr obj)
    {
        var result = new Dictionary<string, object?>();
        if (obj == IntPtr.Zero) return result;
        var klass = Native.il2cpp_object_get_class(obj);
        result["type"] = ClassName(klass);
        int objectSize = (int)Native.il2cpp_class_instance_size(klass);
        foreach (var field in Fields(klass))
        {
            var type = Native.il2cpp_field_get_type(field);
            var fieldClass = Native.il2cpp_class_from_type(type);
            if (fieldClass == IntPtr.Zero || !Native.il2cpp_class_is_valuetype(fieldClass)) continue;
            int size = Native.il2cpp_class_value_size(fieldClass, out _);
            int offset = (int)Native.il2cpp_field_get_offset(field);
            if (size < 1 || size > 64 || offset < 16 || offset + size > objectSize) continue;
            var data = new byte[size];
            Marshal.Copy(IntPtr.Add(obj, offset), data, 0, size);
            int kind = Native.il2cpp_type_get_type(type);
            string fieldType = ClassName(fieldClass);
            object value = kind switch
            {
                0x02 => data[0] != 0,
                0x04 => (sbyte)data[0],
                0x05 => data[0],
                0x06 => BitConverter.ToInt16(data),
                0x07 => BitConverter.ToUInt16(data),
                0x08 => BitConverter.ToInt32(data),
                0x09 => BitConverter.ToUInt32(data),
                0x0a => BitConverter.ToInt64(data),
                0x0b => BitConverter.ToUInt64(data),
                0x0c => BitConverter.ToSingle(data).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                0x0d => BitConverter.ToDouble(data).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                _ => new { type = fieldType, hex = Convert.ToHexString(data) }
            };
            if ((fieldType.EndsWith(".Coord") || fieldType.EndsWith(".Size")) && size == 8)
                value = new { type = fieldType, first = BitConverter.ToInt32(data, 0), second = BitConverter.ToInt32(data, 4) };
            result[Name(field)] = value;
        }
        return result;
    }

    private static List<IntPtr> Fields(IntPtr klass)
    {
        if (fieldCache.TryGetValue(klass, out var cached)) return cached;
        var fields = new List<IntPtr>();
        for (var current = klass; current != IntPtr.Zero; current = Native.il2cpp_class_get_parent(current))
        {
            IntPtr iter = IntPtr.Zero, field;
            while ((field = Native.il2cpp_class_get_fields(current, ref iter)) != IntPtr.Zero)
                if ((Native.il2cpp_field_get_flags(field) & (0x10 | 0x40)) == 0) fields.Add(field);
        }
        fieldCache[klass] = fields;
        return fields;
    }

    private static IntPtr Find(IntPtr obj, string name, bool required = true)
    {
        var field = Fields(Native.il2cpp_object_get_class(obj)).FirstOrDefault(f => Name(f) == name);
        if (field == IntPtr.Zero && required) throw new MissingFieldException(name);
        return field;
    }

    private static IntPtr Ref(IntPtr obj, string name)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        var field = Find(obj, name);
        var klass = Native.il2cpp_class_from_type(Native.il2cpp_field_get_type(field));
        if (klass == IntPtr.Zero || Native.il2cpp_class_is_valuetype(klass)) throw new InvalidOperationException("Not a reference: " + name);
        Native.il2cpp_field_get_value(obj, field, out IntPtr value);
        return value;
    }

    private static int IntField(IntPtr obj, string name)
    {
        var field = Find(obj, name);
        if (Native.il2cpp_type_get_type(Native.il2cpp_field_get_type(field)) != 0x08)
            throw new InvalidOperationException("Not an int: " + name);
        Native.il2cpp_field_get_value_int(obj, field, out int value);
        return value;
    }

    private static string Name(IntPtr field) => Marshal.PtrToStringUTF8(Native.il2cpp_field_get_name(field)) ?? "";
    private static string ClassName(IntPtr klass)
    {
        if (klass == IntPtr.Zero) return "";
        string ns = Marshal.PtrToStringUTF8(Native.il2cpp_class_get_namespace(klass)) ?? "";
        string name = Marshal.PtrToStringUTF8(Native.il2cpp_class_get_name(klass)) ?? "";
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

internal static class Native
{
    private const string Dll = "GameAssembly.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_domain_get();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_domain_assembly_open(IntPtr domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_assembly_get_image(IntPtr assembly);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string ns, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_field_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void il2cpp_field_static_get_value(IntPtr field, out IntPtr value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_object_get_class(IntPtr obj);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_parent(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int il2cpp_field_get_flags(IntPtr field);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_field_get_type(IntPtr field);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_from_type(IntPtr type);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool il2cpp_class_is_valuetype(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int il2cpp_class_value_size(IntPtr klass, out uint align);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint il2cpp_class_instance_size(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern UIntPtr il2cpp_field_get_offset(IntPtr field);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int il2cpp_type_get_type(IntPtr type);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_field_get_name(IntPtr field);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_name(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_namespace(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void il2cpp_field_get_value(IntPtr obj, IntPtr field, out IntPtr value);
    [DllImport(Dll, EntryPoint = "il2cpp_field_get_value", CallingConvention = CallingConvention.Cdecl)] internal static extern void il2cpp_field_get_value_int(IntPtr obj, IntPtr field, out int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint il2cpp_class_get_rank(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int il2cpp_array_element_size(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_type(IntPtr klass);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern UIntPtr il2cpp_array_length(IntPtr array);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
