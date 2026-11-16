using System.Runtime.InteropServices;
namespace PrintDesk.Imaging;

/// <summary>
/// PDF pages to sheets through Windows' own PDF renderer (Windows.Data.Pdf), called through its raw WinRT ABI so it
/// stays NativeAOT-clean. Each page renders to a temporary PNG at the printer's resolution, then goes through the same
/// placement as a picture. Interface ids and vtable slots are from the SDK's windows.data.pdf.h.
/// </summary>
static unsafe partial class Pdf
{
    private static readonly Guid IID_IPdfDocumentStatics = new("433a0b5f-c007-4788-90f2-08143d922599");
    private static readonly Guid IID_IPdfDocument = new("ac7ebedd-80fa-4089-846e-81b77ff5a86c");
    private static readonly Guid IID_IPdfPage = new("9db4b0c8-5320-4cfc-ad76-493fdad0e594");
    private static readonly Guid IID_IPdfPageRenderOptions = new("3c98056f-b7cf-4c29-9a04-52d90267f425");
    private static readonly Guid IID_IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    [LibraryImport("shcore.dll")]
    private static partial int CreateRandomAccessStreamOnFile([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint accessMode,
        Guid* riid, void** ppv);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, void** factory);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, void** instance);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, nint* str);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint str);

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);   // 1 = multithreaded

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True once Windows' PDF renderer has been loaded in this process (see <see cref="ExitSafely"/>).</summary>
    public static bool Used { get; private set; }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    /// <summary>
    /// Windows.Data.Pdf.dll keeps a process-wide static holding a Direct3D 11 device and releases it from its
    /// DLL_PROCESS_DETACH. By then the GPU driver has torn its own state down, and on this Qualcomm Adreno driver that
    /// release faults (cdb: Windows_Data_Pdf!`dynamic atexit destructor for 's_pdfStaticsInstance'' -> d3d11 ->
    /// qcdx11arm64xum). The static is out of reach, so once the window has closed and everything is written, a process
    /// that loaded the renderer ends without running DLL detach.
    /// </summary>
    public static void ExitSafely()
    {
        if (!Used) return;
        Console.Out.Flush();
        Console.Error.Flush();
        TerminateProcess(-1 /* GetCurrentProcess() */, 0);
    }

    // ── vtable helpers (IUnknown 0-2, IInspectable 3-5, interface methods from 6) ──
    private static void** V(void* o) => *(void***)o;
    private static uint Release(void* o) => o == null ? 0 : ((delegate* unmanaged[MemberFunction]<void*, uint>)V(o)[2])(o);
    private static int QI(void* o, Guid iid, void** r) => ((delegate* unmanaged[MemberFunction]<void*, Guid*, void**, int>)V(o)[0])(o, &iid, r);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new PrintException($"{what} (0x{hr:X8}).");
    }

    /// <summary>Wait for an IAsyncOperation/IAsyncAction by polling IAsyncInfo (no completion-handler COM object needed).</summary>
    private static void Await(void* op, string what)
    {
        void* info = null;
        Check(QI(op, IID_IAsyncInfo, &info), what);
        try
        {
            var deadline = Environment.TickCount64 + 60_000;
            while (true)
            {
                int status = 0;   // 0 started, 1 completed, 2 canceled, 3 error
                Check(((delegate* unmanaged[MemberFunction]<void*, int*, int>)V(info)[7])(info, &status), what);
                if (status == 1) return;
                if (status >= 2)
                {
                    int err = 0;
                    ((delegate* unmanaged[MemberFunction]<void*, int*, int>)V(info)[8])(info, &err);
                    throw new PrintException(err == unchecked((int)0x80004005) || err == 0
                        ? $"{what}." : $"{what} (0x{err:X8}). The file may be password-protected or damaged");
                }
                if (Environment.TickCount64 > deadline) throw new PrintException($"{what}: timed out.");
                Thread.Sleep(5);
            }
        }
        finally { Release(info); }
    }

    private static nint HString(string s)
    {
        nint h;
        Check(WindowsCreateString(s, (uint)s.Length, &h), "Windows runtime string");
        return h;
    }

    /// <summary>Render every page of a PDF, handing each sheet to <paramref name="onPage"/> (page, pageCount, sheet) as it
    /// lands, so a long document never holds every page in memory. Returns the page count.</summary>
    public static int Render(string path, PageSpec page, Action<int, int, PageImage> onPage, CancellationToken ct)
    {
        // Windows' PDF renderer gets a thread of its own that opens and closes its own Windows Runtime session. Used
        // from a borrowed thread-pool thread (initialised, never uninitialised), its leftovers were still alive when the
        // process unloaded, and the process crashed on exit.
        Used = true;
        int result = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var worker = new Thread(() =>
        {
            int hr = RoInitialize(1);
            try { result = RenderCore(path, page, onPage, ct); }
            catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            finally { if (hr >= 0) RoUninitialize(); }
        })
        { IsBackground = true, Name = "PrintDesk PDF" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
        error?.Throw();
        return result;
    }

    private static int RenderCore(string path, PageSpec page, Action<int, int, PageImage> onPage, CancellationToken ct)
    {
        void* statics = null, docOp = null, doc = null, input = null;
        nint cls = HString("Windows.Data.Pdf.PdfDocument");
        try
        {
            Guid iid = IID_IPdfDocumentStatics;
            Check(RoGetActivationFactory(cls, &iid, &statics), "Windows' PDF renderer is unavailable");

            Guid ras = IID_IRandomAccessStream;
            Check(CreateRandomAccessStreamOnFile(path, 0 /* Read */, &ras, &input), "Can't open the PDF");

            // IPdfDocumentStatics::LoadFromStreamAsync (slot 8)
            Check(((delegate* unmanaged[MemberFunction]<void*, void*, void**, int>)V(statics)[8])(statics, input, &docOp), "Can't open the PDF");
            Await(docOp, "Can't open the PDF");
            // IAsyncOperation<PdfDocument>::GetResults (slot 8)
            Check(((delegate* unmanaged[MemberFunction]<void*, void**, int>)V(docOp)[8])(docOp, &doc), "Can't open the PDF");

            uint count = 0;   // IPdfDocument::get_PageCount (slot 7)
            Check(((delegate* unmanaged[MemberFunction]<void*, uint*, int>)V(doc)[7])(doc, &count), "Can't read the PDF");
            if (count == 0) throw new PrintException("The PDF has no pages.");

            for (uint k = 0; k < count; k++)
            {
                ct.ThrowIfCancellationRequested();
                onPage((int)k + 1, (int)count, RenderPage(doc, k, page));
            }
            return (int)count;
        }
        finally
        {
            Release(doc); Release(docOp); Release(input); Release(statics);
            WindowsDeleteString(cls);
        }
    }

    private static PageImage RenderPage(void* doc, uint index, PageSpec page)
    {
        void* pdfPage = null, options = null, output = null, renderOp = null;
        nint optCls = HString("Windows.Data.Pdf.PdfPageRenderOptions");
        string tmp = Path.Combine(Path.GetTempPath(), $"printdesk-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        try
        {
            // IPdfDocument::GetPage (slot 6)
            Check(((delegate* unmanaged[MemberFunction]<void*, uint, void**, int>)V(doc)[6])(doc, index, &pdfPage), "Can't read a PDF page");

            // IPdfPage::get_Size (slot 10): DIPs (1/96 inch).
            System.Numerics.Vector2 size = default;
            Check(((delegate* unmanaged[MemberFunction]<void*, System.Numerics.Vector2*, int>)V(pdfPage)[10])(pdfPage, &size), "Can't read a PDF page");
            double wIn = size.X / 96.0, hIn = size.Y / 96.0;

            // Render at the printer's resolution, as large as the printable area takes it (turned to match the sheet).
            bool turn = (wIn > hIn) != (page.AreaW > page.AreaH) && Math.Abs(wIn - hIn) > 0.01;
            double pw = turn ? hIn : wIn, ph = turn ? wIn : hIn;
            double fit = Math.Min(page.AreaW / (pw * page.Dpi), page.AreaH / (ph * page.Dpi));
            uint dw = (uint)Math.Max(1, Math.Round(wIn * page.Dpi * fit));
            uint dh = (uint)Math.Max(1, Math.Round(hIn * page.Dpi * fit));

            void* insp = null;
            Check(RoActivateInstance(optCls, &insp), "PDF render options");
            try { Check(QI(insp, IID_IPdfPageRenderOptions, &options), "PDF render options"); }
            finally { Release(insp); }
            // put_DestinationWidth (9), put_DestinationHeight (11)
            Check(((delegate* unmanaged[MemberFunction]<void*, uint, int>)V(options)[9])(options, dw), "PDF render options");
            Check(((delegate* unmanaged[MemberFunction]<void*, uint, int>)V(options)[11])(options, dh), "PDF render options");

            File.Create(tmp).Dispose();   // ReadWrite opens an existing file; it does not create one
            Guid ras = IID_IRandomAccessStream;
            Check(CreateRandomAccessStreamOnFile(tmp, 1 /* ReadWrite */, &ras, &output), "Can't write a temporary page");
            // IPdfPage::RenderWithOptionsToStreamAsync (slot 7) -> IAsyncAction
            Check(((delegate* unmanaged[MemberFunction]<void*, void*, void*, void**, int>)V(pdfPage)[7])(pdfPage, output, options, &renderOp),
                "Rendering a PDF page failed");
            Await(renderOp, "Rendering a PDF page failed");
            Release(output); output = null;   // flush and close the PNG before WIC opens it

            // Already sized and turned for the sheet: place it as a picture (Fit keeps it 1:1 and centred).
            return Wic.RenderFile(tmp, page, Placement.Fit);
        }
        finally
        {
            Release(renderOp); Release(output); Release(options); Release(pdfPage);
            WindowsDeleteString(optCls);
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
