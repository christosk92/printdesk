using System.Diagnostics;
using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.WindowsApi.Dialogs;
using PrintDesk.Imaging;
using PrintDesk.Ipp;
using PrintDesk.State;
using static FluentGpu.Dsl.Ui;

namespace PrintDesk;

/// <summary>The Windows 11 page rhythm (win11-design skill, "Standard page"), in one place.</summary>
static class Win11Page
{
    /// <summary>Content column cap (WinUI Gallery pages: MaxWidth 1064).</summary>
    public const float MaxContentWidth = 1064f;

    /// <summary>A page on the content layer: page padding 36 / 24 / 36 / 36, one column capped and centred, scrolling.</summary>
    public static Element Scaffold(params Element[] content) => ScrollView(new BoxEl
    {
        Direction = 0,
        Justify = FlexJustify.Center,
        Padding = Spacing.PagePadWide,
        Children =
        [
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = MaxContentWidth, Children = content },
        ],
    }) with { Grow = 1f };

    /// <summary>A section header: Body Strong on the page, 30 above and 6 below (WinUI Gallery SettingsSectionHeader 1,30,0,6).</summary>
    public static Element SectionHeader(string text, Element? trailing = null) => new BoxEl
    {
        Direction = 0,
        AlignItems = FlexAlign.End,
        Margin = new Edges4(1f, 30f, 0f, 6f),
        Children = trailing is null ? [BodyStrong(text)] : [BodyStrong(text), Spacer(), trailing],
    };

    /// <summary>Settings cards stack 4 apart (SettingsCardSpacing = 4).</summary>
    public static Element CardStack(IEnumerable<Element> cards) => new BoxEl { Direction = 1, Gap = Spacing.XS, Children = [.. cards] };
}

