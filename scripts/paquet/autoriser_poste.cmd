@echo off
rem Autorise ce PC comme poste de travail (une seule fois, apres installer.ps1) : affiche le code a saisir dans le logiciel.
cd /d "%~dp0api"
if not exist "appsettings.Production.json" (
    echo L'installation n'est pas faite : lancez d'abord installer.ps1 ^(voir LISEZMOI.txt^).
    pause
    exit /b 1
)
echo Compte du titulaire de demonstration : titulaire, mot de passe : demo
"%~dp0api\Pharma.Api.exe" --autoriser-poste "Poste essai" --identifiant titulaire
echo.
echo Notez le code ci-dessus, lancez demarrer.cmd, puis tapez ce code dans "Autoriser ce poste".
pause
