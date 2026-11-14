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

The panel is a separate process over a GPU-composited window. On a machine where it cannot run, or when setup is not attached to a console a person can use, it falls back to the console prompts (`--console` forces them). The consent screen offers Install, Update, Repair, Uninstall and Close depending on what is already installed.

### Updating
Download the new package, extract it, and run `VRPC.exe` again. The installer detects the existing version and updates in place. Future versions will have automatic unattended updates.

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
- C# .NET 9 (or higher)

Build the development version:

```
dotnet build
```

Build a release package with the payload manifest and zip:

```
.\packaging\build-release.ps1 -Version 1.10.0
```

The package is written to `artifacts\VRPC-1.10.0-win-x64.zip`. The version comes from `Directory.Build.props` unless `-Version` is supplied like in the example.

## Troubleshooting / IT DOESN'T WORK!!
### Native Messaging Permissions
Please make sure the extension is allowed native-messaging permissions from the browser. Check the registry keys listed above ([Native Messaging Support (Windows)](#native-messaging-support-windows)). If your browser is not listed, look up its native-messaging registry path online and create a `vrpc` key pointing at the manifest (`%LOCALAPPDATA%\Viktir\VRPCApp\manifests`).

### Logs
- Application/runtime logs: `%LOCALAPPDATA%\Viktir\VRPCApp\state\logs`
- Setup/update logs: the same folder, named `setup-*.log`

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