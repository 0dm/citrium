using System.Runtime.InteropServices;

namespace Citrium.Services;

internal readonly record struct JumpListItem(string Title, string Link);

/// <summary>
/// Favorites on Citrium's taskbar menu. Windows' tidy API for this needs an installed package, so this uses the
/// shell's older one, called through vtables as <see cref="Viewer.CitrixSessionApi"/> does and for the same reason:
/// Citrium's trimmed, CsWinRT-hosted runtime has no classic COM interop.
/// </summary>
internal static unsafe class JumpList
{
    private static readonly Guid DestinationListClass = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid CollectionClass = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ICustomDestinationList = new("6332DEBF-87B5-4670-90C0-5E57B408A49E");
    private static readonly Guid IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid IObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");
    private static readonly Guid IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid TitleFormat = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const ushort VT_LPWSTR = 31;
    private const uint TitlePropertyId = 2;

    // A slot is 3 (IUnknown) plus the method's position, counting the interfaces it derives from.
    private const int SlotRelease = 2;
    private const int ListBegin = 4, ListAppendCategory = 5, ListCommit = 8;
    private const int ArrayGetCount = 3, ArrayGetAt = 4, CollectionAddObject = 5;
    private const int LinkSetDescription = 7, LinkGetArguments = 10, LinkSetArguments = 11, LinkSetIconLocation = 17, LinkSetPath = 20;
    private const int StoreSetValue = 6, StoreCommit = 7;

    private static readonly object Gate = new();
    private static IReadOnlyList<JumpListItem>? _pending;

    /// <summary>Replaces the list on a thread of its own: the shell takes a moment, and this is called from the UI.</summary>
    public static void Update(IReadOnlyList<JumpListItem> favorites)
    {
        if (AppPaths.IsDemo) return; // its links would open the real Citrium

        lock (Gate)
        {
            var running = _pending is not null;
            _pending = favorites;
            if (running) return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                IReadOnlyList<JumpListItem>? items;
                lock (Gate) items = _pending;
                try { Commit(items!); }
                catch (Exception ex) { Log.Error("Couldn't update the jump list", ex); }
                lock (Gate)
                {
                    if (ReferenceEquals(items, _pending)) { _pending = null; return; }
                }
            }
        }) { IsBackground = true, Name = "Citrium jump list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Commit(IReadOnlyList<JumpListItem> favorites)
    {
        IntPtr list = IntPtr.Zero, removed = IntPtr.Zero, collection = IntPtr.Zero, array = IntPtr.Zero;
        try
        {
            list = Create(DestinationListClass, ICustomDestinationList);
            uint slots = 0;
            var iid = IObjectArray;
            Check(((delegate* unmanaged<IntPtr, uint*, Guid*, IntPtr*, int>)Slot(list, ListBegin))(list, &slots, &iid, &removed), "BeginList");

            // Windows refuses a whole category if it holds something the user took off the list by hand.
            var unwanted = RemovedArguments(removed);
            collection = Create(CollectionClass, IObjectCollection);
            var added = 0;
            foreach (var item in favorites.Where(f => !unwanted.Contains(Argument(f.Link))).Take((int)Math.Max(slots, 1)))
            {
                var link = CreateLink(item);
                try
                {
                    Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(collection, CollectionAddObject))(collection, link), "AddObject");
                    added++;
                }
                finally { Release(link); }
            }

            if (added > 0)
            {
                Check(Marshal.QueryInterface(collection, in IObjectArray, out array), "IObjectArray");
                fixed (char* name = "Favorites")
                    Check(((delegate* unmanaged<IntPtr, char*, IntPtr, int>)Slot(list, ListAppendCategory))(list, name, array), "AppendCategory");
            }
            Check(((delegate* unmanaged<IntPtr, int>)Slot(list, ListCommit))(list), "CommitList");
        }
        finally
        {
            Release(array);
            Release(collection);
            Release(removed);
            Release(list);
        }
    }

    private static string Argument(string link) => $"\"{link}\"";

    private static HashSet<string> RemovedArguments(IntPtr removed)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (removed == IntPtr.Zero) return result;
        uint count = 0;
        if (((delegate* unmanaged<IntPtr, uint*, int>)Slot(removed, ArrayGetCount))(removed, &count) < 0) return result;

        var iid = IShellLinkW;
        var buffer = stackalloc char[2048];
        for (uint i = 0; i < count; i++)
        {
            IntPtr item = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, Guid*, IntPtr*, int>)Slot(removed, ArrayGetAt))(removed, i, &iid, &item) < 0) continue;
            if (((delegate* unmanaged<IntPtr, char*, int, int>)Slot(item, LinkGetArguments))(item, buffer, 2048) >= 0)
                result.Add(new string(buffer));
            Release(item);
        }
        return result;
    }

    private static IntPtr CreateLink(JumpListItem item)
    {
        var link = Create(ShellLinkClass, IShellLinkW);
        var store = IntPtr.Zero;
        var title = Marshal.StringToCoTaskMemUni(item.Title);
        try
        {
            var exe = Environment.ProcessPath!;
            SetText(link, LinkSetPath, exe);
            SetText(link, LinkSetArguments, Argument(item.Link));
            SetText(link, LinkSetDescription, item.Title);
            fixed (char* path = exe)
                Check(((delegate* unmanaged<IntPtr, char*, int, int>)Slot(link, LinkSetIconLocation))(link, path, 0), "SetIconLocation");

            // Without a title the shell shows the program's name on every row.
            Check(Marshal.QueryInterface(link, in IPropertyStore, out store), "IPropertyStore");
            var key = new PropertyKey { Format = TitleFormat, Id = TitlePropertyId };
            var value = new PropVariant { Type = VT_LPWSTR, Text = title };
            Check(((delegate* unmanaged<IntPtr, PropertyKey*, PropVariant*, int>)Slot(store, StoreSetValue))(store, &key, &value), "SetValue");
            Check(((delegate* unmanaged<IntPtr, int>)Slot(store, StoreCommit))(store), "Commit");
            return link;
        }
        catch
        {
            Release(link);
            throw;
        }
        finally
        {
            Release(store);
            Marshal.FreeCoTaskMem(title);
        }
    }

    private static void SetText(IntPtr link, int slot, string text)
    {
        fixed (char* p = text)
            Check(((delegate* unmanaged<IntPtr, char*, int>)Slot(link, slot))(link, p), "IShellLink");
    }

    private static IntPtr Create(Guid classId, Guid interfaceId)
    {
        IntPtr instance = IntPtr.Zero;
        Check(CoCreateInstance(&classId, IntPtr.Zero, CLSCTX_INPROC_SERVER, &interfaceId, &instance), "CoCreateInstance");
        return instance;
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new InvalidOperationException($"{step} failed: 0x{hr:X8}");
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(instance, SlotRelease))(instance);
    }

    private static IntPtr Slot(IntPtr comObject, int slot) => (*(IntPtr**)comObject)[slot];

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }

    // PROPVARIANT: only the string form is used, but the shell reads the union's full size.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Text;
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);
}