/// <summary>
/// One printer, modelled on Windows Settings › Printers &amp; scanners › (printer): the device header with its own
/// picture, status and the one accent action; InfoBars for anything wrong; then setting-card sections for printing
/// options, the queue, ink and the printer's details.
/// </summary>
sealed class PrinterPage : Component
{
    public override Element Render()
    {
        var store = Program.Store;
        var uri = UseProps<UriProps>().Uri;
        var p = store.Fleet.Value.FirstOrDefault(x => x.Uri == uri);
        var queue = store.Queue.Value.Where(q => q.PrinterUri == uri).ToList();
        if (p is null) return Win11Page.Scaffold(Title("Printer removed"));

        var content = new List<Element> { DeviceHeader(p) };
        content.AddRange(Problems(p));

        content.Add(Win11Page.SectionHeader("Print settings"));
        content.Add(Win11Page.CardStack(
        [
            SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Copies",
                HeaderIcon = Glyph.Copies,
                Content = NumberBox.Create(store.Copies, null, new NumberBox.NumberBoxOptions
                {
                    Initial = store.Copies.Peek(),   // the field's text seeds from Initial even when a value signal is passed
                    Minimum = 1, Maximum = 99, SmallChange = 1, LargeChange = 5,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                }),
            }),
            SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Picture size",
                Description = "How pictures fill the page. PDFs always print at their own size.",
                HeaderIcon = Glyph.Resize,
                Content = ComboBox.Create(["Fit to page", "Actual size"], store.PlacementIndex, width: 180f),
            }),
            SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Color",
                HeaderIcon = Glyph.Color,
                IsEnabled = p.Info?.ColorSupported ?? true,
                Content = ComboBox.Create(["Color", "Black and white"], store.ColorIndex, width: 180f),
            }),
        ]));

        bool anyFinished = queue.Any(q => q.IsFinished);
        content.Add(Win11Page.SectionHeader("Print queue",
            anyFinished ? HyperlinkButton.Create("Clear finished", store.ClearFinished) : null));
        content.Add(Win11Page.CardStack(queue.Count == 0
            ? [SettingsCard.Create(new SettingsCard.Options
                {
                    Header = "Nothing is printing",
                    Description = "Drop pictures or PDFs anywhere in this window to print them here.",
                    HeaderIcon = Glyph.Document,
                })]
            : queue.AsEnumerable().Reverse().Select(q => QueueRow(q, store) with { Key = q.Key })));

        if (p.Info is { Supplies.Count: > 0 } withInk)
        {
            content.Add(Win11Page.SectionHeader("Ink"));
            content.Add(Win11Page.CardStack(withInk.Supplies.Select(InkRow)));
        }

        content.Add(Win11Page.SectionHeader("About this printer"));
        content.Add(Win11Page.CardStack(Details(p, store)));

        return Win11Page.Scaffold([.. content]);
    }

    /// <summary>Picture, name, status line, then the actions (one accent button, left-aligned on the page).</summary>
    private static Element DeviceHeader(PrinterState p)
    {
        Element art = p.Image is { } img
            ? Image(img, 96f, 96f, placeholder: ColorF.FromRgba(0, 0, 0, 0)) with { Fit = ImageFit.Contain }
            : new BoxEl
            {
                Width = 96f, Height = 96f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Glyph.Printer, 64f, Tok.TextSecondary)],
            };

        var actions = new List<Element>
        {
            Button.Create("Print files", PrintFiles, ButtonAppearance.Accent, glyph: Glyph.Folder,
                isEnabled: p.Info is not null && p.Error is null),
        };
        if (p.Info?.AdminUrl is { Length: > 0 } admin && admin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            actions.Add(Button.Create("Printer website", () => Open(admin), glyph: Glyph.Open));

        return new BoxEl
        {
            Direction = 0,
            Gap = Spacing.XL,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [art] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        Title(p.DisplayName) with { Wrap = TextWrap.Wrap },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children = [StatusDot(p.Health), Body(StatusLine(p)).Secondary()],
                        },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.M, 0f, 0f),
                            Children = [.. actions],
                        },
                    ],
                },
            ],
        };
    }

    private static string StatusLine(PrinterState p)
    {
        var bits = new List<string> { p.Error is not null ? "Offline" : p.Info is null ? "Connecting…" : Words.Headline(p.Info) };
        bits.Add(p.Ref.Host);
        if (p.Info?.Location is { } loc) bits.Add(loc);
        return string.Join("  ·  ", bits);
    }

    private static Element StatusDot(Health h) => new BoxEl
    {
        Width = 8f, Height = 8f, Shrink = 0f, Corners = Radii.FullAll,
        Fill = h switch
        {
            Health.Ready => Tok.SystemFillSuccess,
            Health.Busy => Tok.AccentDefault,
            Health.Warning => Tok.SystemFillCaution,
            Health.Error => Tok.SystemFillCritical,
            _ => Tok.TextTertiary,
        },
    };

    /// <summary>Status lives in InfoBars at the top of the content: inline, persistent, never floating.</summary>
    private static IEnumerable<Element> Problems(PrinterState p)
    {
        var bars = new List<Element>();
        if (p.Error is not null)
        {
            bars.Add(InfoBar.Create(InfoBarSeverity.Error, "Can't reach this printer", p.Error, isClosable: false));
        }
        else if (p.Info is { } info)
        {
            // One bar per problem that stops printing; every lesser warning folded into a single bar.
            var issues = Words.Issues(info);
            foreach (var (_, text) in issues.Where(i => i.Serious).Take(2))
                bars.Add(InfoBar.Create(InfoBarSeverity.Error, text, "Printing waits until this is fixed.", isClosable: false));
            var warnings = issues.Where(i => !i.Serious).Select(i => i.Text).ToList();
            if (warnings.Count > 0)
                bars.Add(InfoBar.Create(InfoBarSeverity.Warning, warnings[0],
                    warnings.Count > 1 ? string.Join(". ", warnings.Skip(1)) + "." : "", isClosable: false));
        }
        if (bars.Count == 0) return [];
        return [new BoxEl { Direction = 1, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.XXL, 0f, 0f), Children = [.. bars] }];
    }

    private static BoxEl QueueRow(QueueItem q, Store store)
    {
        var (glyph, color) = q.Phase switch
        {
            Phase.Done => (Glyph.Completed, Tok.SystemFillSuccess),
            Phase.Failed => (Glyph.Error, Tok.SystemFillCritical),
            Phase.Stopped => (Glyph.Warning, Tok.SystemFillCaution),
            Phase.Canceled => (Glyph.Cancel, Tok.TextTertiary),
            _ => (q.Path is { } path && Pdf.IsPdf(path) ? Glyph.Pdf : q.Path is { } i && Wic.IsImage(i) ? Glyph.Picture : Glyph.Document, Tok.TextPrimary),
        };
        bool working = q.Phase is Phase.Preparing or Phase.Sending or Phase.Printing;
        var trailing = new List<Element>();
        if (working) trailing.Add(ProgressRing.Create(null, 20f));
        if (q.CanCancel) trailing.Add(ToolTip.Wrap(IconButton.Create(Glyph.Cancel, () => store.Cancel(q)), "Cancel"));

        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                SettingsCard.Create(new SettingsCard.Options
                {
                    Header = q.Mine ? q.Name : $"{q.Name} (from another device)",
                    Description = q.Detail,
                    HeaderIconElement = Icon(glyph, 20f, color),
                    Content = trailing.Count == 0 ? null : new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [.. trailing] },
                }),
            ],
        };
    }

    private static Element InkRow(Supply s)
    {
        string level = s.Percent is { } v ? (v == 0 ? "Empty" : $"{v}%") : s.Level == -3 ? "Some left" : "Unknown";
        string? note = s.Percent switch { 0 => "Replace this cartridge", < 15 => "Running low", _ => null };
        float filled = s.Percent ?? (s.Level == -3 ? 50f : 0f);
        var meter = new BoxEl
        {
            Direction = 0, Width = 160f, Height = 4f, Corners = Radii.FullAll, ClipToBounds = true,
            Fill = Tok.FillControlStrong with { A = 0.25f },
            Children =
            [
                new BoxEl { Grow = filled, Basis = 0f, Corners = Radii.FullAll, Fill = InkColor(s) },
                new BoxEl { Grow = 100f - filled, Basis = 0f },
            ],
        };
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = Words.PrettySupply(s.Name),
            Description = note,
            HeaderIconElement = new BoxEl
            {
                Width = 12f, Height = 12f, AlignSelf = FlexAlign.Center, Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
                Corners = Radii.FullAll, Fill = InkColor(s), BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            },
            Content = new BoxEl
            {
                // Control height (32), like every other card's content, so the header centres the same way.
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Height = 32f,
                Children = [Caption(level) with { MinWidth = 64f, TextAlign = TextAlign.End }, meter],
            },
        });
    }

    private static IEnumerable<Element> Details(PrinterState p, Store store)
    {
        var info = p.Info;
        var rows = new List<Element>
        {
            SettingsExpander.Item("Address", null, Value(p.Ref.Host)),
        };
        if (info is not null)
        {
            var (w, h) = Media.Size(info.MediaDefault);
            rows.Add(SettingsExpander.Item("Model", null, Value(info.Model)));
            if (info.Location is { } loc) rows.Add(SettingsExpander.Item("Location", null, Value(loc)));
            rows.Add(SettingsExpander.Item("Paper", null, Value(
                $"{Media.Label(info.MediaDefault)} ({w.ToString("0.#", CultureInfo.InvariantCulture)} × {h.ToString("0.#", CultureInfo.InvariantCulture)} mm)")));
            rows.Add(SettingsExpander.Item("Resolution", null, Value($"{info.Dpi} dpi")));
            rows.Add(SettingsExpander.Item("Prints", "The formats PrintDesk can send without a driver", Value(Words.FormatList(info))));
            if (info.Firmware is { Length: > 0 } fw) rows.Add(SettingsExpander.Item("Firmware", null, Value(fw)));
        }

        var cards = new List<Element>
        {
            SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = "Printer properties",
                Description = "Model, address, paper and supported formats",
                HeaderIcon = Glyph.Info,
                Items = rows,
            }),
        };
        if (info?.AdminUrl is { Length: > 0 } admin && admin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            cards.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Printer website",
                Description = "Settings, firmware updates and maintenance on the printer itself",
                HeaderIcon = Glyph.Network,
                IsClickEnabled = true,
                OnClick = () => Open(admin),
                ActionIcon = Glyph.Open,
                ActionIconToolTip = "Open in your browser",
            }));
        }
        cards.Add(SettingsCard.Create(new SettingsCard.Options
        {
            Header = "Remove this printer",
            Description = "It comes back if PrintDesk finds it on the network again",
            HeaderIcon = Glyph.Delete,
            Content = Button.Standard("Remove", () => store.Forget(p.Uri)),
        }));
        return cards;
    }

    private static Element Value(string text) => Body(text).Secondary();

    private static ColorF InkColor(Supply s)
    {
        if (s.ColorHex is { Length: 7 } hex && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return ColorF.FromRgba((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var n = s.Name.ToLowerInvariant();
        if (n.Contains("cyan")) return ColorF.FromRgba(0x00, 0xA3, 0xE0);
        if (n.Contains("magenta")) return ColorF.FromRgba(0xE0, 0x00, 0x7A);
        if (n.Contains("yellow")) return ColorF.FromRgba(0xFF, 0xD4, 0x00);
        if (n.Contains("black") || n.Contains("bk")) return ColorF.FromRgba(0x20, 0x20, 0x20);
        return Tok.AccentDefault;
    }

    internal static void PrintFiles()
    {
        var picked = FilePicker.OpenFiles(FluentApp.WindowHandle, "Choose files to print",
            ("Pictures and PDFs", "*.pdf;" + string.Join(";", Wic.ImageExtensions.Select(e => "*" + e))),
            ("All files", "*.*"));
        if (picked.Length > 0) Program.Store.PrintFiles(picked);
    }

    internal static void Open(string url)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>Add a printer: search the network, or add one by address.</summary>
sealed class AddPrinterPage : Component
{
    public override Element Render()
    {
        var store = Program.Store;
        bool searching = store.Discovering.Value;
        var error = store.AddError.Value;
        int count = store.Fleet.Value.Count;
        var address = UseSignal("");

        void Add()
        {
            store.AddAddress(address.Peek());
            if (store.AddError.Peek() is null) address.Value = "";
        }

        var content = new List<Element>
        {
            Title("Add a printer"),
            Body("PrintDesk prints straight to printers that support driverless printing (IPP Everywhere, AirPrint or Mopria). Most network printers made since 2015 do.")
                .Secondary() with { Margin = new Edges4(0f, Spacing.S, 0f, 0f) },
        };
        if (error is not null)
            content.Add(new BoxEl
            {
                Direction = 1, Margin = new Edges4(0f, Spacing.XXL, 0f, 0f),
                Children = [InfoBar.Create(InfoBarSeverity.Error, "Couldn't add that printer", error, onClose: () => store.AddError.Value = null)],
            });

        content.Add(Win11Page.SectionHeader("Find a printer"));
        content.Add(Win11Page.CardStack(
        [
            SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Search your network",
                Description = searching ? "Searching…" : count == 0 ? "No printers found yet" : $"{count} printer{(count == 1 ? "" : "s")} in your list",
                HeaderIcon = Glyph.Search,
                Content = new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
                    Children = searching
                        ? [ProgressRing.Create(null, 20f), Button.Create("Search again", store.Discover, isEnabled: false)]
                        : [Button.Standard("Search again", store.Discover)],
                },
            }),
            SettingsCard.Create(new SettingsCard.Options
            {
                Header = "Add by address",
                Description = "The printer's IP address or host name, for example 192.168.1.20",
                HeaderIcon = Glyph.Network,
                Content = new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        TextBox.Create(address, null, new TextBox.TextBoxOptions { Placeholder = "Address", Width = 200f, OnCommit = _ => Add() }),
                        Button.Standard("Add", Add),
                    ],
                },
            }),
        ]));
        return Win11Page.Scaffold([.. content]);
    }
}
