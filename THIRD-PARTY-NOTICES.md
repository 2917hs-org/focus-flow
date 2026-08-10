# Third-party notices

FocusFlow is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)). It's built on
open-source packages, listed below by what they're used for. Test-only packages never ship
in a built `.app`/`.exe`; everything else does, since `FocusFlow.App` publishes
self-contained.

This list is compiled from each project's own published licensing and kept in sync with
`PackageReference` versions in the `.csproj` files by hand — it's offered in good faith, not
as a legal audit. If a version bump changes a license, this file can lag until someone
notices; check the package's own repository if it matters for your use.

## Shipped in the built app

| Package | Version | License | Used for |
|---|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | 12.1.1 | MIT | UI framework |
| [Avalonia.Desktop](https://github.com/AvaloniaUI/Avalonia) | 12.1.1 | MIT | Desktop windowing backend |
| [Avalonia.Themes.Fluent](https://github.com/AvaloniaUI/Avalonia) | 12.1.1 | MIT | Base control theme |
| [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) | 12.1.1 | MIT (packaging code); bundled [Inter](https://github.com/rsms/inter) typeface under [SIL OFL 1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt) | Default UI font on Windows — macOS uses the system font instead, see [Platform implementations](#platform-implementations) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVM (`ObservableObject`, `[RelayCommand]`) |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | 11.0.0-preview.6 | MIT | The composition root in `App.axaml.cs` |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | 7.1.3 | MIT | Windows toast notifications (Windows build only) |
| [System.Drawing.Common](https://github.com/dotnet/runtime) | 9.0.0 | MIT | Pinned only to patch a transitive advisory from the notifications package (Windows build only); nothing in FocusFlow uses `System.Drawing` directly |
| System.Text.Json | (part of the .NET runtime) | MIT | Reading/writing `config.json`, `session.json`, `history.jsonl` |

`AvaloniaUI.DiagnosticsSupport` (MIT) is referenced but excluded from Release builds — it's
a Debug-only developer tool and never ships.

## Development and test only — not shipped

| Package | License |
|---|---|
| [xunit](https://github.com/xunit/xunit) / xunit.runner.visualstudio | Apache 2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |
| [Microsoft.Extensions.TimeProvider.Testing](https://github.com/dotnet/extensions) | MIT |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | MIT |
