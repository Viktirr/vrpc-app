# VRPC-APP  
This application sends Rich Presence data from [vrpc-extension](https://github.com/Viktirr/vrpc-extension) to Discord, showing whether you're listening to YouTube Music or Soundcloud (TBD).    
  
Understanding the nature of security, I allow viewing the source code.    
  
Please note that this project is NOT open source, I allow viewing the code, however you're not able to modify it. You're allowed to fork the project instead and create your own version.

The code quality should match the industry standard for newer projects.

## Installation
I do not have an installation script/program yet, so installation will be manual (for now...)
Due to the nature of how Native Messaging works in browsers, you are required to modify the registry key to allow the app to work.

### Application
- [Download](https://github.com/Viktirr/vrpc-app/releases) the application from the releases tab or build it yourself.
- Unzip it

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
You may build the application by using:  
`dotnet build`  
This builds the debug version of the application and I have only used this so far, you may try using  
`dotnet build --configuration Release`  
for a release build, however no major difference will be visible.  
