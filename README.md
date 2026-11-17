# VRPC-APP  
This application sends Rich Presence data from [vrpc-extension](https://github.com/Viktirr/vrpc-extension) to Discord, showing whether you're listening to YouTube Music or Soundcloud.    
  
Understanding the nature of security, I allow viewing the source code.    
  
Please note that this project is NOT open source, I allow viewing the code, however you're not able to modify it.

## Installation
### Windows
- [Download](https://github.com/Viktirr/vrpc-app/releases) the Windows package from the releases tab or build it yourself.
- Unzip it.
- Run `VRPC.exe` and follow the setup panel. No administration privileges are required.

The installer puts the application in `%LOCALAPPDATA%\Viktir\VRPCApp`.

The panel is a separate process over a GPU-composited window. On a machine where it cannot run, or when setup is not attached to a console a person can use, it falls back to the console prompts (`--console` forces them, `--panel` shows the panel even when the console is redirected). The consent screen offers Install, Update, Repair, Uninstall and Close depending on what is already installed.

### Updating
From 1.1.0 VRPC checks a signed release feed in the background and downloads a newer version automatically; the extension popup offers **Update now**, and the downloaded package is verified (signature, exact size, SHA-256, every payload file hash) before the setup panel is started. You still approve the update, and the running app is only closed after the verified setup starts. Settings and listening history are kept.

Installing 1.1.0 once by hand is required: older versions do not have the updater code. Download the package, extract it, and run `VRPC.exe` again; the installer detects the existing version and updates in place. After that, newer releases can arrive through the updater.

### Uninstalling
Settings > Apps > Installed apps > VRPC > Uninstall. By default it keeps your settings and listening history intact.

If you want to remove your data too, delete `%LOCALAPPDATA%\Viktir\VRPCApp\data` manually. (soon you'll be able to choose)

### Linux
- [Download](https://github.com/Viktirr/vrpc-app/releases) the Linux package from the releases tab or build it yourself.
- Unzip it.
- Run `./install-linux.sh`

The Linux script installs to `~/.local/share/Viktir/VRPCApp`, detects native and Flatpak browsers, and writes one manifest per browser. To update, run `install-linux.sh` again. To uninstall, run `./install-linux.sh uninstall`.

## Native Messaging Support (Windows)
The installer writes the native-messaging registration under `HKEY_CURRENT_USER`.

If you installed manually or need to troubleshoot, check these per-user keys:

- Firefox: `HKEY_CURRENT_USER\SOFTWARE\Mozilla\NativeMessagingHosts\vrpc`
- Chrome: `HKEY_CURRENT_USER\SOFTWARE\Google\Chrome\NativeMessagingHosts\vrpc`
- Edge: `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\vrpc`

The value should point at a manifest in `%LOCALAPPDATA%\Viktir\VRPCApp\manifests`, and that manifest's `path` value should point at `%LOCALAPPDATA%\Viktir\VRPCApp\app\VRPC.exe`.

## Building
Prerequisites:
- .NET 9 SDK or newer
- Rust with Cargo (the setup panel is a separate Rust process, and the release script builds it)
- Node.js 20+ for the update-signing/release scripts
- Windows for the packaged panel; the Linux publish ships without it

Build the development version (application only, no panel):

```
dotnet build
```

Build the release package — application, setup panel, payload manifest and zip:

```
.\packaging\build-release.ps1 -Version 1.1.0
```

If your execution policy blocks unsigned scripts, run it through a bypass:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 -Version 1.1.0
```

The script publishes self-contained `win-x64`, runs `cargo build --release --locked` in `installer-ui/`, copies `vrpc-installer-ui.exe` into the package before `payload.json` is hashed, and zips the result:

- `artifacts\VRPC-<version>-win-x64\` — the package; extract it and run `VRPC.exe`
- `artifacts\VRPC-<version>-win-x64.zip`

The version comes from `Directory.Build.props` unless `-Version` is supplied like in the example. The first panel build fetches the pinned framework revision, so it needs network access; if the panel cannot be built, the release fails instead of shipping a console-only installer. The panel can also be built on its own with `cargo build --release --locked` in `installer-ui/` (see its README).

Run the test suite (xunit, including updater trust and installer-recovery tests):

```
dotnet test VRPC.sln
```

Sign the release package and prepare/verify the update feed:

```
.\packaging\publish-update.ps1 -Version 1.1.0 -KeyId vrpc-updates-2026-11 `
    -KeyPath "$env:USERPROFILE\.vrpc-release-keys\vrpc-updates-2026-11.private.pem" -DryRun
```

The private signing key lives outside the repository. `-DryRun` only builds, hashes, signs and verifies locally; add `-PublishGitHub -GitHubToken $env:GH_TOKEN` to create/upload/verify the GitHub release, and `-DeployFeed` to write the signed manifest into the website/API repository (the only step that publishes the feed). Never rebuild the ZIP after it has been uploaded — the signed metadata binds its exact size and SHA-256.

Development install (Debug package, updates the installed application in place):

```
python qd.py
```

Linux publish:

```
dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=false
```

## Troubleshooting / IT DOESN'T WORK!!
### Native Messaging Permissions
Please make sure the extension is allowed native-messaging permissions from the browser. Check the registry keys listed above ([Native Messaging Support (Windows)](#native-messaging-support-windows)). If your browser is not listed, look up its native-messaging registry path online and create a `vrpc` key pointing at the manifest (`%LOCALAPPDATA%\Viktir\VRPCApp\manifests`).

### Logs
- Application/runtime logs: `%LOCALAPPDATA%\Viktir\VRPCApp\state\logs`
- Setup/update logs: the same folder, named `setup-*.log`
- Auto-updater state: `%LOCALAPPDATA%\Viktir\VRPCApp\state\updater-state.json`; update launch output: `state\logs\update-*.log` and `state\logs\update-setup-console-*.log`

### Missing dependencies
The release build is self-contained. A development build requires the .NET 9 runtime.  

If you are using an older `minimal` build, please switch to a `full` build, relaunching the installer works fine.

### Double-check Paths/Directories
Make sure the manifest path exists and that `VRPC.exe` is in the `app` folder. If you moved the installation manually, reinstall it rather than editing the registry.

### Reinstalling
Run the latest package again and choose repair/reinstall, or uninstall and reinstall if the installation is damaged.

### Crashes
If you encounter a crash, create an Issue or contact viktir@uninbox.me with the setup/runtime log.

## Software used
This project wouldn't have been possible without the following projects/software:  
- C# .NET
- [Discord RPC C#](https://github.com/Lachee/discord-rpc-csharp)
- Newtonsoft.json