using System.Globalization;
using System.Net.Http.Headers;

namespace PrintDesk.Ipp;

/// <summary>One ink or toner supply as the printer reports it.</summary>
sealed record Supply(string Name, int Level, string? ColorHex)
{
    /// <summary>A known level 0..100, or null when the printer only says "unknown" (-2) or "some left" (-3).</summary>
    public int? Percent => Level is >= 0 and <= 100 ? Level : null;
}

/// <summary>A printer-side job.</summary>
sealed record PrinterJob(int Id, string Name, int State, string Reasons, int PagesDone, string Owner)
{
    // job-state: 3 pending, 4 held, 5 processing, 6 processing-stopped, 7 canceled, 8 aborted, 9 completed
    public bool IsActive => State is >= 3 and <= 6;
}

/// <summary>Everything the UI shows about a printer, from one Get-Printer-Attributes call.</summary>
sealed record PrinterInfo
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public int State { get; init; }                         // 3 idle, 4 processing, 5 stopped
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> Alerts { get; init; } = [];
    public string? Message { get; init; }
    public IReadOnlyList<Supply> Supplies { get; init; } = [];
    public IReadOnlyList<string> MediaReady { get; init; } = [];
    public string MediaDefault { get; init; } = "iso_a4_210x297mm";
    public IReadOnlyList<string> Formats { get; init; } = [];
    public int Dpi { get; init; } = 300;
    public bool ColorSupported { get; init; } = true;
    public bool AcceptingJobs { get; init; } = true;
    public int QueuedJobs { get; init; }
    public string? Firmware { get; init; }
    public string? AdminUrl { get; init; }
    public string? Location { get; init; }
    /// <summary>printer-icons: pictures of the device, smallest first (48, 128, 512 px by convention).</summary>
    public IReadOnlyList<string> Icons { get; init; } = [];

    public bool Supports(string mime) => Formats.Contains(mime, StringComparer.OrdinalIgnoreCase);

    /// <summary>A cheap content fingerprint, so an identical poll does not wake the UI.</summary>
    public string Fingerprint => string.Join("|", Name, Model, State, string.Join(",", Reasons), string.Join(",", Alerts), Message,
        string.Join(",", Supplies.Select(s => $"{s.Name}:{s.Level}")), string.Join(",", MediaReady), AcceptingJobs, QueuedJobs,
        Location, string.Join(",", Icons));
}

/// <summary>A minimal IPP client: the four operations a print panel needs, over plain HTTP.</summary>
sealed class IppClient : IDisposable
{
    private readonly HttpClient _http;
    public string PrinterUri { get; }
    private readonly Uri _httpUri;

