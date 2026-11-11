@echo off
rem Compila el motor de dano de Faro y lo deja en ..\aion2-overlay\motor (un .exe + carpeta Data).
rem Requiere el SDK de .NET 10:  winget install Microsoft.DotNet.SDK.10
rem Cerra Faro antes de compilar (el .exe no se puede reemplazar mientras corre).
cd /d "%~dp0Aion2DPSMeter-main"
dotnet publish src\Aion2DpsMeter.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "%~dp0..\aion2-overlay\motor"
if errorlevel 1 (
  echo.
  echo *** La compilacion fallo. Copia el error de arriba y pasaselo a Claude. ***
) else (
  echo.
  echo Listo: motor actualizado en aion2-overlay\motor
)
pause
