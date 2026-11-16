using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using PrintDesk.State;
using static FluentGpu.Dsl.Ui;

namespace PrintDesk;

/// <summary>The window root: an overlay host (combo box popups, tooltips, dialogs) around the shell.</summary>
sealed class App : Component
{
    public override Element Render() => OverlayHost.Create(Embed.Comp(() => new Shell()));
}

/// <summary>
/// The Windows 11 shell: the tall title bar and the navigation pane sit on Mica; the page sits on the NavigationView's
/// content layer (LayerFillColorDefault, 1px card stroke on top and left, 8px top-left corner). Files dropped anywhere
/// print on the selected printer.
/// </summary>
sealed class Shell : Component
{
    public override Element Render()
    {
        var post = UsePost();
        var store = Program.Store;
        var paneToggle = UseSignal(0);
        UseEffect(() =>
        {
            store.Start(post, Program.PrintAtStart, Program.AddPrinters);
            // A --shot capture reads the back buffer, not the screen. Off-screen, no other window can cover it, and a
            // covered window is parked by the engine (no frames, timers or updates), which froze earlier captures.
            if (Program.IsShot) FluentGpu.FluentApp.MoveWindowPx(-20000f, -20000f);
        });
        UseInterval(store.Tick, 2500f);

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Children =
            [
                TitleBar.Create(new TitleBarOptions
                {
                    Title = "PrintDesk",
                    IconGlyph = Glyph.Printer,
                    ShowPaneToggle = true,
                    OnPaneToggle = () => paneToggle.Update(n => n + 1),
                }),
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Basis = 0f,
                    MinHeight = 0f,
                    Children =
                    [
                        DropZone.Window([DropKinds.Files], s =>
                        {
                            if (s.Payload is FileDropData d) store.PrintFiles(d.Paths);
                        }, Embed.Comp(new NavProps(paneToggle), () => new Navigation())),
                    ],
                },
            ],
        };
    }
}

sealed record NavProps(Signal<int> PaneToggle);
sealed record UriProps(string Uri);

/// <summary>
/// The navigation pane: one item per printer (its own picture as the icon, a badge when it needs attention) and
/// "Add a printer" in the footer. The NavigationView's items are fixed at mount, so it is keyed on the printer list,
/// names and badge state: those changes remount it; the icon is a live component and follows on its own.
/// </summary>
sealed class Navigation : Component
{
    public const string AddKey = "add";

    public override Element Render()
    {
        var store = Program.Store;
        var props = UseProps<NavProps>();
        var fleet = store.Fleet.Value;
        // The key carries what the frozen items show: names and badge state (the icon is a live component).
        string listKey = "nav|" + string.Join("|", fleet.Select(p => $"{p.Uri}={p.DisplayName}={BadgeOf(p)}"));

        var items = new List<NavItem>();
        if (fleet.Count > 0) items.Add(new NavItem("printers", "", "Printers", IsHeader: true));
        foreach (var p in fleet)
        {
            items.Add(new NavItem(p.Uri, Glyph.Printer, p.DisplayName)
            {
                IconContent = Embed.Comp(new UriProps(p.Uri), () => new NavIcon()),
                // A plain element, not a component: a component in the InfoBadge slot blanks the whole pane (engine
                // bug, recorded in the win11-design skill's pitfalls).
                InfoBadge = BadgeOf(p) is { } severity ? InfoBadge.Dot(severity) : null,
            });
        }

        var nav = NavigationView.Create(new NavigationViewOptions
        {
            Items = [.. items],
            Footer = [new NavItem(AddKey, Glyph.Add, "Add a printer")],
            Initial = store.CurrentUri.Peek() ?? AddKey,
            OnSelect = key => { if (key != AddKey) store.Select(key); },
            Content = key => key == AddKey
                ? Embed.Comp(() => new AddPrinterPage())
                : Embed.Comp(new UriProps(key), () => new PrinterPage()),
            ShowPaneToggle = false,          // the title bar owns the toggle (WinUI Gallery pattern)
            PaneToggleRequest = props.PaneToggle,
        });

        return new BoxEl { Direction = 1, Grow = 1f, Children = [nav with { Key = listKey }] };
    }

    /// <summary>The nav item's badge: red when the printer needs attention, amber for warnings, none otherwise.</summary>
    private static InfoBadgeSeverity? BadgeOf(PrinterState p) => p.Health switch
    {
        Health.Error => InfoBadgeSeverity.Critical,
        Health.Warning => InfoBadgeSeverity.Caution,
        _ => null,
    };
}

/// <summary>A printer's own picture as its nav icon (20 px), or the printer glyph until one is known.</summary>
sealed class NavIcon : Component
{
    public override Element Render()
    {
        var uri = UseProps<UriProps>().Uri;
        var p = Program.Store.Fleet.Value.FirstOrDefault(x => x.Uri == uri);
        return p?.Image is { } img
            ? Image(img, 20f, 20f, placeholder: ColorF.FromRgba(0, 0, 0, 0)) with { Fit = ImageFit.Contain }
            : Icon(Glyph.Printer, 16f);
    }
}

/// <summary>Segoe Fluent Icons code points used by the app.</summary>
static class Glyph
{
    public const string Printer = "";
    public const string Add = "";
    public const string Pdf = "";
    public const string Picture = "";
    public const string Document = "";
    public const string Completed = "";
    public const string Error = "";
    public const string Warning = "";
    public const string Cancel = "";
    public const string Copies = "";
    public const string Resize = "";
    public const string Color = "";
    public const string Open = "";
    public const string Delete = "";
    public const string Search = "";
    public const string Network = "";
    public const string Info = "";
    public const string Folder = "";
}
