# Citrium

A better Windows app for your Citrix desktops and apps.

Citrium replaces the Citrix Workspace interface with a native Windows 11 app. Citrix still handles the connection, so your sign-in, policies, and HDX features work as usual.

## Why use it

- **Picture in picture.** Keep a live view of your desktop in a small window on top of everything else, without changing the remote desktop's resolution.
- **Session view.** See every running session in one place, including ones started outside Citrium. Reopen or disconnect any of them.
- **Send to Citrium.** Put a desktop away without disconnecting it, then reopen it from Sessions.
- **A full-screen toolbar that stays out of the way.** Dock it to any screen edge or float it anywhere.
- **All your stores in one app.** Add multiple accounts, search everything with Ctrl+E, and pin your favorites.
- **Viewer settings in the session.** Change scaling, audio, the microphone, and where Windows shortcuts go without leaving your desktop.

## Requirements

- Windows 10 version 2004 or later, or Windows 11 (x64 or ARM64)
- [Citrix Workspace app](https://www.citrix.com/downloads/workspace-app/), installed but not used directly
- A Citrix StoreFront store or Citrix Gateway address

## Build

You need the .NET 10 SDK.

```powershell
dotnet publish src/Citrium/Citrium.csproj -c Release -p:Platform=x64 -r win-x64 -o artifacts/Citrium-win-x64
```

Then run `artifacts/Citrium-win-x64/Citrium.exe`. For ARM64, use `-p:Platform=ARM64 -r win-arm64`.

## Known limitations

- Picture in picture is view-only. Go back to the full window to use the desktop.
- Stores on the newer Citrix Cloud platform (`*.cloud.com`) only work if they still expose the classic StoreFront endpoints.
- Favorites are stored locally and don't sync with your store.
- Disconnect a desktop in Citrium before opening it in Citrix's own viewer, or Citrium will take it over.

## License

[Apache License 2.0](LICENSE)

Citrium is an independent project and isn't affiliated with or endorsed by Citrix. Citrix and related names are trademarks of their respective owners.
