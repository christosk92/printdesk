using FluentGpu;
using FluentGpu.Dsl;
using PrintDesk.State;

namespace PrintDesk;

static class Program
{
    public static Store Store { get; private set; } = null!;

    /// <summary>Files named on the command line (a file dragged onto the exe, "Open with", Send to): printed on start.</summary>
    public static readonly List<string> PrintAtStart = [];

    /// <summary>--printer &lt;address&gt; (repeatable): printers to add on start, besides the remembered and found ones.</summary>
    public static readonly List<string> AddPrinters = [];

    // STA: the file picker and the OLE drop target are STA COM objects.
    [STAThread]
    static void Main(string[] args)
    {
        string[] valued = ["--shot", "--frames", "--w", "--h", "--save-to", "--printer"];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--printer" && i < args.Length - 1) AddPrinters.Add(args[i + 1]);
            if (Array.IndexOf(valued, args[i]) >= 0) { i++; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) && (File.Exists(args[i]) || Directory.Exists(args[i])))
                PrintAtStart.Add(Path.GetFullPath(args[i]));
        }
        Store = new Store(Arg(args, "--shot") is null ? "PrintDesk" : "PrintDesk.Check");
        Store.SaveDir = Arg(args, "--save-to");

        var options = new AppOptions
        {
            Title = "PrintDesk",
            Width = 1180,
            Height = 780,
            MinWidth = 900,
            MinHeight = 600,
            Mica = true,
            CustomFrame = true,
        };

        // Layout check: --shot <png> [--frames n] [--w dip] [--h dip] [--light] [--dark] renders n frames (100 ms apart,
        // so the first printer poll lands) off the back buffer to a PNG, then exits.
        string? shot = Arg(args, "--shot");
        if (shot is not null)
        {
            IsShot = true;
            if (args.Contains("--light")) Theme.Dark = false;
            if (args.Contains("--dark")) Theme.Dark = true;
            int frames = int.TryParse(Arg(args, "--frames"), out var f) ? f : 60;
            float w = float.TryParse(Arg(args, "--w"), out var ww) ? ww : 1180f;
            float h = float.TryParse(Arg(args, "--h"), out var hh) ? hh : 780f;
            FluentAppHarness.Run(() => new App(), options with { Width = (int)w, Height = (int)h, Mica = false },
                new HarnessOptions { Frames = frames, Screenshot = shot, FrameWaitMs = 100 });
            Imaging.Pdf.ExitSafely();
            return;
        }

        FluentApp.Run(() => new App(), options);
        Imaging.Pdf.ExitSafely();
    }

    /// <summary>A --shot run: the window is moved off-screen (see <see cref="Shell"/>).</summary>
    public static bool IsShot { get; private set; }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }
}
