# PrintDesk

Driverless printing for Windows 11, built on [FluentGpu](https://github.com/christosk92/fluent-gpu).

PrintDesk prints pictures and PDFs straight to network printers that speak IPP (IPP Everywhere, AirPrint, Mopria),
so no vendor driver is needed. That makes it work on Windows on ARM, where many printer drivers don't install.

- Finds printers on your network (DNS-SD / mDNS `_ipp._tcp`), or add one by IP address or host name.
- Shows each printer with its own picture (from its IPP `printer-icons`), status, problems, ink levels and paper.
- Drop files anywhere in the window, or choose them: pictures (JPEG, PNG, GIF, BMP, TIFF, HEIC, WebP) are laid out on
  the page with Windows Imaging Component; PDFs are rendered with Windows' own PDF renderer (`Windows.Data.Pdf`).
  Pages go to the printer as JPEG or PWG raster. Other file types are handed to Windows' print verb.
- A print queue per printer: progress, cancel, and jobs other devices send.

## Build

PrintDesk references the FluentGpu engine checkout by path. By default it expects the engine at
`..\fgpu\fluent-gpu\` next to this folder; point `EngineRoot` elsewhere with a git-ignored `EngineRoot.local.props`:

```xml
<Project><PropertyGroup><EngineRoot>D:\src\fluent-gpu\</EngineRoot></PropertyGroup></Project>
```

```powershell
dotnet build src/PrintDesk.csproj -c Debug
dotnet publish src/PrintDesk.csproj -c Release   # NativeAOT exe for this machine's architecture
```

Command line: files named on the command line print on start (`PrintDesk.exe label.png`); `--printer <address>` adds
a printer. Layout checks: `--shot <png> [--light|--dark] [--w <dip>] [--h <dip>] [--frames <n>]` renders to a PNG and
exits (it uses separate settings, so test printers never land in your list); `--save-to <folder>` writes what would be
sent to the printer instead of sending it.

## Layout

| Folder | What |
|---|---|
| `src/Ipp` | IPP messages and client (Get-Printer-Attributes, Get-Jobs, Print-Job, Cancel-Job), mDNS discovery |
| `src/Imaging` | page layout, WIC decode and JPEG encode, PWG raster encoder, PDF rendering |
| `src/State` | the store (printers, polling, queue, print pipeline) and plain-language status text |
| `src/UI` | the Windows 11 shell (title bar, navigation pane) and pages |
