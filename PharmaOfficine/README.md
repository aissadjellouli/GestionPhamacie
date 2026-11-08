# Pharma Officine

Logiciel de gestion d’officine pour les pharmacies algériennes : caisse, stock par lot, alertes de péremption, traçabilité de chaque boîte du grossiste au patient, contrôle de caisse et reprise des données de l’ancien logiciel. Il fonctionne **sans cloud**, sur le réseau local de la pharmacie, et cohabite avec Chifa_Officine pour le tiers payant.

## Structure du dépôt

| Dossier | Contenu |
| --- | --- |
| `src/Pharma.Domain` | Entités, statuts typés et règles métier (FEFO, péremption, crédit, EAN-13, Data Matrix GS1) |
| `src/Pharma.Application` | Cas d’usage par fonctionnalité et ports (interfaces) qu’ils utilisent |
| `src/Pharma.Infrastructure` | Adaptateurs : PostgreSQL (Npgsql + EF Core), sécurité, impression, Chifa |
| `src/Pharma.Api` | API ASP.NET Core locale, authentification JWT, droits vérifiés en base, tâche des alertes |
| `src/Pharma.Desktop` | Poste client WPF + Blazor (BlazorWebView) |
| `src/Pharma.Shared` | Contrat HTTP (DTO) entre l’API et les postes |
| `tests/Pharma.Domain.Tests` | Tests unitaires du domaine (xUnit) |
| `tests/Pharma.Application.Tests` | Tests des cas d’usage, avec doublures |
| `tests/Pharma.Architecture.Tests` | Sens des dépendances entre couches, mapping EF |
| `database/` | Scripts SQL numérotés, source de vérité du schéma |
| `database/demo/` | Données fictives pour le développement et les tests |
| `scripts/` | Initialisation de la base locale, sauvegarde quotidienne |
| `docs/` | Documentation et maquettes |

## Prérequis

- SDK .NET 10
- PostgreSQL 17 ou 18 installé en local (avec `psql`)
- Runtime WebView2 (présent sur Windows 10/11 à jour)
- Visual Studio 2022 (`PharmaOfficine.sln`), ou Visual Studio 2022 17.14+ / VS Code avec C# Dev Kit (`PharmaOfficine.slnx`). Les deux solutions contiennent les mêmes projets : préciser le fichier dans les commandes `dotnet`.

Procédure détaillée (installation de PostgreSQL, création des bases, lancement depuis Visual Studio, problèmes connus) : [docs/poste-de-developpement.md](docs/poste-de-developpement.md).

## Démarrage rapide

```powershell
cd C:\MesDev\PharmaOfficine

# 1. Créer les bases locales pharmacie_dev et pharmacie_test avec les données fictives
#    (PostgreSQL 18 : ajouter -PgBin "C:\Program Files\PostgreSQL\18\bin")
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
$env:PGCLIENTENCODING = "UTF8"
.\scripts\initialiser_base_locale.ps1 -Recreer

# 2. Compiler et tester
dotnet build PharmaOfficine.slnx
dotnet test PharmaOfficine.slnx

# 3. Lancer l'API (http://localhost:5080)
dotnet run --project src\Pharma.Api

# 4. Dans un autre terminal, lancer le poste client
dotnet run --project src\Pharma.Desktop

# Ou dans Visual Studio : profil « API + Poste client » (PharmaOfficine.slnLaunch), puis F5
```

Comptes de démonstration (mot de passe `demo`) : `titulaire`, `assistant`, `vendeur`.

Vérification rapide de l’API : `GET http://localhost:5080/api/sante`.

## Documentation

- [Architecture](docs/architecture.md)
- [Base de données](docs/base-de-donnees.md)
- [Fonctionnalités et état d’avancement](docs/fonctionnalites.md)
- [Traçabilité des boîtes](docs/tracabilite.md)
- [Chifa et cadre réglementaire](docs/chifa.md)
- [Sécurité et droits](docs/securite-et-droits.md)
- [Mise en production et reprise de données](docs/mise-en-production.md)
- [Feuille de route](docs/feuille-de-route.md)
- [Décisions d’architecture](docs/adr/)
- [Maquettes des écrans](docs/maquettes/README.md)

## Données

Aucune donnée réelle de patient dans ce dépôt, ni dans les tickets, ni dans les tests. Les données de démonstration sont fictives.
