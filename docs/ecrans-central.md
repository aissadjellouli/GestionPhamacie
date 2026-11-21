# Écrans du serveur central (lot S7)

> **Statut (2026-11-21)** : lot **S7.1 réalisé en local** (écrans de l'éditeur), go du titulaire, toutes les recommandations de la
> conception retenues. **Rien n'est en ligne** : la mise en ligne (hébergeur, certificat, SMTP, connexion de production) viendra plus tard.
> Lots suivants : S7.2 (titulaire, multi-officines), voir « Reste à faire ».

Documents liés : [conception-synchronisation.md](conception-synchronisation.md) § 1, § 6, § 6.1, § 6.2 et § 32 ;
[securite-et-droits.md](securite-et-droits.md) ; [charte-graphique.md](charte-graphique.md) ; [database/cloud/README.md](../database/cloud/README.md) ;
[poste-de-developpement.md](poste-de-developpement.md).

## 1. Ce qui existe

| Élément | Où | Rôle |
|---|---|---|
| Projet `src/Pharma.Cloud.Web` | Blazor en **rendu serveur statique** (formulaires classiques, aucun script Blazor, aucune interactivité) | Écrans ; **processus et port distincts** du serveur central (5090) |
| Script central `030_comptes_distants.sql` | base centrale | Rôle `pharma_cloud_ecrans`, comptes, sessions, codes de second facteur, journal des accès |
| Script central `031_ecrans_editeur.sql` | base centrale | Lecture de l'éditeur et actions |
| Commandes de la console du central | `Pharma.Cloud.Api.exe` | `--creer-compte-editeur`, `--comptes-distants`, `--desactiver-compte`, `--deverrouiller-compte` |
| Tests | `tests/Pharma.Cloud.Web.Tests`, `tests/Pharma.Cloud.Tests/ComptesDistantsConsoleTests.cs`, `database/cloud/tests/030_…`, `031_…` | Voir § 7 |

## 2. Rôles

| Rôle | Écrans | Données vues |
|---|---|---|
| **Éditeur** (support, S7.1) | Officines, fiche d'une officine, mon journal des accès, actions techniques | **Jamais de donnée nominative** (décision n° 4) : état technique seulement |
| **Titulaire** (S7.2, à venir) | Ses officines (table `compte_officine`, créée vide en S7.1) | À concevoir en S7.2 |

Un compte titulaire qui se connecterait aujourd'hui voit « Accès refusé » (aucun écran en S7.1 ; aucun compte titulaire ne peut être créé par la console).

## 3. Écrans S7.1 (éditeur)

| Route | Écran | Contenu |
|---|---|---|
| `/connexion` | Connexion | Identifiant + mot de passe ; **même message** pour un identifiant inconnu, un mot de passe faux ou un compte bloqué |
| `/code` | Code de confirmation | 6 chiffres, 5 minutes, 5 essais ; « Recevoir un nouveau code » ; « Abandonner » |
| `/mot-de-passe` | Mot de passe | Obligatoire tant que le mot de passe est **provisoire** ; ensuite accessible depuis l'en-tête (les autres sessions sont fermées) |
| `/officines` (et `/`) | Officines | Filtres au-dessus (nom, état : liste bornée), tableau paginé (20) : officine, état, créée le, dernier contact, en retard (plus de 24 h sans contact, officine active), dernier lot, époque, chargement en cours, échéance de certificat la plus proche, anomalies |
| `/officines/{code}?rubrique=…` | Fiche d'une officine | Rubriques à gauche : `resume`, `certificats`, `codes` (sans le code), `chargements` (20), `lots` (50 derniers, sans contenu), `epoques`, `changements`, `volumes` (31 jours), `integrite` (chaînes), `anomalies` (nature et rang, sans détail, paginé), `restaurations` (sans montant), `ordres` (type et statut, sans données, paginé), `journal` (journal de sécurité, paginé) |
| `/officines/nouvelle` | Créer une officine | Action (§ 4) ; serveur principal ou second |
| `/officines/{code}/nouveau-code?serveur=principal\|second` | Nouveau code d'activation | Action (§ 4) |
| `/officines/{code}/revoquer-certificat?empreinte=<12 caractères>` | Révoquer un certificat | Action (§ 4), confirmation par le début d'empreinte |
| `/officines/{code}/revoquer-serveur?serveur=principal\|second` | Révoquer un serveur | Action (§ 4), confirmation par le nom de l'officine |
| `/officines/{code}/code-affiche?ref=…` | Code d'activation | Affiché **une seule fois** (mémoire du processus, 2 minutes, même session), bouton « Copier » |
| `/mon-journal` | Mon journal des accès | Accès du compte, paginé (20) |

**Restent en console** (non exposés aux écrans) : départ et effacement d'une officine, purges, familles nominatives, autorité de certification, réglages mutualisés, ordres.

## 4. Actions de l'éditeur

Chaque action : **nouveau code de second facteur** (« Recevoir un code de confirmation », objet `action`, usage unique), **motif obligatoire**
(5 à 300 caractères), **confirmation par ressaisie** (nom de l'officine, ou début d'empreinte du certificat), puis note au **journal des accès**
(sans texte libre) **et** au **journal de sécurité** du central (avec le motif, comme les révocations de la console). Les contrôles de saisie
passent **avant** le code : une faute de frappe ne consomme pas le code ; un code faux est compté (5 essais).

| Action | Fonction SQL | Réutilise |
|---|---|---|
| Créer une officine + code d'activation (24 h) | `cloud_ecran_creer_officine` | même enregistrement que `--creer-officine` |
| Nouveau code d'activation (principal / second) | `cloud_ecran_nouveau_code_activation` | même enregistrement que `--code-activation` ; refusé pour une officine partie |
| Révoquer un certificat (et ses renouvellements) | `cloud_ecran_revoquer_certificat` | déclencheur de cascade du script 001 |
| Révoquer un serveur | `cloud_ecran_revoquer_serveur` | `cloud_revoquer_serveur` (001) |

Le **code d'activation** est tiré par les écrans (`CodeActivationSync`, comme la console) à partir du **certificat public** de l'autorité
(`autorite.crt`) : seule son empreinte va en base. Les écrans ne lisent **jamais** la clé de l'autorité (test d'architecture).

