// PdfText: extract PDF text through the Windows PDF IFilter (what Windows Search uses).
// No packages, no SDK: load with Add-Type -Path PdfText.cs from PowerShell 5.1 or 7.
// Text comes out as chunks; a chunk break becomes a newline, which is close to line order for reports.
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Voyager
{
    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFilter
    {
        [PreserveSig] int Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pFlags);
        [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
        [PreserveSig] int GetText(ref uint pcwcBuffer, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer);
        [PreserveSig] int GetValue(ref IntPtr ppPropValue);
        [PreserveSig] int BindRegion(FILTERREGION origPos, ref Guid riid, out IntPtr ppunk);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILTERREGION { public uint idChunk; public uint cwcStart; public uint cwcExtent; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FULLPROPSPEC { public Guid guidPropSet; public uint kind; public IntPtr propid; }

    [StructLayout(LayoutKind.Sequential)]
    public struct STAT_CHUNK
    {
        public uint idChunk; public int breakType; public int flags; public uint locale;
        public FULLPROPSPEC attribute; public uint idChunkSource; public uint cwcStartSource; public uint cwcLenSource;
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistStream
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load(IStream pStm);
        void Save(IStream pStm, bool fClearDirty);
        void GetSizeMax(out ulong pcbSize);
    }

    // Console progress bar (carriage-return redraw, rate + ETA). Port of Pluto's scripts/lib/Types.cs ProgressBar
    // so the scraper has no dependency on the Pluto lib. Not Write-Progress: that one is slow and hides the console.
    public class Progress
    {
        readonly int total, width;
        readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int current, lastLen;
        double lastDraw;

        public Progress(int total, string title) : this(total, title, 40) { }
        public Progress(int total, string title, int width)
        {
            this.total = total; this.width = width;
            if (!string.IsNullOrEmpty(title)) Console.WriteLine(title);
        }

        static string Fmt(double s)
        {
            if (double.IsInfinity(s) || double.IsNaN(s)) return "?";
            var t = TimeSpan.FromSeconds(s);
            if (t.TotalHours >= 1) return string.Format("{0}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
            if (t.TotalMinutes >= 1) return string.Format("{0}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0}s", (int)t.TotalSeconds);
        }

        public void Tick(string label)
        {
            current++;
            double el = sw.Elapsed.TotalSeconds;
            if (el - lastDraw < 0.1 && current != total) return;
            lastDraw = el;
            double pct = total > 0 ? (double)current / total : 1;
            int filled = (int)(pct * width);
            double rate = current / Math.Max(el, 1e-9);
            string line = string.Format("\r[{0}{1}] {2}/{3} ({4:F0}%) {5:F1}/s ETA {6}  {7}",
                new string('#', filled), new string('-', width - filled), current, total, pct * 100, rate,
                Fmt((total - current) / rate), label ?? "");
            int max = 200; try { max = Console.BufferWidth - 1; } catch (Exception) { }   // no console when redirected
            if (max > 0 && line.Length > max) line = line.Substring(0, max);
            Console.Write(line.PadRight(lastLen));
            lastLen = line.Length;
        }

        public void Finish()
        {
            double el = sw.Elapsed.TotalSeconds;
            string line = string.Format("\r[{0}] {1}/{1} (100%) {2:F1}/s {3} total",
                new string('#', width), total, total / Math.Max(el, 1e-9), Fmt(el));
            Console.WriteLine(line.PadRight(lastLen));
        }
    }

    public static class PdfText
    {
        // Windows built-in PDF filter (Windows.Data.Pdf.dll). Resolved 2026-09-11 from
        // HKCR\.pdf\PersistentHandler -> PersistentAddinsRegistered -> filter CLSID.
        static readonly Guid PdfFilterClsid = new Guid("6C337B26-3E38-4F98-813B-FBA18BAB64F5");

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern IStream SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IStream pstmTemplate);

        [DllImport("query.dll", CharSet = CharSet.Unicode)]
        static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, out IFilter ppIUnk);

        // LoadIFilter returns E_NOINTERFACE for the Windows PDF filter (it has no IPersistFile).
        // Create it by CLSID and load through IPersistStream instead.
        static IFilter Open(string path, out IStream keepAlive)
        {
            keepAlive = null;
            IFilter f;
            if (LoadIFilter(path, IntPtr.Zero, out f) == 0 && f != null) return f;

            object o = Activator.CreateInstance(Type.GetTypeFromCLSID(PdfFilterClsid));
            var ps = (IPersistStream)o;
            keepAlive = SHCreateStreamOnFileEx(path, 0x00000000 /*STGM_READ*/ | 0x00000020 /*STGM_SHARE_DENY_WRITE*/, 0, false, null);
            ps.Load(keepAlive);
            return (IFilter)o;
        }

        const int FILTER_E_NO_MORE_TEXT = unchecked((int)0x80041802);
        const int FILTER_E_NO_MORE_CHUNKS = unchecked((int)0x80041801);
        const int FILTER_E_NO_TEXT = unchecked((int)0x80041804);
        const int FILTER_E_END_OF_CHUNKS = unchecked((int)0x80041700);
        const int CHUNK_TEXT = 1;
        const uint IFILTER_INIT_APPLY_INDEX_ATTRIBUTES = 0x10;

        // Whole document.
        public static string Extract(string path) { return Extract(path, null, 0, 0); }

        // Early stop: the filter streams roughly one chunk per page. Stop after the chunk containing
        // stopMarker plus extraChunks more (a table can spill onto the next page), or at maxChunks
        // (0 = no cap). A 1000-page calc with the table on page 7 then costs ~10 chunks, not 1000.
        public static string Extract(string path, string stopMarker, int extraChunks, int maxChunks)
        {
            IStream stm;
            IFilter f = Open(path, out stm);
            try
            {
                uint flags;
                int hr = f.Init(IFILTER_INIT_APPLY_INDEX_ATTRIBUTES, 0, IntPtr.Zero, out flags);
                if (hr != 0) throw new COMException("IFilter.Init failed", hr);

                var sb = new StringBuilder();
                var buf = new StringBuilder(65536);
                var chunk = new StringBuilder();
                STAT_CHUNK st;
                int chunks = 0, remaining = -1;
                while (remaining != 0 && (maxChunks <= 0 || chunks < maxChunks))
                {
                    hr = f.GetChunk(out st);
                    if (hr == FILTER_E_END_OF_CHUNKS || hr == FILTER_E_NO_MORE_CHUNKS) break;
                    if (hr != 0) continue;                       // skip unreadable chunk
                    if ((st.flags & CHUNK_TEXT) == 0) continue;  // value chunks (metadata)
                    chunk.Length = 0;
                    while (true)
                    {
                        uint n = (uint)buf.Capacity;
                        buf.Length = 0;
                        hr = f.GetText(ref n, buf);
                        if (hr == FILTER_E_NO_MORE_TEXT || hr == FILTER_E_NO_TEXT) break;
                        if (hr != 0 && hr != 0x41709 /*FILTER_S_LAST_TEXT*/) break;
                        chunk.Append(buf.ToString(0, (int)n));
                        if (hr == 0x41709) break;
                    }
                    sb.Append(chunk).Append('\n');
                    chunks++;
                    if (remaining > 0) remaining--;
                    else if (remaining < 0 && stopMarker != null
                             && chunk.ToString().IndexOf(stopMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                        remaining = extraChunks;
                }
                return sb.ToString();
            }
            finally
            {
                Marshal.ReleaseComObject(f);
                if (stm != null) Marshal.ReleaseComObject(stm);
            }
        }
    }
}
