@echo off
rem Build MyMemo.exe with the C# compiler that ships with Windows (.NET Framework 4).
rem lib\ holds Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll (NuGet Microsoft.Web.WebView2 1.0.2903.40, lib\net462)
rem and WebView2Loader.dll (runtimes\win-x64\native).
cd /d "%~dp0"
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /win32icon:app.ico /out:bin\MyMemo.exe ^
  /r:lib\Microsoft.Web.WebView2.Core.dll /r:lib\Microsoft.Web.WebView2.WinForms.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll MyMemo.cs || exit /b 1
copy /y lib\*.dll bin\ >nul
copy /y app.ico bin\ >nul
echo Build OK: bin\MyMemo.exe