## 5. Sécurité

- **Base** : rôle `pharma_cloud_ecrans` **sans connexion**, sans aucun droit sur les tables ; EXECUTE sur les seules fonctions `cloud_ecran_*`
  (`SECURITY DEFINER`, `search_path` fixé, retirées à PUBLIC). Chaque fonction **revérifie le jeton** (session ouverte, rôle). L'officine courante
  est posée **par la base** dans `cloud_ecran_ouvrir(empreinte du jeton, officine)` après vérification de la session et du droit ; poser
  `app.officine_id` soi-même ne donne rien. Au démarrage, les écrans refusent une connexion superutilisateur, BYPASSRLS, propriétaire d'une table
  ou non membre de `pharma_cloud_ecrans`.
- **Mots de passe** : BCrypt coût 12, calculé par l'application ; mot de passe **provisoire** (16 caractères tirés au sort, affiché une fois par
  la console) à changer à la première connexion ; nouveau mot de passe : 12 à 128 caractères, lettres et chiffres, sans l'identifiant,
  différent de l'actuel. Une vérification BCrypt est toujours faite (empreinte factice si le compte est inconnu ou bloqué) : même durée de réponse.
- **Verrouillage** : 5 échecs → 15 minutes, puis durée **doublée** à chaque nouveau blocage, 24 heures au plus ; remis à zéro par une connexion
  complète ou `--deverrouiller-compte`.
- **Second facteur** : 6 chiffres, empreinte **HMAC-SHA256** (clé tirée au démarrage du processus des écrans, jamais en base : un redémarrage annule
  les codes en cours), 5 minutes, 5 essais, usage unique ; 5 codes au plus par compte et par quart d'heure. Après le bon code, **nouveau jeton**
  (pas de fixation de session).
- **Sessions** : jeton de 32 octets dans un cookie `__Host-pharma-ecrans` (Secure, HttpOnly, SameSite=Strict, Path=/, sans domaine ni date) ; la
  base n'en garde que l'**empreinte SHA-256**. Éditeur : 2 heures ; titulaire (S7.2) : 30 minutes d'inactivité, 8 heures au plus. Déconnexion,
  changement de mot de passe (autres sessions) et désactivation du compte ferment les sessions.
