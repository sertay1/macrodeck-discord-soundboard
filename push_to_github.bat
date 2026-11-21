@echo off
cd /d "%~dp0"
echo ========================================================
echo  GitHub'a gonderiliyor: sertay1/macrodeck-discord-soundboard
echo ========================================================
git push origin main
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [BILGI] Push reddedildi, uzak degisiklikler birlestirilip tekrar deneniyor...
    git pull --rebase origin main
    git push origin main
)
echo.
pause
