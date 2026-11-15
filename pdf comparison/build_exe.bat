@echo off
chcp 65001 > nul
echo PDFDiffStudio.exe をビルドしています...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:PDFDiffStudio.exe /resource:index.html,index.html /reference:System.Windows.Forms.dll /reference:System.Drawing.dll Program.cs
if %ERRORLEVEL% EQU 0 (
    echo ビルド成功: PDFDiffStudio.exe
) else (
    echo ビルド失敗
)