- **HTTP** : HTTPS seulement (TLS 1.2 ou 1.3), jeton anti-falsification sur chaque formulaire (cookie `__Host-pharma-ecrans-af`),
  `Cache-Control: no-store`, CSP stricte (`default-src 'none'; script-src 'self'; style-src 'self'; …; frame-ancestors 'none'`), `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, HSTS hors développement, hôtes permis `127.0.0.1` et `localhost` (`AllowedHosts`), limites de débit par adresse
  (connexion 10 / 5 min, codes 20 / 5 min, global 300 / min). Page d'erreur fixe, sans détail technique. Aucune route de l'API des officines.
- **Journal des accès** (`journal_acces_distant`) : ajout seul (déclencheurs `cloud_ajout_seul`), **chaîné SHA-256** (format `acces-v1`,
  vérifiable par `SELECT * FROM cloud_ecran_verifier_journal_acces()`), sans texte libre ni donnée nominative : n° du compte, n° de session,
  officine, événement, résultat, objet technique borné. Le code n'est **jamais** écrit dans un journal ni sur la console.
- **Démarrage refusé** : adresse hors boucle locale en développement ; mode « fichier » des codes hors développement ou hors boucle locale ;
  SMS (aucun fournisseur) ; SMTP incomplet en production ; connexion absente ; certificat de l'autorité autre qu'un `.crt` ; port 5090.

## 6. Développement local

1. Passer les scripts centraux **030** puis **031** sur `pharmacie_cloud_dev` (propriétaire `pharma_app`, après relecture) :
   ```powershell
   $env:PGCLIENTENCODING = "UTF8"
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\030_comptes_distants.sql
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\031_ecrans_editeur.sql
   ```
2. `.\scripts\dev\preparer_central_vs.ps1 -Preparer -WhatIf`, puis `-Preparer` : rôle `pharma_cloud_ecrans_dev_vs`, variable d'utilisateur
   `PHARMA_CLOUD_ECRANS_CONNEXION`, dossier `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes`, compte **`editeur.dev`** (mot de passe provisoire
   affiché **une fois** : le noter le temps de la première connexion). Sans 030/031, rien n'est fait pour les écrans (avertissement).
3. Redémarrer Visual Studio ; profil **« Central + Écrans »** (ou « API + Poste + Central ») ; F5 : le navigateur s'ouvre sur
   **https://127.0.0.1:5094/** (certificat de développement ASP.NET : `dotnet dev-certs https --trust` ; il couvre `127.0.0.1`, vérifié le 2026-11-21).
4. Connexion `editeur.dev` ; le code est dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\editeur.dev.txt` (remplacé à chaque envoi) ;
   changer le mot de passe provisoire.
5. `-Etat` montre le rôle, la variable, le dossier des codes, le port 5094 et le compte ; `-Retirer` (après `-WhatIf`) retire le rôle
   `pharma_cloud_ecrans_dev_vs` et la variable, le dossier des codes avec `central-dev` ; le compte `editeur.dev` **reste** au central.

Mot de passe de `editeur.dev` perdu : aucune commande ne remet un mot de passe en S7.1 ; `--desactiver-compte editeur.dev` puis créer un autre
compte (`--creer-compte-editeur editeur.dev2 --nom "…" --email …`) avec l'outil (`%LOCALAPPDATA%\PharmaOfficine\central-dev\outil`).

Réglages (`appsettings*.json`, jamais de secret) : `Ecrans:Adresse` (127.0.0.1), `Ecrans:Port` (5094), `Ecrans:ModeCodes` (`fichier` en
développement, `courriel` sinon), `Ecrans:CertificatAutorite` (sinon `%PHARMA_CLOUD_DOSSIER%\autorite.crt`), `Ecrans:DossierCodes`.
Variables : `PHARMA_CLOUD_ECRANS_CONNEXION` (obligatoire), `PHARMA_CLOUD_DOSSIER` (certificat public), `PHARMA_CLOUD_SMTP_*` (production).

## 7. Tests

- SQL (transaction annulée) : `database/cloud/tests/030_comptes_distants_tests.sql`, `031_ecrans_editeur_tests.sql`.
- `tests/Pharma.Cloud.Web.Tests` : réglages (adresse non locale refusée, mode fichier refusé hors développement, SMS, clé de l'autorité),
  second facteur, contrôle d'accès, mots de passe ; architecture (aucun style en ligne, aucune route de l'API des officines, jamais la clé de
  l'autorité, aucun script Blazor ni interactivité, anti-falsification sur chaque formulaire, lecture par `cloud_ecran_*` seulement) ;
  base jetable : verrouillage, code faux / réutilisé / expiré, sessions (déconnexion, expiration), aucune colonne ni valeur interdite ;
  **bout en bout HTTPS** (serveur démarré sur un port libre de 127.0.0.1) : connexion, code, mot de passe provisoire, liste, toutes les
  rubriques, création d'une officine et code affiché une seule fois, déconnexion, chaîne du journal intacte.
- `tests/Pharma.Cloud.Tests/ComptesDistantsConsoleTests.cs` : commandes de la console.

## 8. Reste à faire

- **Mise en ligne** (plus tard) : hébergeur, nom de domaine et certificat, proxy inverse (adresse réelle pour les limites de débit), connexion de
  production rattachée à `pharma_cloud_ecrans`, compte SMTP, clé HMAC commune si plusieurs instances, comptes éditeur réels, test d'intrusion.
- **S7.2** : écrans du titulaire (multi-officines, `compte_officine`, création des comptes titulaires, rattachement aux officines, ce que le
  titulaire voit de ses données), SMS (fournisseur à choisir), remise à zéro d'un mot de passe par l'éditeur, alertes (refus en série).
- Mesure des écrans à 1 366 × 768 et 1 536 × 890, clair et sombre (règle de l'écran fixe) : **à faire** (voir le rapport du lot).
