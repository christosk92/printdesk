using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Storage;
using PrintDesk.Imaging;
using PrintDesk.Ipp;

namespace PrintDesk.State;

enum Phase : byte { Preparing, Sending, Waiting, Printing, Stopped, Done, Failed, Canceled, Handed }

/// <summary>One row of the queue: a file this app is printing, or a job someone else sent to a printer.</summary>
sealed record QueueItem(string Key, string Name, Phase Phase, string Detail, string PrinterUri, int JobId = 0,
    bool Mine = true, string? Path = null)
{
    public bool IsFinished => Phase is Phase.Done or Phase.Failed or Phase.Canceled or Phase.Handed;
    public bool CanCancel => !IsFinished;
}

/// <summary>Everything known about one printer: how to reach it, its last status, and its picture on disk.</summary>
sealed record PrinterState(PrinterRef Ref, PrinterInfo? Info = null, string? Error = null, bool Connecting = true,
    string? Image = null)
{
    public string Uri => Ref.Uri;
    public string DisplayName => Info?.Model ?? Ref.Name;
    public Health Health => Error is not null || Info is null ? Health.Offline : Words.HealthOf(Info);
    public string Headline => Error is not null ? "Offline" : Info is null ? "Connecting…" : Words.Headline(Info);
}

/// <summary>
/// The app's state. Every printer found or added is polled (the selected one every tick, the rest every fourth), keeps
/// its own client, and has its picture fetched once from its printer-icons and cached on disk. Signals are written only
/// on the UI thread (through <see cref="_post"/>); network and rendering run on the thread pool.
/// </summary>
sealed class Store
{
    public readonly Signal<IReadOnlyList<PrinterState>> Fleet = new([]);
    public readonly Signal<string?> CurrentUri = new(null);
    public readonly Signal<bool> Discovering = new(false);
    public readonly Signal<IReadOnlyList<QueueItem>> Queue = new([]);
    public readonly Signal<string?> AddError = new(null);
    public readonly Signal<int> PlacementIndex = new(0);   // 0 fit, 1 actual size
    public readonly Signal<int> ColorIndex = new(0);       // 0 colour, 1 black & white
    public readonly Signal<double> Copies = new(1);

    /// <summary>When set (--save-to), print jobs are written to this folder instead of sent.</summary>
    public string? SaveDir { get; set; }

    /// <summary>What --save-to lays pages out for when no printer answers: A4, 600 dpi, JPEG and PWG raster.</summary>
    private static readonly PrinterInfo StandInPrinter = new()
    {
        Name = "Save to file", Model = "Save to file", Dpi = 600,
        Formats = ["image/jpeg", "image/pwg-raster"], MediaDefault = "iso_a4_210x297mm",
    };

    private Action<Action> _post = a => a();
    private readonly Dictionary<string, IppClient> _clients = [];
    private readonly HashSet<string> _polling = [];
    private readonly HashSet<string> _fetchingImage = [];
    private readonly Dictionary<string, string> _fingerprints = [];
    private readonly Dictionary<string, CancellationTokenSource> _work = [];
    private readonly AppDataStore? _settings;
    private int _tick;

    /// <param name="product">The settings and cache area: "PrintDesk", or a separate one for layout checks so their test
    /// printers never land in the real list.</param>
    public Store(string product)
    {
        try { _settings = AppDataStore.ForUnpackaged("PrintDesk", product); }
        catch (Exception) { _settings = null; }   // settings are a convenience; the app works without them
    }

    public PrinterState? Current => Fleet.Peek().FirstOrDefault(p => p.Uri == CurrentUri.Peek());

    /// <summary>Wire the UI-thread poster, restore the remembered printers, look for more on the network.</summary>
    public void Start(Action<Action> post, IReadOnlyList<string>? printAtStart = null, IReadOnlyList<string>? addresses = null)
    {
        _post = post;
        foreach (var p in LoadRemembered()) Add(p, select: false);
        if (_settings?.GetString("printer.uri") is { Length: > 0 } last && Fleet.Peek().Any(p => p.Uri == last)) Select(last);
        foreach (var a in addresses ?? []) AddAddress(a);
        if (CurrentUri.Peek() is null && Fleet.Peek().Count > 0) Select(Fleet.Peek()[0].Uri);

        if (printAtStart is { Count: > 0 })
        {
            // Files named on the command line print once the selected printer has answered (the layout needs its paper
            // size and formats).
            void WhenReady()
            {
                if (Current?.Info is null && SaveDir is null) { _ = Task.Delay(250).ContinueWith(_ => _post(WhenReady), TaskScheduler.Default); return; }
                PrintFiles(printAtStart);
            }
            _post(WhenReady);
        }
        PollAll();
        Discover();
    }

