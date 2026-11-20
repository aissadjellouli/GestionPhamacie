# syntax=docker/dockerfile:1
# =====================================================================
# Image de l'API PharmaOfficine (démo cloud, étape 1) : docs/demo-cloud.md
#
# Construction depuis la RACINE du dépôt (contexte = racine, filtré par .dockerignore) :
#   docker build -f src/Pharma.Api/Dockerfile -t pharmaofficine-api:demo-local .
# ou par deploy/demo/docker-compose.yml.
#
# Aucun secret dans l'image : chaîne de connexion (ConnectionStrings__Pharmacie) et clé des jetons (Jwt__Cle)
# sont fournies au lancement par des variables d'environnement. Sans Jwt__Cle valable, l'API refuse de démarrer.
# =====================================================================

# Étiquette des images .NET : la distribution de l'étiquette par défaut « 10.0 » est à constater au premier
# démarrage (cat /etc/os-release). Le script d'installation ci-dessous suit la distribution trouvée (Debian ou Ubuntu).
ARG DOTNET_TAG=10.0
# Version majeure du client PostgreSQL : >= celle du serveur (image postgres:18 de deploy/demo/docker-compose.yml)
ARG PG_MAJEURE=18

# ---------------------------------------------------------------------
# Étape 1 : compilation
# ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
WORKDIR /depot

# Restauration d'abord (couche mise en cache tant que ces fichiers ne changent pas)
COPY NuGet.Config global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Pharma.Domain/Pharma.Domain.csproj                 src/Pharma.Domain/
COPY src/Pharma.Shared/Pharma.Shared.csproj                 src/Pharma.Shared/
COPY src/Pharma.Application/Pharma.Application.csproj       src/Pharma.Application/
COPY src/Pharma.Infrastructure/Pharma.Infrastructure.csproj src/Pharma.Infrastructure/
COPY src/Pharma.Api/Pharma.Api.csproj                       src/Pharma.Api/
RUN dotnet restore src/Pharma.Api/Pharma.Api.csproj

# Sources des seuls projets de l'API, puis scripts de la base livrés avec l'API (Pharma.Api.csproj : database/migration/*.sql)
COPY src/Pharma.Domain/         src/Pharma.Domain/
COPY src/Pharma.Shared/         src/Pharma.Shared/
COPY src/Pharma.Application/    src/Pharma.Application/
COPY src/Pharma.Infrastructure/ src/Pharma.Infrastructure/
COPY src/Pharma.Api/            src/Pharma.Api/
COPY database/migration/        database/migration/

# Réglages du poste de développement et HTTPS du PC serveur : jamais dans l'image (déjà exclus par .dockerignore, retirés ici par sûreté)
RUN dotnet publish src/Pharma.Api/Pharma.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false \
 && rm -f /app/publish/appsettings.Development.json /app/publish/appsettings.Https.json

# ---------------------------------------------------------------------
# Étape 2 : exécution (ASP.NET Core, avec ICU : pas d'image « chiseled » ni Alpine)
# ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS final
ARG PG_MAJEURE

# tzdata (fuseau Africa/Algiers), curl (HEALTHCHECK et clé du dépôt PostgreSQL), client PostgreSQL (pg_dump, pg_restore, psql)
# de version >= serveur : dépôt apt officiel PostgreSQL (PGDG), la distribution n'ayant pas forcément la version voulue.
RUN set -eux; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates curl tzdata; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc; \
    . /etc/os-release; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends postgresql-client-${PG_MAJEURE}; \
    rm -rf /var/lib/apt/lists/*; \
    test -x /usr/lib/postgresql/${PG_MAJEURE}/bin/pg_dump; \
    test -x /usr/lib/postgresql/${PG_MAJEURE}/bin/pg_restore; \
    test -x /usr/lib/postgresql/${PG_MAJEURE}/bin/psql; \
    test -f /usr/share/zoneinfo/Africa/Algiers

# Dossier des données de l'API (Donnees:Racine) : à monter sur un volume persistant (clés, GED, archives fiscales…)
RUN install -d -o app -g app -m 750 /var/lib/pharmaofficine

WORKDIR /app
COPY --from=build /app/publish .

# Fuseau de l'officine ; culture fr-FR avec ICU (pas de mode invariant) ; écoute HTTP 8080 dans le conteneur.
# appsettings.json règle « Urls » (http://0.0.0.0:5080) : la variable sans préfixe « Urls », lue après lui, impose le port 8080.
ENV TZ=Africa/Algiers \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    Urls=http://+:8080 \
    Donnees__Racine=/var/lib/pharmaofficine \
    Sauvegardes__PgDump=/usr/lib/postgresql/${PG_MAJEURE}/bin/pg_dump

# Compte non administrateur fourni par les images .NET
USER app

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/healthcheck || exit 1

ENTRYPOINT ["dotnet", "Pharma.Api.dll"]
