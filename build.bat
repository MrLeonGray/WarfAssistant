@echo off
echo ====================================================
echo  Сборка WarfAssistant в один exe файл в корне...
echo ====================================================
dotnet publish src\WarfAssistant.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .
if %ERRORLEVEL% EQU 0 (
    echo.
    echo [УСПЕХ] WarfAssistant.exe успешно собран в корне проекта!
) else (
    echo.
    echo [ОШИБКА] При сборке произошла ошибка.
)
echo ====================================================
pause

