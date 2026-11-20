# TapoCam for Windows

TapoCam is a Windows desktop application for local RTSP cameras. The repository currently contains a portable .NET 8 core library, a WPF shell, and xUnit tests for the core behavior.

## Build and test

```powershell
dotnet test TapoCam.sln
dotnet build src/TapoCam.App/TapoCam.App.csproj
```

The app project targets Windows; its WPF UI requires Windows to run. The core library and its tests are cross-platform.
