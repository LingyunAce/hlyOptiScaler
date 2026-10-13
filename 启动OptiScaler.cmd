@echo off
setlocal

set "APP_DIR=%~dp0tools\OptiScalerDeployer\src\Deployer.App\bin\Release\net8.0-windows"
call :CheckPackage "%APP_DIR%"
if not errorlevel 1 goto Launch

set "APP_DIR=%~dp0tools\OptiScalerDeployer\artifacts\publish"
call :CheckPackage "%APP_DIR%"
if not errorlevel 1 goto Launch

set "APP_DIR=%~dp0tools\OptiScalerDeployer\artifacts\payload-rate-limit-fix-20261013\verified-release"
call :CheckPackage "%APP_DIR%"
if not errorlevel 1 goto Launch

echo Cannot find a complete OptiScaler Deployer Release directory.
echo Build it from the repository root with:
echo   dotnet build .\tools\OptiScalerDeployer\OptiScalerDeployer.sln -c Release
exit /b 1

:Launch
start "" /D "%APP_DIR%" "%APP_DIR%\opti-deploy-app.exe"
exit /b 0

:CheckPackage
if not exist "%~1\opti-deploy-app.exe" exit /b 1
if not exist "%~1\opti-deploy-app.dll" exit /b 1
if not exist "%~1\opti-deploy-app.deps.json" exit /b 1
if not exist "%~1\opti-deploy-app.runtimeconfig.json" exit /b 1
if not exist "%~1\worker\Deployer.Worker.exe" exit /b 1
if not exist "%~1\worker\Deployer.Worker.dll" exit /b 1
if not exist "%~1\worker\Deployer.Worker.deps.json" exit /b 1
if not exist "%~1\worker\Deployer.Worker.runtimeconfig.json" exit /b 1
if not exist "%~1\worker\Deployer.Core.dll" exit /b 1
exit /b 0
