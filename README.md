# VRPC-APP  
This application sends Rich Presence data from [vrpc-extension](https://github.com/Viktirr/vrpc-extension) to Discord, showing whether you're listening to YouTube Music or Soundcloud.    
  
Understanding the nature of security, I allow viewing the source code.    
  
Please note that this project is NOT open source, I allow viewing the code, however you're not able to modify it.

The code quality should match the industry standard for newer projects ( /s ).

## Installation
Installation is now one-click! Just run the application and click install. No administration privileges are required.  
Otherwise, if you feel to manually install the application, feel free to follow the following steps.

### Application
- [Download](https://github.com/Viktirr/vrpc-app/releases) the (full-zipped version) application from the releases tab or build it yourself.
- Unzip it
- Run it (VRPC.exe)

### Native Messaging Support Installation
- Open regedit and head over to one of the following locations depending on the browser you use,
for Firefox, the registry key is:
`HKEY_LOCAL_MACHINE\SOFTWARE\Mozilla\NativeMessagingHosts`
- Create a new key (folder) inside NativeMessagingHosts named vrpc
- Under (Default) paste the path of [vrpc.json](https://github.com/Viktirr/vrpc-app/blob/main/NativeMessagingJson/vrpc.json) (Download this file locally, you'll need to modify it)
- Inside of vrpc.json, paste the path of VRPC.exe, make sure there are double backslashes.

## Building
Prerequisites:
- C# .NET 8 (or higher)  
You may build the application by using one of the following:  
`dotnet build`  
This builds the debug version of the application.
  
`dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=false`  
This builds the release (full-zipped) version of the application. Zipping is done manually.  
  
`dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`  
This builds the release (single-file application - VRPC.exe) version of the application.  
  
`dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=false`  
This builds the release (minimal) version of the application. Zipping is done manually.  

## Troubleshooting / IT DOESN'T WORK!!
### Native Messaging Permissions
Please make sure the script is allowed native messaging permissions from the browser. Check the following registry keys:  
Firefox: `HKEY_CURRENT_USER\SOFTWARE\Mozilla\NativeMessagingHosts`  
Chrome: `HKEY_CURRENT_USER\SOFTWARE\Google\Chrome\NativeMessagingHosts`  
Edge: `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Edge\NativeMessagingHosts`  
If your browser is not listed here, please look online for "NativeMessaging (your browser name here)"  
You should create a key with the name of the extension and on the (Default) value, select the path to the manifest file available [here](https://github.com/Viktirr/vrpc-app/blob/main/NativeMessagingJson/vrpc.json).  

### Missing dependencies
I'm not sure if this is required, however if needed, you may install the  
[.NET Desktop Runtime/.NET SDK](https://dotnet.microsoft.com/en-us/download)

### Double-check Paths/Directories
Make sure every directory is rightly listed. If you need to change the directory of the app, I recommend reinstalling it.  

If you installed the application manually, please double check the vrpc.json file that's connected to the NativeMessagingHosts registry key. If it exists, check if the path to the application exists, make sure there are double slashes in vrpc.json.

### Reinstalling
Uninstalling and installing the software again may help.

### Crashes
It can also be that this is not a problem of user error, so there might be possible crashes from the application. If you encounter such, feel free to create an Issue (I'm not sure if that works, if it doesn't, feel free to contact me via email at viktir@skiff.com instead).

## Software used
This project wouldn't have been possible without the following projects/software:  
- C# .NET
- [Discord RPC C#](https://github.com/Lachee/discord-rpc-csharp)
- Newtonsoft.json
- GTK#