    public IppClient(string printerUri)
    {
        PrinterUri = printerUri;
        var u = new Uri(printerUri.Replace("ipps://", "https://").Replace("ipp://", "http://"));
        // IPP's default port is 631 for both schemes.
        _httpUri = new UriBuilder(u) { Port = u.IsDefaultPort ? 631 : u.Port }.Uri;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            // Printers use self-signed certificates on ipps.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public void Dispose() => _http.Dispose();

    private async Task<IppResponse> SendAsync(byte[] body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));   // a sleeping inkjet takes a few seconds to wake its network
        using var resp = await _http.PostAsync(_httpUri, content, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IppException($"The printer answered HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        return IppResponse.Parse(bytes);
    }

    private static readonly string[] s_printerAttrs =
    [
        "printer-name", "printer-make-and-model", "printer-info", "printer-state", "printer-state-reasons",
        "printer-state-message", "printer-alert-description", "marker-names", "marker-levels", "marker-colors",
        "media-ready", "media-default", "document-format-supported", "printer-resolution-supported",
        "pwg-raster-document-resolution-supported", "color-supported", "printer-is-accepting-jobs", "queued-job-count",
        "printer-firmware-string-version", "printer-more-info", "printer-location", "printer-icons",
    ];

    public async Task<PrinterInfo> GetPrinterAsync(CancellationToken ct = default)
    {
        var req = new IppRequest(0x000B, PrinterUri).Strs(Tag.Keyword, "requested-attributes", s_printerAttrs).Build();
        var r = await SendAsync(req, ct).ConfigureAwait(false);
        if (!r.Ok) throw new IppException($"The printer refused the status request ({r.StatusText}).");
        var p = r.First(Tag.PrinterGroup) ?? new IppGroup(Tag.PrinterGroup);

        var names = p.Strs("marker-names");
        var levels = p.Ints("marker-levels");
        var colors = p.Strs("marker-colors");
        var supplies = new List<Supply>();
        for (int k = 0; k < names.Count; k++)
            supplies.Add(new Supply(names[k], k < levels.Count ? levels[k] : -2, k < colors.Count ? colors[k] : null));

        int dpi = 300;
        var res = p.Get("pwg-raster-document-resolution-supported").Concat(p.Get("printer-resolution-supported"))
            .Where(v => v.Tag == Tag.Resolution).Select(v => v.AsResolution())
            .Select(x => x.Units == 4 ? (int)Math.Round(x.X * 2.54) : x.X).Where(x => x > 0).ToList();
        if (res.Count > 0) dpi = res.Contains(300) ? 300 : res.OrderBy(x => Math.Abs(x - 300)).First();

        string model = p.Str("printer-make-and-model") ?? p.Str("printer-info") ?? "Printer";
        return new PrinterInfo
        {
            Name = p.Str("printer-info") is { Length: > 0 } info ? info : model,
            Model = model,
            State = p.Int("printer-state") ?? 3,
            Reasons = p.Strs("printer-state-reasons").Where(s => s != "none").ToList(),
            Alerts = p.Strs("printer-alert-description").Where(s => s.Length > 0).ToList(),
            Message = p.Str("printer-state-message") is { Length: > 0 } m ? m : null,
            Supplies = supplies,
            MediaReady = p.Strs("media-ready"),
            MediaDefault = p.Str("media-default") ?? "iso_a4_210x297mm",
            Formats = p.Strs("document-format-supported"),
            Dpi = dpi,
            ColorSupported = p.Get("color-supported") is { Count: > 0 } c ? c[0].AsBool() : true,
            AcceptingJobs = p.Get("printer-is-accepting-jobs") is { Count: > 0 } a ? a[0].AsBool() : true,
            QueuedJobs = p.Int("queued-job-count") ?? 0,
            Firmware = p.Str("printer-firmware-string-version"),
            AdminUrl = p.Str("printer-more-info"),
            Location = p.Str("printer-location") is { Length: > 0 } loc ? loc : null,
            Icons = p.Strs("printer-icons"),
        };
    }

    /// <summary>Fetch a file the printer serves (its icon), trusting its self-signed certificate like the IPP calls do.</summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        // Some printers advertise their icon on the IPP scheme; it is plain HTTP(S) underneath.
        var u = url.Replace("ipps://", "https://").Replace("ipp://", "http://");
        using var resp = await _http.GetAsync(u, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PrinterJob>> GetJobsAsync(CancellationToken ct = default)
    {
        var req = new IppRequest(0x000A, PrinterUri)
            .Str(Tag.Keyword, "which-jobs", "not-completed")
            .Strs(Tag.Keyword, "requested-attributes", "job-id", "job-name", "job-state", "job-state-reasons",
                "job-impressions-completed", "job-originating-user-name")
            .Build();
        var r = await SendAsync(req, ct).ConfigureAwait(false);
        if (!r.Ok) return [];
        return r.All(Tag.JobGroup).Select(ToJob).Where(j => j.Id > 0).ToList();
    }

    public async Task<PrinterJob?> GetJobAsync(int jobId, CancellationToken ct = default)
    {
        var req = new IppRequest(0x0009, PrinterUri)
            .Int(Tag.Integer, "job-id", jobId)
            .Strs(Tag.Keyword, "requested-attributes", "job-id", "job-name", "job-state", "job-state-reasons",
                "job-impressions-completed", "job-originating-user-name")
            .Build();
        var r = await SendAsync(req, ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        var g = r.First(Tag.JobGroup);
        return g is null ? null : ToJob(g) with { Id = jobId };
    }

    private static PrinterJob ToJob(IppGroup g) => new(
        g.Int("job-id") ?? 0,
        g.Str("job-name") ?? "Untitled",
        g.Int("job-state") ?? 3,
        string.Join(",", g.Strs("job-state-reasons")),
        g.Int("job-impressions-completed") ?? 0,
        g.Str("job-originating-user-name") ?? "");

    /// <summary>Print-Job: one document. Returns the printer's job id.</summary>
    public async Task<int> PrintAsync(byte[] document, string mime, string jobName, int copies, string media,
        bool color, CancellationToken ct = default)
    {
        var req = new IppRequest(0x0002, PrinterUri)
            .Str(Tag.NameWithoutLanguage, "job-name", jobName.Length > 200 ? jobName[..200] : jobName)
            .Str(Tag.MimeMediaType, "document-format", mime)
            .Group(Tag.JobGroup)
            .Str(Tag.Keyword, "media", media)
            .Str(Tag.Keyword, "print-color-mode", color ? "color" : "monochrome");
        if (copies > 1) req.Int(Tag.Integer, "copies", copies);
        var r = await SendAsync(req.Build(document), ct, TimeSpan.FromSeconds(110)).ConfigureAwait(false);
        if (!r.Ok) throw new IppException($"The printer refused the job ({r.StatusText}).");
        return r.First(Tag.JobGroup)?.Int("job-id") ?? 0;
    }

    public async Task<bool> CancelAsync(int jobId, CancellationToken ct = default)
    {
        var req = new IppRequest(0x0008, PrinterUri).Int(Tag.Integer, "job-id", jobId).Build();
        var r = await SendAsync(req, ct).ConfigureAwait(false);
        return r.Ok;
    }
}

/// <summary>PWG media names ("iso_a4_210x297mm", "na_letter_8.5x11in") to size and a friendly label.</summary>
static class Media
{
    public static (double WidthMm, double HeightMm) Size(string media)
    {
        // The last underscore-separated part is "<w>x<h><unit>".
        var dims = media.Split('_').LastOrDefault() ?? "";
        bool inch = dims.EndsWith("in", StringComparison.Ordinal);
        var core = dims.TrimEnd('m', 'i', 'n');
        var parts = core.Split('x');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0)
            return inch ? (w * 25.4, h * 25.4) : (w, h);
        return (210, 297);
    }

    public static string Label(string media)
    {
        var bits = media.Split('_');
        if (bits.Length < 3) return media;
        var name = bits[1];
        return name switch
        {
            "letter" => "Letter",
            "legal" => "Legal",
            "index-4x6" or "4x6" => "4×6 photo",
            "5x7" => "5×7 photo",
            "govt-letter" => "Letter",
            _ when name.Length <= 3 => name.ToUpperInvariant(),   // a4, a5, b5
            _ => char.ToUpperInvariant(name[0]) + name[1..].Replace('-', ' '),
        };
    }
}
