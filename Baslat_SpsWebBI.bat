@echo off
chcp 65001 >nul
title SpsWebBI Web Sunucusu (Port: 5180)
cd /d "c:\Projeler\SpsWebBI"
echo ====================================================================
echo   SpsWebBI Web Host Uygulamasi Baslatiliyor...
echo   Adres: http://localhost:5180
echo   Kapatmak icin bu pencereyi kapatabilir veya Ctrl+C basabilirsiniz.
echo ====================================================================
dotnet run --project "c:\Projeler\SpsWebBI\SpsWebBI.csproj" --urls http://0.0.0.0:5180
pause

