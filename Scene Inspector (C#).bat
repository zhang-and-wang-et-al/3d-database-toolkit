@echo off
rem Scene inspector, C# / WPF version. Builds it on the first start (needs the .NET 8 SDK:
rem https://dotnet.microsoft.com/download/dotnet/8.0), then opens the window.
rem Drag a scene file onto this .bat or:  "Scene Inspector (C#).bat" city.flt
setlocal
set "EXE=%~dp0csharp\SceneInspector.Wpf\bin\Release\net8.0-windows\SceneInspector.exe"
if not exist "%EXE%" (
  where dotnet >nul 2>nul || ( echo The .NET 8 SDK is needed: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1 )
  echo Building the scene inspector ^(first start only^) ...
  dotnet build "%~dp0csharp\SceneInspector.Wpf\SceneInspector.Wpf.csproj" -c Release -nologo -v q || ( pause & exit /b 1 )
)
start "" "%EXE%" %*
