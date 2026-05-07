# AGENTS.md

## Project Notes

NeeView is a Windows WPF application targeting .NET 10. Prefer existing project patterns and keep changes narrowly scoped.

## Build

Use this command for normal verification:

```powershell
dotnet build NeeView.sln -c Debug -p:Platform=x64
```

If NeeView is currently running, the build may fail because `NeeView.exe` or `NeeView.dll` is locked. Close the running app before rebuilding.

## Mobile Web Viewer

The experimental web host is implemented in:

```text
NeeView/System/WebImageHostService.cs
```

It listens on port `28228` and serves:

- `/`
- `/current.jpg`
- `/command/prev`
- `/command/next`
- `/bookshelf`
- `/bookshelf/open`
- `/bookshelf/nav`
- `/bookshelf/thumb`

The web UI is embedded as a raw string in `WebImageHostService.cs`. Keep the implementation dependency-light and preserve mobile touch behavior when editing it.

## ngrok QR Command

The ngrok QR code command is implemented in:

```text
NeeView/Command/Commands/ShowNgrokTunnelQrCodeCommand.cs
```

It reads the current tunnel from:

```text
http://127.0.0.1:4040/api/tunnels
```

The QR code is generated with the `QRCoder` NuGet package.

## Git

Do not revert unrelated local changes. Commit focused changes with clear messages, and verify the working tree before pushing.