    // ── the fleet ──

    public void Discover()
    {
        if (Discovering.Peek()) return;
        Discovering.Value = true;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<PrinterRef> found = [];
            try { found = await Discovery.FindAsync(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false); }
            catch (Exception) { }
            _post(() =>
            {
                Discovering.Value = false;
                foreach (var p in found) Add(p, select: CurrentUri.Peek() is null);
            });
        });
    }

    public void AddAddress(string address)
    {
        AddError.Value = null;
        if (string.IsNullOrWhiteSpace(address)) return;
        PrinterRef p;
        try { p = PrinterRef.FromAddress(address); }
        catch (UriFormatException) { AddError.Value = $"\"{address.Trim()}\" isn't a printer address."; return; }
        Add(p, select: true);
    }

    private void Add(PrinterRef p, bool select)
    {
        if (!Fleet.Peek().Any(x => x.Uri == p.Uri))
        {
            Fleet.Value = Fleet.Peek().Append(new PrinterState(p, Image: CachedImage(p.Uri))).ToList();
            _clients[p.Uri] = new IppClient(p.Uri);
            Remember();
            Poll(p.Uri);
        }
        if (select) Select(p.Uri);
    }

    public void Select(string uri)
    {
        if (!Fleet.Peek().Any(x => x.Uri == uri)) return;
        CurrentUri.Value = uri;
        _settings?.SetString("printer.uri", uri);
        Poll(uri);
    }

    public void Forget(string uri)
    {
        Fleet.Value = Fleet.Peek().Where(p => p.Uri != uri).ToList();
        if (_clients.Remove(uri, out var c)) c.Dispose();
        _fingerprints.Remove(uri);
        Queue.Value = Queue.Peek().Where(q => q.Mine || q.PrinterUri != uri).ToList();
        if (CurrentUri.Peek() == uri) CurrentUri.Value = Fleet.Peek().FirstOrDefault()?.Uri;
        Remember();
    }

    private void SetState(string uri, Func<PrinterState, PrinterState> change) =>
        Fleet.Value = Fleet.Peek().Select(p => p.Uri == uri ? change(p) : p).ToList();

    private IEnumerable<PrinterRef> LoadRemembered()
    {
        var text = _settings?.GetString("printers") ?? "";
        // The first version remembered one printer (printer.uri + printer.name) and no list: carry it over.
        if (text.Length == 0 && _settings?.GetString("printer.uri") is { Length: > 0 } single)
            yield return new PrinterRef(_settings.GetString("printer.name") ?? single, single);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2 && parts[1].StartsWith("ipp", StringComparison.Ordinal)) yield return new PrinterRef(parts[0], parts[1]);
        }
    }

    private void Remember() =>
        _settings?.SetString("printers", string.Join("\n", Fleet.Peek().Select(p => $"{p.Info?.Model ?? p.Ref.Name}\t{p.Uri}")));

    // ── status polling ──

    /// <summary>The timer tick: the selected printer every time, the others every fourth tick.</summary>
    public void Tick()
    {
        _tick++;
        foreach (var p in Fleet.Peek())
            if (p.Uri == CurrentUri.Peek() || _tick % 4 == 0) Poll(p.Uri);
    }

    public void PollAll()
    {
        foreach (var p in Fleet.Peek()) Poll(p.Uri);
    }

    /// <summary>One status round for one printer: its attributes, its active jobs, and the fate of jobs sent to it.</summary>
    public void Poll(string uri)
    {
        if (!_clients.TryGetValue(uri, out var client) || !_polling.Add(uri)) return;
        var mine = Queue.Peek().Where(q => q.Mine && q.PrinterUri == uri && q.JobId > 0 && !q.IsFinished).Select(q => q.JobId).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await client.GetPrinterAsync().ConfigureAwait(false);
                var active = await client.GetJobsAsync().ConfigureAwait(false);
                var finals = new Dictionary<int, PrinterJob>();
                foreach (int id in mine)
                    if (!active.Any(j => j.Id == id) && await client.GetJobAsync(id).ConfigureAwait(false) is { } j)
                        finals[id] = j;
                _post(() =>
                {
                    _polling.Remove(uri);
                    if (!_clients.TryGetValue(uri, out var live) || !ReferenceEquals(live, client)) return;
                    bool changed = !_fingerprints.TryGetValue(uri, out var fp) || fp != info.Fingerprint;
                    _fingerprints[uri] = info.Fingerprint;
                    var before = Fleet.Peek().FirstOrDefault(p => p.Uri == uri);
                    if (changed || before?.Error is not null || before?.Connecting == true)
                    {
                        SetState(uri, p => p with { Info = info, Error = null, Connecting = false });
                        if (before?.Info is null) Remember();   // store the model name for the next start
                    }
                    MergeJobs(uri, active, finals, info);
                    FetchImage(uri, client, info);
                });
            }
            catch (Exception ex)
            {
                // Every failure must clear the in-flight mark, or this printer is never polled again.
                string why = ex switch
                {
                    IppException => ex.Message,
                    HttpRequestException or TaskCanceledException or IOException => "Isn't answering. Is it switched on and on this network?",
                    _ => $"Sent a reply PrintDesk can't read ({ex.GetType().Name}).",
                };
                _post(() =>
                {
                    _polling.Remove(uri);
                    if (!_clients.ContainsKey(uri)) return;
                    _fingerprints.Remove(uri);
                    SetState(uri, p => p with { Error = why, Connecting = false });
                });
            }
        });
    }

    // ── the printer's picture ──

    private string? ImageDir => _settings is null ? null : System.IO.Path.Combine(_settings.CacheFolder, "printers");

    private string? ImagePath(string uri) => ImageDir is { } dir
        ? System.IO.Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri)))[..16] + ".png")
        : null;

    private string? CachedImage(string uri) => ImagePath(uri) is { } p && File.Exists(p) ? p : null;

    /// <summary>Download the printer's largest icon once (IPP printer-icons, smallest first) and cache it on disk.</summary>
    private void FetchImage(string uri, IppClient client, PrinterInfo info)
    {
        if (info.Icons.Count == 0 || ImagePath(uri) is not { } target) return;
        if (Fleet.Peek().FirstOrDefault(p => p.Uri == uri)?.Image is not null || !_fetchingImage.Add(uri)) return;
        string url = info.Icons[^1];
        _ = Task.Run(async () =>
        {
            string? saved = null;
            try
            {
                var bytes = await client.DownloadAsync(url).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
                    saved = target;
                }
            }
            catch (Exception) { }   // no picture is fine: the glyph stands in, and the next poll tries again
            _post(() =>
            {
                _fetchingImage.Remove(uri);
                if (saved is not null) SetState(uri, p => p with { Image = saved });
            });
        });
    }

    // ── jobs ──

    private void MergeJobs(string uri, IReadOnlyList<PrinterJob> active, Dictionary<int, PrinterJob> finals, PrinterInfo info)
    {
        var next = new List<QueueItem>();
        foreach (var q in Queue.Peek())
        {
            if (q.PrinterUri != uri) { next.Add(q); continue; }
            if (!q.Mine) continue;                                  // this printer's foreign rows are rebuilt below
            if (q.JobId > 0 && !q.IsFinished)
            {
                var job = active.FirstOrDefault(j => j.Id == q.JobId) ?? (finals.TryGetValue(q.JobId, out var f) ? f : null);
                next.Add(job is null ? q with { Phase = Phase.Done, Detail = "Printed" } : FromJob(q, job, info));
            }
            else next.Add(q);
        }
        foreach (var j in active)
            if (!next.Any(q => q.PrinterUri == uri && q.JobId == j.Id))
                next.Add(FromJob(new QueueItem($"{uri}#{j.Id}", j.Name, Phase.Waiting, "", uri, j.Id, Mine: false), j, info));
        if (!next.SequenceEqual(Queue.Peek())) Queue.Value = next;
    }

    private static QueueItem FromJob(QueueItem q, PrinterJob j, PrinterInfo info)
    {
        string pages = j.PagesDone > 0 ? $" · {j.PagesDone} page{(j.PagesDone == 1 ? "" : "s")} done" : "";
        return j.State switch
        {
            3 or 4 => q with { Phase = Phase.Waiting, Detail = j.State == 4 ? "Held by the printer" : "Waiting its turn" },
            5 => q with { Phase = Phase.Printing, Detail = "Printing" + pages },
            6 => q with { Phase = Phase.Stopped, Detail = Words.StoppedBecause(info) },
            7 => q with { Phase = Phase.Canceled, Detail = "Canceled" },
            8 => q with { Phase = Phase.Failed, Detail = "The printer aborted this job" },
            _ => q with { Phase = Phase.Done, Detail = "Printed" + pages },
        };
    }

    public string PrinterName(string uri) => Fleet.Peek().FirstOrDefault(p => p.Uri == uri)?.DisplayName ?? "printer";

    // ── printing ──

    /// <summary>Print files on the selected printer.</summary>
    public void PrintFiles(IEnumerable<string> paths)
    {
        var target = Current;
        string uri = target?.Uri ?? "";
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { PrintFiles(Directory.EnumerateFiles(path)); continue; }
            if (!File.Exists(path)) continue;
            string key = Guid.NewGuid().ToString("N");
            var item = new QueueItem(key, System.IO.Path.GetFileName(path), Phase.Preparing, "Getting it ready", uri, Path: path);
            Queue.Value = Queue.Peek().Append(item).ToList();
            var cts = new CancellationTokenSource();
            _work[key] = cts;
            var job = new JobSettings(
                uri, _clients.GetValueOrDefault(uri), target?.Info, (Placement)PlacementIndex.Peek(), ColorIndex.Peek() == 0,
                Math.Clamp((int)Math.Round(double.IsNaN(Copies.Peek()) ? 1 : Copies.Peek()), 1, 99));
            _ = Task.Run(() => Run(key, path, job, cts.Token));
        }
    }

    private sealed record JobSettings(string Uri, IppClient? Client, PrinterInfo? Info, Placement Placement, bool Color, int Copies);

    private void Update(string key, Func<QueueItem, QueueItem> change) => _post(() =>
        Queue.Value = Queue.Peek().Select(q => q.Key == key ? change(q) : q).ToList());

    private async Task Run(string key, string path, JobSettings s, CancellationToken ct)
    {
        try
        {
            bool image = Wic.IsImage(path), pdf = Pdf.IsPdf(path);
            if (!image && !pdf)
            {
                HandToWindows(key, path);
                return;
            }
            var info = s.Info ?? (SaveDir is not null ? StandInPrinter : null);
            if (info is null || (s.Client is null && SaveDir is null))
                throw new PrintException(s.Client is null ? "No printer is selected yet." : "The printer hasn't answered yet; try again in a moment.");
            var (wmm, hmm) = Media.Size(info.MediaDefault);
            var page = new PageSpec(wmm, hmm, info.Dpi, 4.0, s.Color && info.ColorSupported);
            bool jpeg = info.Supports("image/jpeg"), pwg = info.Supports("image/pwg-raster");
            if (!jpeg && !pwg) throw new PrintException("This printer takes neither JPEG nor PWG raster, so it can't print driverless.");
            string name = System.IO.Path.GetFileName(path);

            if (image)
            {
                Update(key, q => q with { Detail = "Laying out the page" });
                // A JPEG carries its own size, so 300 dpi is plenty (a 600 dpi A4 sheet is 100 MB of pixels for nothing);
                // PWG raster has to match a resolution the printer lists.
                var sheet = Wic.RenderFile(path, jpeg ? page with { Dpi = 300 } : page, s.Placement);
                ct.ThrowIfCancellationRequested();
                byte[] doc;
                string mime;
                if (jpeg) { doc = Wic.EncodeJpeg(sheet); mime = "image/jpeg"; }
                else
                {
                    using var ms = new MemoryStream();
                    PwgRaster.Begin(ms);
                    PwgRaster.WritePage(ms, sheet, page.Color, 1, s.Copies);
                    doc = ms.ToArray(); mime = "image/pwg-raster";
                }
                await Send(key, s, info, doc, mime, name, ct).ConfigureAwait(false);
                return;
            }

            // PDF: 300 dpi JPEG pages when the printer takes JPEG (a quarter of the pixels of a 600 dpi raster, so several
            // times faster), else one PWG raster job for the whole document at a resolution the printer lists.
            if (!jpeg)
            {
                using var ms = new MemoryStream();
                PwgRaster.Begin(ms);
                Pdf.Render(path, page, (k, n, sheet) =>
                {
                    Update(key, q => q with { Detail = $"Rendering page {k} of {n}" });
                    PwgRaster.WritePage(ms, sheet, page.Color, n, s.Copies);
                }, ct);
                await Send(key, s, info, ms.ToArray(), "image/pwg-raster", name, ct).ConfigureAwait(false);
            }
            else
            {
                var jpegs = new List<byte[]>();
                Pdf.Render(path, page with { Dpi = 300 }, (k, n, sheet) =>
                {
                    Update(key, q => q with { Detail = $"Rendering page {k} of {n}" });
                    jpegs.Add(Wic.EncodeJpeg(sheet));
                }, ct);
                for (int k = 0; k < jpegs.Count; k++)
                    await Send(key, s, info, jpegs[k], "image/jpeg", $"{name} ({k + 1}/{jpegs.Count})", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Update(key, q => q with { Phase = Phase.Canceled, Detail = "Canceled" });
        }
        catch (Exception ex)
        {
            string why = ex switch
            {
                HttpRequestException => "Lost the connection to the printer",
                PrintException or IppException => ex.Message,
                OutOfMemoryException => "Not enough memory to lay out this page",
                _ => $"Something went wrong: {ex.Message}",
            };
            Update(key, q => q with { Phase = Phase.Failed, Detail = why });
        }
        finally
        {
            _post(() => { if (_work.Remove(key, out var c)) c.Dispose(); });
        }
    }

    private async Task Send(string key, JobSettings s, PrinterInfo info, byte[] doc, string mime, string name, CancellationToken ct)
    {
        double mb = doc.Length / 1048576.0;
        if (SaveDir is { } dir)
        {
            // --save-to: write exactly what the printer would receive, instead of sending it.
            Directory.CreateDirectory(dir);
            // Job names carry "(2/5)" for multi-page sends: keep the slash out of the file name.
            string safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            string file = System.IO.Path.Combine(dir, $"{safe}.{(mime == "image/jpeg" ? "jpg" : "pwg")}");
            await File.WriteAllBytesAsync(file, doc, ct).ConfigureAwait(false);
            Update(key, q => q with { Phase = Phase.Done, Detail = $"Saved {mb:0.#} MB to {file}" });
            return;
        }
        Update(key, q => q with { Phase = Phase.Sending, Detail = $"Sending to the printer ({mb:0.#} MB)" });
        if (s.Client is null) throw new PrintException("No printer is selected.");
        int id = await s.Client.PrintAsync(doc, mime, name, s.Copies, info.MediaDefault, s.Color && info.ColorSupported, ct).ConfigureAwait(false);
        Update(key, q => q with { Phase = Phase.Waiting, Detail = "Waiting its turn", JobId = id > 0 ? id : q.JobId });
        _post(() => Poll(s.Uri));
    }

    private void HandToWindows(string key, string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path) { Verb = "print", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            Update(key, q => q with { Phase = Phase.Handed, Detail = "Handed to Windows (prints on the default Windows printer)" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            string ext = System.IO.Path.GetExtension(path);
            Update(key, q => q with { Phase = Phase.Failed, Detail = $"Can't print {(ext.Length > 0 ? ext : "this file")} files: only pictures and PDFs print directly, and no app on this PC prints this type" });
        }
    }

    public void Cancel(QueueItem item)
    {
        if (_work.TryGetValue(item.Key, out var cts)) cts.Cancel();
        if (item.JobId > 0 && _clients.TryGetValue(item.PrinterUri, out var client))
        {
            Queue.Value = Queue.Peek().Select(q => q.Key == item.Key ? q with { Detail = "Canceling…" } : q).ToList();
            _ = Task.Run(async () =>
            {
                bool ok = false;
                try { ok = await client.CancelAsync(item.JobId).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IppException) { }
                _post(() =>
                {
                    if (ok) Update(item.Key, q => q with { Phase = Phase.Canceled, Detail = "Canceled" });
                    Poll(item.PrinterUri);
                });
            });
        }
    }

    public void ClearFinished() => Queue.Value = Queue.Peek().Where(q => !q.IsFinished).ToList();
}
