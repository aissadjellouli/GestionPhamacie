# Architecture et diagrammes de séquence

Vue technique de bout en bout : les composants, puis les échanges principaux sous forme de diagrammes de séquence (Mermaid).
Compléments : [architecture.md](architecture.md) (couches de l'officine), [conception-synchronisation.md](conception-synchronisation.md)
(détail de la synchronisation), [ecrans-central.md](ecrans-central.md) (écrans du serveur central), [base-de-donnees.md](base-de-donnees.md).

## 1. Composants

```mermaid
flowchart LR
    subgraph Officine["Officine (réseau local)"]
        direction TB
        POSTE["Pharma.Desktop<br/>WPF + Blazor (WebView2)<br/>caisse, back-office"]
        API["Pharma.Api<br/>ASP.NET Core, service Windows<br/>port 5080, JWT"]
        PG[("PostgreSQL<br/>base de l'officine<br/>sync_sortie, sync_lot…")]
        API2["2e serveur / PC de secours<br/>lecture seule tant qu'il est en veille"]
        POSTE -->|HTTP + JWT<br/>en-tête X-Pharma-Poste| API
        API --> PG
        PG -. réplication .-> API2
    end
    subgraph Central["Serveur central (éditeur)"]
        direction TB
        CAPI["Pharma.Cloud.Api<br/>port 5090, HTTPS<br/>certificat client + jeton"]
        CWEB["Pharma.Cloud.Web<br/>écrans éditeur et titulaire<br/>port 5094, second facteur"]
        CPG[("PostgreSQL central<br/>lot_recu, tables miroir,<br/>descendant, ordre_central")]
        CAPI -->|rôle pharma_cloud_api| CPG
        CWEB -->|rôle pharma_cloud_ecrans<br/>fonctions cloud_ecran_*| CPG
    end
    API -->|"HTTPS sortant seulement<br/>/api/v1/*"| CAPI
    NAV["Navigateur<br/>éditeur, titulaire"] --> CWEB
```

| Brique | Rôle | Où dans le code |
| --- | --- | --- |
| Poste | Écrans du comptoir et du back-office ; ne touche jamais la base | `src/Pharma.Desktop` |
| API de l'officine | Cas d'usage, droits, tâches de fond (dont la synchronisation) | `src/Pharma.Api`, `Pharma.Application`, `Pharma.Infrastructure` |
| Base de l'officine | Seule source de vérité de l'officine ; file de synchronisation | `database/migration`, `database/init` |
| API centrale | Reçoit les lots, renvoie les accusés et la voie descendante | `src/Pharma.Cloud.Api` (`PointsEntree.cs`) |
| Écrans du central | Éditeur : officines, comptes, envoi des codes ; titulaire : tableau de bord, ordres | `src/Pharma.Cloud.Web` |
| Base centrale | Lots bruts, tables miroir cloisonnées par officine (RLS), files descendantes | `database/cloud` |

**Tâches de fond de l'API de l'officine** liées à la synchronisation : `EnvoiSynchronisation` (passages), `SurveillanceSynchronisation`
(alertes : échéance du certificat, retard), `PurgeSynchronisation` (lots accusés au-delà du délai de conservation).

## 1 bis. Déploiement : où tourne chaque composant

Source : [mise-en-production.md](mise-en-production.md) (ports, services, dossiers). Les points encore ouverts y sont marqués
**[À CONFIRMER]** et le restent ici.

```mermaid
flowchart TB
    subgraph OFF["Officine : réseau privé"]
        direction TB
        subgraph SRV["PC serveur (Windows 11 / Server 2025)"]
            direction TB
            SAPI["Service « PharmaOfficine API »<br/>Pharma.Api, démarrage automatique<br/>HTTPS 5443 (réseau) · HTTP 5080 (localhost)"]
            SPG[("Service postgresql-x64-18<br/>port 5432, localhost seulement")]
            SFIC["C:\ProgramData\PharmaOfficine<br/>GED, clés chiffrées, journaux"]
            SCHIFA["CHIFA-Officine (CNAS)<br/>dossier d'export lu par l'API"]
            SAPI --> SPG
            SAPI --> SFIC
            SAPI -->|lit les factures exportées| SCHIFA
        end
        subgraph POSTES["Postes (caisse, back-office)"]
            DESK["Pharma.Desktop<br/>WPF + Blazor (WebView2)<br/>PHARMA_API = https://serveur:5443/"]
        end
        TEL["Téléphones<br/>scanner mobile (navigateur)"]
        subgraph S2["2e serveur (facultatif, parcours C)"]
            S2PG[("PostgreSQL réplique<br/>lecture seule")]
            S2API["PharmaOfficine API<br/>démarrage manuel (bascule)"]
        end
        subgraph SEC["PC de secours (facultatif, parcours D)"]
            SECPG[("PostgreSQL + copie de secours")]
            SECAPI["PharmaOfficine API<br/>démarrage manuel"]
        end
        MAT["Imprimantes ticket (Windows) et étiquettes (ZPL, port 9100)<br/>afficheur client, lecteur code-barres"]
        DISQUE["Deux disques externes en rotation<br/>sauvegardes"]
        GSM["Modem GSM (port COM)<br/>SMS au titulaire"]
        DESK -->|HTTPS 5443 + JWT| SAPI
        TEL -->|HTTPS 5443| SAPI
        DESK --- MAT
        SPG -. réplication 5432 .-> S2PG
        SAPI -->|sauvegardes automatiques| DISQUE
        SAPI --> GSM
    end
    subgraph CEN["Serveur central (hébergement de l'éditeur) [À CONFIRMER : hébergeur, Windows ou Linux]"]
        direction TB
        CAPI["Pharma.Cloud.Api<br/>HTTPS 5090, certificat client<br/>(pas de service Windows dans le code [À CONFIRMER])"]
        CWEB["Pharma.Cloud.Web<br/>écrans éditeur et titulaire<br/>HTTPS 5094 (127.0.0.1 par défaut)"]
        CPG[("PostgreSQL 18 central<br/>rôles pharma_cloud_api, pharma_cloud_ecrans")]
        CAPI --> CPG
        CWEB --> CPG
    end
    SMTP["Serveur SMTP<br/>e-mails au titulaire ; codes des écrans"]
    NAV["Navigateur<br/>éditeur, titulaire"]
    SAPI -->|"HTTPS sortant seulement<br/>/api/v1/* (synchronisation)"| CAPI
    SAPI -->|alertes| SMTP
    CWEB -->|codes de second facteur| SMTP
    NAV -->|HTTPS| CWEB
```

| Machine | Composants | Ports ouverts | Démarrage |
| --- | --- | --- | --- |
| **PC serveur** | `PharmaOfficine API` (Pharma.Api), PostgreSQL 18, GED et clés (`C:\ProgramData\PharmaOfficine`), CHIFA-Officine | 5443 (HTTPS, postes et téléphones) ; 5080 et 5432 en `localhost` seulement (5432 ouvert au seul 2e serveur) | Automatique |
| **Postes** | Pharma.Desktop (`PHARMA_API` vers le PC serveur), imprimantes, afficheur, lecteur | Aucun | À l'ouverture de session |
| **Téléphones** | Navigateur (scanner mobile) | Aucun | — |
| **2e serveur** (facultatif) | PostgreSQL en réplique (lecture seule), API arrêtée | — | API démarrée seulement à la bascule |
| **PC de secours** (facultatif) | PostgreSQL et API, copie de secours | — | API démarrée seulement à la bascule |
| **Serveur central** | Pharma.Cloud.Api, Pharma.Cloud.Web, PostgreSQL central | 5090 (API centrale) ; 5094 (écrans) **[À CONFIRMER : exposition à l'hébergement]** | **[À CONFIRMER]** |

L'officine n'ouvre **aucun port vers Internet** : la synchronisation est un appel sortant de l'API vers le central.

## 2. Vente au comptoir (chemin d'une écriture)

Toute écriture d'une table envoyée est notée dans la file `sync_sortie`, **dans la même transaction** : rien ne peut être
enregistré sans être noté, rien n'est noté sans être enregistré.

```mermaid
sequenceDiagram
    autonumber
    actor Caissier
    participant P as Poste (Pharma.Desktop)
    participant A as Pharma.Api
    participant E as Encaisser (Application)
    participant DB as PostgreSQL officine
    Caissier->>P: panier, mode de paiement, montant reçu
    P->>A: POST /api/caisse/encaisser (JWT, X-Pharma-Poste, clé d'encaissement)
    A->>E: ExecuterAsync(commande)
    E->>DB: vente déjà enregistrée avec cette clé ? (renvoi après coupure)
    alt clé déjà connue
        DB-->>E: vente existante
        E-->>A: même ticket, monnaie recalculée
    else nouvelle vente
        E->>DB: BEGIN : vente, lignes, stock, paiement, registre, journal
        Note over DB: déclencheur zz_sync_sortie :<br/>une ligne par changement dans sync_sortie
        DB-->>E: COMMIT (n° de ticket)
        E-->>A: VenteEncaissee
    end
    A-->>P: 200 VenteEncaisseeDto (ticket, rendu)
```

## 3. Activation de la synchronisation (une fois par serveur)

```mermaid
sequenceDiagram
    autonumber
    actor Ed as Éditeur
    participant CW as Écrans du central
    participant C as Pharma.Cloud.Api
    actor T as Titulaire (poste)
    participant A as Pharma.Api (officine)
    participant DB as Base officine
    Ed->>CW: créer l'officine, demander un code d'activation
    CW-->>Ed: code (usage unique, 24 h) + empreinte de l'autorité
    Ed-->>T: transmet le code
    T->>A: Administration › Synchronisation › Activer (code)
    A->>A: clé privée générée ici, demande de certificat (CSR)
    A->>C: POST /api/v1/activation (code, CSR, code officine)
    C-->>A: certificat client 3 ans + autorité épinglée
    A->>DB: sync_inscrire : officine inscrite, synchronisation active
    Note over A,DB: première activation : chargement complet demandé
```

## 4. Un passage de synchronisation

Lancé par `EnvoiSynchronisation` (régulièrement, ou à la demande). Un seul passage à la fois (`VerrouSync`). Le serveur en lecture
seule (2e serveur en veille) ne passe jamais.

```mermaid
sequenceDiagram
    autonumber
    participant S as EnvoiSynchronisation
    participant L as EnvoyerLotsSync (EnvoiLots.cs)
    participant DB as Base officine
    participant H as ClientServeurCentralHttp
    participant C as Pharma.Cloud.Api
    participant CDB as Base centrale
    S->>L: ExecuterAsync()
    L->>H: POST /api/v1/jeton (certificat client)
    H->>C: jeton
    C-->>H: JWT 10 min lié au certificat
    L->>H: POST /api/v1/echange (version descendante appliquée)
    H->>C: échange
    C->>CDB: cloud_descendants(version) — au plus 1 000 éléments, 768 Kio
    C-->>H: éléments (réglages, intégrité, ordres)
    H-->>L: éléments gardés (+ nombre d'écartés)
    L->>DB: sync_recevoir_descendant(...) pour chacun, puis version acquittée
    L->>C: GET /api/v1/etat?epoque= (retour en arrière ?)
    L->>DB: sync_constituer_lot : file sync_sortie -> lots sync_lot (époque, n°, empreinte)
    loop chaque lot non accusé
        L->>H: POST /api/v1/lots (lot sync-v1, X-Sync-Descendants)
        H->>C: dépôt
        C->>CDB: cloud_appliquer_lot(époque, n°, empreinte, contenu)
        alt accepte / deja_recu
            C-->>H: 200 accusé (+ éléments descendants)
            L->>DB: sync_enregistrer_accuse(époque, n°)
        else trou (lots manquants au central)
            C-->>H: 409 accusé JSON (dernier reçu)
            L->>H: renvoie les lots manquants, puis ce lot
        else empreinte_differente (autre contenu)
            C-->>H: 409 accusé JSON (dernier reçu)
            L->>DB: sync_noter_retour_arriere_refus : suspension
        end
    end
    opt certificat à moins de 60 jours de l'échéance
        L->>H: POST /api/v1/certificat/renouveler (nouvelle CSR)
        Note over L: un refus n'est qu'une note : nouvel essai au passage suivant
    end
    L->>DB: sync_noter_envoi / sync_noter_passage (état affiché à l'écran)
```

## 5. Retour en arrière de la base et nouvelle époque

Après une restauration ou une bascule sur le 2e serveur, la base peut ignorer des lots que le central a déjà reçus.

```mermaid
sequenceDiagram
    autonumber
    participant A as Pharma.Api (démarrage)
    participant DB as Base officine
    participant C as Pharma.Cloud.Api
    actor T as Titulaire
    A->>DB: encaissement suspendu (aucun n° de ticket) jusqu'au contrôle
    A->>C: GET /api/v1/etat?epoque=N
    C-->>A: dernier lot reçu = 120 (la base n'en a que 100)
    A->>DB: sync_noter_retour_arriere, sync_avancer_compteur_ticket, sync_avancer_sequences
    Note over DB: tickets et identifiants avancés au-delà<br/>de ce que le central connaît
    A->>DB: synchronisation suspendue, alerte au titulaire
    T->>A: « Repartir sur une nouvelle époque »
    A->>DB: sync_nouvelle_epoque (époque N+1)
    A->>C: POST /api/v1/epoque (déclaration, fins de chaîne)
    C-->>A: époque enregistrée
    Note over A,C: les lots repartent en époque N+1 ;<br/>les ventes perdues restent au central, signalées
```

## 6. Ordre envoyé par le central

```mermaid
sequenceDiagram
    autonumber
    actor X as Éditeur ou titulaire
    participant CW as Écrans du central
    participant CDB as Base centrale
    participant C as Pharma.Cloud.Api
    participant L as Passage de synchronisation
    participant O as AppliquerOrdresCentraux
    participant DB as Base officine
    actor T as Titulaire (poste)
    X->>CW: ordre (prix, commande, employé, message…)
    CW->>CDB: ordre_central + élément descendant
    L->>C: échange ou dépôt d'un lot
    C-->>L: élément « ordre »
    L->>DB: sync_recevoir_descendant -> sync_ordre_recu (à appliquer)
    O->>O: verrou des ordres (un traitement à la fois)
    O->>DB: relit l'ordre
    alt confirmation exigée (prix +20 %, référentiel, employé…)
        O->>DB: sync_ordre_noter(à confirmer) + alerte au titulaire
        T->>O: Confirmer ou Refuser (Synchronisation › Ordres du central)
        O->>DB: exécution puis sync_ordre_noter(appliqué ou refusé)
    else application directe
        O->>DB: exécution puis sync_ordre_noter(appliqué)
    end
    L->>C: résultat remonté avec les lots suivants (sync_ordre_resultat)
```

## 7. Connexion aux écrans du central

```mermaid
sequenceDiagram
    autonumber
    actor U as Éditeur ou titulaire
    participant W as Pharma.Cloud.Web
    participant CDB as Base centrale (cloud_ecran_*)
    participant M as Envoi du code (e-mail ; fichier en développement)
    U->>W: identifiant + mot de passe
    W->>CDB: cloud_ecran_debut_connexion / fin_connexion (BCrypt vérifié dans l'appli)
    alt mot de passe provisoire non utilisé dans sa durée de vie
        CDB-->>W: provisoire_expire (refus)
    else accepté
        CDB-->>W: code attendu (session à l'étape « code »)
        W->>M: code à 6 chiffres (5 min, usage unique)
        U->>W: code
        W->>CDB: session ouverte (empreinte du jeton seulement)
        opt mot de passe provisoire
            W-->>U: choisir son mot de passe
        end
    end
    loop chaque requête
        W->>CDB: cloud_ecran_session_valable : échéance glissante
        Note over W,U: inactivité réglée par rôle (5 à 60 min) ;<br/>bandeau « Rester connecté » 60 s avant
    end
```

## 8. Workflow de synchronisation entre officines et avec le central

**Aucun lien direct entre officines** : chaque officine ne parle qu'au central, par appels sortants. Ce qui « passe » d'une officine
à une autre transite toujours par le central, sous l'une de ces formes : un **modèle de réglages** tiré d'une officine et publié aux
autres (mutualisation), un **réglage commun** ou un **ordre** du titulaire envoyé à plusieurs de ses officines, un **référentiel** de
l'éditeur envoyé à toutes. Les données de vente d'une officine ne descendent jamais dans une autre.

### 8.1 Vue d'ensemble des flux

```mermaid
flowchart LR
    subgraph A["Officine A"]
        A1[("base A<br/>sync_sortie -> sync_lot")]
        A2["reçus : réglages,<br/>ordres, référentiels"]
    end
    subgraph B["Officine B"]
        B1[("base B<br/>sync_sortie -> sync_lot")]
        B2["reçus : réglages,<br/>ordres, référentiels"]
    end
    subgraph C["Serveur central"]
        direction TB
        LR[("lot_recu + tables miroir<br/>cloisonnées par officine (RLS)")]
        MOD["modèles de réglages<br/>(imposé / proposé, mutu-v1)"]
        ORD[("ordre_central<br/>(éditeur, titulaire)")]
        REF["référentiels<br/>(nomenclature, DCI, organismes…)"]
        DESC[("descendant / officine_descendant<br/>une file par officine")]
        MOD --> DESC
        ORD --> DESC
        REF --> DESC
    end
    ED["Éditeur<br/>(écrans, console)"]
    TIT["Titulaire de plusieurs officines<br/>(écrans du central)"]
    A1 -->|"① lots (montée)"| LR
    B1 -->|"① lots (montée)"| LR
    LR -->|"② modèle tiré d'une officine<br/>(sans identifiant propre)"| MOD
    LR -->|"③ vue d'ensemble<br/>(nombres, jamais de patient)"| TIT
    ED --> MOD
    ED --> REF
    ED --> ORD
    TIT -->|"④ ordres, réglages communs"| ORD
    DESC -->|"⑤ voie descendante"| A2
    DESC -->|"⑤ voie descendante"| B2
    A2 -. "⑥ résultat d'ordre<br/>(sync_ordre_resultat, montant)" .-> LR
    B2 -. "⑥ résultat d'ordre" .-> LR
```

| Étape | Ce qui circule | Sens | Mécanisme |
| --- | --- | --- | --- |
| ① | Changements de chaque officine (ventes, stock, caisse, réglages…) minimisés | officine -> central | file `sync_sortie`, lots `sync_lot`, `POST /api/v1/lots`, `cloud_appliquer_lot` |
| ② | Valeurs réelles des réglages d'une officine prise comme modèle | central -> central | console `--modele-depuis-officine` (`ModeleDepuisOfficine`) : lit la table miroir, retire tout identifiant propre à l'officine, ne publie rien |
| ③ | Synthèse des officines du titulaire (tableau de bord, vue d'ensemble) | central -> écrans | fonctions `cloud_ecran_titulaire_*` sur les tables miroir des seules officines rattachées |
| ④ | Ordres à distance, réglages communs proposés | écrans -> central | `ordre_central` : un ordre par officine cochée (droit `ordres` accordé depuis le poste de chaque officine) |
| ⑤ | Réglages mutualisés, ordres, référentiels, résumé d'intégrité | central -> chaque officine | file descendante par officine ; `POST /api/v1/echange` ou accusé d'un lot ; `sync_recevoir_descendant` |
| ⑥ | Résultat de chaque ordre (appliqué, refusé, expiré, motif) | officine -> central | table montante `sync_ordre_resultat`, avec les lots suivants |

### 8.2 Mutualisation : un réglage de l'officine A devient le modèle de l'officine B

```mermaid
sequenceDiagram
    autonumber
    participant A as Officine A (base)
    participant C as Serveur central
    actor Ed as Éditeur (console du central)
    participant B as Officine B
    actor TB as Titulaire de B (poste)
    A->>C: lots : réglages de A (tables du groupe 2) -> tables miroir de A
    Ed->>C: --modele-depuis-officine (table, officine A)
    C->>C: modèle mutu-v1 sans identifiant propre à A
    Ed->>C: --mode-reglage (imposé ou proposé), --publier-reglage
    C->>C: élément descendant (reglage_central, clé mutu_<table>) pour chaque officine
    B->>C: passage de synchronisation (échange)
    C-->>B: modèle publié
    alt table imposée
        B->>B: valeur du central appliquée, écran grisé « fixé par le serveur central »<br/>(ancienne valeur gardée au journal, titulaire prévenu)
    else table proposée
        B->>TB: Synchronisation › Réglages mutualisés : actuel / proposé
        TB->>B: accepte ou refuse (mot de passe du titulaire)
    end
    Note over A,B: A ne voit jamais B, et B ne reçoit jamais de donnée de vente de A
```

### 8.3 Réglage commun proposé par un titulaire à plusieurs officines

```mermaid
sequenceDiagram
    autonumber
    actor T as Titulaire (écrans du central)
    participant W as Pharma.Cloud.Web
    participant C as Base centrale
    participant O1 as Officine 1
    participant O2 as Officine 2
    T->>W: Mes officines › Réglages communs : un réglage, sa valeur, officines cochées, code
    W->>C: cloud_ecran_titulaire_envoyer_reglages : tout ou rien<br/>(droit « ordres » sur chaque officine, 24 h, 5 envois / heure)
    C->>C: un ordre « reglages » par officine (même envoi)
    par chaque officine, à son propre passage
        O1->>C: échange
        C-->>O1: ordre « reglages »
        O1->>O1: revérifié (liste fermée, bornes) puis « à confirmer »
    and
        O2->>C: échange
        C-->>O2: ordre « reglages »
        O2->>O2: revérifié puis « à confirmer »
    end
    Note over O1,O2: sur chaque poste, le titulaire accepte ou refuse<br/>(mot de passe ressaisi) ; rien ne s'applique sans acceptation
    O1-->>C: résultat (sync_ordre_resultat) avec les lots suivants
    O2-->>C: résultat
    W-->>T: Propositions envoyées : Proposé / Accepté / Refusé / Expiré par officine
```

### 8.4 Référentiel publié par l'éditeur à toutes les officines (exemple : nomenclature)

```mermaid
sequenceDiagram
    autonumber
    actor Ed as Éditeur
    participant C as Serveur central
    participant O as Chaque officine
    actor T as Titulaire (poste)
    Ed->>C: dépose le fichier du référentiel, ordre « nomenclature » pour les officines
    O->>C: passage : échange
    C-->>O: ordre (n° du fichier, taille, empreinte)
    O->>O: à confirmer par le titulaire (taille du fichier affichée)
    T->>O: Confirmer
    O->>C: GET /api/v1/referentiels/{id} (certificat de l'officine), téléchargé à la confirmation
    C-->>O: fichier
    O->>O: taille et empreinte SHA-256 vérifiées, import (mêmes règles que l'import local), sync_ordre_noter(appliqué)
    Note over O,C: fichier inconnu (404) ou différent : refus définitif ;<br/>central injoignable : l'ordre reste « à confirmer », rien n'est perdu
    Note over O: autres référentiels (DCI, classes suivies, organismes, prestations) :<br/>aperçu des changements calculé avant la confirmation
```

### 8.5 Cycle de vie d'un changement, de l'officine au central

```mermaid
stateDiagram-v2
    [*] --> EnAttente: écriture métier validée<br/>(zz_sync_sortie)
    EnAttente --> DansUnLot: sync_constituer_lot
    DansUnLot --> Envoye: POST /api/v1/lots
    Envoye --> Accuse: accepte / deja_recu<br/>(sync_enregistrer_accuse)
    Envoye --> DansUnLot: coupure, 5xx, 429<br/>(renvoi au passage suivant)
    Envoye --> Renvoi: trou (lots manquants au central)
    Renvoi --> Accuse: lots manquants redéposés
    Envoye --> Suspendu: empreinte_differente<br/>(retour en arrière)
    Suspendu --> NouvelleEpoque: « Repartir sur une nouvelle époque »
    NouvelleEpoque --> EnAttente: changements suivants, époque N+1
    Accuse --> Purge: délai de conservation passé<br/>(sync_purger)
    Purge --> [*]
```

## 9. Garanties transversales

| Garantie | Comment |
| --- | --- |
| Aucune entrée réseau dans l'officine | Seule l'API de l'officine appelle le central (HTTPS sortant) |
| Identité de l'officine | Certificat client émis par l'autorité du central, vérifié à chaque appel (révocation, échéance, officine résiliée) ; jeton de 10 min lié au certificat |
| Pas de donnée nominative au central | Patients et porteurs sous code (`sync_code_patient`, `sync_code_porteur`) ; colonnes interdites retirées (`sync_colonnes_interdites`) |
| Cloisonnement au central | RLS forcée sur chaque table (`officine_id = cloud_officine_courante()`) |
| Rien de perdu, rien en double | File dans la transaction métier ; lots numérotés et empreintés ; accusé avant de marquer reçu ; époques après un retour en arrière |
| Droits côté serveur | Chaque route de l'API de l'officine porte un `Exiger*` (test `RoutesProtegeesTests`) |
