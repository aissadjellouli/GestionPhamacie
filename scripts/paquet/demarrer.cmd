@echo off
rem Lance Pharma Officine (essai) : le serveur, puis le poste de travail.
cd /d "%~dp0"
if not exist "api\appsettings.Production.json" (
    echo L'installation n'est pas faite : lancez d'abord installer.ps1 ^(voir LISEZMOI.txt^).
    pause
    exit /b 1
)
start "Pharma Officine - serveur (ne pas fermer)" /min /d "%~dp0api" "%~dp0api\Pharma.Api.exe"
echo Démarrage du serveur...
powershell -NoProfile -Command "for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest http://localhost:5080/api/sante -UseBasicParsing -TimeoutSec 2 | Out-Null; exit 0 } catch { Start-Sleep -Seconds 1 } }; exit 1"
if errorlevel 1 (
    echo Le serveur ne répond pas. Regardez la fenêtre « Pharma Officine - serveur » pour le message d'erreur.
    pause
    exit /b 1
)
set PHARMA_POSTE=Poste essai
start "" "%~dp0poste\Pharma.Desktop.exe"
