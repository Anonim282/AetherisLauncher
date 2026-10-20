@echo off
chcp 65001 > nul
echo ========================================================
echo   Сборка автономного AetherisLauncher.exe (Single-File)
echo ========================================================
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./
if %ERRORLEVEL% equ 0 (
    echo.
    echo [УСПЕХ] Файл AetherisLauncher.exe успешно собран в корне проекта!
) else (
    echo.
    echo [ОШИБКА] Сборка завершилась с ошибкой!
)
pause
