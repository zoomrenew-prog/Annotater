@echo off
chcp 65001 >nul
setlocal

set "PROJECT_DIR=%~dp0"
set "OUTPUT_DIR=%PROJECT_DIR%publish"

echo ========================================
echo  Annotater — Сборка для развертывания
echo ========================================
echo.

:: Очистка предыдущей сборки
if exist "%OUTPUT_DIR%" (
    echo Удаление предыдущей сборки...
    rmdir /s /q "%OUTPUT_DIR%"
)

echo Публикация (self-contained, win-x64)...
echo.

dotnet publish "%PROJECT_DIR%Annotater.csproj" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%OUTPUT_DIR%"

if %errorlevel% neq 0 (
    echo.
    echo ОШИБКА: Сборка завершилась с ошибкой.
    pause
    exit /b 1
)

:: Удаление ненужных файлов для уменьшения размера
echo.
echo Очистка лишних файлов...
if exist "%OUTPUT_DIR%\libvlc\win-x86" rmdir /s /q "%OUTPUT_DIR%\libvlc\win-x86"
del /q "%OUTPUT_DIR%\libvlc\win-x64\libvlc.lib" 2>nul
del /q "%OUTPUT_DIR%\libvlc\win-x64\libvlccore.lib" 2>nul
if exist "%OUTPUT_DIR%\libvlc\win-x64\lua" rmdir /s /q "%OUTPUT_DIR%\libvlc\win-x64\lua"
if exist "%OUTPUT_DIR%\libvlc\win-x64\hrtfs" rmdir /s /q "%OUTPUT_DIR%\libvlc\win-x64\hrtfs"
del /q "%OUTPUT_DIR%\*.pdb" 2>nul

echo.
echo ========================================
echo  Готово!
echo  Папка: %OUTPUT_DIR%
echo.
echo  Скопируйте всю папку publish на целевую
echo  машину и запустите Annotater.exe
echo  (установка .NET не требуется)
echo ========================================
echo.
pause
