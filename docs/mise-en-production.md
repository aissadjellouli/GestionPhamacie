# Manuel de mise en production

Ce manuel dit quoi faire, dans quel ordre, sur quel PC et avec quel compte. Il sert au technicien ou au titulaire. Suivre chaque parcours **de haut en bas**, sans sauter d'étape. Cocher chaque case au fur et à mesure.

Mise à jour : 2026-11-16. Les points marqués **[À CONFIRMER]** ne sont pas tranchés ou pas vérifiables dans le dépôt : ne pas les deviner, demander la réponse au titulaire ou au développeur.

## Sommaire

- [Avant de commencer](#avant-de-commencer) : rôles, comptes, matériel et Windows du PC serveur, conventions, ports, services, variables, fichiers
- [Scripts de la base de données](scripts-base-de-donnees.md) : quel script lancer (base neuve, mise à jour, serveur central), avec quel compte, et que faire en cas d'échec
- [Parcours A, installation neuve](#parcours-a-installation-neuve) : une officine qui démarre de zéro (étapes 1 à 15)
- [Parcours B, mises à jour](#parcours-b-mises-à-jour) : une officine déjà en service (étapes 1 à 14)
- [Parcours C, 2e serveur](#parcours-c-2e-serveur) : réplication et API de secours (facultatif)
- [Parcours D, PC de secours](#parcours-d-pc-de-secours) : copie de secours (facultatif, fortement conseillé)
- [Parcours E, Tracodex Réseau (serveur central de synchronisation)](#parcours-e-tracodex-réseau-serveur-central-de-synchronisation) : hébergement central (facultatif)
- [Procédures occasionnelles](#procédures-occasionnelles) : restauration, PC serveur en panne, bascule, retour, révocation
- [Annexes](#annexes) : ports, services, variables, fichiers, dépannage
- [Checklist avant ouverture](#checklist-avant-ouverture)
- [Checklist après chaque mise à jour](#checklist-après-chaque-mise-à-jour)

---

## Avant de commencer

### Rôles

| Rôle | Qui | Ce qu'il fait |
| --- | --- | --- |
| **Technicien** | personne qui installe | Commandes PowerShell, services, pare-feu, base |
| **Titulaire** | pharmacien titulaire | Réglages dans l'application, décisions (sauvegardes, Chifa, synchronisation) |
| **Administrateur Windows** | compte qui a les droits d'administrateur sur le PC | Installe, change les variables système, lance les scripts |
| **Superutilisateur PostgreSQL** | compte `postgres` (mot de passe noté à l'installation) | Crée les rôles, lance `111b`, les procédures de rôles |
| **Compte `pharma_app`** | compte de l'application, propriétaire de la base avant `111b` | Lance les scripts de la base (voir parcours B) |

### Les PC et les lieux

| Nom dans ce manuel | Ce que c'est |
| --- | --- |
| **PC serveur** | PC principal de l'officine : PostgreSQL et API. Toujours allumé, onduleur obligatoire |
| **Poste** | poste de caisse (un par caisse), qui ouvre Tracodex Officine |
| **PC des sources** | PC qui a le code du dépôt et le SDK .NET 10 : c'est là qu'on compile (`dotnet publish`) |
| **2e serveur** | 2e PC de l'officine, réplique de la base (parcours C, facultatif) |
| **PC de secours** | PC de l'officine avec PostgreSQL et l'API, prêt à prendre le relais (parcours D, facultatif) |
| **Serveur central** | serveur de synchronisation hors officine (parcours E, facultatif) : hébergement **[À VÉRIFIER : hébergeur agréé]** |
| **Téléphone** | scanner mobile (Android ou iPhone) |
| **Clé USB** | support de transport (exemple : `E:\`). Jamais de certificat ni de secret par le réseau |

Dans les commandes, les valeurs entre `<` et `>` sont **à remplacer** (exemple : `<serveur>`, `<base>`, `<mot de passe>`). Ne jamais écrire de vrai mot de passe dans un fichier du dépôt.

### Matériel et Windows du PC serveur (2026-11-17)

Concerne le **PC serveur** et le **2e serveur** (même configuration : en bascule, il porte toute la charge). Les **postes** ne sont pas
concernés par le chiffrement ; leur minimum (application du poste et Chifa_Officine) n'est pas mesuré **[À VÉRIFIER]**. Un poste sous
Windows 10 n'a plus de mises à jour de sécurité depuis octobre 2025 : sujet à traiter à part.

**Version minimale de Windows** (exigée par le chiffrement résistant au quantique, [chiffrement-data.md](chiffrement-data.md) § 3 bis) :

| Fonction | Windows 11 Pro | Windows Server | Source |
| --- | --- | --- | --- |
| ML-KEM et ML-DSA (.NET 10, chiffrement et signature) | **24H2** ou 25H2, mise à jour de novembre 2025 (KB5068861, build 26100.7171) ou plus | **2025**, même mise à jour | [Microsoft, novembre 2025](https://techcommunity.microsoft.com/t5/microsoft-security-community/post-quantum-cryptography-apis-now-generally-available-on/ba-p/4469093) |
| Échange de clé hybride en HTTPS (Schannel, TLS 1.3, `X25519_MLKEM768`) | 24H2 et 25H2 : KB5089573 (builds 26100.8524 et 26200.8524) ; 26H1 : KB5095091 (build 28000.2340) | **2025** : KB5099536 (build 26100.33158, 14 juillet 2026) | [Microsoft, FAQ TLS 1.3 et post-quantique](https://techcommunity.microsoft.com/t5/post-quantum-crypto-tech-blog/windows-tls-1-3-and-post-quantum-crypto-faq/ba-p/4556389) |

- Windows 10, Windows 11 23H2 et avant, Windows Server 2022 et avant : **non cités par Microsoft**, donc considérés comme non pris en charge.
- L'échange hybride est **désactivé par défaut** : l'activer sur chaque serveur, en PowerShell administrateur
  `Enable-TlsEccCurve -Name "X25519_MLKEM768" -Position 0`, **ou** par stratégie de groupe (pas les deux : la stratégie l'emporte).
  Il ne sert que si l'autre côté le gère aussi ; sinon la connexion se fait en échange classique, sans erreur. Central sous Linux :
  OpenSSL 3.5 ou plus **[À VÉRIFIER avec l'hébergeur]**.
- Constaté le 2026-11-17 sur le poste de développement (Windows 11 build 26300, .NET 10) : `MLKem.IsSupported`, `MLDsa.IsSupported` et
  `CompositeMLDsa.IsSupported` vrais ; `SlhDsa.IsSupported` faux.
- Sources lues le 2026-11-17 : listes complétées par Microsoft au fil des mises à jour, **à revérifier avant la mise en service**.

**Plancher matériel de Windows 11** ([exigences Microsoft](https://www.microsoft.com/fr-fr/windows/windows-11-specifications)) :
processeur 64 bits de 2 cœurs à 1 GHz **de la liste compatible** (Intel Core 8e génération ou plus, AMD Ryzen 2000 ou plus), 4 Go de
mémoire, 64 Go de disque, UEFI avec Secure Boot, puce TPM 2.0. Un PC hors de ce plancher ne passe pas en Windows 11 24H2, donc pas de
chiffrement résistant au quantique.

**Configuration conseillée du PC serveur** (estimation, **non mesurée** sur une officine réelle **[À VÉRIFIER : essai de charge]**) :

| Élément | Conseillé | Pourquoi |
| --- | --- | --- |
| Processeur | 4 cœurs : Intel Core i3 12e génération ou plus, ou AMD Ryzen 3 5000 ou plus | PostgreSQL, API, synchronisation et chiffrement ensemble |
| Mémoire | **16 Go** (8 Go au strict minimum) | PostgreSQL profite de la mémoire libre ; 4 Go trop juste |
| Disque | SSD NVMe de 512 Go | Rapidité de la base ; place de la GED **[À VÉRIFIER : volume réel]** |
| Sauvegarde | Deux disques externes en rotation, dont un toujours hors de l'officine (**obligatoire** sans 2e serveur) | [ged-protection-a-decider.md](ged-protection-a-decider.md) |
| Réseau | Ethernet gigabit, pas de Wi-Fi | Liaison stable avec les postes |
| Onduleur | **Obligatoire** | Une coupure pendant une écriture peut abîmer la base |
| Windows | Windows 11 Pro 24H2 ou plus, à jour | Tableau ci-dessus |

**Coût de la licence Windows par serveur** (estimation du 2026-11-17 ; prix en dollars relevés chez des revendeurs, taux **supposé**
1 $ ≈ 132 DA, non vérifié ; au taux du marché parallèle, environ ×1,8 ; aucun prix officiel en dinars trouvé) :

| Licence | Prix | 1 serveur | Avec le 2e serveur | Remarque |
| --- | --- | --- | --- | --- |
| **Windows 11 Pro** (recommandé) | ≈ 200 $ (prix public, **[À VÉRIFIER]**) | ≈ 26 000 DA | ≈ 53 000 DA | Suffit pour une officine ; souvent inclus avec un PC neuf ; **0 DA** si le PC a déjà une licence Windows 10 ou 11 et passe le plancher ci-dessus (mise à niveau gratuite) |
| Windows Server 2025 Essentials | ≈ 500 $ **[À VÉRIFIER : prix non trouvé]** | ≈ 66 000 DA | ≈ 132 000 DA | Sans licence par poste ; n'apporte rien de nécessaire ici |
| Windows Server 2025 Standard | ≈ 1 176 $ (16 cœurs) + ≈ 40 $ par poste (CAL) ; 5 postes ≈ 1 376 $ | ≈ 182 000 DA | ≈ 364 000 DA | Trop cher pour une officine |

- Pour un vieux PC, le vrai coût est souvent **un PC neuf**, généralement livré avec Windows 11.
- **Clés vendues 1 000 à 5 000 DA** sur les petites annonces (« licence officielle ») : très probablement des clés de revente non
  autorisées, désactivables par Microsoft ; **déconseillées** pour une pharmacie (contrôles, données de santé) : exiger la facture
  d'un revendeur agréé.
- Pour un chiffre exact **[À VÉRIFIER]** : nombre d'officines et de 2e serveurs, âge et Windows des PC actuels, prix d'un revendeur
  agréé en Algérie ou taux de change à retenir ; prix des PC conformes en Algérie non relevés.

### Conventions de commandes

- Une fenêtre **PowerShell « en administrateur »** : clic droit sur PowerShell, « Exécuter en tant qu'administrateur ».
- Dans chaque fenêtre PowerShell du PC serveur, définir une fois :
  ```powershell
  $psql = "C:\Program Files\PostgreSQL\18\bin\psql.exe"
  ```
  Si la version de PostgreSQL est différente, adapter le numéro de dossier.
- psql demande le mot de passe à chaque connexion. Pour éviter la saisie répétée, PostgreSQL accepte un fichier `pgpass.conf` (documentation officielle de PostgreSQL). **[À CONFIRMER : le technicien le crée-t-il ?]**
- Si PowerShell refuse un script (stratégie d'exécution), lancer : `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1 <paramètres>`.
- Les scripts se lancent **depuis le dossier du dépôt** (ou de la livraison), là où se trouve le dossier `scripts`.

### Décisions déjà prises

- **HTTPS seul sur le réseau de l'officine** (décision du 2026-11-16). HTTP seulement sur le PC serveur lui-même (`http://localhost:5080`). Hors développement, l'API **refuse de démarrer** tant qu'elle écoute en HTTP sur le réseau.
- **Synchronisation avec le serveur central désactivée par défaut.** Rien ne part tant que le titulaire ne l'active pas (parcours E).
- **Clé de scellement des journaux** : exportée par le titulaire dans un fichier protégé par mot de passe. Voir la section « Clé de scellement » dans les annexes.
- **Le paquet `scripts/paquet/` est pour un essai** (données de démonstration fictives). Ne jamais l'utiliser pour une officine réelle.

### Ports

| Port | Où | Qui peut s'y connecter | Rôle |
| --- | --- | --- | --- |
| 5432 | PC serveur | **Personne sur le réseau**, sauf le 2e serveur (parcours C) | PostgreSQL |
| 5080 | PC serveur | `localhost` seulement (jamais le réseau) | API en HTTP, pour le PC serveur lui-même |
| 5443 | PC serveur | Postes, téléphones (réseau privé) | API en HTTPS |
| 5090 | Serveur central | `127.0.0.1` par défaut **[À VÉRIFIER : exposition à l'hébergement]** | Serveur central (HTTPS) |

### Services Windows

| Service | Où | Démarrage | Remarque |
| --- | --- | --- | --- |
| `postgresql-x64-18` (nom donné par l'installeur PostgreSQL) | PC serveur, 2e serveur, PC de secours | Automatique | Ne pas arrêter pendant la journée |
| `PharmaOfficine API` | PC serveur | Automatique | Créé à l'étape 4.7 du parcours A |
| `PharmaOfficine API` | 2e serveur, PC de secours | **Manuel** (arrêté) | Démarré seulement en cas de bascule |
| Serveur central | Hébergement | **[À CONFIRMER : pas de service Windows dans le code, voir parcours E, étapes 3.2 et 5.2]** | |

### Variables d'environnement de l'API (système, scope Machine)

| Variable | Obligatoire | Exemple (à remplacer) | Rôle |
| --- | --- | --- | --- |
| `ConnectionStrings__Pharmacie` | oui | `Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe>` | Connexion à la base. Pas de caractère « ; » dans le mot de passe |
| `Jwt__Cle` | oui | 64 caractères minimum, aléatoire | Signature des connexions. Refus si absente ou trop courte |
| `Chifa__DossierExport` | oui | `<dossier d'export de CHIFA-Officine sur ce PC>` | Par défaut `D:\CHIFA_OFFICINE\CNAS_SIGNED` : **à remplacer** |
| `Ged__Dossier` | non | `C:\ProgramData\PharmaOfficine\GED` (défaut) | Dossier de tous les documents |
| `Ged__RetentionSauvegardesJours` | non | `7` (défaut) | Conservation des copies de base dans `GED\Sauvegardes` |
| `Ordonnances__Dossier` | non | défaut : `GED\Ordonnances` | Scans d'ordonnances |
| `Journaux__Dossier`, `Journaux__Prefixe`, `Journaux__TailleMaxMo` (50), `Journaux__RetentionMois` (3), `Journaux__Niveau` (`Information` ou `Warning`) | non | | Journal du serveur. Valeurs écrites dans la section `Journaux` de `appsettings.json` (dossier, taille maximale d'un fichier, durée de conservation, niveau) ; les variables d'environnement les remplacent. Le dossier accepte `%VARIABLE%` |
| `LimiteConnexions__ParMinute` | non | `20` (défaut) | Essais de connexion permis par minute et par adresse |
| `AutoriserHttpReseau` | **non, à ne pas régler** | | Dérogation provisoire : HTTP servi sur le réseau, en clair |
| `SecondServeur__AutreServeur` | seulement si parcours C | IP de l'autre serveur | Évite que deux API écrivent |
| `SuperAdmin:Fichier` | non | `C:\ProgramData\PharmaOfficine\super-admin.dat` (défaut) | Compte super admin |
| `Integrite:FichierCle` | non | `C:\ProgramData\PharmaOfficine\cle-integrite.dat` (défaut) | Clé de scellement |

Côté postes :

| Variable | Où | Valeur |
| --- | --- | --- |
| `PHARMA_API` | chaque poste | `https://<serveur>:5443/` (HTTPS, obligatoire sauf sur le PC serveur) |
| `PHARMA_POSTE` | chaque poste | un nom **différent** par poste, ex. `Caisse 1`, `Caisse 2` (c'est le tiroir de la caisse) |

Côté serveur central (outils d'administration) : voir parcours E, étape 3.2.

### Fichiers et dossiers à connaître

| Chemin | Où | Contenu | À sauvegarder |
| --- | --- | --- | --- |
| `C:\Program Files\PharmaOfficine\Api` | PC serveur | Programme de l'API, `appsettings.Https.json`, `database\` (scripts 001 à 159, sans `111b`) | Non (republié à chaque mise à jour) |
| `C:\ProgramData\PharmaOfficine\super-admin.dat` | PC serveur | Compte super admin, chiffré pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\cle-integrite.dat` | PC serveur | Clé de scellement des journaux, chiffrée pour ce PC | **Oui**, sur le disque externe |
| `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM` | PC serveur | Rapports scellés, à conserver 6 ans au moins | **Oui** |
| `C:\ProgramData\PharmaOfficine\GED` | PC serveur | Documents (ordonnances, Chifa, factures, rapports…) | Oui (sous-dossier `GED` du disque externe) |
| `C:\ProgramData\PharmaOfficine\Journaux` | PC serveur | Journal du serveur (`pharma-api-AAAAMMJJ.log`) | Non |
| `C:\ProgramData\PharmaOfficine\Journaux\Central` | serveur central | Journaux de l'API du central (`central-api-AAAAMMJJ.log`) et des écrans du central (`central-ecrans-AAAAMMJJ.log`) : section `Journaux` de leur `appsettings.json` (même règles : un fichier par jour, 50 Mo puis `_2`, `_3`…, conservés 3 mois) | Non |
| `C:\ProgramData\PharmaOfficine\poste.json` | chaque poste | Réglages du poste (imprimantes, adresse de secours) | Non |
| `C:\ProgramData\PharmaOfficine\copie-secours.json` | PC serveur | Réglages et état de la copie de secours | Non |
| `C:\ProgramData\PharmaOfficine\Synchronisation` | PC serveur | Clé privée de synchronisation (DPAPI, ce PC seulement) | **Ne jamais copier sur un autre serveur** |

---

## Parcours A, installation neuve

Ordre : étapes 1 à 15. Chaque étape dépend de la précédente. Ne pas démarrer le service de l'API (étape 4.7) avant le certificat HTTPS (étape 4.6), sinon l'API refuse de démarrer.

Calendrier type :

| Jour | Étapes |
| --- | --- |
| J-15 | Répétition générale complète sur une copie (étapes 1 à 4, puis 9 à 12 si reprise de données) |
| J-7 | Formation, paramétrage et sauvegardes (étapes 8 à 11) |
| J-1 au soir | Clôture de caisse de l'ancien logiciel, sauvegarde, export, reprise (étape 12), contrôle des totaux, vérifications (étape 14) |
| J | Ouverture, accompagnement sur place (étape 15) |
| J+1 à J+30 | Ancien logiciel en lecture seule ; étiquetage progressif du stock |

**Critère de retour arrière**, fixé à l'avance : si les totaux ne concordent pas la veille au soir, on rouvre l'ancien logiciel et on reporte.

### 1. Préparer avant le jour J (J-15 à J-7)

- [ ] **1.1** Matériel. **Où** : sur place. **Compte** : aucun.
  - PC serveur toujours allumé, avec un **onduleur obligatoire** contre les coupures de courant. Désactiver la mise en veille du PC serveur (Paramètres, Système, Alimentation).
  - Un poste par caisse. Chaque poste a : une imprimante de tickets avec son pilote Windows, et un lecteur de code-barres (douchette) si utilisé.
  - Imprimante d'étiquettes réseau (facultative).
  - Deux disques externes pour les sauvegardes, et une clé USB pour les certificats et la clé de scellement.
  - **Attendu** : tout le matériel est là et branché.

- [ ] **1.2** Versions de Windows. **Où** : chaque PC. **Compte** : aucun.

  | Machine | Minimum technique | Recommandé |
  | --- | --- | --- |
  | Poste de caisse | Windows 10 version 2004 (build 19041) | Windows 11 (22H2 ou plus), ou Windows 10 IoT Enterprise LTSC |
  | PC serveur | Windows 10 ou Windows Server 2016 | Windows 11 Pro, ou Windows Server 2019, 2022 ou 2025 |

  Windows 10 22H2 n'est plus maintenu depuis le 14 octobre 2025, sauf éditions LTSC. Pour des données de santé, préférer Windows 11. Le logiciel n'a été essayé que sous Windows 11 : **faire un essai sur le système réel avant le jour J**.
  Les téléphones n'ont besoin que d'un navigateur récent.

- [ ] **1.3** Réseau. **Où** : PC serveur et box. **Compte** : administrateur Windows.
  - Donner au PC serveur une **adresse IP fixe** (réglage de la box ou du PC). Noter l'adresse.
  - Vérifier : `ipconfig` affiche l'adresse retenue.
  - Wi-Fi seulement si le scanner mobile est utilisé.

- [ ] **1.4** Informations à demander à la pharmacie. **Où** : papier, au titulaire.
  - Nom, adresse, téléphone de la pharmacie.
  - **RC, NIF, NIS, AI.**
  - N° d'officine Chifa.
  - Liste des employés et de leurs rôles (assistant, vendeur, stagiaire).
  - Banques.
  - Organismes conventionnés (par exemple les militaires) avec leur adresse et leur NIF.
  - Compte e-mail (SMTP) et/ou modem GSM pour les alertes au titulaire.
  - Le logiciel **CHIFA-Officine** est installé sur le PC serveur, et son dossier d'export est connu (**[À CONFIRMER : procédure d'installation fournie par la CNAS, hors du dépôt]**). Lecteur de carte et Token : **[À CONFIRMER : à installer sur quel PC]**.

- [ ] **1.5** Nomenclature nationale. **Où** : PC des sources ou PC serveur. **Compte** : aucun.
  - Le fichier livré est `database\data\NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx`.
  - Si le ministère a publié une version plus récente (miph.gov.dz), la garder pour l'étape 9.8.

- [ ] **1.6** Outils sur le PC des sources. **Où** : PC des sources. **Compte** : administrateur Windows.
  - Installer le **SDK .NET 10** (nécessaire pour `dotnet publish`). **[À CONFIRMER : qui compile les livraisons de production ?]**
  - Vérifier : `dotnet --version` affiche une version 10.x.
  - **Attendu** : la commande répond. Si elle ne répond pas, installer le SDK puis rouvrir PowerShell.

- [ ] **1.7** Répétition. **Où** : copie de test. Pour une reprise de données, faire une répétition générale complète sur une **copie** de l'ancienne base (voir étape 12). L'ancienne base n'est jamais modifiée.

- [ ] **1.8** Décider si un PC de secours est prévu. Si oui, **réaliser le parcours D maintenant**, après l'étape 4 (il dépend seulement du PC serveur installé).

### 2. PC serveur : PostgreSQL

- [ ] **2.1** Installer PostgreSQL 18, avec ses outils `psql` et `pg_dump` (inclus par défaut dans l'installeur). **Où** : PC serveur. **Compte** : administrateur Windows.
  - Noter le mot de passe du superutilisateur `postgres` : il ne sert qu'à l'installation et aux procédures de la base. Le garder au coffre.
  - Garder le port `5432` proposé par défaut.
  - Prérequis : Windows 10 ou plus (runtime C « UCRT »).
  - **Attendu** : le service `postgresql-x64-18` existe et démarre.
  - Vérifier :
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\psql.exe" --version
    Get-Service postgresql* | Select-Object Name, Status, StartType
    ```
    La version commence par `psql (PostgreSQL) 18`. Le service est `Running` et `Automatic`.
  - **Si échec** : relancer l'installeur ; vérifier qu'aucune autre version PostgreSQL n'est en conflit sur le port 5432.

- [ ] **2.2** Compte de l'application `pharma_app`. **Où** : PC serveur. **Compte** : superutilisateur `postgres`.
  **Rien à faire ici pour une installation neuve** : le script de l'étape 3.1 crée le compte `pharma_app` (avec le droit `CREATEDB`, qui permet au super admin de restaurer une sauvegarde depuis l'application) **et** la base, et demande le mot de passe à lui donner : **propre à cette pharmacie**, jamais celui de démonstration, 12 caractères minimum, noté au coffre. **Ne pas créer la base à la main** : le script refuse une base qui existe déjà.
  Seul cas où l'on crée le compte à la main (PC de secours, parcours D) : psql en interactif (`& $psql -h localhost -U postgres -d postgres`), puis, ligne par ligne (le mot de passe est demandé par `\password`, jamais écrit dans une commande) :
  ```sql
  CREATE ROLE pharma_app LOGIN CREATEDB;
  \password pharma_app
  \q
  ```
  - Compte déjà créé sans le droit `CREATEDB` : `ALTER ROLE pharma_app CREATEDB;`.
  - **Attendu** : `CREATE ROLE`, `\password` demande deux fois le mot de passe.
  - **Si échec** : `already exists` signifie que le compte existe déjà. Vérifier avec `\du pharma_app`.

- [ ] **2.3** Régler le fuseau horaire de Windows (Algérie, UTC+1 toute l'année). **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Set-TimeZone -Id "W. Central Africa Standard Time"
  Get-TimeZone
  ```
  - **Attendu** : `Get-TimeZone` affiche « (UTC+01:00) Afrique centrale - Ouest ». Sinon, l'API écrit un avertissement au démarrage et les dates des ventes sont décalées.
  - **Si échec** : régler à la main dans Paramètres, Heure et langue, Date et heure, Fuseau horaire.

- [ ] **2.4** Vérifier que PostgreSQL n'est pas ouvert au réseau. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Get-NetTCPConnection -LocalPort 5432 -State Listen | Select-Object LocalAddress
  ```
  - **Attendu** : seulement `127.0.0.1` et `::1`. Aucune règle de pare-feu n'ouvre 5432 aux postes : l'application passe par l'API, pas par PostgreSQL.
  - **Si échec** : une adresse du réseau apparaît. Vérifier `listen_addresses` dans le fichier `postgresql.conf` de PostgreSQL et le remettre sur `localhost`.

### 3. Base de données : structure, référentiel, droits

Un seul script enchaîne tout dans le bon ordre, avec des contrôles : `scripts\mep\creer_base_officine.ps1`. Référence complète (options, étapes internes, retour arrière) : [scripts-base-de-donnees.md](scripts-base-de-donnees.md). Lancer la commande **depuis le dossier du dépôt** (ou de la livraison).

- [ ] **3.1** Créer la base, la structure, le référentiel et séparer les rôles. **Où** : PC serveur. **Compte** : superutilisateur `postgres` pour créer le compte et la base (mot de passe saisi à l'écran), puis `pharma_app` pour la structure (mot de passe saisi à l'écran, ou à définir si le compte n'existe pas encore). Aucun mot de passe n'est écrit dans une commande.
  ```powershell
  .\scripts\mep\creer_base_officine.ps1 -Base <base>
  ```
  - `<base>` : exemple `pharmacie`. Pour voir le plan sans rien créer : ajouter `-WhatIf`.
  - Le script : vérifie que l'init (`schema.sql`, `seed_referentiel.sql`) est à jour (« 001 à NNN » = dernier script) ; vérifie que PostgreSQL répond et que la base **n'existe pas** ; crée le compte `pharma_app` s'il manque (avec le droit `CREATEDB`) ; crée la base ; passe `schema.sql` (tous les scripts `001` à `NNN`, sauf le fuseau horaire), `083_fuseau_horaire.sql`, puis `seed_referentiel.sql` (identité par défaut, rôles, droits, organismes CNAS, CASNOS et MDN, substances surveillées, coffre, catégories, motifs, réglages de synchronisation désactivée, compte de départ `titulaire`) ; puis la séparation des rôles `111b` avec `postgres` (journaux inaltérables : `pharma_app` ne peut plus modifier ni supprimer les journaux, ni désactiver leurs déclencheurs) ; enfin ses vérifications (`objets_encore_a_pharma_app = 0`, comptages de référence, lecture et écriture par `pharma_app`).
  - **Attendu** : se termine par « Terminé », code de sortie 0 (`$LASTEXITCODE`), message « Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à changer à la première connexion). »
  - **Interdit** : ne jamais lancer les fichiers `database\data\9xx_*.sql` (données fictives, réservées aux tests). Le script `101b` n'est jamais lancé par le script.
  - Option `-SansSeparationRoles` : **essais seulement**. Sans `111b`, `pharma_app` peut désactiver les déclencheurs qui protègent les journaux : la vérification nocturne détecte alors la falsification, mais ne l'empêche pas. Le script le signale en avertissement.
  - Après cette étape, les **futurs** scripts passent par `mettre_a_jour_base_officine.ps1` (parcours B), avec `postgres` et `SET ROLE pharma_proprietaire`. Ne plus jamais les lancer avec `pharma_app`.
  - **Si échec** : le script s'arrête à la première erreur et affiche la cause. « La base existe déjà » : normal, il ne crée que des bases neuves et ne supprime jamais rien (voir « Recommencer depuis zéro »). « L'en-tête de schema.sql ne cite pas 001 à NNN » : l'init est en retard, rien n'a été fait, voir le développeur. `à exécuter par un superutilisateur` : le compte d'administration n'est pas `postgres`.

- [ ] **3.2** Contrôler la base (le script l'a déjà fait ; contrôle indépendant, facultatif). **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -tAc "show timezone"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM utilisateur"
  & $psql -h localhost -U pharma_app -d <base> -tAc "SELECT count(*) FROM vente"
  ```
  - **Attendu** : `Africa/Algiers` (l'API ajoute aussi `Timezone=Africa/Algiers` à sa chaîne de connexion si elle n'en précise pas) ; au moins `1` utilisateur (compte titulaire) ; `0` vente (base vierge, aucune donnée de démonstration).
  - **Si échec** : `relation does not exist` : la structure n'a pas été créée, relancer l'étape 3.1 sur une base neuve.

- **Recommencer depuis zéro** (seulement avant la mise en service, jamais sur une base réelle) :
  1. Faire une sauvegarde (étape 11.2) si la base contient quoi que ce soit à garder.
  2. Le script refuse une base qui existe. La supprimer à la main est une **décision humaine**, jamais faite par un script : avec `postgres`, en psql interactif (`& $psql -h localhost -U postgres -d postgres`), taper la commande de suppression de la base `<base>` en toute connaissance de cause, puis `\q`.
  3. Reprendre à l'étape 3.1. (Le fichier `database\init\clean.sql`, qui vide une base sans la supprimer, ne sert plus à ce parcours : le script a besoin d'une base absente.)

### 4. PC serveur : API, configuration, HTTPS, service

- [ ] **4.1** Publier l'API. **Où** : PC des sources. **Compte** : administrateur Windows. Depuis le dossier du dépôt :
  ```powershell
  dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
  ```
  - `--self-contained true` : aucun runtime .NET à installer sur le PC serveur.
  - Le dossier publié contient `Pharma.Api.exe` et `database\` (scripts `001` à `159`, sans `111b`, qui se lance à part).
  - Contrepartie : les correctifs de sécurité de .NET n'arrivent qu'avec une nouvelle publication (parcours B).
  - **Attendu** : `Pharma.Api.exe` et le dossier `database` sont présents dans `<dossier de livraison>\Api`.
  - **Si échec** : SDK absent (étape 1.6) ou version différente de .NET 10.

- [ ] **4.2** Copier l'API sur le PC serveur. **Où** : PC serveur (depuis la clé USB). **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "C:\Program Files\PharmaOfficine" | Out-Null
  Copy-Item -Recurse -Force "E:\<dossier de livraison>\Api" "C:\Program Files\PharmaOfficine\"
  Test-Path "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"
  ```
  - **Attendu** : `True`.

- [ ] **4.3** Copier le dossier `scripts` du dépôt (ou de la livraison) sur le PC serveur. **Où** : PC serveur. **Compte** : administrateur Windows. Il contient `certificat_https.ps1`, `sauvegarde_pharmacie.ps1`, `restaurer_pharmacie.ps1`, `basculer-pc-secours.ps1` et `second-serveur\`. **[À CONFIRMER : dossier de destination officiel sur le PC serveur, par exemple `C:\Program Files\PharmaOfficine\scripts`.]**
  - Ces scripts **ne sont pas** dans le dossier publié de l'API.

- [ ] **4.4** Régler les variables d'environnement de l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  Ouvrir une **nouvelle** fenêtre PowerShell en administrateur. Remplacer les valeurs `<...>` :
  ```powershell
  [Environment]::SetEnvironmentVariable("ConnectionStrings__Pharmacie", "Host=localhost;Port=5432;Database=<base>;Username=pharma_app;Password=<mot de passe pharma_app>", "Machine")
  [Environment]::SetEnvironmentVariable("Chifa__DossierExport", "<dossier d'export de CHIFA-Officine>", "Machine")
  ```
  Générer la clé de signature (64 caractères minimum, 72 ici). La noter au coffre, puis la coller dans la commande suivante :
  ```powershell
  $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
  $octets = New-Object byte[] 72
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($octets)
  -join ($octets | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
  ```
  ```powershell
  [Environment]::SetEnvironmentVariable("Jwt__Cle", "<clé générée>", "Machine")
  ```
  - **Ne jamais** mettre la clé ou le mot de passe dans `appsettings.json`, dans le dépôt, ni dans un courriel.
  - **Vérifier** sans afficher les secrets :
    ```powershell
    [Environment]::GetEnvironmentVariable("Jwt__Cle", "Machine").Length    # doit être 64 ou plus
    [Environment]::GetEnvironmentVariable("ConnectionStrings__Pharmacie", "Machine") -ne $null
    ```
  - **Attendu** : `True`, et un nombre ≥ 64.
  - **Si échec** : les variables ne sont pas vues par l'API tant qu'elle n'a pas été redémarrée (étape 4.7 : le service se lance avec les variables système).

- [ ] **4.5** Régler les réglages facultatifs (seulement si besoin). **Où** : PC serveur. **Compte** : administrateur Windows. Mêmes commandes que 4.4 (`SetEnvironmentVariable`, scope `Machine`) :
  - `Ged__Dossier` : dossier unique des documents (défaut `C:\ProgramData\PharmaOfficine\GED`) ;
  - `Ged__RetentionSauvegardesJours` : défaut `7` ;
  - `Ordonnances__Dossier` : à régler seulement pour sortir les scans de la GED ;
  - `Journaux__Dossier`, `Journaux__TailleMaxMo`, `Journaux__RetentionMois`, `Journaux__Niveau` : journal du serveur (un fichier par jour) ;
  - `LimiteConnexions__ParMinute` : défaut `20`.
  - **Ne pas** régler `AutoriserHttpReseau`.
  - `Sauvegardes__PgDump` : chemin de `pg_dump.exe` si l'API ne le trouve pas. **[À CONFIRMER : comportement de l'API quand la valeur est vide.]**
  - Ne pas modifier `Urls` dans `appsettings.json` : c'est le fichier `appsettings.Https.json` (étape 4.6) qui prend le relais.

- [ ] **4.6** Chiffrer les échanges (HTTPS) : **une seule autorité pour toute la pharmacie**. **Où** : PC serveur. **Compte** : administrateur Windows. Deux clés USB : celle des **postes** (ici `E:\`, certificat public) et celle du **coffre** (ici `F:\`, clé privée de l'autorité).
  Le script crée l'autorité de la pharmacie (une seule fois, valable 10 ans) et sauvegarde sa clé **une fois** dans un fichier PFX protégé par une phrase de passe saisie à l'écran. Il crée ensuite le certificat du serveur (825 jours), écrit `appsettings.Https.json`, ouvre le port 5443 en réseau **privé** seulement, et exporte le certificat **public** de l'autorité et son empreinte sur la clé USB des postes. Cette autorité signera aussi les certificats du 2e serveur et du PC de secours : les postes l'installent **une seule fois** (étape 5.4).
  Dans le magasin Windows du PC, la clé de l'autorité est **non exportable** : le PFX du coffre est donc le **seul exemplaire de sauvegarde**, et il ne peut pas être refait plus tard. Le perdre oblige, le jour d'un remplacement du PC serveur, à créer une nouvelle autorité et à la réinstaller sur tous les postes et téléphones.

  0. **Se placer dans le dossier qui contient `scripts`** (celui de l'étape 4.3) :
     ```powershell
     Set-Location "<dossier qui contient scripts>"
     Test-Path .\scripts\certificat_https.ps1
     ```
     - **Attendu** : `True`. Si PowerShell refuse ensuite le script, voir « Conventions de commandes » (stratégie d'exécution).
  1. **Essai à blanc d'abord** (rien n'est écrit, aucune phrase de passe demandée) :
     ```powershell
     .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -PareFeu -WhatIf
     ```
     - **Attendu** : chaque action est annoncée avec la mention `[SIMULATION]` (création de l'autorité, sauvegarde du PFX, certificat du serveur, configuration, export, pare-feu), et la fin dit « rien n'a été modifié ».
  2. **Lancement réel** (clé USB du coffre `F:\` branchée) :
     ```powershell
     .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -PareFeu
     ```
     - Le script demande une **phrase de passe** (12 caractères au moins), puis la même une seconde fois. Elle protège le PFX ; elle n'est jamais passée en paramètre ni écrite dans un fichier ou un journal. **La noter sur papier, au coffre, à part de la clé USB.**
     - Le script affiche les noms et adresses du certificat (par défaut : `localhost`, `127.0.0.1`, le nom du PC et ses adresses IPv4 privées), puis l'empreinte SHA-256 de l'autorité.
     - Noter cette empreinte. Elle est aussi écrite dans `E:\empreinte-autorite-pharmacie.txt`.
     - Relancé plus tard, le script réutilise l'autorité (il n'a plus besoin de `-FichierSauvegardeAutorite`) et ne recrée pas le fichier PFX.
  3. **Noms et adresses supplémentaires** : un poste ne fait confiance au serveur que pour un nom ou une adresse **présents dans le certificat**. Rien n'est deviné : ajouter seulement ce qui sert, par exemple l'adresse VPN du PC serveur (accès du titulaire, parcours « acces-distant.md ») : `-AdressesSupplementaires <adresse VPN>`, `-NomsSupplementaires <nom>`. Relancer alors la commande avec les paramètres ajoutés (idempotente : l'autorité est conservée, seul le certificat du serveur est refait). **Le certificat du 2e serveur et du PC de secours se génère à part** (parcours C, étape 8, et parcours D, étape 6) : ne pas mettre leurs noms ici.
  4. **Ranger le PFX au coffre** : retirer la clé USB `F:\` ; la ranger **hors ligne**, au coffre ou à l'extérieur de l'officine. Faire une **seconde copie** du PFX sur une autre clé (même phrase de passe) conservée ailleurs. Ne jamais copier ce PFX sur un poste, un téléphone, la clé USB `E:\`, ni par le réseau ou courriel.
  5. **Fichiers créés** : vérifier.
     ```powershell
     Get-Content "C:\Program Files\PharmaOfficine\Api\appsettings.Https.json"
     Get-ChildItem E:\, F:\ | Select-Object FullName
     Get-NetFirewallRule -DisplayName "PharmaOfficine API (https 5443)" | Select-Object DisplayName, Enabled
     Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*PharmaOfficine*" } | Select-Object Subject, NotAfter, HasPrivateKey
     ```
     - **Attendu** : `appsettings.Https.json` contient `"Url": "http://localhost:5080"` et `"Url": "https://0.0.0.0:5443"`. `E:\` contient `autorite-pharmacie.crt` et `empreinte-autorite-pharmacie.txt` (et **pas** de `.pfx`). `F:\` contient `autorite-pharmacie.pfx`. La règle de pare-feu est `Enabled : True`. Le magasin contient deux certificats : `CN=PharmaOfficine Autorite pharmacie` (10 ans) et `CN=PharmaOfficine Mobile` (825 jours), `HasPrivateKey : True`.
  6. **Test de relecture du PFX** (conseillé, sans rien modifier ; le script ne l'importe pas) : sur le PC serveur, `certutil -dump F:\autorite-pharmacie.pfx` demande la phrase de passe ; une phrase juste affiche le certificat, une fausse est refusée.
  - **Décision** : le certificat du serveur est valable 825 jours : le renouveler avant l'échéance (procédure F6).
  - **Si échec** : « Dossier de l'API introuvable » : étape 4.2 non faite. « Adresse IP supplémentaire invalide » : vérifier la syntaxe. « Première exécution : … Préciser -FichierSauvegardeAutorite » : ajouter le paramètre. « Dossier introuvable pour -FichierSauvegardeAutorite » : clé du coffre non branchée. « Le fichier existe déjà » : le PFX n'est jamais écrasé ; pour l'utiliser, voir F2 bis. « Les deux saisies sont différentes » ou « 12 caractères au moins » : recommencer, rien n'a été écrit. « ne doit pas être rangé avec le certificat public » : choisir un autre dossier que celui des postes.
  - **Ancienne autorité « par PC »** : si le script avertit « Ancienne autorité … trouvée », suivre la procédure F7. Elle n'est jamais supprimée automatiquement.

- [ ] **4.7** Installer l'API comme service Windows (démarrage automatique). **Où** : PC serveur. **Compte** : administrateur Windows.
  Le service doit démarrer **après** l'étape 4.6.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Automatic
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Status : Running`.
  - Les guillemets autour du chemin sont **obligatoires** (le chemin contient des espaces). Les versions précédentes de ce manuel donnaient une commande `sc.exe` sans ces guillemets : elle était fausse.
  - **Si échec** : le service se ferme aussitôt. Lire le journal du serveur (voir « Dépannage » en annexe) : le message « Démarrage refusé : l'API écoute en HTTP (non chiffré) » signifie que l'étape 4.6 n'est pas faite. Un message sur `Jwt:Cle` signifie que la variable (4.4) est absente ou trop courte.

- [ ] **4.8** Vérifier l'API sur le PC serveur. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  (Invoke-WebRequest http://localhost:5080/api/healthcheck -UseBasicParsing).StatusCode
  ```
  - **Attendu** : `200`.
  - **Si échec** : ouvrir le service (`Get-Service`), puis le journal.

- [ ] **4.9** Sauvegarder la clé de scellement créée au premier démarrage. **Où** : PC serveur. **Compte** : administrateur Windows.
  - Vérifier que le fichier existe : `Test-Path "C:\ProgramData\PharmaOfficine\cle-integrite.dat"` → `True`.
  - Copier `cle-integrite.dat` sur le **disque externe**, dans un dossier à part (`E:\Securite\`). Le fichier n'est lisible que sur **ce** PC. Sans lui, les sceaux déjà posés ne peuvent plus être vérifiés.
  - **Attendu** : copie faite et vérifiée (taille identique).

### 5. Postes de caisse

Faire ces étapes **sur chaque poste**, une caisse après l'autre.

- [ ] **5.1** Vérifier le navigateur intégré (WebView2). **Où** : chaque poste. **Compte** : administrateur Windows.
  - Il est présent sur Windows 10 et 11 à jour. Vérifier dans Paramètres, Applications, Applications installées : « Microsoft Edge WebView2 Runtime ».
  - S'il manque, l'installer (installeur gratuit de Microsoft) avant d'ouvrir le poste.
  - **Attendu** : l'élément est présent.

- [ ] **5.2** Installer les pilotes des imprimantes de tickets et d'étiquettes, et brancher la douchette. **Où** : chaque poste. **Compte** : administrateur Windows. Les pilotes viennent du fabricant. Imprimer une page de test depuis Windows.

- [ ] **5.3** Copier l'autorité de la pharmacie sur le poste. **Où** : chaque poste. **Compte** : administrateur Windows.
  - Copier `autorite-pharmacie.crt` de la clé USB vers un dossier temporaire du poste (par exemple `C:\Temp\`). **Jamais** par le réseau HTTP.
  - Vérifier l'empreinte : la commande compare le fichier copié avec l'empreinte notée par le script (3e ligne de `empreinte-autorite-pharmacie.txt`) :
    ```powershell
    $calculee = (Get-FileHash "C:\Temp\autorite-pharmacie.crt" -Algorithm SHA256).Hash
    $attendue = ((Get-Content "E:\empreinte-autorite-pharmacie.txt")[2]) -replace '[^0-9A-Fa-f]', ''
    "Calculée : $calculee"; "Attendue : $attendue"; $calculee -eq $attendue
    ```
  - **Attendu** : `True` (la comparaison ignore les `:` et la casse).
  - **Si échec** : `False` = ne pas installer, ne pas continuer, refaire la copie depuis la clé USB.

- [ ] **5.4** Installer l'autorité dans les racines de confiance de l'ordinateur. **Où** : chaque poste. **Compte** : administrateur Windows.
  ```powershell
  certutil -addstore -f Root "C:\Temp\autorite-pharmacie.crt"
  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite pharmacie" } | Select-Object Subject, NotAfter
  ```
  - Alternative graphique : double-clic sur le fichier, « Installer le certificat… », « Ordinateur local », « Placer tous les certificats dans le magasin suivant », **Autorités de certification racines de confiance**.
  - **Attendu** : la commande `Get-ChildItem` affiche une ligne.
  - Supprimer ensuite le fichier du dossier temporaire.

- [ ] **5.5** Installer le programme du poste. **Où** : chaque poste. **Compte** : administrateur Windows. Depuis le PC des sources :
  ```powershell
  dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
  ```
  Copier le dossier obtenu sur chaque poste, par exemple dans `C:\Program Files\PharmaOfficine\Poste`. Créer sur le bureau un raccourci vers `Pharma.Desktop.exe`.
  - Aucun runtime .NET à installer (publication autonome).
  - Il n'existe pas encore d'installateur ni de mise à jour automatique des postes : la copie, les variables et le raccourci se font à la main.

- [ ] **5.6** Régler les variables du poste. **Où** : chaque poste. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("PHARMA_API", "https://<serveur>:5443/", "Machine")
  [Environment]::SetEnvironmentVariable("PHARMA_POSTE", "Caisse 1", "Machine")
  ```
  - `<serveur>` : nom ou IP du PC serveur, **présent dans le certificat** (étape 4.6). La barre finale est ajoutée si elle manque.
  - `PHARMA_POSTE` : **un nom différent pour chaque poste** (`Caisse 1`, `Caisse 2`…). Deux postes avec le même nom partagent la même caisse.
  - Sur le PC serveur lui-même, `http://localhost:5080/` reste permis.
  - **Attendu** : fermer et rouvrir le poste après le réglage.

- [ ] **5.7** Ouvrir Tracodex Officine. **Où** : chaque poste. **Compte** : employé.
  - **Attendu** : l'écran de connexion s'affiche, sans avertissement de certificat.
  - **Si échec** : avertissement de certificat = l'autorité n'est pas installée (5.4) ou le nom du serveur n'est pas dans le certificat (4.6, option 3). Message « serveur injoignable » = l'API ne tourne pas ou le pare-feu bloque 5443.

### 6. Téléphones (scanner mobile, facultatif)

- [ ] **6.1** Transférer l'autorité sur le téléphone **hors réseau HTTP** : câble USB ou clé USB OTG. Jamais par un lien HTTP ni par courriel.
- [ ] **6.2** Vérifier l'empreinte **avant** d'installer. Elle est affichée dans les détails du certificat du téléphone. Elle doit être **identique**, caractère pour caractère, à `empreinte-autorite-pharmacie.txt`. Sinon, ne pas installer.
  - **Android** : Paramètres, Sécurité, Chiffrement et identifiants, Installer un certificat, **Certificat CA**.
  - **iPhone** : installer le profil, puis Réglages, Général, Informations, **Réglages des certificats** (activer la confiance).
  - Pas de QR code de l'empreinte : texte seulement.
- [ ] **6.3** Ouvrir le scanner mobile : `https://<serveur>:5443/mobile/`. **Attendu** : la page s'ouvre sans avertissement. Mode d'emploi : [scanner-mobile.md](scanner-mobile.md).

### 7. Fermer le réseau HTTP et vérifier le HTTPS

Faire cette étape **après** les postes et les téléphones (sinon ils n'ont plus d'accès).

- [ ] **7.1** Vérifier qu'aucune règle de pare-feu n'ouvre le port 5080. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains '5080' } | Select-Object DisplayName
  ```
  - **Attendu** : aucune ligne. Si une règle apparaît, la désactiver : `Disable-NetFirewallRule -DisplayName '<nom de la règle>'`. Le script `certificat_https.ps1` avertit aussi s'il en trouve une.

- [ ] **7.2** Contrôler les écoutes. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  netstat -ano | findstr :5080
  netstat -ano | findstr :5443
  ```
  - **Attendu** : 5080 n'est écouté que sur `127.0.0.1` et `[::1]`. 5443 est écouté sur `0.0.0.0`.

- [ ] **7.3** Contrôler depuis un poste. **Où** : un poste. **Compte** : employé.
  ```powershell
  (Invoke-WebRequest https://<serveur>:5443/api/healthcheck -UseBasicParsing).StatusCode
  (Invoke-WebRequest https://<serveur>:5443/api/healthcheck -UseBasicParsing).Headers["Strict-Transport-Security"]
  Invoke-WebRequest http://<serveur>:5080/ -UseBasicParsing
  Invoke-WebRequest https://<serveur>:5443/mobile/autorite-pharmacie.crt -UseBasicParsing
  ```
  - **Attendu** :
    - le premier affiche `200`, sans erreur de certificat ;
    - l'en-tête `Strict-Transport-Security` est présent ;
    - la commande en `http://…:5080` **échoue** (connexion refusée ou délai dépassé) ;
    - la commande du fichier `.crt` **échoue** avec un code 404 : le certificat n'est plus publié par l'API.

- [ ] **7.4** Essai réel sur un vrai poste, avant le jour J : connexion, vente, impression, scanner mobile en HTTPS. **Où** : un poste et un téléphone. **Compte** : employé.

### 8. Premier démarrage et sécurité

- [ ] **8.1** Se connecter avec le compte de départ. **Où** : un poste. **Compte** : `titulaire` / `Provisoire-2026`.
- [ ] **8.2** Choisir le mot de passe personnel (écran « Choisissez votre mot de passe », 8 caractères minimum). Tant que ce n'est pas fait, le serveur refuse toute autre action, code comptoir compris.
  - À savoir : changer un mot de passe, désactiver un compte (employé parti, téléphone perdu) ou effacer un code comptoir ferme aussitôt les connexions déjà ouvertes de ce compte, sur tous les postes et téléphones.

### 9. Paramétrage (titulaire)

Dans l'ordre. Chaque réglage est fait dans l'application, par le titulaire ou sous sa supervision.

- [ ] **9.1** Administration, Langue et pharmacie : nom de la pharmacie, titulaire, adresse, téléphone, **RC, NIF, NIS, AI**, n° d'officine Chifa. Ces informations sont imprimées sur les tickets, factures et rapports.
- [ ] **9.2** Même page, Organismes payeurs : ajouter les organismes conventionnés (militaires…), avec adresse et NIF.
- [ ] **9.3** Administration, Utilisateurs et droits : créer **un compte par employé**, avec un identifiant et un mot de passe personnels, et le bon rôle (assistant, vendeur, stagiaire).
- [ ] **9.4** Vérifier les droits de chaque rôle. Réservés par défaut au titulaire : trésorerie, factures, inventaire complet, organismes. Le titulaire les accorde ensuite à qui il veut.
- [ ] **9.5** Chaque employé définit son **code comptoir** (bandeau, « Mon code comptoir ») et peut changer son mot de passe (bandeau, « Mot de passe »).
- [ ] **9.6** Administration, Caisse et contrôle : fond de caisse par défaut, seuil d'écart, comptage aveugle, déconnexion automatique, versement des caisses au coffre (manuel ou automatique), seuil de validation des sorties d'argent par le tiroir (défaut 2 000 DA).
  - Administration, Motifs et catégories : motifs du tiroir et leur effet (sortie, entrée, aucun).
  - Administration, Contrôle interne : alerte au-delà de N sorties du tiroir par employé et par jour (défaut 5).
- [ ] **9.7** Administration, Alertes et seuils, puis Seuils de stock : seuils de péremption et de stock. Administration, Emplacements : rayons, réfrigérateur, armoire des psychotropes, réserves.
- [ ] **9.8** Administration, Nomenclature nationale : « Importer le fichier Excel… » avec le fichier de l'étape 1.5. Contrôler les compteurs (en vigueur, non renouvelés, retirés).
  - À faire **avant** toute reprise de données : la nomenclature donne la classe réglementaire des produits.
- [ ] **9.9** Trésorerie : régler la date et le solde de départ du coffre, puis déclarer les banques (une seule reçoit les paiements par carte).
- [ ] **9.10** Administration, Notifications au titulaire : SMTP et/ou modem GSM, destinataires, puis un **message d'essai**. Le modem GSM sert aussi aux codes SMS du super admin (étape 10).
- [ ] **9.11** **Sur chaque poste** : Administration, Imprimantes et étiquettes (imprimante de tickets, largeur du rouleau, ticket automatique, imprimante d'étiquettes, afficheur client). Faire un essai d'impression et un essai de l'afficheur.
- [ ] **9.12** Si le PC de secours a été installé à l'étape 1.8 (parcours D fait), régler sur **chaque poste** l'adresse de secours : Administration, Imprimantes et étiquettes, Serveur et PC de secours. Sinon, passer cette étape.

### 10. Compte super admin (reprise de données et restauration)

- [ ] **10.1** Créer le compte. **Où** : PC serveur. **Compte** : administrateur Windows. Dans une fenêtre PowerShell en administrateur, **après** l'étape 4.4 (les variables sont lues par l'API) :
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --super-admin
  ```
  - Saisir : identifiant, mot de passe (12 caractères au moins), puis un **téléphone mobile** (SMS), une **adresse e-mail**, ou les deux. **Au moins un canal vérifié est obligatoire.** Avec les deux, le même code part sur les deux à chaque action.
  - Un code est envoyé par SMS au numéro saisi, pour le vérifier (modem GSM de l'étape 9.10), et un code est envoyé à l'adresse e-mail saisie, pour la vérifier (serveur e-mail de l'étape 9.10) : ils se saisissent dans la console. Un canal saisi mais non vérifié n'est pas enregistré, et la console le dit.
  - **Si aucun canal ne fonctionne, le compte n'est pas créé** : la console demande de régler le modem GSM ou le serveur e-mail (Administration › Notifications, étape 9.10), puis de relancer la commande. Il n'y a plus de création « sans vérification ».
  - Le compte est chiffré pour ce PC (`super-admin.dat`). Il fonctionne même si la base est détruite, et il est illisible sur un autre PC.
  - **Attendu** : le compte est créé, puis l'invite revient.
- [ ] **10.2** Sauvegarder `super-admin.dat` sur le disque externe (dossier `E:\Securite\`), comme `cle-integrite.dat` (étape 4.9).
- [ ] **10.3** Pour changer le mot de passe, le téléphone ou l'e-mail : relancer la même commande. Il faut le mot de passe actuel et le code envoyé sur les canaux vérifiés du compte (un seul code, reçu par l'un ou l'autre), puis, si le numéro change, le code reçu par SMS sur le nouveau numéro ; si l'adresse change, le code reçu par e-mail à la nouvelle adresse. Pour une saisie, laisser vide garde la valeur actuelle et « - » retire le canal ; **le dernier canal vérifié ne peut pas être retiré**, et un nouveau canal non vérifié n'est pas enregistré (l'ancien reste).
- [ ] **10.4** Téléphone et e-mail perdus : un administrateur du PC serveur supprime `super-admin.dat`, puis recrée le compte (étape 10.1). Ne jamais supprimer le compte sans cette procédure.

### 11. Sauvegardes (avant toute vraie donnée)

- [ ] **11.1** Choisir **un seul** mécanisme. **[À CONFIRMER : décision du titulaire.]**
  - (a) **Sauvegarde de l'application** : Administration, Sauvegardes. Elle copie la base sur le disque externe et dans `GED\Sauvegardes` (gardée 7 jours par défaut), et les documents de la GED sur le disque externe, dans son sous-dossier `GED` (nouveaux et modifiés seulement). Un disque absent n'empêche pas la copie dans la GED.
  - (b) **Script planifié** `scripts\sauvegarde_pharmacie.ps1`. Son en-tête indique : « Choisir ce script OU la sauvegarde automatique de l'application : pas les deux ».
  - Ne jamais garder les deux.
- [ ] **11.2** Option (a) : Administration, Sauvegardes : régler le dossier (disque externe) et la rétention, puis lancer une **première sauvegarde**. **Où** : un poste ou le PC serveur. **Compte** : titulaire.
  - **Attendu** : un fichier `pharmacie_AAAAMMJJ_HHMMSS.dump` apparaît dans le dossier. Vérifier sa lisibilité :
    ```powershell
    & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" --list "E:\Sauvegardes\Pharmacie\<fichier>.dump" > $null
    $LASTEXITCODE
    ```
    Le résultat doit être `0`.
- [ ] **11.2 bis** **Officine sans 2e serveur : deux disques externes en rotation, obligatoire** (décision du titulaire du 2026-11-18, [ged-protection-a-decider.md](ged-protection-a-decider.md)). **Où** : PC serveur. **Compte** : titulaire.
  - Deux disques avec la même lettre de lecteur (réglée dans Gestion des disques de Windows) et le même dossier de sauvegarde. Un disque branché au PC serveur, **l'autre toujours hors de l'officine** (domicile du titulaire) ; échange au moins une fois par semaine.
  - Après chaque échange : « Sauvegarder maintenant » ; la sauvegarde complète le dossier `GED` du disque (documents nouveaux et modifiés).
  - Délai d'alerte : Administration › Sauvegardes, « Alerte si aucune sauvegarde sur le disque depuis (jours) », 7 par défaut (1 à 90). Le titulaire est prévenu (SMS si « SMS sauvegarde » est coché, et e-mail) quand aucune sauvegarde n'a réussi sur le disque depuis ce délai, même si la sauvegarde automatique est désactivée ; une alerte par période.
  - L'alerte lit le journal des sauvegardes de l'application ; le script planifié de l'option (b) y inscrit aussi chaque passage (réussie = copiée sur le disque externe, disque absent, échouée) : l'alerte fonctionne avec les deux options.
- [ ] **11.3** Option (b) : installer le script. **Où** : PC serveur. **Compte** : administrateur Windows.
  1. Créer le fichier de mot de passe (une seule ligne, mot de passe de `pharma_app` **dans le fichier seulement**) :
     `C:\ProgramData\PharmaOfficine\pgpass.conf`, contenu : `localhost:5432:<base>:pharma_app:<mot de passe>`.
  2. Limiter l'accès au compte qui lance la tâche :
     ```powershell
     icacls C:\ProgramData\PharmaOfficine\pgpass.conf /inheritance:r /grant:r "<compte>:R"
     ```
  3. Adapter les 4 variables en tête du script (`$DbName`, `$DossierLocal`, `$DossierExterne`, `$RetentionJours`) dans la copie livrée. **[À CONFIRMER : valeurs de `D:\` et `E:\` à remplacer.]**
  4. Planifier : Planificateur de tâches, Créer une tâche, déclencheur **chaque soir**, action : programme `powershell.exe`, arguments `-ExecutionPolicy Bypass -File "C:\...\scripts\sauvegarde_pharmacie.ps1"`.
  - **Attendu** : le journal `C:\ProgramData\PharmaOfficine\sauvegarde.log` est créé après la première exécution.
- [ ] **11.4** Tester une restauration **dans une base d'essai**, avant la mise en service puis chaque mois. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie" -Base pharmacie_essai -SansGed
  ```
  - **Attendu** : le résumé affiche le nombre de ventes et la date de la dernière. La base en service n'est pas touchée.
  - Supprimer ensuite la base d'essai : `& $psql -h localhost -U postgres -d postgres -c "DROP DATABASE pharmacie_essai"`.
  - Voir la procédure complète dans « Restauration » (procédures occasionnelles).

### 12. Reprise des données de l'ancien logiciel (si besoin)

L'ancienne base n'est **jamais** modifiée : on travaille sur une copie. Prérequis : étapes 9.8 (nomenclature) et 10.1 (super admin) faites.

- [ ] **12.1** Super admin : sur l'écran de connexion, « Connexion super admin ». Ce compte ne voit que la reprise de données, et se déconnecte après 10 minutes sans activité. L'**extraction**, le **chargement** et l'**abandon** d'une reprise demandent chacun : un avertissement, le mot de passe ressaisi, puis le **code à 6 chiffres, le même envoyé par SMS et par e-mail** (un seul canal suffit ; la fenêtre dit si l'un n'est pas parti, et l'action est refusée si aucun ne part), valable 5 minutes, à usage unique. 3 codes faux bloquent les validations 15 minutes.
- [ ] **12.2** Transit : Administration, Reprise de données (super admin). L'assistant lit l'ancienne base en lecture seule.
  - Sources : fichiers CSV ou Excel (lus sans pilote, encodage UTF-8 ou ANSI et séparateur détectés ; si les accents sont faux, imposer l'encodage dans « Encodage des fichiers »), ou une base accessible par un pilote ODBC installé sur le PC, ou une connexion saisie champ par champ.
  - L'assistant propose la correspondance des tables et des colonnes, à confirmer, puis copie les données dans le schéma `import`.
  - Les mots de passe ne sont ni extraits ni enregistrés.
- [ ] **12.3** Rapprochement des produits : par code-barres, n° d'enregistrement, puis nom approché à confirmer. Un produit absent de la nomenclature est créé « à vérifier » et refusé en caisse jusqu'à la confirmation de sa classe par le titulaire.
- [ ] **12.4** Contrôles : toute anomalie bloquante empêche le chargement.
- [ ] **12.5** Chargement, en une transaction. Un lot sans date de péremption devient « date à vérifier ». Le prix d'achat de chaque lot est repris s'il est dans l'export, sinon celui du produit.
  - Les données chargées (réception de reprise, stock, registre des psychotropes) sont attribuées au **pharmacien titulaire**.
  - Le journal garde l'identifiant du super admin et le canal du code (numéro ou adresse masqués).
- [ ] **12.6** Vérification des totaux (unités, valeur du stock au prix d'achat, crédits clients, dettes grossistes, nombre de clients), avec un rapport signé par le pharmacien. Si les totaux ne concordent pas la veille au soir : **retour arrière** (critère du calendrier).
- [ ] **12.7** Ne **jamais** lancer les fichiers `database\data\9xx_*.sql` pendant la reprise.

### 13. Options

- [ ] **13.1** Accès du titulaire hors de la pharmacie, par VPN : voir [acces-distant.md](acces-distant.md). Ne **jamais** ouvrir l'API sur internet. Si une adresse VPN est utilisée, relancer le script de l'étape 4.6 avec `-AdressesSupplementaires <adresse VPN>`.
- [ ] **13.2** 2e serveur : parcours C (facultatif, fortement conseillé). À faire **après** la présente installation, sur un PC de l'officine.
- [ ] **13.3** Synchronisation avec le serveur central : parcours E. La synchronisation reste **désactivée** tant que le serveur central n'est pas prêt et que le titulaire ne l'a pas activée. Ne pas la toucher avant le jour J.

### 14. Vérifications avant ouverture (J-1)

- [ ] **14.1** Ouvrir une caisse, faire une vente test en espèces, l'annuler, puis clôturer : l'écart doit être nul.
- [ ] **14.2** Imprimer un ticket, une étiquette et un rapport de clôture.
- [ ] **14.3** Vérifier qu'un vendeur ne voit pas ce qui ne lui est pas permis (trésorerie, administration).
- [ ] **14.4** Administration, Contrôle interne, Intégrité des journaux : « Vérifier maintenant ». Le résultat doit être sans anomalie.
- [ ] **14.5** Faire une sauvegarde complète juste avant l'ouverture (étape 11).
- [ ] **14.6** Compléter la [checklist avant ouverture](#checklist-avant-ouverture).

### 15. Jour J et suite

- [ ] **15.1** Ouverture sur le nouveau logiciel, avec accompagnement sur place.
- [ ] **15.2** J+1 à J+30 : l'ancien logiciel reste en **lecture seule**. Étiqueter le stock progressivement (Achats, Étiquettes internes, ou à la fin de chaque inventaire tournant).

---

## Parcours B, mises à jour

Pour une officine **déjà en service**. Ordre : étapes 1 à 14. Ne jamais relancer `schema.sql` ni `clean.sql` sur une base en service.

Les scripts `database\migration\NNN_*.sql` sont réexécutables sans dégât. Le script `scripts\mep\mettre_a_jour_base_officine.ps1` (étape 4) les lance un par un, **hors transaction**, avec `ON_ERROR_STOP`, après une sauvegarde obligatoire. Les scripts « b » (`101b`, `111b`) se lancent à part (étapes 6 et 8).

Les publications étant autonomes (.NET inclus), Windows Update ne corrige **pas** .NET. À chaque correctif de sécurité .NET important, republier l'API et les postes avec le SDK à jour, même sans nouvelle fonction.

- [ ] **1.** Lire la note de version et choisir un créneau **sans caisse ouverte**. **Où** : PC serveur. **Compte** : aucun. Prévenir les employés.
  - **Préparer la livraison avant le créneau** (le service reste en marche pendant ce temps). **Où** : PC des sources. **Compte** : administrateur Windows.
    ```powershell
    dotnet publish src\Pharma.Api -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Api"
    dotnet publish src\Pharma.Desktop -c Release -r win-x64 --self-contained true -o "<dossier de livraison>\Poste"
    ```
  - Copier `<dossier de livraison>` (avec son dossier `Api\database\migration`) et le dossier `scripts` du dépôt sur la clé USB. Copier aussi `database\migration\111b_roles_integrite.sql` du dépôt (il est exclu du dossier publié) si la séparation des rôles n'est pas encore faite.
  - **Attendu** : `Api\Pharma.Api.exe`, `Api\database\migration\148_sauvegarde_alerte_sans_disque_externe.sql` et `Poste\Pharma.Desktop.exe` sont présents sur la clé.
- [ ] **2.** Si l'officine est synchronisée avec le serveur central : **mettre le serveur central à jour d'abord** (parcours E, étape 10), puis l'officine.
- [ ] **3.** Arrêter le service. **Où** : PC serveur. **Compte** : administrateur Windows. Le script de l'étape 4 refuse de travailler si le service tourne.
  ```powershell
  Stop-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Status : Stopped`.
- [ ] **4.** Lancer le script de mise à jour de la base : **sauvegarde, détection des rôles, contrôle Chifa, rejeu des scripts**. **Où** : PC serveur. **Compte** : `pharma_app` (mot de passe saisi à l'écran) ; si la séparation des rôles est déjà faite ou demandée : aussi le superutilisateur `postgres` (mot de passe saisi à l'écran). Aucun mot de passe n'est écrit dans une commande. Référence complète : [scripts-base-de-donnees.md](scripts-base-de-donnees.md).
  ```powershell
  .\scripts\mep\mettre_a_jour_base_officine.ps1 -Base <base> -DossierSauvegarde "E:\Sauvegardes\Pharmacie" -DossierMigration "E:\<dossier de livraison>\Api\database\migration" -Script111b "E:\111b_roles_integrite.sql"
  ```
  - `-DossierMigration` et `-Script111b` : le dossier de livraison et le fichier copiés à l'étape 1 (depuis le dépôt, ces deux options sont inutiles). Ajouter `-SeparerRoles` si les rôles ne sont pas encore séparés et qu'on les sépare maintenant (étape 6). Pour voir le plan sans rien modifier : ajouter `-WhatIf`.
  - Le script : vérifie que le service est arrêté ; lit l'état des rôles (`111b` fait ou non : rôle `pharma_proprietaire` présent, propriétaire de la base et des tables) ; fait la **sauvegarde** `pg_dump -Fc` (`<base>_avant_maj_<date>_<heure>.dump`) et la relit avec `pg_restore --list` (sans sauvegarde valide, rien n'est modifié) ; contrôle les n° de facture Chifa avant le script `101` (étape 5) ; rejoue les scripts numérotés dans l'ordre, un par un, hors transaction (le `033` ne peut pas être dans une transaction), toujours depuis le script `001` (toute la suite est rejouable sur une base à jour ; l'option `-ScriptDepart` est réservée aux incidents) ; passe par `pharma_app` avant `111b`, par `postgres` avec `SET ROLE pharma_proprietaire` après (méthode **[À VÉRIFIER]**, voir étape 7) ; affiche le résumé (scripts joués, durée, état des rôles). Le script `101b` n'est **jamais** lancé (étape 8).
  - Copier aussi `super-admin.dat` et `cle-integrite.dat` et la sauvegarde `.dump` hors du PC serveur (disque externe, déjà fait à l'étape 4.9 du parcours A : vérifier qu'elle est à jour).
  - **Attendu** : « Terminé », code de sortie 0 (`$LASTEXITCODE`), la sauvegarde existe, « N scripts rejoués sans erreur ».
  - **Si échec** : le message donne le script en cause. Corriger la cause, relancer le même script sans `-ScriptDepart` (toute la suite est rejouable ; `-ScriptDepart` n'est qu'un secours en cas d'incident). La restauration d'une sauvegarde depuis l'application rejoue de la même façon toute la suite. Voir l'étape 10 pour le retour arrière.
- [ ] **5.** Si le script s'arrête sur des **n° de facture Chifa à examiner**. **Où** : PC serveur. **Compte** : aucun (le titulaire décide). Le script contrôle, avant `101`, trois cas en lecture seule : n° non conformes (pas 8 chiffres), conflits (les deux colonnes remplies et différentes), recopie impossible (n° déjà porté par une autre vente non annulée). Il affiche les lignes et s'arrête.
  - **Décision** : **ne pas continuer** avant d'en parler au titulaire. Ces anomalies ne se corrigent pas sans lui. La suite (`101`) range les conflits dans `vente_chifa_facture_conflit`, à trancher dans l'application (Administration, Conflits de n° de facture Chifa).
  - Après la décision du titulaire, relancer la commande de l'étape 4 avec `-ContinuerMalgreConflitsChifa`.
  - **Attendu** : aucune ligne, ou des lignes examinées et tranchées. Après `101`, `SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;` doit valoir `0` une fois les conflits tranchés. **[À CONFIRMER : faut-il attendre ce `0` avant la remise en service ?]**
- [ ] **6.** **Séparation des rôles** (`111b`), **une fois par base**. **Où** : PC serveur. **Compte** : **superutilisateur** `postgres`. Si l'étape 4 a été lancée avec `-SeparerRoles` : fait (juste après le script `111`). Sinon, et si l'état affiché à l'étape 4 est « pas encore faite » : relancer l'étape 4 avec `-SeparerRoles`.
  - Le script `111b` retire à `pharma_app` la modification et la suppression des journaux (tables chaînées, `sceau_integrite`, `verification_integrite`, `journal_audit`, `journal_sauvegarde`, `boite_evenement`, `registre_psychotrope`) et donne la base et les objets à `pharma_proprietaire`.
  - **Attendu** : `objets_encore_a_pharma_app` vaut **0** (le script le contrôle).
  - Ensuite, **tous les scripts suivants** passent par `postgres` avec `SET ROLE pharma_proprietaire` : le script de l'étape 4 le fait seul. Ne plus utiliser `pharma_app` pour les scripts.
  - **Si échec** : `à exécuter par un superutilisateur` : le compte utilisé n'est pas `postgres`.
- [ ] **7.** Rôles déjà séparés (base déjà passée par `111b`) : méthode `SET ROLE`. **Où** : PC serveur. **Compte** : `postgres` avec `SET ROLE pharma_proprietaire`. Le script rejoue alors tous les scripts avec ce compte, dans la même session `psql` (`-c "SET ROLE pharma_proprietaire" -f <script>`) : il contrôle au départ que `SET ROLE` donne bien le rôle attendu.
  - **[À VÉRIFIER : cette méthode n'a pas encore été validée par un essai avec le compte `postgres`. À essayer sur une COPIE de la base (restaurée sous un autre nom) avant la production.]**
  - **Si échec** : « SET ROLE pharma_proprietaire n'a pas donné le rôle attendu » : ne pas continuer, voir le technicien.
- [ ] **8.** Script `101b` (suppression de `num_facture_chifa`) : **jamais lancé par le script**, seulement sur décision du titulaire. **Où** : PC serveur. **Compte** : voir ci-dessous.
  - `num_facture_chifa` est **conservée** (obsolète) par `101`. Sa suppression est le script **`101b`** (`database/migration/101b_suppression_num_facture_chifa.sql`), **écrit mais non exécuté** : opération **destructive et irréversible** (sauf restauration), **à ne lancer qu'après la validation explicite du titulaire**. Il n'est pas dans le dossier des scripts publiés (comme `111b`) : le prendre dans le dépôt. Sans la décision du titulaire, **ne rien lancer** : la colonne reste, sans effet sur le logiciel.
  - **Procédure `101b`** (dans cet ordre, seulement si le titulaire a dit « go ») :
    1. **Prérequis** : `101` exécuté, logiciel et API **arrêtés** (le script prend un verrou exclusif sur `vente`), aucune autre session ouverte sur la base.
    2. **Sauvegarde complète obligatoire** de la base (`pg_dump`, ou la sauvegarde de l'application), vérifiée lisible et copiée hors du PC serveur. Sans elle, ne pas continuer : l'opération est irréversible.
    3. **Contrôles en lecture seule** (psql interactif), qui doivent tous renvoyer **0** :
       ```sql
       SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;                       -- conflits non tranchés
       SELECT count(*) FROM vente WHERE NULLIF(btrim(num_facture_chifa), '') IS NOT NULL
                                    AND NULLIF(btrim(chifa_facture), '') IS NULL;                        -- valeurs qui seraient perdues
       SELECT count(*) FROM vente v WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL AND NULLIF(btrim(v.chifa_facture), '') IS NOT NULL
                                      AND btrim(v.num_facture_chifa) <> btrim(v.chifa_facture)
                                      AND NOT EXISTS (SELECT 1 FROM vente_chifa_facture_conflit c WHERE c.vente_id = v.id AND c.tranche_le IS NOT NULL); -- écarts sans décision
       ```
       Le script refait ces contrôles lui-même et **refuse** (sans rien modifier) s'ils ne valent pas 0 : il affiche alors les comptes.
    4. **Compte** : **avant `111b`** (cas normal de cette étape) : `pharma_app` (propriétaire de `vente`). **Après `111b`** (rôles déjà séparés) : `postgres` avec `SET ROLE pharma_proprietaire` (`pharma_app` n'est plus propriétaire : le script refuse).
    5. **Commande** (avant `111b`) :
       ```powershell
       & $psql -h localhost -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f "<dépôt>\database\migration\101b_suppression_num_facture_chifa.sql"
       ```
       Après `111b` : même commande avec `-U postgres` et `-c "SET ROLE pharma_proprietaire"` placé **avant** `-f` (psql exécute les `-c` et `-f` dans l'ordre, dans la même session).
    6. **Résultat attendu** : `101b : colonne vente.num_facture_chifa supprimée (… vente(s), dont … avec une valeur, toutes déjà portées par chifa_facture)`, puis deux lignes d'historique conservé. Relancé, le script affiche « n'existe déjà plus, rien à faire ». Une ligne `colonne_supprimee` apparaît dans `journal_audit`. En cas de refus (`101b REFUSÉ`), rien n'a été modifié : lire les comptes affichés, corriger (trancher les conflits, relancer `101`), reprendre à l'étape 3.
    7. **Vérification** : `SELECT count(*) FROM information_schema.columns WHERE table_name = 'vente' AND column_name = 'num_facture_chifa';` doit valoir `0` ; redémarrer l'API et le logiciel, ouvrir une vente et la liste des ventes Chifa.
    8. **Retour arrière** : **restauration de la sauvegarde du point 2** (aucun `Down`). Les ventes saisies depuis la sauvegarde sont alors perdues : décider vite, avant la reprise de l'activité.
    9. **Après la suppression** (à faire par le développeur, dans le dépôt) : retirer la colonne de `database/init/schema.sql` (colonne et commentaire de `vente`), régénérer le miroir du serveur central (`database/cloud/002_tables_miroir.sql`), voir `docs/a-faire.md`.
- [ ] **9.** Vérifier l'état de la base après le script (lecture seule). **Où** : PC serveur. **Compte** : `pharma_app`.
  ```powershell
  & $psql -h localhost -U pharma_app -d <base> -c "SELECT tableowner, count(*) FROM pg_tables WHERE schemaname = 'public' GROUP BY tableowner"
  ```
  - **Attendu** : si les rôles sont séparés, toutes les tables appartiennent à `pharma_proprietaire` ; sinon à `pharma_app` (et le résumé du script l'a signalé : les journaux ne sont pas protégés).
- [ ] **10.** Retour arrière si la mise à jour de la base échoue et que la base reste inutilisable. **Où** : PC serveur. **Compte** : administrateur Windows et `postgres`.
  - Laisser le service **arrêté**. Restaurer la sauvegarde `.dump` faite à l'étape 4 (procédure F1, « Restauration », en base neuve puis renommage). Les ventes saisies depuis la sauvegarde sont perdues : décider vite, avant la reprise de l'activité.
  - Les scripts déjà appliqués avant l'échec sont sans perte de données : le retour arrière n'est nécessaire que si la base reste inutilisable.
- [ ] **11.** Remplacer les fichiers de l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  - Utiliser le dossier `Api` préparé à l'étape 1 (clé USB).
  - Renommer l'ancien dossier (`C:\Program Files\PharmaOfficine\Api` devient `Api_ancien`) pour pouvoir revenir en arrière. Copier le nouveau dossier à la place.
  - `appsettings.Https.json` n'est pas dans le dossier publié : **vérifier qu'il est recopié** depuis `Api_ancien` si absent. Les variables d'environnement restent en place.
- [ ] **12.** Démarrer le service et vérifier. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  Start-Service "PharmaOfficine API"
  Get-Service "PharmaOfficine API"
  (Invoke-WebRequest http://localhost:5080/api/healthcheck -UseBasicParsing).StatusCode
  ```
  - **Attendu** : `Running`, et `200`.
  - **Si échec** : journal du serveur (annexe, dépannage). Pour revenir en arrière : arrêter le service, remettre `Api_ancien` à la place de `Api`, redémarrer. Les scripts de base déjà appliqués restent (ils sont sans perte).
- [ ] **13.** Mettre à jour les postes. **Où** : chaque poste. **Compte** : administrateur Windows.
  - Fermer Tracodex Officine sur le poste, puis remplacer le dossier du poste par le dossier `Poste` préparé à l'étape 1 (clé USB). Les variables `PHARMA_API` et `PHARMA_POSTE` ne changent pas.
  - Si la nomenclature nationale a été mise à jour : Administration, Nomenclature, « Importer le fichier Excel… ». L'import remplace l'ancienne liste sans toucher au catalogue.
- [ ] **14.** Vérifications après mise à jour. **Où** : un poste. **Compte** : titulaire.
  - Une vente test, une clôture de caisse.
  - Administration, Contrôle interne, Intégrité des journaux, « Vérifier maintenant ».
  - Si l'officine a un 2e serveur : lancer `verifier-second-serveur.ps1` sur le 2e serveur (parcours C, étape 12). Les deux serveurs doivent avoir la **même version** (l'écran « 2e serveur » signale un écart en rouge).

---

## Parcours C, 2e serveur

Option facultative, fortement conseillée. L'officine fonctionne sans 2e serveur. Le 2e serveur reçoit **en continu** la base du PC serveur (réplication PostgreSQL en streaming, avec slot), et garde une API de secours **arrêtée**. En cas de panne du principal, une **personne** décide la bascule (procédure F3).

Toujours **une seule base en écriture** : la base du 2e serveur est en lecture seule tant qu'elle n'est pas basculée. Les postes ne l'utilisent pas.

Scripts (dans `scripts\second-serveur\`, à copier sur les deux serveurs avec `commun.ps1` à côté des autres). Ils demandent un administrateur Windows et le superutilisateur PostgreSQL. **Chacun a un mode `-Essai`** qui n'écrit rien : **toujours le lancer d'abord**.

| Script | Où | Rôle |
| --- | --- | --- |
| `preparer-principal.ps1` | PC serveur | Rôle de réplication, `postgresql.conf`, `pg_hba.conf`, slot `second_serveur`, `pg_monitor` pour `pharma_app` |
| `initialiser-second-serveur.ps1` | 2e serveur | `pg_basebackup` avec slot et `-R`, démarrage, contrôle « lecture seule », import de la clé de scellement |
| `verifier-second-serveur.ps1` | l'un ou l'autre | Rôle, réplication, version de l'API, service, empreinte de la clé, dossiers ; inscrit le relevé sur le principal |
| `basculer-second-serveur.ps1` | 2e serveur | **Bascule** (procédure F3) |
| `remettre-en-second.ps1` | ancien principal | **Retour** (procédure F4) |
| `partager-ged.ps1` | serveur qui est principal | Partage de la GED **en lecture seule**, au seul compte de copie ; pare-feu 445 limité à l'IP du serveur qui copie (étape 12 bis) |
| `copier-ged.ps1` | serveur qui reçoit | **Copie de la GED sans rien supprimer, à la fréquence réglée dans l'application** (1 à 60 minutes, 5 par défaut ; tâche planifiée chaque minute, étapes 12 ter, 12 ter bis et 12 quater ; procédures F3 et F4) |
| `autoriser-lecture-copie-ged.ps1` | principal, puis 2e serveur | Rôle de lecture sans mot de passe pour que la tâche de copie lise la fréquence réglée dans l'application (étape 12 ter bis) |

### À préparer

- [ ] **1.** Matériel et versions. **Où** : les deux serveurs. **Compte** : administrateur Windows.
  - 2e serveur : un PC de l'officine **allumé en permanence**, sur le même réseau local (sans Internet), avec une **IP fixe**.
  - **La même version majeure de PostgreSQL** sur les deux serveurs (parcours A, étape 2.1).
  - **La même version de l'API**, publiée de la même façon (parcours A, étape 4.1), dans `C:\Program Files\PharmaOfficine\Api`. **[À CONFIRMER : livraison officielle de l'API sur le 2e serveur.]**
  - Pare-feu du 2e serveur : 5443 ouvert aux postes (réglé par `certificat_https.ps1 -Importer -PareFeu`), 5080 jamais ouvert au réseau. Port 5432 jamais ouvert, sauf à l'autre serveur après une bascule ou un retour (`ouvrir-port-base.ps1`, étape 4 bis, lancé sur le serveur devenu principal). Port 445 (SMB, copie de la GED) jamais ouvert au réseau : seulement à l'IP de l'autre serveur, sur le serveur principal (`partager-ged.ps1`, étape 12 bis).

- [ ] **2.** Copier le dossier `scripts\second-serveur` sur les **deux** serveurs. **Où** : les deux serveurs. **Compte** : administrateur Windows. **[À CONFIRMER : dossier de destination.]**

- [ ] **3.** Principal, essai. **Où** : PC serveur. **Compte** : administrateur Windows (+ mot de passe `postgres` demandé).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal> -Essai
  ```
  - **Attendu** : les actions prévues sont affichées, rien n'est modifié.

- [ ] **4.** Principal, lancement réel. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal>
  ```
  - Noter le **mot de passe du rôle de réplication** (12 caractères au moins, au coffre).
  - Si le script demande un redémarrage de PostgreSQL, ajouter `-Redemarrer` à un moment calme.
  - **Attendu** : le script se termine sans erreur. Il règle aussi `pg_hba.conf` (lignes limitées à `<IP>/32`) et `listen_addresses`.

- [ ] **4 bis.** Principal : ouvrir le port 5432 au 2e serveur **seulement**. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\ouvrir-port-base.ps1 -AdresseSecond <IP du 2e> -WhatIf
  .\ouvrir-port-base.ps1 -AdresseSecond <IP du 2e>
  Get-NetFirewallRule -DisplayName "PharmaOfficine PostgreSQL 5432 (2e serveur)" | Get-NetFirewallAddressFilter | Select-Object RemoteAddress
  ```
  - Le script crée la règle entrante « PharmaOfficine PostgreSQL 5432 (2e serveur) » : TCP 5432, profil Privé, adresse distante = **l'IP du 2e serveur seulement**. Rien n'est deviné : saisir l'IP fixe du 2e serveur (étape 1). Relancé, il remplace la règle sans doublon.
  - **Le port 5432 ne doit JAMAIS être ouvert au reste du réseau** (postes, Wi-Fi, téléphones) : l'application passe par l'API, pas par PostgreSQL.
  - **Attendu** : « Pare-feu : port 5432 ouvert en entrée, profil Privé, au seul 2e serveur (<IP>). » puis « Contrôle : aucune autre règle active n'ouvre le port 5432. » ; la vérification affiche `RemoteAddress` = l'IP du 2e serveur (jamais `Any`).
  - **Si échec** : « À lancer dans PowerShell en administrateur » = rouvrir PowerShell en administrateur. « Adresse IPv4 invalide » = une seule IP, sans masque ni plage. Avertissement « Une autre règle ouvre le port 5432 » = vérifier son adresse distante et la désactiver (`Disable-NetFirewallRule -DisplayName '<nom affiché>'`), puis relancer le script ; **ne pas continuer** tant que l'avertissement demeure.
  - **Changement d'IP du 2e serveur** : relancer ce script avec la nouvelle adresse, relancer `preparer-principal.ps1` avec la nouvelle IP, puis supprimer à la main les anciennes lignes `/32` de `pg_hba.conf` (une copie datée est gardée) et lancer `SELECT pg_reload_conf();`. Plus de 2e serveur : `.\ouvrir-port-base.ps1 -AdresseSecond <IP> -Retirer`.

- [ ] **5.** Principal : déclarer le 2e serveur à l'API. **Où** : PC serveur. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du 2e>", "Machine")
  Restart-Service "PharmaOfficine API"
  ```
  - Cette variable est équivalente à `"SecondServeur": { "AutreServeur": "<IP du 2e>" }` dans la configuration de l'API. Ne pas écrire la clé à deux endroits.

- [ ] **6.** Titulaire : exporter la clé de scellement. **Où** : un poste ou le PC serveur. **Compte** : titulaire, dans l'application.
  - Administration, 2e serveur, **Clé de scellement** : saisir un mot de passe de 12 caractères au moins (deux fois), choisir le dossier de la clé USB, « Exporter la clé ».
  - Fichier produit : `cle-scellement-AAAAMMJJ-<empreinte>.pocle` (protégé par le mot de passe). La clé n'est jamais en clair.
  - Ranger la clé USB **au coffre** et le mot de passe **à part**.

- [ ] **7.** 2e serveur : mêmes variables que le principal. **Où** : 2e serveur. **Compte** : administrateur Windows.
  - `ConnectionStrings__Pharmacie` : `Host=localhost` (la base locale), même compte `pharma_app`, même mot de passe.
  - `Jwt__Cle` : **la même valeur** que le principal (transmise au coffre, jamais par courriel).
  - `Chifa__DossierExport` : propre à ce PC (étape 4.4 du parcours A).
  - **Ne pas** régler `AutoriserHttpReseau`.
  - Vérifier la longueur de `Jwt__Cle` (64 ou plus) comme à l'étape 4.4 du parcours A.

- [ ] **8.** 2e serveur : certificat HTTPS, **signé par la même autorité que le PC serveur**. Les postes et téléphones lui font déjà confiance (parcours A, étapes 5.4 et 6.2) : **rien à réinstaller sur les postes**. Aucune autorité n'est créée sur le 2e serveur. Clé USB branchée (ici `E:\`). Prérequis : parcours A, étape 4.6 fait (autorité créée et PFX au coffre).

  **8a. Sur le PC serveur** (administrateur Windows) : générer le certificat du 2e serveur.
  ```powershell
  Set-Location "<dossier qui contient scripts>"
  .\scripts\certificat_https.ps1 -CertificatPourAutrePc -NomsAutrePc <nom du 2e> -AdressesAutrePc <IP du 2e> -FichierCertificat E:\certificat-second.pfx -WhatIf
  .\scripts\certificat_https.ps1 -CertificatPourAutrePc -NomsAutrePc <nom du 2e> -AdressesAutrePc <IP du 2e> -FichierCertificat E:\certificat-second.pfx
  ```
  - Rien n'est deviné : saisir **exactement** le nom et l'IP que les postes utiliseront (étape 13). Plusieurs valeurs : `-NomsAutrePc SERVEUR2,SERVEUR2.local`, `-AdressesAutrePc 192.168.1.30,10.8.0.30` (adresse VPN comprise si elle sert).
  - Le script demande une **phrase de passe** (12 caractères au moins, deux fois). La noter à part, au coffre ; elle n'est jamais écrite par le script.
  - **Attendu** : `Certificat de l'autre PC écrit : E:\certificat-second.pfx (valable jusqu'au …)` et la liste des noms et adresses du certificat. L'essai `-WhatIf` annonce chaque action avec `[SIMULATION]` sans rien écrire ni demander de phrase.
  - **Si échec** : « Aucune autorité … dans Cert:\LocalMachine\My » = ce n'est pas le PC serveur (ou l'autorité n'y est pas : procédure F2 bis). « Le fichier existe déjà » : choisir un autre nom (par exemple avec la date). « Les deux saisies sont différentes » : recommencer.

  **8b. Sur le 2e serveur** (administrateur Windows) : importer, avec la clé USB.
  ```powershell
  .\scripts\certificat_https.ps1 -Importer -FichierCertificat E:\certificat-second.pfx -DossierApi "C:\Program Files\PharmaOfficine\Api" -PareFeu -WhatIf
  .\scripts\certificat_https.ps1 -Importer -FichierCertificat E:\certificat-second.pfx -DossierApi "C:\Program Files\PharmaOfficine\Api" -PareFeu
  ```
  - Le script demande la phrase de passe, importe le certificat (clé **non exportable** dans le magasin Windows), écrit `appsettings.Https.json` (HTTP `localhost:5080`, HTTPS `0.0.0.0:5443`) et ouvre le port 5443 en réseau privé, comme sur le PC serveur. Il affiche les noms et adresses du certificat et avertit si ni le nom ni une adresse de ce PC n'y figure.
  - Vérifier : `Get-Content "C:\Program Files\PharmaOfficine\Api\appsettings.Https.json"` et `Get-NetFirewallRule -DisplayName "PharmaOfficine API (https 5443)" | Select-Object DisplayName, Enabled`.
  - **Attendu** : `Certificat importé dans Cert:\LocalMachine\My …`, fichier de configuration identique à celui du PC serveur, règle `Enabled : True`. Le service de l'API reste **arrêté** (étape 10) : il est démarré par la bascule seulement.
  - **Si échec** : « phrase de passe incorrecte » = ressaisir. « Ce fichier est une autorité » = mauvais fichier (l'autorité ne s'importe qu'avec `-RestaurerAutorite`, procédure F2 bis). Le certificat est valable **825 jours** : voir F6 pour le renouveler.
  - Supprimer ensuite le PFX de la clé USB des postes ; le garder au coffre avec sa phrase, à part.
  - Essai réel après la bascule (étape F3) : un poste ouvre `https://<IP du 2e>:5443/api/healthcheck` sans avertissement.

- [ ] **9.** 2e serveur : initialiser la réplique. **Où** : 2e serveur. **Compte** : administrateur Windows (+ mot de passe `postgres`, mot de passe de réplication).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -FichierCle E:\cle-scellement-<...>.pocle -Essai
  .\initialiser-second-serveur.ps1 -Principal <IP du principal> -FichierCle E:\cle-scellement-<...>.pocle
  ```
  - Le script demande de taper `OUI`, puis le mot de passe du rôle de réplication, puis **le titulaire** tape le mot de passe du fichier de clé.
  - **Attendu** : le contrôle « lecture seule » réussit. Le service de l'API reste **arrêté**.
  - **Si échec** : relire le message du script. Ne pas relancer sans comprendre la cause (il peut réinitialiser le dossier de données ; l'ancien dossier est gardé).

- [ ] **10.** 2e serveur : déclarer le principal, service en démarrage manuel. **Où** : 2e serveur. **Compte** : administrateur Windows.
  ```powershell
  [Environment]::SetEnvironmentVariable("SecondServeur__AutreServeur", "<IP du principal>", "Machine")
  Set-Service "PharmaOfficine API" -StartupType Manual
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Stopped`. Le démarrage est fait seulement en cas de bascule.

- [ ] **11.** Titulaire : réglages propres à chaque serveur. **Où** : application, sur le PC serveur. **Compte** : titulaire.
  - Administration, 2e serveur, **Réglages par serveur** : cliquer sur un serveur, régler son dossier d'échange **CHIFA-Officine** et son dossier du **logiciel MDN** (vide : réglages `Chifa__DossierExport` et `Mdn:Dossier` de ce serveur), cocher « CHIFA-Officine installé », « Lecteur de carte et Token disponibles », « Logiciel MDN installé », « Douchette branchée et testée ».
  - Ces réglages sont dans la base (table `serveur_officine`) et sont répliqués. À la bascule, l'API du 2e serveur prend **les dossiers de sa propre ligne**.

- [ ] **12.** Contrôler la réplique. **Où** : 2e serveur. **Compte** : administrateur Windows (+ mot de passe de `pharma_app` pour inscrire le relevé).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\verifier-second-serveur.ps1 -Principal <IP du principal> -DossierChifa "<dossier CHIFA du 2e>" -DossierMdn "<dossier MDN du 2e>"
  ```
  - Puis, titulaire : Administration, 2e serveur, **État de la réplication** : « 2e serveur connecté », retard en secondes et en octets, slot actif. Réglages par serveur : versions identiques, clé présente et **identique**, dossiers joignables.
  - **Attendu** : tout est vert. Un écart de version est affiché en rouge.

- [ ] **12 bis.** Principal : partager la GED en lecture seule, au seul 2e serveur. **Où** : PC serveur. **Compte** : administrateur Windows.
  La GED (ordonnances scannées, factures, fichiers Chifa, pièces des prêts, copies de sauvegarde…) **ne va jamais au serveur central** : le 2e serveur la reçoit par une copie automatique, sinon il ne pourrait pas reprendre l'activité après une bascule (la réplication PostgreSQL ne copie que la base).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\partager-ged.ps1 -AdresseSecond <IP du 2e> -Essai
  .\partager-ged.ps1 -AdresseSecond <IP du 2e>
  Get-SmbShareAccess -Name 'PharmaGED$'
  Get-NetFirewallRule -DisplayName "PharmaOfficine SMB 445 GED (2e serveur)" | Get-NetFirewallAddressFilter | Select-Object RemoteAddress
  ```
  - Le script crée : le **compte local `PharmaGedCopie`** (aucun groupe, pas d'administrateur ; il demande son mot de passe, 14 caractères au moins, deux saisies, **à noter au coffre** : il sera ressaisi à l'identique sur le 2e serveur) ; le droit NTFS « lecture et exécution » de ce compte sur la GED (ajout, rien n'est retiré) ; le partage masqué `PharmaGED$`, **lecture seule pour ce compte seulement** (jamais « Tout le monde »), chiffré ; la règle de pare-feu « PharmaOfficine SMB 445 GED (2e serveur) » : TCP 445, profil Privé, **IP du 2e serveur seulement**. Relancé, il remplace sans doublon.
  - Si `Ged__Dossier` n'est pas réglé, la GED est `C:\ProgramData\PharmaOfficine\GED` (sinon : `-DossierGed`). Si `Ordonnances__Dossier` sort de la GED : ajouter `-DossierOrdonnances <dossier>` (deuxième partage `PharmaOrdonnances$`).
  - **Attendu** : « Partage "PharmaGED$" créé : lecture seule, PharmaGedCopie seulement » ; `Get-SmbShareAccess` : **une seule ligne**, `PharmaGedCopie`, `Read` ; `RemoteAddress` = l'IP du 2e serveur (jamais `Any`) ; « Contrôle : aucune autre règle active n'ouvre le port 445 ».
  - **Si échec** : « À lancer dans PowerShell en administrateur » = rouvrir PowerShell en administrateur. « Dossier de la GED introuvable » = préciser `-DossierGed`. Avertissement « donne un droit à … » (partage) = retirer ce droit (la commande est affichée). Avertissement « Une autre règle ouvre le port 445 » = vérifier son adresse distante et la désactiver (`Disable-NetFirewallRule -DisplayName '<nom affiché>'`) ; **ne pas continuer** tant qu'elle ouvre 445 à tout le réseau. Changement d'IP du 2e serveur : relancer avec la nouvelle adresse. Plus de 2e serveur : `.\partager-ged.ps1 -AdresseSecond <IP> -Retirer` (les documents ne sont pas touchés).

- [ ] **12 ter.** 2e serveur : essai de copie de la GED. **Où** : 2e serveur. **Compte** : administrateur Windows.
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Essai
  ```
  - Le script **tire** les fichiers du principal (le principal n'écrit jamais sur le 2e serveur). Il copie les fichiers **nouveaux et modifiés**, **ne supprime jamais rien** (un fichier supprimé sur le principal reste sur le 2e serveur), ne remplace jamais un fichier par un plus ancien. Destination par défaut : `Ged__Dossier` du 2e serveur, sinon `C:\ProgramData\PharmaOfficine\GED` (sinon `-Destination`). Ordonnances à part : `-SourceOrdonnances \\<IP du principal>\PharmaOrdonnances$ -DestinationOrdonnances <dossier local>`.
  - **Attendu** : « [ESSAI : rien n'est modifié] GED : fichiers à copier (simulation) » avec la liste, puis « Simulation terminée : rien n'a été écrit ». Rien n'est écrit sur le disque.
  - **Si échec** : « REFUS : source injoignable … Rien n'a été copié ni modifié » (code 1) = vérifier que l'étape 12 bis est faite, que le principal est allumé, l'IP, et que le 2e serveur a bien l'IP déclarée à l'étape 12 bis. Le contrôle d'une source injoignable peut durer une vingtaine de secondes.

- [ ] **12 ter bis.** Lecture de la fréquence de copie par le 2e serveur (une fois, sans mot de passe). **Où** : d'abord le **principal**, puis le **2e serveur**. **Compte** : administrateur Windows et superutilisateur PostgreSQL (mot de passe saisi à l'écran, jamais écrit).
  ```powershell
  cd "<dossier>\scripts\second-serveur"
  .\autoriser-lecture-copie-ged.ps1 -Essai      # sur le principal puis sur le 2e serveur : affiche ce qui serait fait
  .\autoriser-lecture-copie-ged.ps1             # sur le principal d'abord, puis sur le 2e serveur (après la réplication)
  ```
  - **Pourquoi** : la fréquence de la copie (« toutes les N minutes ») se règle dans l'application, **Administration › 2e serveur › Copie de la GED** (titulaire seul, 1 à 60 minutes, **5 par défaut**). Le 2e serveur a une copie répliquée de la base : la tâche de copie lit ce réglage dans **sa base locale, en lecture seule**. Il faut un compte de lecture, **sans mot de passe stocké nulle part** : un rôle PostgreSQL `pharma_ged_lecture` (connexion seulement, ni superutilisateur ni création, sessions en lecture seule, droit de lire **cette seule colonne**, un nombre de minutes) reconnu par **l'authentification Windows (SSPI)** : PostgreSQL n'accepte comme `pharma_ged_lecture` que le **compte Windows de la tâche de copie** (`PharmaGedCopie`, créé à l'étape 12 quater) sur ce PC, et seulement depuis la **boucle locale** (127.0.0.1 et ::1 : aucun accès depuis le réseau). Un autre programme du PC, sous un autre compte Windows, est refusé. Les autres comptes (`postgres`, `pharma_app`) ne sont pas touchés. Écartés : une règle `trust` (tout programme du PC pouvait se présenter comme ce rôle) et un fichier de mot de passe du compte de la tâche (secret en clair). Si la tâche utilise un autre compte que `PharmaGedCopie` (`copier-ged.ps1 -Compte`), passer le même nom : `.\autoriser-lecture-copie-ged.ps1 -CompteWindows <compte>`.
  - Le script 143 (mise à jour de la base) doit être passé avant. Sur le **principal** : crée le rôle et le droit (répliqués ensuite vers le 2e serveur). Sur le **2e serveur** : vérifie que le rôle est arrivé, ajoute la correspondance au `pg_ident.conf` et les deux lignes `sspi` au `pg_hba.conf` (copie datée de chaque fichier gardée ; lignes placées avant les autres règles ; anciennes lignes `trust` de ce rôle retirées ; lignes en erreur signalées ; rechargement sans redémarrage). À refaire sur l'ancien principal après une bascule si ses fichiers n'ont pas été recopiés (F3/F4).
  - **Attendu** : « Rôle pharma_ged_lecture : existe », « Droit de lecture de la seule colonne ged_copie_minutes donné », « pg_ident.conf complété : PharmaGedCopie@<NOM DU PC> -> pharma_ged_lecture », « pg_hba.conf complété ». **Contrôle après l'étape 12 quater** : le journal de la copie affiche « Réglage de l'application : toutes les N minute(s) ». **Si la lecture est refusée** (« Réglage de la fréquence illisible ») : le journal de PostgreSQL du 2e serveur donne le nom de compte reçu ; s'il diffère de `PharmaGedCopie@<NOM DU PC>` (casse, nom du PC), relancer le script avec ce nom ou signaler l'écart. **Si on saute cette étape** : la copie marche quand même, **toutes les 5 minutes** (valeur par défaut) ; le journal de la copie le dit (« Réglage de la fréquence illisible ») et le réglage de l'écran n'a alors aucun effet sur le 2e serveur.

- [ ] **12 quater.** 2e serveur : installer la copie automatique (fréquence réglée dans l'application, 5 minutes par défaut) et la contrôler. **Où** : 2e serveur. **Compte** : administrateur Windows.
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Installer -Essai
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Installer
  Start-ScheduledTask -TaskName "PharmaOfficine copie de la GED (2e serveur)"
  Get-ScheduledTaskInfo -TaskName "PharmaOfficine copie de la GED (2e serveur)" | Select-Object LastRunTime, LastTaskResult
  Get-Content "C:\ProgramData\PharmaOfficine\Journaux\Copie-GED\copie-ged-$(Get-Date -Format yyyyMM).log" -Tail 20
  ```
  - Le script demande le mot de passe du compte `PharmaGedCopie` : **le même que sur le principal** (étape 12 bis). Si le compte n'existe pas encore sur ce serveur, il le crée (aucun groupe, pas d'administrateur). Le mot de passe n'est écrit nulle part : il est gardé par le Planificateur de tâches de Windows.
  - La tâche « PharmaOfficine copie de la GED (2e serveur) » tourne **sous ce compte dédié** (jamais SYSTEM, jamais administrateur) : lecture seule de la GED du principal, modification de la seule GED locale et du dossier `Journaux\Copie-GED`. Relancée, l'installation **remplace** la tâche (pas de doublon), y compris l'ancienne tâche de 5 minutes. Une seule copie à la fois ; si l'ordinateur était éteint, la copie reprend à son rallumage.
  - **Fréquence** : la tâche est déclenchée **chaque minute** ; le script ne copie que si **N minutes** (réglage de l'application, **5 par défaut**) se sont écoulées depuis le début de la dernière copie **réussie** (repère : `derniere-copie-ged.txt` dans `Journaux\Copie-GED`). Entre deux copies il sort aussitôt, sans journal. Si le réglage est illisible (base arrêtée, étape 12 ter bis non faite), il prend **5 minutes**, le journalise une fois et copie quand même. Une copie refusée (principal éteint) n'est retentée qu'après N minutes. Le titulaire change la fréquence dans l'application ; elle s'applique **au passage suivant (une minute)**, sans rien relancer. Pour une copie tout de suite (essai, après une bascule) : `.\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Maintenant`. Priorité : `-Maintenant` > `-ToutesLesMinutes N` (essais seulement) > réglage de l'application > 5 minutes ; la tâche installée n'a ni l'un ni l'autre. `-Installer -Essai` affiche le réglage lu (« Réglage de l'application : toutes les N minute(s) »).
  - **Première copie** : elle peut durer longtemps (toute la GED). Les copies suivantes ne prennent que ce qui est nouveau ou modifié. Un fichier en cours d'écriture sur le principal est recopié au passage suivant.
  - **Attendu** : `LastTaskResult` = `0` ; journal : une ligne par **copie** (« rien à copier » ou « fichiers copiés » avec la liste, et la fréquence appliquée : « 5 min (application) »). Le titulaire peut comparer la taille de la GED des deux serveurs : le 2e serveur a **au moins** autant de fichiers que le principal.
  - **Si échec** : `LastTaskResult` = `1` : le journal dit « REFUS : source injoignable » (principal éteint, partage, pare-feu, IP) ou « paramètre invalide » ; `2` : des fichiers n'ont pas pu être copiés (le détail est au journal, relancer). Tâche qui ne démarre pas (résultat `0x80070569` ou `0x8007052E`) : mot de passe différent de celui du principal (relancer `-Installer`), ou droit « Ouvrir une session en tant que tâche » à accorder au compte `PharmaGedCopie` (stratégie de sécurité locale). **[À CONFIRMER : à essayer sur les deux vrais serveurs (SMB entre PC hors domaine avec un compte local de même nom et même mot de passe).]**
  - **Limite connue** : la copie a N minutes de retard au plus (5 par défaut, réglable de 1 à 60 : l'écran **Administration › 2e serveur › Copie de la GED** le rappelle : « Documents déposés dans les N dernières minutes avant une panne du serveur principal : possiblement absents du 2e serveur »). Si le principal tombe, les documents déposés dans les **dernières minutes** avant la panne peuvent manquer sur le 2e serveur. Les documents que le poste n'a pas pu envoyer (serveur injoignable) restent sur le poste (dossier « A envoyer ») et partent à la GED à la connexion suivante ; **mais un document déjà reçu par le principal n'est pas gardé au poste** (copie temporaire seulement) : ceux-là ne sont couverts que par la copie. Les copies de sauvegarde de la GED (disque externe) restent la garantie.
  - **Retrait** : plus de 2e serveur : `.\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Desinstaller` sur le 2e serveur, `.\partager-ged.ps1 -AdresseSecond <IP> -Retirer` sur le principal. Les fichiers copiés et le compte sont gardés (`Remove-LocalUser PharmaGedCopie` pour supprimer le compte).

- [ ] **13.** Postes : adresse de secours. **Où** : chaque poste. **Compte** : titulaire.
  - Administration, Imprimantes et étiquettes, **Serveur et PC de secours** : « PC de secours » = adresse de l'API du 2e serveur, en HTTPS : `https://<nom ou IP du 2e>:5443/`. Le port 5080 n'écoute que sur le PC lui-même : jamais `http://…:5080/` pour un poste. Le nom ou l'IP saisi doit figurer dans le certificat du 2e serveur (étape 8a), sinon le poste refuse de basculer. L'écran de réglage du poste propose l'exemple `https://192.168.1.20:5443/` ; une adresse `http://` autre que `localhost` est acceptée mais signalée par un bandeau d'attention (le poste ne pourrait pas se connecter).
  - Délai avant le bandeau : 60 s par défaut.

- [ ] **14.** Alertes. **Où** : application. **Compte** : titulaire.
  - Le titulaire est prévenu (SMS ou e-mail des alertes de sauvegarde, au plus une fois par heure) si le 2e serveur décroche ou si le retard dépasse les seuils (300 s et 64 Mo par défaut). Vérifier les seuils dans l'écran « 2e serveur ».
  - **Rattrapage** : au redémarrage de l'un ou l'autre PC, la réplication reprend seule.
  - **Limite** : un 2e serveur éteint longtemps fait grossir le retard sur le principal, jusqu'à `max_slot_wal_keep_size` (20 Go par défaut). Au-delà, le 2e serveur doit être **réinitialisé** (étape 9).

---

## Parcours D, PC de secours

Option facultative, **fortement conseillée**. Un PC de l'officine (souvent un poste de caisse) reçoit une **copie de secours** de la base, à chaque sauvegarde. En cas de panne du PC serveur, la pharmacie repart dessus (procédure F2).

Les copies ne sont **pas chiffrées** : les ranger comme les sauvegardes (accès restreint au dossier).

- [ ] **1.** Choisir le PC de secours (PC de l'officine, allumé et branché). **Où** : sur place. **Compte** : aucun.
- [ ] **2.** PostgreSQL, même version majeure que le PC serveur (parcours A, étape 2.1). **Où** : PC de secours. **Compte** : administrateur Windows.
- [ ] **3.** Compte `pharma_app` avec `CREATEDB` (parcours A, étape 2.2), **sans créer de base**. **Où** : PC de secours. **Compte** : superutilisateur `postgres`.
  ```sql
  CREATE ROLE pharma_app LOGIN CREATEDB;
  \password pharma_app
  ```
  Créer aussi le rôle `pharma_proprietaire` (sans connexion) : `CREATE ROLE pharma_proprietaire NOLOGIN;`. **[À CONFIRMER : la restauration rejoue les propriétaires des objets ; les rôles doivent exister avant la restauration.]**
- [ ] **4.** API à la **même version** que le PC serveur, dans `C:\Program Files\PharmaOfficine\Api` (parcours A, étape 4.1 et 4.2). **Où** : PC de secours. **Compte** : administrateur Windows.
- [ ] **5.** Variables de l'API (parcours A, étape 4.4) : `ConnectionStrings__Pharmacie` (base `<base>`, même compte, même mot de passe), `Jwt__Cle` **[À CONFIRMER : même valeur que le PC serveur ? Sinon, reconnexion obligatoire après bascule.]**, `Chifa__DossierExport` propre à ce PC.
- [ ] **6.** Certificat HTTPS du PC de secours : **la même autorité** que le PC serveur (aucune autorité n'est créée sur le PC de secours, rien à réinstaller sur les postes). Deux temps, comme pour le 2e serveur (parcours C, étape 8) : **6a** sur le PC serveur, génération du certificat du PC de secours ; **6b** sur le PC de secours, import. Voir ces deux sous-étapes à l'étape 8 du parcours C, en remplaçant « 2e serveur » par « PC de secours » ; fichier conseillé : `E:\certificat-secours.pfx`, noms et adresses : ceux que les postes saisiront dans « PC de secours » (étape 13). **Où** : PC serveur (6a) puis PC de secours (6b). **Compte** : administrateur Windows.
  - Le script `basculer-pc-secours.ps1` (procédure F2) affiche les adresses du PC de secours en `https://<IP>:5443/` : cette IP doit figurer dans le certificat.
- [ ] **7.** Service de l'API : créer, puis **arrêter et passer en démarrage manuel** (la bascule le démarre). **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  New-Service -Name "PharmaOfficine API" -BinaryPathName '"C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe"' -DisplayName "PharmaOfficine API" -StartupType Manual
  Get-Service "PharmaOfficine API"
  ```
  - **Attendu** : `Stopped`.
- [ ] **8.** Clé de scellement : l'importer sur le PC de secours. **Où** : PC de secours. **Compte** : administrateur Windows. Le titulaire tape le mot de passe du fichier.
  ```powershell
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --importer-cle E:\cle-scellement-<...>.pocle
  & "C:\Program Files\PharmaOfficine\Api\Pharma.Api.exe" --empreinte-cle
  ```
  - **Attendu** : l'empreinte affichée est **la même** que sur l'écran du titulaire.
  - Ajouter `--remplacer` seulement si le titulaire le demande. **[À CONFIRMER : comportement de `--importer-cle` si une clé existe déjà.]**
  - L'API refuse de démarrer si la clé est illisible : elle n'est jamais remplacée en silence.
- [ ] **9.** Dossier partagé de la copie de secours. **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\CopieSecours" | Out-Null
  New-SmbShare -Name CopieSecours -Path "D:\CopieSecours" -FullAccess "<compte autorisé>"
  ```
  - **[À CONFIRMER : le service de l'API du PC serveur tourne en compte système local par défaut ; il accède au partage avec le compte de l'ordinateur. Quel compte donner ? Tester.]**
  - **Attendu** : le partage existe : `\\<PC de secours>\CopieSecours`.
- [ ] **10.** PC serveur : configurer la copie de secours. **Où** : application, sur le PC serveur. **Compte** : titulaire.
  - Administration, Sauvegardes, **Copie de secours** : dossier `\\<PC de secours>\CopieSecours`, nombre de copies conservées, délai d'alerte (26 h par défaut).
  - Après chaque sauvegarde réussie, le fichier est copié, puis **vérifié** (taille et empreinte SHA-256). L'empreinte est notée à côté (`<copie>.sha256`).
  - Échec de copie ou dernière copie trop vieille : le titulaire est prévenu. Réglages et état : `C:\ProgramData\PharmaOfficine\copie-secours.json`.
- [ ] **11.** Vérifier la première copie. **Où** : PC de secours. **Compte** : administrateur Windows.
  ```powershell
  Get-ChildItem "D:\CopieSecours" | Select-Object Name, Length
  ```
  - **Attendu** : un `pharmacie_*.dump` et son fichier `.sha256`.
- [ ] **12.** Test de la copie, **chaque mois** : titulaire, Administration, Sauvegardes, **« Tester la restauration de la dernière copie »**. Résultat affiché et inscrit au journal. La base en service n'est pas touchée.
  - Simulation sur le PC de secours, sans rien modifier :
    ```powershell
    .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
    ```
    Le script se trouve dans `scripts\` (étape 4.3 du parcours A). **[À CONFIRMER : copie du script sur le PC de secours.]**
- [ ] **13.** Postes : adresse de secours (parcours C, étape 13) : `https://<IP du PC de secours>:5443/`, délai 60 s. **Où** : chaque poste. **Compte** : titulaire. Cette IP (ou ce nom) doit être **dans le certificat du PC de secours** (étape 6a) ; le poste vérifie le certificat avec l'autorité de la pharmacie déjà installée (étape 5.4 du parcours A).

---

## Parcours E, Tracodex Réseau (serveur central de synchronisation)

Option facultative. Le serveur central reçoit la copie des données des officines (`docs/conception-synchronisation.md`, section 12). **Il tourne pour l'instant sur le PC de développement** (base `pharmacie_cloud_dev`, port 5090). L'hébergement réel est **[À VÉRIFIER : hébergeur agréé]**.

Règles : aucun secret dans un fichier du dépôt ni dans `appsettings*.json`. Tout passe par des variables d'environnement du compte de service. **Ne jamais copier le dossier `Synchronisation` d'un serveur d'officine à l'autre.**

Les scripts de la base centrale sont dans `database\cloud\` (001 au dernier, 042 à ce jour). Ils ne se passent **jamais** sur la base d'une officine.

### 1. Hébergement et prérequis

- [ ] **1.1** Hébergement agréé. **[À VÉRIFIER : hébergeur, emplacement, exposition réseau, reverse proxy.]**
- [ ] **1.2** PostgreSQL sur ce serveur (même version majeure que les officines, ou plus récente). **Où** : serveur central. **Compte** : administrateur.
- [ ] **1.3** Un **compte propriétaire** de la base (distinct du rôle applicatif) : **[À CONFIRMER : nom du compte propriétaire et nom de la base de production.]**

### 2. Base centrale

- [ ] **2.1** Créer la base centrale et passer les scripts `database\cloud\001` à `028` (tous, **dans l'ordre**). **Où** : serveur central. **Compte** : superutilisateur `postgres` pour créer la base (mot de passe saisi à l'écran), puis le compte propriétaire `<propriétaire>` pour les scripts (mot de passe saisi à l'écran). Aucun mot de passe n'est écrit dans une commande. Référence : [scripts-base-de-donnees.md](scripts-base-de-donnees.md).
  ```powershell
  .\scripts\mep\creer_base_centrale.ps1 -Base <base centrale> -Proprietaire <propriétaire>
  ```
  - Le compte `<propriétaire>` doit exister (étape 1.3) ; le script ne le crée pas. Pour voir le plan sans rien créer : ajouter `-WhatIf`. Si le serveur est distant : `-Hote <hôte>`.
  - Le script : vérifie la suite des scripts cloud (sans trou) ; refuse une base qui contient déjà des tables centrales (jamais de suppression) ; crée la base (propriétaire `<propriétaire>`, UTF8) si elle n'existe pas ; passe `001` à `029` (tous les scripts numérotés du dossier) ; vérifie les tables centrales, la sécurité au niveau des lignes sur les tables miroir, les rôles `pharma_cloud_api` et `pharma_cloud_etat`. Il n'y a pas de `schema.sql` central : les scripts `cloud` sont la seule source.
  - `001` crée le rôle applicatif `pharma_cloud_api` **sans connexion** et non propriétaire des tables.
  - `029` crée le rôle de lecture technique `pharma_cloud_etat` **sans connexion** (lecture par colonnes de 7 tables techniques, jamais une table miroir, jamais le contenu d'un lot ni l'empreinte d'un code d'activation, aucune écriture). Il est prévu pour les écrans S7 ; **aucune connexion n'est créée en production** : elle sera décidée au déploiement de ces écrans (`GRANT pharma_cloud_etat TO <connexion>`). Comme `001`, il demande au propriétaire le droit `CREATEROLE` (ou un rôle `pharma_cloud_etat` déjà créé par le superutilisateur). `029` retire aussi à `PUBLIC` l'exécution des fonctions `SECURITY DEFINER` (accordée explicitement à `pharma_cloud_api` : droits de l'API inchangés).
  - **Attendu** : « Terminé », code de sortie 0 (`$LASTEXITCODE`).
  - **Si échec** : le script s'arrête au premier script en erreur et le nomme. « Le compte propriétaire n'existe pas » : l'étape 1.3 n'est pas faite. « contient déjà des tables centrales » : base déjà installée ; pour une mise à jour du central, voir l'étape 10.
- [ ] **2.2** Tests (en transaction annulée, rien n'est conservé). **Où** : serveur central. **Compte** : `<propriétaire>`. Les tests se lancent avec le même script, à la création, en ajoutant `-AvecTests` à la commande de l'étape 2.1 : chaque fichier de `database\cloud\tests` doit finir par « tous réussis », sinon le script s'arrête.
  - Il n'existe pas de fichier de test pour `002`. **[À CONFIRMER : faut-il lancer les tests sur la base de production, ou seulement sur une copie ?]** En attendant : les lancer sur une base d'essai (autre nom de base, même commande avec `-AvecTests`).
- [ ] **2.3** Contrôle indépendant (facultatif, le script l'a déjà fait). **Où** : serveur central. **Compte** : `<propriétaire>`.
  ```powershell
  & $psql -h <hôte> -U <propriétaire> -d <base centrale> -tAc "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'pharma_cloud_api'"
  ```
  - **Attendu** : `f` (le rôle applicatif n'a pas encore de connexion : étape 2.4).
- [ ] **2.4** Mot de passe du rôle applicatif. **Où** : serveur central. **Compte** : superutilisateur `postgres`.
  ```sql
  ALTER ROLE pharma_cloud_api LOGIN;
  \password pharma_cloud_api
  ```
  - Mot de passe tiré au sort, noté au coffre, **jamais** dans un script.
  - `pharma_cloud_api` n'est **pas** propriétaire des tables (la sécurité au niveau des lignes s'applique).

### 3. Compte de service et variables

- [ ] **3.1** Créer le dossier de l'autorité **hors de tout dépôt de sources**, par exemple `D:\PharmaCentral\Autorite`. Accès réservé au compte du service et aux administrateurs. **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  New-Item -ItemType Directory -Force "D:\PharmaCentral\Autorite" | Out-Null
  icacls "D:\PharmaCentral\Autorite" /inheritance:r /grant:r "Administrateurs:(OI)(CI)F" "<compte du service>:(OI)(CI)M"
  ```
- [ ] **3.2** Variables d'environnement. **Où** : serveur central. **Compte** : administrateur Windows. Pour les commandes d'administration, les mettre dans la **session** PowerShell :
  ```powershell
  $env:PHARMA_CLOUD_DOSSIER = "D:\PharmaCentral\Autorite"
  $env:PHARMA_CLOUD_MOT_DE_PASSE = "<mot de passe des clés, 16 caractères au moins>"
  $env:PHARMA_CLOUD_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=pharma_cloud_api;Password=<mot de passe>"
  $env:PHARMA_CLOUD_ADMIN_CONNEXION = "Host=<hôte>;Port=5432;Database=<base centrale>;Username=<propriétaire>;Password=<mot de passe>"
  ```
  - `PHARMA_CLOUD_MOT_DE_PASSE` : **sans lui, l'autorité est perdue**. À ranger au coffre.
  - `PHARMA_CLOUD_ADMIN_CONNEXION` : outil d'administration seulement, jamais le service.
  - **Compte SMTP de l'éditeur** (envoi du mot de passe de l'archive de départ au titulaire, F8) : outil d'administration seulement, dans la **session** PowerShell, juste avant `--depart-officine`. **Jamais** dans `appsettings*.json`, un script ou un fichier. Valeurs ci-dessous **factices** :
    ```powershell
    $env:PHARMA_CLOUD_SMTP_HOTE = "smtp.exemple.invalid"
    $env:PHARMA_CLOUD_SMTP_PORT = "587"            # 465 = TLS direct ; tout autre port = STARTTLS exigé ; jamais d'envoi en clair
    $env:PHARMA_CLOUD_SMTP_UTILISATEUR = "<identifiant du compte d'envoi>"
    $env:PHARMA_CLOUD_SMTP_MOT_DE_PASSE = "<mot de passe du compte d'envoi>"
    $env:PHARMA_CLOUD_SMTP_EXPEDITEUR = "<adresse d'expédition>"
    ```
    Les cinq variables sont obligatoires pour envoyer ; s'il en manque une, la commande le dit et n'envoie rien (le mot de passe reste affiché). **[À CONFIRMER : compte SMTP de l'éditeur à créer (fournisseur, adresse d'expédition) ; aucun envoi réel essayé le 2026-11-20.]**
  - Pour le **service** qui tourne en continu, ces variables doivent être réglées au niveau de la machine ou du compte de service. **[À CONFIRMER : le code ne fournit pas de service Windows pour le serveur central ; mode de lancement et compte du service à définir.]**
- [ ] **3.3** Publier le serveur central. **Où** : PC des sources. **Compte** : administrateur Windows. **[À CONFIRMER : même forme que l'API, `dotnet publish src\Pharma.Cloud.Api -c Release -r win-x64 --self-contained true -o "<dossier>\Cloud"`, à valider.]**

- [ ] **3.4** Compte e-mail des **codes de second facteur** des écrans du central (2026-11-21). **Où** : serveur central. **Compte** :
  administrateur Windows, session PowerShell de 3.2 (`PHARMA_CLOUD_DOSSIER`, `PHARMA_CLOUD_ADMIN_CONNEXION`), après le passage des scripts
  centraux (037 compris). Le compte d'envoi est celui de l'**éditeur** (jamais celui d'un pharmacien). Gmail : validation en deux étapes
  activée, puis un **mot de passe d'application** créé **sur ce compte** (16 lettres).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --envoi-codes --serveur smtp.gmail.com --port 587 --tls starttls `
      --identifiant <adresse du compte d'envoi> --operateur "<nom>" --essai <votre adresse>
  ```
  - Le mot de passe d'application est **demandé au clavier** (rien ne s'affiche) ; jamais en argument ni en variable.
  - **Dossier des clés** (2026-11-22) : par défaut `<PHARMA_CLOUD_DOSSIER>\cles-protection`. Si les écrans du central utilisent un autre
    dossier (`Ecrans:DossierCles`), ajoutez `--dossier-cles "<ce même dossier>"` à `--envoi-codes`, et aussi à `--depart-officine … --email`
    et `--creer-comptes … --envoyer-identifiants` (sinon la console ne relit pas le mot de passe). En lecture, la console ne crée jamais de
    dossier ni de clé : « aucune clé dans … (rien n'a été créé…) » = mauvais dossier. « Attention : aucune clé dans … » pendant
    `--envoi-codes` : de nouvelles clés vont être créées ; vérifiez que c'est bien le dossier des écrans.
  - **Attendu** : « E-mail d'essai envoyé à x***@… » puis « Envoi des codes enregistré… ». L'e-mail d'essai ne contient **pas** de code.
  - « identifiant ou mot de passe refusé » : mot de passe d'application d'un **autre** compte, mot de passe habituel du compte, ou
    validation en deux étapes absente. Rien n'est enregistré.
  - Ensuite, **compte administrateur (éditeur) avec l'adresse qui recevra ses codes** :
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-compte-editeur <identifiant> --nom "<nom>" --email <adresse de l'éditeur>
    ```
    Le mot de passe provisoire s'affiche une fois ; à la première connexion, le **code arrive par e-mail** à cette adresse (envoyé par le
    compte réglé ci-dessus), puis le mot de passe est à changer.
  - **Adresse erronée, perdue ou à changer** (l'écran « Comptes » exige un code, donc une adresse qui marche) : sur le serveur,
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --contact-compte <identifiant> --email <nouvelle adresse> --auteur "<nom>"
    ```
    (`--telephone <n°>` aussi ; `-` retire ; au moins un contact reste). Noté au journal des accès et au journal de sécurité, sans l'adresse.
  - **Plusieurs comptes d'un coup** (script central 040, facultatif) : fichier CSV en UTF-8, en-tête `role;identifiant;nom;email;telephone`,
    200 comptes au plus (tout ou rien) :
    ```powershell
    & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-comptes <fichier.csv> --auteur "<nom>" --envoyer-identifiants --adresse-ecrans https://<adresse des écrans>/
    ```
    La liste identifiant + mot de passe provisoire s'affiche **une fois** (jamais par e-mail : SMS ou de vive voix) ; seul l'identifiant
    part par e-mail (ajouter `--dossier-cles "<dossier des clés des écrans>"` si les écrans utilisent `Ecrans:DossierCles`). Les mots de
    passe provisoires non utilisés expirent après 7 jours (réglable dans « Envoi des codes »).
  - **Après la mise en ligne des écrans** : régler dans « Envoi des codes » la durée de vie des mots de passe provisoires (7 jours par
    défaut) et la déconnexion après inactivité (éditeur 15 min, titulaire 30 min par défaut) ; facultatif : `Ecrans:AdressePublique`
    (adresse https des écrans, écrite dans l'e-mail de l'identifiant). Les sessions actives n'ont plus de durée maximale.
  - Le réglage d'envoi se modifie ensuite à l'écran « Envoi des codes », les contacts à l'écran « Comptes ».

### 4. Autorité et certificat du serveur central

- [ ] **4.1** Créer l'autorité (une seule fois). **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --initialiser-autorite
  ```
  - **Attendu** : l'empreinte SHA-256 de l'autorité s'affiche. **La noter.** L'autorité n'est jamais remplacée.
  - Sauvegarder séparément `autorite.crt`, `autorite.cle` (chiffrée) et le mot de passe.
- [ ] **4.2** Créer le certificat HTTPS du serveur (2 ans, à refaire avant l'échéance). **Où** : serveur central. **Compte** : administrateur Windows.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --certificat-serveur --noms <nom DNS>,<adresse IP>
  ```

### 5. Démarrage

- [ ] **5.1** Réglages de l'adresse d'écoute dans la configuration (`Cloud:Adresse`, `Cloud:Port`). Par défaut : `127.0.0.1`, port `5090`. **[À VÉRIFIER : adresse réelle et exposition par reverse proxy à l'hébergement. Ne pas mettre `0.0.0.0` sans avoir vérifié.]**
- [ ] **5.2** Démarrer : `& "<dossier>\Cloud\Pharma.Cloud.Api.exe"`. **Où** : serveur central. **Compte** : compte de service.
  - Seul le port HTTPS est ouvert. La base n'est jamais exposée.
  - **Attendu** : le serveur écoute sans erreur. **[À CONFIRMER : point de contrôle de santé (aucune route de santé n'est prévue dans le code).]**

### 6. Tâches planifiées

- [ ] **6.1** Chaque mois : `Pharma.Cloud.Api.exe --partitions` (partitions mensuelles des lots reçus). **Où** : serveur central. **Compte** : compte de service. Planificateur de tâches.
- [ ] **6.3** Toutes les 15 minutes : `Pharma.Cloud.Api.exe --verifier-integrite` (vérification d'intégrité des données reçues, lot S4 ; résultat renvoyé à chaque officine, anomalies : `--anomalies-integrite`). **Où** : serveur central. **Compte** : compte de service (variable `PHARMA_CLOUD_ADMIN_CONNEXION`, propriétaire de la base). Planificateur de tâches.
- [ ] **6.4** Chaque semaine : `Pharma.Cloud.Api.exe --verifier-integrite --complete` (depuis le rang 1 : retrouve aussi une ligne modifiée directement dans la base centrale). Même compte.
- [ ] **6.5** Chaque heure : `Pharma.Cloud.Api.exe --expirer-ordres` (ordres envoyés aux officines non traités dans les 7 jours : « expiré », republiés ; lots S5.1 et S5.5). Même compte. Ordres : `--ordres`, `--ordre-essai`, `--ordre-employe-creer`, `--ordre-employe-modifier`, `--ordre-employe-bloquer`, `--ordre-employe-debloquer`, `--ordre-droits-role`, `--ordre-prix` (lot S5.2), `--importer-nomenclature` puis `--publier-nomenclature` (lot S5.3), `--ordre-commande` (lot S5.4), `--ordre-classe-suivie`, `--ordre-organismes`, `--ordre-prestations`, `--ordre-indications` et `--ordre-dci` (référentiels), `--annuler-ordre` (auteur obligatoire).
- [ ] **6.2** Chaque jour (facultatif) : `Pharma.Cloud.Api.exe --purger-lots`. **Sans effet** tant que l'effacement des lots trop anciens n'est pas activé (défaut). Voir les réglages (étape 7.3).

### 7. Réglages du serveur central (titulaire et technicien)

Outil d'administration (`PHARMA_CLOUD_ADMIN_CONNEXION`). Chaque réglage est inscrit au journal de sécurité. `Pharma.Cloud.Api.exe --reglages` affiche tout.

- [ ] **7.1** Familles de données nominatives. Défauts : `employes`, `banque` et `clients_factures` **non** envoyées ; `pharmacie`, `fournisseurs`, `courriel_commandes` **oui**.
  - Valeur globale : `Pharma.Cloud.Api.exe --regler-famille <famille> oui|non`
  - Surcharge pour une officine : `--regler-famille <famille> oui|non --officine <code>`
  - Retour à la valeur globale : `--regler-famille <famille> global --officine <code>`
  - Le réglage **descend** dans la base de l'officine à son prochain passage (une minute environ). Visible en lecture seule dans l'officine : Administration, Synchronisation, Réglages.
  - Décocher une famille ne supprime rien. Pour effacer ce qui a déjà été reçu : `--effacer-famille <famille> [--officine <code>]` affiche d'abord ce qui serait effacé (simulation). L'effacement a lieu **seulement** avec `--confirmer <famille>`. **C'est une suppression de données : validation du titulaire obligatoire.**
  - Cocher de nouveau une famille ne renvoie pas les valeurs déjà retirées (elles repartent à la prochaine modification, ou par « Recharger toutes les données »).
- [ ] **7.2** Conservation des lots bruts : 6 ans par défaut, **6 ans minimum** (refus en dessous), pas de maximum.
  - `--conservation <ans>` (globale), `--conservation <ans> --officine <code>` (surcharge), `--conservation global --officine <code>` (retour).
- [ ] **7.3** Effacement des lots trop anciens : **désactivé par défaut**. `--purger-lots --simulation` compte ce qui serait effacé. `--purge-lots activer` (ou `desactiver`) pour changer le réglage. **[À VÉRIFIER avant d'activer : durées réglementaires (6 ans fiscaux ; registre des psychotropes : durée à confirmer).]**
- [ ] **7.4** Plafonds (valeurs à connaître) :
  - `cloud_parametre.quota_octets_jour` : 512 Mio par jour par officine (`UPDATE cloud_parametre SET quota_octets_jour = <octets>`) ;
  - `cloud_parametre.quota_chargement_octets` : 8 Gio par chargement complet (`UPDATE cloud_parametre SET quota_chargement_octets = <octets>`) ;
  - sur 24 h glissantes : `chargements_max_24h` et `quota_chargements_octets_24h` (script `009`) ;
  - débit : 60 lots par minute par certificat, 600 pendant un chargement (`Cloud:LotsParMinute`, `Cloud:LotsParMinuteChargement`).
- [ ] **7.5** Renouvellement des certificats d'officine : accepté seulement dans les 60 derniers jours, une fois par jour au plus.
- [ ] **7.6** Révocation : voir procédure F5.
- [ ] **7.7** Proxy inverse : les limites par adresse lisent l'adresse de la connexion. Derrière un proxy, c'est celle du proxy. **[À VÉRIFIER à l'hébergement.]** Réglage : `Cloud:LimiteParAdresseMinute` (600 par défaut).
- [ ] **7.8** Conservation des données d'une officine partie (script `021`) : 6 ans par défaut, **6 ans minimum** (refus en dessous), pas de maximum, comptée depuis la date de départ. `--conservation-depart <ans>` (globale), `--conservation-depart <ans> --officine <code>` (surcharge), `--conservation-depart global --officine <code>` (retour). `--reglages` affiche aussi les officines parties et la date à partir de laquelle leur effacement est possible. **[À VÉRIFIER : avis du juriste, loi 18-07 et durée fiscale.]** Départ et effacement : procédure F8.

### 8. Activer une officine

Ordre pour **chaque** officine.

- [ ] **8.1** Créer l'officine. **Où** : serveur central. **Compte** : compte d'administration.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --creer-officine "<nom commercial>"
  ```
  - Affiche le **code provisoire** de l'officine et un **code d'activation** du serveur principal (usage unique, 24 h).
  - Pour le 2e serveur de la même officine : `Pharma.Cloud.Api.exe --code-activation <code officine> --serveur second` (son propre code, son propre certificat).
- [ ] **8.2** Transmettre le code au titulaire **par un canal sûr**, jamais dans le même message que le nom de l'officine.
- [ ] **8.3** Régler la synchronisation dans l'officine. **Où** : PC serveur de l'officine, application. **Compte** : titulaire (droit `admin.parametres`).
  - Administration, Synchronisation, sous-rubrique **Réglages** : adresse du serveur central (`https://…`), cocher « Synchronisation activée », Enregistrer.
- [ ] **8.4** Activer. Même écran, sous-rubrique **Activation** : coller le code, Activer.
  - La clé privée est créée sur le PC serveur (`C:\ProgramData\PharmaOfficine\Synchronisation`, protection DPAPI machine). Elle ne quitte jamais ce PC.
  - À la première activation, le serveur central adopte le code de l'officine installée.
- [ ] **8.5** Vérifier dans **État** : « activé (certificat) », dernier envoi et dernier accusé à jour en une à deux minutes.
- [ ] **8.6** Activer par **groupes progressifs** (ex. 10 officines, puis 100, puis le reste). Attendre la fin du chargement complet de chaque groupe avant le suivant (État : chargement « terminé ». Central : table `chargement`, état `termine`).

### Procédure d'activation des officines

Ce qui se passe à la première activation :

1. Le serveur central adopte le code de l'officine installée (`sync_parametre.officine_id`).
2. Le réglage des familles nominatives descend dans la base de l'officine (table `reglage_central`). Tant qu'il n'est pas reçu, rien de nominatif ne part.
3. Un **chargement complet** part : toutes les données envoyées. Il est débité à 600 lots par minute, et ses lots ne comptent pas dans le quota du jour.
4. Ensuite, seules les modifications partent, chaque minute.

Une officine installée ou mise à jour **avant** le serveur central n'envoie rien : la synchronisation est désactivée par défaut, aucun déclencheur, aucun coût sur les ventes.

**Compatibilité des versions** : le serveur central accepte la version actuelle du contrat et la précédente. Aujourd'hui, une seule version existe et une seule est acceptée (lots `sync-v1`, éléments descendants `desc-v1`). Ordre de mise à jour : **serveur central d'abord**, officines ensuite.

**Volume** : compter environ 10 000 lignes par minute (mesure faite sur la base de démonstration : 34 000 lignes). Vérifier le quota journalier avant le chargement d'une grosse officine. **[À VÉRIFIER par mesure.]**

**Fin de chargement** : le serveur central marque « absentes de l'officine » (`sync_absente_le`) les lignes qu'il a et que l'officine n'a plus. Il ne les efface **jamais**. Le titulaire décide (ressaisie, annulation tracée ou justification). Détail : table `chargement` (`absentes_par_table`) et journal de sécurité (`chargement_absentes`).

### Chargement complet (quand, et que faire)

- **Quand** : à la première activation, à la réactivation (les modifications faites pendant la désactivation ne sont pas inscrites), sur demande (Administration, Synchronisation, État, « Recharger toutes les données », droit `admin.parametres`, confirmation), et automatiquement si le serveur central a perdu des lots que l'officine n'a plus.
- **Effet** : l'état actuel de chaque donnée envoyée repart, minimisé (codes patients, colonnes retirées, textes filtrés), par lots de 500 lignes ou 1 Mo. Le travail continue : les ventes ne sont retenues que le temps d'un verrou court (2 s au plus).
- **Suivi** : la progression s'affiche (lignes accusées / total). Après une coupure, l'envoi reprend au premier lot non accusé.

### Supervision et purge de la file (script 140)

- **Bandeau et alertes** : réglés dans Administration, Synchronisation, **Supervision et purge** (droit `admin.parametres`). Valeurs de départ : bandeau après 60 minutes, alerte de retard après 24 h, alerte d'erreur après 60 minutes. Le droit **« Voir le bandeau de synchronisation »** (`sync.suivre`) est donné au titulaire ; l'attribuer aux autres rôles voulus dans Administration, Droits par rôle. Les alertes partent aux destinataires vérifiés abonnés au sujet **« Synchronisation »** (Administration, Notifications) : vérifier qu'il y en a au moins un.
- **Purge** : les lots accusés sont gardés **30 jours** (réglable), puis leurs lignes sont retirées de la file ; si le serveur central perd ensuite un lot purgé, l'officine demande seule un chargement complet. Si l'hébergeur garde les sauvegardes du serveur central plus de 30 jours, allonger la conservation.

### Lots manquants

Le serveur central a perdu des lots déjà accusés (restauration du central) :

- si l'officine les a encore (`sync_lot`), elle les renvoie à l'identique, sans intervention ;
- sinon, elle demande **d'elle-même** un chargement complet (message « rechargement complet de l'officine demandé »). Au passage suivant, l'instantané est pris et annoncé. Le central abandonne les lots perdus (journal de sécurité `lots_abandonnes`) et l'envoi reprend normalement. **Ne rien supprimer dans l'officine.**
- si le même message revient alors qu'un chargement « lots manquants » est déjà annoncé (« reprise impossible automatiquement … procédure »), comparer `officine_epoque`, `lot_recu` et `chargement` du central avec `sync_lot` et `sync_chargement` de l'officine. Les opérations restent en attente dans l'officine : rien n'est perdu. **Appeler le développeur.**
- **Restauration de la base de l'officine** (n° de lots revenus en arrière) : voir ci-dessous.

### Base de l'officine revenue en arrière (restauration, bascule ; script 149)

Après une **restauration** de la base de l'officine (F1, ou la restauration faite par une bascule sur le PC de secours, F2) ou une **bascule sur le 2e serveur** (F3) qui a perdu les dernières transactions, le serveur central a pu recevoir des lots que la base n'a plus. **Ordre de mise en production** : script central `020` passé **avant** le script `149` des officines (étape 10.2 du parcours E) ; sans lui, la détection et la nouvelle époque fonctionnent, mais les n° ne sont pas avancés (message « Avance des n° non faite… », nouvel essai à chaque passage).

- **Détection (automatique)** : à chaque passage, avant de former un lot, l'officine lit le dernier lot reçu par le serveur central pour l'époque en cours. S'il est plus grand que le dernier lot formé ici, ou si un lot jamais reformé est refusé « empreinte différente » alors que le central a déjà reçu ce n° : **envoi suspendu** (rien n'est formé ni envoyé), message dans Administration, Synchronisation, État (« Base revenue en arrière… »), une **alerte** au titulaire (SMS et e-mail, sujet « Synchronisation ») par détection, et les n° des nouvelles opérations sont **avancés** au-delà de ceux déjà reçus par le central (table verrouillée le temps de l'avance : jamais en arrière). Le travail de l'officine continue normalement ; les opérations restent en file. Depuis le 2026-11-18, si le serveur central ne répond pas à ce contrôle, **rien n'est formé ni envoyé** à ce passage (« Serveur central injoignable pour le contrôle de l'état… », nouvel essai au passage suivant).
- **Intégrité (décision du titulaire, 2026-11-18)** : des anomalies d'intégrité sont **attendues** au serveur central pour les rangs restaurés (journal des ventes et autres journaux chaînés) : elles ne signifient pas une falsification. Ne rien corriger ; depuis le lot S6.5 (2026-11-19, scripts centraux `022` à `024`, script officine `151`), le serveur central les classe « remplacées après restauration » quand la nouvelle époque lui a été **déclarée** avant tout lot (ce que l'officine fait d'elle-même après « Repartir sur une nouvelle époque ») et que toutes les conditions sont réunies ; l'ancienne suite y est gardée comme preuve. Le compteur des tickets est aussi avancé au-delà du plus grand n° reçu par le central (plage sautée notée).
- [ ] **À faire par le titulaire**, une fois le service redémarré et les postes reconnectés : Administration, Synchronisation, État, bandeau « Envoi suspendu… », **« Repartir sur une nouvelle époque »**, saisir **son mot de passe** (même vérification que « Changer mon mot de passe » : 5 mots de passe faux bloquent quelques minutes), puis **Confirmer**. **Compte** : titulaire connecté par mot de passe (refusé à tout autre compte, et refusé s'il n'y a pas de retour en arrière détecté). Le serveur central doit répondre (il donne la plus grande époque qu'il connaît pour l'officine) ; sinon refus, rien ne change, recommencer plus tard. Action inscrite au journal des actions (`synchronisation_nouvelle_epoque`) dans la même transaction que le changement d'époque.
  - **Effet** : époque = plus grande des deux (époque locale, plus grande époque connue du central) + 1, compteurs de lots à 0, puis **chargement complet** (il part au passage suivant ; « Synchroniser maintenant » pour le lancer tout de suite). Les lots et opérations de l'ancienne époque restent dans la base (signalés « lot(s) d'une époque précédente jamais accusé(s) », jamais renvoyés). À la fin du chargement, le serveur central **signale sans les effacer** les données qu'il a et que la base n'a plus (ventes perdues par la restauration) : le titulaire décide (ressaisie, annulation tracée ou justification).
  - **Attendu** : bandeau disparu, « Lots constitués » à l'époque suivante, chargement « en cours (demande) » puis terminé.
- [ ] **À faire par le titulaire, après le chargement complet** : Administration, Synchronisation, Intégrité au central, « Restaurations constatées » : **« Prendre acte »** et saisir la cause (panne, sauvegarde utilisée, date). **Attendu** : la restauration reste listée, prise d'acte « Faite » ; l'état repasse à « Aucune anomalie » au passage suivant de la vérification du central si rien d'autre n'est en anomalie. Classe « À justifier auprès de l'éditeur » (ligne scellée perdue, restaurations trop fréquentes) : prévenir l'éditeur.
- **Ne rien supprimer** dans l'officine ni au serveur central. Si la détection revient juste après une nouvelle époque (base restaurée deux fois), recommencer : chaque nouvelle époque est sûre (jamais une époque déjà connue du central).
- « Synchroniser maintenant » pendant la suspension : réponse normale, le message s'affiche comme un **avertissement** (plus d'erreur « serveur central »).

### 9. Réglages mutualisés (titulaire et technicien)

Le serveur central peut proposer ou imposer aux officines la valeur de 11 réglages (`docs/conception-synchronisation.md`, § 16 bis). Prérequis : scripts centraux `010` et `011` (correctif D-M8-1) passés (étape 2.1) et variables de l'étape 3.2 (`PHARMA_CLOUD_ADMIN_CONNEXION`, jamais `PHARMA_CLOUD_CONNEXION`). **Rien n'est publié par défaut** : tant que vous n'avez rien publié pour une table, rien ne descend et l'officine reste maîtresse de cette table. Chaque action est inscrite au journal central (ajout seul) avec l'**auteur** (obligatoire), le compte Windows et le poste. Les 11 tables : `motif_reglable`, `parametre_alerte`, `parametre_fiscal`, `parametre_formulaire_sms`, `parametre_inventaire`, `parametre_serialisation`, `parametre_substitution`, `parametre_surveillance`, `parametre_tracabilite`, `regle_alerte_psychotrope`, `remise_automatique`. **Où** : serveur central. **Compte** : compte d'administration, dans une session PowerShell où les variables sont réglées.

- [ ] **9.1** Voir l'état des 11 tables : mode, version, auteur, date, nombre d'officines qui ont appliqué ou rejeté (et le motif de chaque rejet).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --reglages-mutualises
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --reglages-mutualises parametre_inventaire
  ```
  - Avec un nom de table : le détail officine par officine. L'état des officines est celui de leur dernier envoi au central.
- [ ] **9.2** Premier modèle (facultatif) : fabriquer un fichier à partir des valeurs réelles d'une officine de référence (reçues au central). **Ne publie rien.**
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --modele-depuis-officine <code officine> parametre_inventaire --fichier "D:\PharmaCentral\Modeles\inventaire.json" [--mode propose|impose] [--ecraser]
  ```
  - Aucun identifiant propre à l'officine n'est repris. Pour les remises, seules les catégories en cours sont gardées ; pour les motifs, **relire** : les motifs locaux de cette officine y figurent, retirer ceux qui ne doivent pas être communs.
  - Le fichier est au format `mutu-v1` : version, date et auteur du fichier sont remplacés par le central à la publication.
  - **Si échec** : « L'officine n'a encore rien envoyé pour cette table » : l'officine n'a pas encore envoyé cette table (attendre son chargement complet).
- [ ] **9.3** Simuler, puis publier un modèle.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --publier-reglage <table> --fichier "<modèle.json>" --auteur "<nom>" --simulation
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --publier-reglage <table> --fichier "<modèle.json>" --auteur "<nom>" [--motif "<texte>"]
  ```
  - Le modèle est contrôlé en entier avant publication (mêmes règles métier que la saisie dans l'officine, remises terminées refusées à la date du jour). **Une seule erreur : rien n'est publié**, code de sortie 1 ; `--simulation` montre le mode, la version et les erreurs sans rien écrire.
  - Publier un modèle **identique** à celui en vigueur ne crée aucune version. Une publication en mode **imposé** demande une confirmation (taper `oui`, ou `--oui`).
  - Le modèle descend dans chaque officine à son prochain échange (une minute environ), puis l'officine l'applique ; elle peut le **rejeter** s'il ne passe pas ses contrôles (visible en 9.1).
- [ ] **9.4** Changer le mode d'une table (`propose` ou `impose`).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --mode-reglage <table> impose|propose --auteur "<nom>" [--motif "<texte>"] [--oui]
  ```
  - **Passer en imposé remplace, dans chaque officine, les valeurs modifiées localement** par celles du modèle, avec un message au titulaire de chacune : la commande l'affiche et demande de taper `oui` (sauf `--oui`). **C'est une décision du titulaire.**
  - Le mode « local » n'existe pas pour une table : pour qu'une officine redevienne maîtresse de ses réglages, voir 9.5.
  - Une table non publiée : le mode est seulement noté, rien ne descend.
- [ ] **9.5** Départ d'une officine : la libérer (ses tables repassent en `local`, valeurs gardées), ou annuler la libération.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --liberer-reglages <code officine> --auteur "<nom>" [--motif "<texte>"]
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --retablir-reglages <code officine> --auteur "<nom>" [--motif "<texte>"]
  ```
- [ ] **9.6** Lire le journal (qui, quand, quelle table, mode avant et après, version).
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --journal-reglages [<table>] [--depuis AAAA-MM-JJ] [--limite <n>]
  ```
  - Les 200 dernières entrées par défaut, affichées de la plus ancienne à la plus récente.
- **Codes de sortie** : 0 si tout est bon, 1 en cas d'erreur (auteur manquant, modèle invalide, confirmation refusée, officine inconnue, base inaccessible). Aucune commande n'écrit sans passer par les fonctions du script `010`.
- **[À VÉRIFIER]** L'application d'un modèle dans l'officine (lot M4) est en cours de réalisation : tant qu'elle n'est pas livrée avec la mise à jour des officines, un modèle publié descend mais n'est pas appliqué. **Ne pas publier en production avant ce lot.**

### 10. Mise à jour du serveur central

- [ ] **10.1** Sauvegarder la base centrale. **Où** : serveur central. **Compte** : propriétaire.
- [ ] **10.2** Repasser `002` (colonnes `sync_chargement`, `sync_absente_le`, etc.), puis `003` au dernier (036 à ce jour), **dans cet ordre** (`029` à `036` : rôles de lecture et des écrans, comptes distants, écrans de l'éditeur et du titulaire, ordres et réglages communs du titulaire, lignes absentes vues par les écrans — `036` pose aussi un index partiel par table miroir : le repasser après un `002` régénéré ; `026` à `028` : tables sans archive réglables, effacement d'une officine partie avec ses éléments techniques, effacement d'une famille nominative aussi dans les lignes archivées ; `027` et `028` demandent le `022` ; le `025` **avant** le script `152` des officines : n° de tickets réattribués signalés ; le `022` à `024` **avant** le script `151` des officines : tables des restaurations, route `POST /api/v1/epoque`, table miroir `integrite_prise_acte` du `002` régénéré, n° de ticket dans `/maximums` ; le `019` **avant** le script `147` des officines : sinon leurs lots sont refusés ; le `020` **avant** le script `149` des officines : route `GET /api/v1/maximums` et archive des lignes remplacées ; `020` et `021` demandent PostgreSQL 18 et le refusent clairement en tête sinon ; depuis le 2026-11-18 rejouer un ancien script ne ramène plus en arrière une fonction redéfinie par un script plus récent (gardes étendues le 2026-11-20 à `004`, `010`, `012`, `013` à `017`, `020`, `021`, `022`) ; `002` régénéré **avant** le script `148` des officines : colonnes d'alerte de `parametre_sauvegarde`), avec le compte propriétaire, un par un : `& $psql -h <hôte> -U <propriétaire> -d <base centrale> -v ON_ERROR_STOP=1 -q -f "database\cloud\<script>.sql"` (le script `creer_base_centrale.ps1` de l'étape 2.1 ne sert que pour une base neuve et refuse une base déjà installée). Scripts idempotents.
- [ ] **10.3** Relancer les tests de l'étape 2.3 sur la copie.
- [ ] **10.4** Redémarrer le serveur central. **Avant** toute mise à jour des officines.

---

## Procédures occasionnelles

Chaque procédure se lit dans l'ordre, du haut vers le bas.

### F1. Restauration (base ou GED détruite)

Les sauvegardes utiles sont sur le **disque externe**. La copie de la base dans `GED\Sauvegardes` est sur le même PC : elle disparaît avec lui.

- Base : `pharmacie_AAAAMMJJ_HHMMSS.dump` dans le dossier réglé dans Administration, Sauvegardes (les 14 dernières par défaut).
- GED : sous-dossier `GED` de ce même dossier.

**Méthode 1 : depuis l'application** (super admin, étape 10). L'API doit tourner sur le PC serveur. La base peut être détruite : le compte du super admin et le dossier des sauvegardes sont hors de la base.

- [ ] **1.** Sur un poste, « Connexion super admin », écran **Restauration**.
- [ ] **2.** **Base** : choisir la sauvegarde (disque externe ou `GED\Sauvegardes`, la plus récente en haut), puis valider : avertissement, mot de passe, code reçu par SMS et par e-mail (le même ; un canal suffit).
- [ ] **3.** Le serveur, en tâche de fond, met les postes en attente, fait une **copie de sécurité** de la base actuelle (`GED\Sauvegardes\pharmacie_avant_restauration_….dump`, à supprimer à la main quand elle n'est plus utile), restaure la sauvegarde dans une base neuve, la **met à la version du logiciel** (scripts `0xx` livrés avec l'API), puis lui donne le nom de la base en service. L'ancienne est **gardée** sous `<base>_avant_restauration_AAAAMMJJ_HHMM`. L'écran suit les étapes.
  - Un échec avant l'échange laisse la base en service intacte.
  - Il faut le droit `CREATEDB` du compte `pharma_app` (parcours A, étape 2.2).
- [ ] **4.** **GED** : « Recopier les documents de la GED ». Les documents du disque externe absents ou plus anciens au serveur sont recopiés. Rien n'est supprimé.
- [ ] **5.** Contrôler les totaux et ressaisir les opérations faites depuis la sauvegarde (en général la journée). La restauration est inscrite au journal de la base restaurée.
- [ ] **6.** Synchronisation activée : à la première minute, l'envoi se suspend (« Base revenue en arrière… ») ; le **titulaire** repart sur une nouvelle époque (parcours E, « Base de l'officine revenue en arrière »).

**Méthode 2 : sans l'application** (API hors service, PC serveur remplacé).

- [ ] **1.** PC serveur remplacé : installer PostgreSQL (parcours A, étape 2) et recréer le compte `pharma_app` avec `CREATEDB`. **Ne pas** lancer `schema.sql` : la sauvegarde contient toute la structure.
- [ ] **2.** Arrêter le service : `Stop-Service "PharmaOfficine API"` (s'il existe).
- [ ] **3.** Lancer, dans PowerShell sur le PC serveur (le mot de passe de `postgres` est demandé) :
  ```powershell
  .\scripts\restaurer_pharmacie.ps1 -Sauvegardes "E:\Sauvegardes\Pharmacie"
  ```
  - La dernière sauvegarde est vérifiée (`pg_restore --list`) puis restaurée. Une sauvegarde illisible est refusée : en choisir une plus ancienne avec `-Fichier "<fichier>.dump"`.
  - Une base existante du même nom n'est **jamais** supprimée : le script s'arrête, ou, avec `-Remplacer`, la renomme `pharmacie_avant_restauration_AAAAMMJJ_HHMM`.
  - La GED est recopiée dans `C:\ProgramData\PharmaOfficine\GED` sans rien supprimer, en gardant un document plus récent déjà présent.
  - `-SansGed` ou `-SansBase` pour ne restaurer que l'une des deux. Les paramètres `-Base`, `-DossierGed`, `-PgBin`, `-HostPg`, `-Port` existent aussi.
  - **Attendu** : le script se termine sans erreur et affiche le nombre de ventes et la date de la dernière.
- [ ] **4.** Sauvegarde plus ancienne que la version du logiciel : rejouer les scripts de mise à jour avec `scripts\mep\mettre_a_jour_base_officine.ps1` (parcours B, étapes 3 à 10).
- [ ] **5.** Démarrer le service et vérifier : `Start-Service "PharmaOfficine API"`, puis `http://localhost:5080/api/healthcheck`. Les documents restés sur les postes (« A envoyer ») repartent dans la GED à la connexion suivante.
- [ ] **6.** Ressaisir les opérations faites depuis la sauvegarde. Sur un nouveau PC, ressaisir le mot de passe des notifications (chiffré pour le PC d'origine).
- [ ] **6 bis.** Synchronisation activée : après le redémarrage, le **titulaire** repart sur une nouvelle époque (parcours E, « Base de l'officine revenue en arrière »).
- [ ] **7.** Si le PC serveur a changé : refaire le parcours A, étape 4.9, et **restaurer l'autorité HTTPS** depuis le PFX du coffre, puis créer le certificat du nouveau PC (procédure F2 bis ci-dessous). L'autorité est **unique pour la pharmacie** : les postes et téléphones n'ont **rien à réinstaller**. Sans le PFX du coffre, une nouvelle autorité serait créée et il faudrait réinstaller `autorite-pharmacie.crt` partout (parcours A, étapes 5.3 à 5.4 et 6.2).

> La copie de la GED sur le disque externe n'a qu'un exemplaire par document : un document abîmé sur le serveur remplace sa copie à la sauvegarde suivante. Garder deux disques externes en rotation, dont un toujours hors de l'officine (obligatoire sans 2e serveur, étape 11.2 bis).

### F2. PC serveur en panne (bascule sur le PC de secours)

Prérequis : parcours D fait. La pharmacie repart à partir de la **dernière copie de secours**. La bascule fait perdre les opérations faites depuis cette copie.

- [ ] **1.** Constater la panne. Sur les postes, bandeau « Serveur injoignable » après le délai réglé. **Vérifier d'abord le PC serveur** : allumé, réseau, service `PharmaOfficine API`. Si le PC serveur répond, ne pas basculer.
- [ ] **2.** Sur le PC de secours, PowerShell **en administrateur**, dans le dossier `scripts` :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours" -Essai
  ```
  - Simulation : contrôles seulement, rien n'est modifié. Vérifier le résultat.
- [ ] **3.** Lancer la bascule (le mot de passe de `postgres` est demandé) :
  ```powershell
  .\basculer-pc-secours.ps1 -Copies "D:\CopieSecours"
  ```
  - Le script prend la copie la plus récente (ou `-Fichier`), contrôle l'empreinte SHA-256 et la lisibilité, restaure dans une base neuve, **rejoue tous les scripts de la base** livrés avec l'API (le dernier rejoué est affiché : il doit être le dernier de la version installée), garde une base existante sous `<base>_avant_bascule_<date>` (jamais supprimée), démarre le service et affiche l'adresse à donner aux postes.
  - **Attendu** : le script se termine sans erreur et affiche l'adresse du PC de secours.
- [ ] **4.** Sur **chaque poste** : bouton **« Basculer sur le 2e serveur »** du bandeau (il ne règle le poste que si l'API de secours répond déjà), ou rubrique Serveur et PC de secours. Puis fermer et rouvrir Tracodex Officine.
  - **Fonctionnement constaté dans le code** : le bouton appelle `https://<adresse de secours>/api/healthcheck` (4 secondes). Le poste vérifie le certificat avec les autorités installées dans Windows (aucune exception) : la bascule ne marche que si l'autorité de la pharmacie est installée sur le poste (parcours A, étape 5.4) **et** si le nom ou l'IP saisi dans « PC de secours » figure dans le certificat du PC de secours (parcours C étape 8 et parcours D étape 6). Sinon, le poste n'est pas basculé et le bandeau distingue trois cas : **« Le 2e serveur ne répond pas… »** (PC éteint, service pas démarré, port 5443 fermé ou adresse erronée) ; **« Le certificat du 2e serveur est refusé par ce poste… »** (autorité absente du poste : installer l'autorité de la pharmacie, étape 5.4 ; ou adresse saisie absente du certificat : la corriger) ; **« Le 2e serveur est joignable mais n'est pas encore basculé… »** (lancer « Basculer sur le 2e serveur » sur le 2e serveur). Le poste ne contourne jamais la vérification du certificat. Le journal local du poste (`%LOCALAPPDATA%\PharmaOfficine\liaison.log`) note l'état à chaque changement (sans adresse ni donnée personnelle). Si le bandeau indique un certificat refusé, ouvrir `https://<adresse de secours>/api/healthcheck` dans un navigateur du poste : un avertissement de certificat en est la cause.
  - Aucune réinstallation d'autorité n'est nécessaire sur les postes : c'est la même autorité que celle du PC serveur.
- [ ] **5.** GED : recopier le dossier `GED` du disque externe si besoin : `.\scripts\restaurer_pharmacie.ps1 -SansBase` (paramètre `-DossierGed` du PC de secours).
- [ ] **6.** Ressaisir les opérations faites depuis la copie. Ressaisir le mot de passe des notifications.
- [ ] **6 bis.** Synchronisation activée : après le redémarrage, le **titulaire** repart sur une nouvelle époque (parcours E, « Base de l'officine revenue en arrière »).
- [ ] **7.** Retour sur le PC serveur réparé : procédure F4.

### F2 bis. Restaurer l'autorité HTTPS (PC serveur détruit ou remplacé)

À faire quand le PC serveur principal est **remplacé définitivement** (ou qu'un PC de secours devient le nouveau principal) : le nouveau PC doit pouvoir **signer** les certificats. Le PFX de l'autorité, rangé au coffre à l'étape 4.6 du parcours A, est restauré : les postes et téléphones continuent à faire confiance à la même autorité, **rien à réinstaller sur eux**.

Prérequis : la clé USB du coffre (`F:\autorite-pharmacie.pfx`) et sa phrase de passe (papier du coffre).

- [ ] **1.** Sur le **nouveau** PC principal (PowerShell **en administrateur**, dans le dossier qui contient `scripts`), essai à blanc :
  ```powershell
  .\scripts\certificat_https.ps1 -RestaurerAutorite -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx -WhatIf
  ```
  - **Attendu** : `[SIMULATION] L'autorité serait importée …`, aucune phrase demandée.
- [ ] **2.** Restauration réelle :
  ```powershell
  .\scripts\certificat_https.ps1 -RestaurerAutorite -FichierSauvegardeAutorite F:\autorite-pharmacie.pfx
  ```
  - Le script demande la phrase de passe du fichier (saisie masquée), vérifie que c'est bien l'autorité de la pharmacie avec sa clé, et l'importe avec une clé **non exportable**.
  - **Attendu** : `Autorité importée dans Cert:\LocalMachine\My …` et l'empreinte SHA-256. **La comparer** à l'empreinte notée à l'étape 4.6 (ou à `empreinte-autorite-pharmacie.txt`) : elles doivent être **identiques**.
  - **Si échec** : « phrase de passe incorrecte » : ressaisir (la phrase est celle choisie à l'étape 4.6). « Une autre autorité … est déjà dans le magasin » : rien n'est écrasé ; vérifier laquelle est la bonne, retirer l'autre à la main. Phrase ou fichier perdus : voir l'étape 5.
- [ ] **3.** Créer le certificat du nouveau PC et reconstituer la configuration (étape 4.6, sans `-FichierSauvegardeAutorite` puisque l'autorité existe) :
  ```powershell
  .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu
  ```
  - Ajouter `-NomsSupplementaires` / `-AdressesSupplementaires` si le nouveau PC a un nom ou une IP différents : **les postes doivent utiliser un nom ou une IP présent dans ce certificat** (variable `PHARMA_API`, étape 5.6 du parcours A, à mettre à jour si l'adresse a changé).
  - **Attendu** : `Autorité de la pharmacie existante réutilisée`, `Certificat du serveur créé`, et la même empreinte d'autorité qu'avant. Redémarrer le service de l'API.
- [ ] **4.** Un poste ouvre `https://<nouveau serveur>:5443/api/healthcheck`. **Attendu** : 200, **sans avertissement**. Les 2e serveur et PC de secours déjà configurés **gardent leur certificat** (signé par la même autorité) : rien à refaire sur eux.
- [ ] **5.** Si le PFX ou sa phrase sont perdus : la clé n'est pas récupérable (non exportable dans le magasin de l'ancien PC, détruit). Il faut **une nouvelle autorité** : lancer le script avec un nouveau `-FichierSauvegardeAutorite` (étape 4.6), puis **réinstaller** `autorite-pharmacie.crt` sur tous les postes et téléphones (étapes 5.3, 5.4, 6.1, 6.2) et régénérer les certificats du 2e serveur et du PC de secours (parcours C étape 8, parcours D étape 6).

### F3. Bascule du 2e serveur

Prérequis : parcours C fait. Décision du titulaire.

- [ ] **1.** Constater la panne (bandeau « Serveur injoignable » sur les postes). Vérifier le PC principal (allumé, réseau, services). Le titulaire décide.
- [ ] **2.** Sur le PC principal, si possible : arrêter le service de l'API et PostgreSQL, ou **débrancher son câble réseau**.
- [ ] **2 bis.** GED : dernière copie, **si le principal est encore joignable** (allumé, réseau, partage ; bascule voulue ou panne partielle). **Où** : 2e serveur. **Compte** : administrateur Windows. À faire **après** l'arrêt de l'API du principal (étape 2) : plus aucun document n'arrive.
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$
  ```
  - **Attendu** : « rien à copier » ou « fichiers copiés », code 0. Principal éteint ou injoignable : « REFUS : source injoignable », **sans rien toucher** ; continuer : les documents déposés dans les dernières minutes avant la panne (N minutes au plus : 5 par défaut, réglage de l'application) manquent sur ce serveur (voir la limite, parcours C étape 12 quater) ; les noter pour les retrouver au retour (F4, étape 3 ter).
- [ ] **3.** Sur le 2e serveur, PowerShell **en administrateur**, dossier `scripts\second-serveur` :
  ```powershell
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Schema https -PortApi 5443 -Essai
  .\basculer-second-serveur.ps1 -AncienPrincipal <IP du principal> -Schema https -PortApi 5443
  ```
  - `-Schema https -PortApi 5443` : c'est le port que les postes utilisent. Le port 5080 n'écoute que sur le PC lui-même : le contrôle « l'ancien principal répond-il encore ? » doit donc viser 5443, et l'adresse affichée pour les postes devient `https://<IP>:5443/`. Le contrôle local de `/api/healthcheck` en HTTPS peut avertir si l'autorité n'est pas installée sur le 2e serveur lui-même (sans conséquence pour les postes).
  - Le script **refuse** si l'ancien principal répond encore (PostgreSQL ou API). Il demande de taper `BASCULER`.
  - **Attendu** : le script affiche l'heure de la dernière transaction reçue. Ce qui a été saisi après (rare) est à ressaisir.
- [ ] **3 bis.** GED : arrêter la copie automatique sur ce serveur, qui est maintenant le principal. **Où** : 2e serveur devenu principal. **Compte** : administrateur Windows.
  ```powershell
  .\copier-ged.ps1 -Source \\<IP de l'ancien principal>\PharmaGED$ -Desinstaller -Essai
  .\copier-ged.ps1 -Source \\<IP de l'ancien principal>\PharmaGED$ -Desinstaller
  ```
  - **Pourquoi** : la copie ne va que dans un sens, du principal vers le second. Cette tâche tirerait la GED d'un serveur qui n'est plus le principal. Elle ne supprimerait rien, mais le sens doit être inversé au retour (F4). La GED locale (documents copiés) est **gardée** et l'API de ce serveur l'utilise : **vérifier que `Ged__Dossier` de ce serveur (ou le défaut `C:\ProgramData\PharmaOfficine\GED`) est bien le dossier où la copie a écrit** (`Destination` de la tâche), sinon les documents copiés ne seraient pas vus.
  - **Attendu** : « Tâche … supprimée ». Si la tâche était déjà absente : « rien à supprimer ».
- [ ] **4.** Sur chaque poste : bouton **« Basculer sur le 2e serveur »**, puis fermer et rouvrir Tracodex Officine.
- [ ] **5.** Titulaire : écran « 2e serveur ». Ce serveur est maintenant « Principal ». Vérifier les dossiers Chifa et MDN, la clé (identique). **CHIFA-Officine, lecteur de carte et Token** : à confirmer sur ce PC (question posée à la CNAS, `docs/taches/2026-11-15-recette-complete/demande-cnas.md`).
- [ ] **6.** Le principal ne doit **jamais** redevenir actif sans la procédure F4 : l'API refuse de démarrer si l'autre serveur a aussi une base principale.
- [ ] **7.** Synchronisation activée : si des transactions non répliquées avaient déjà été envoyées au serveur central, l'envoi se suspend (« Base revenue en arrière… ») ; le **titulaire** repart alors sur une nouvelle époque (parcours E, « Base de l'officine revenue en arrière »). Sans message, rien à faire.

### F4. Retour du serveur réparé

- [ ] **1.** **Ne pas redémarrer l'API de l'ancien principal** : elle refuserait de démarrer si `SecondServeur__AutreServeur` est réglé.
- [ ] **2.** Sur le **nouveau** principal, PowerShell en administrateur :
  ```powershell
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau> -Essai
  .\preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau>
  ```
  (slot de réplication, `pg_hba.conf`).
  Puis, toujours sur le **nouveau** principal, ouvrir le port 5432 à l'ancien seulement (parcours C, étape 4 bis) :
  ```powershell
  .\ouvrir-port-base.ps1 -AdresseSecond <IP de l'ancien> -WhatIf
  .\ouvrir-port-base.ps1 -AdresseSecond <IP de l'ancien>
  ```
  - **Attendu** : règle créée pour la seule IP de l'ancien, « aucune autre règle active n'ouvre le port 5432 ».
  Puis, toujours sur le **nouveau** principal, partager sa GED en lecture seule à l'ancien seulement (parcours C, étape 12 bis ; le compte `PharmaGedCopie` existe déjà, son mot de passe est celui du coffre) :
  ```powershell
  .\partager-ged.ps1 -AdresseSecond <IP de l'ancien> -Essai
  .\partager-ged.ps1 -AdresseSecond <IP de l'ancien>
  ```
  - **Attendu** : partage `PharmaGED$` en lecture seule, règle 445 pour la seule IP de l'ancien, « aucune autre règle active n'ouvre le port 445 ».
- [ ] **3.** Sur l'**ancien** principal :
  ```powershell
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau> -Essai
  .\remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau>
  ```
  - Son ancienne base est **gardée** à côté (`<dossier>_ancien_principal_<date>`). Elle peut contenir les dernières saisies d'avant la panne : **à examiner avant toute suppression**.
  - Il redevient 2e serveur et rattrape le nouveau principal.
- [ ] **3 bis.** GED : les documents déposés pendant la bascule reviennent sur l'ancien principal, **avant** toute remise en service. **Où** : ancien principal (réparé, redevenu 2e serveur). **Compte** : administrateur Windows. Même copie que d'habitude, dans l'autre sens : elle **ne supprime rien** et ne remplace jamais un fichier par un plus ancien (la GED d'avant la panne, gardée sur cette machine, n'est pas touchée).
  ```powershell
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Essai
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Installer -Essai
  .\copier-ged.ps1 -Source \\<IP du nouveau principal>\PharmaGED$ -Installer
  ```
  - La tâche planifiée existe **uniquement sur le serveur qui est second** : elle a été supprimée sur le nouveau principal (F3, étape 3 bis) et elle est maintenant installée sur l'ancien. Mot de passe de `PharmaGedCopie` : celui du coffre.
  - **Attendu** : « fichiers copiés » (les documents déposés depuis la bascule), puis `LastTaskResult` = `0` (parcours C, étape 12 quater). **Si échec** : « REFUS : source injoignable » = étape 2 non faite sur le nouveau principal (partage, pare-feu 445, IP).
  - L'ancien principal a gardé son ancienne GED : ne rien y supprimer à la main avant d'avoir fait l'étape 3 ter.
- [ ] **3 ter.** GED : retrouver les derniers documents d'avant la panne (**facultatif**, utile si la dernière copie n'avait pas pu être faite, F3 étape 2 bis). Ils n'existent que sur l'ancien principal. **Où** : ancien principal, puis nouveau principal. **Compte** : administrateur Windows.
  - Sur l'**ancien** : `.\partager-ged.ps1 -AdresseSecond <IP du nouveau> -Essai`, puis sans `-Essai` (partage en lecture seule, 445 limité à l'IP du nouveau).
  - Sur le **nouveau** (une fois, à la main, **sans** `-Installer`) : `.\copier-ged.ps1 -Source \\<IP de l'ancien>\PharmaGED$ -Essai`, puis sans `-Essai`. **Attendu** : les documents manquants apparaissent ; rien n'est supprimé ni écrasé par un plus ancien.
  - Sur l'**ancien**, ensuite : `.\partager-ged.ps1 -AdresseSecond <IP du nouveau> -Retirer` (il est redevenu second : plus rien à partager).
- [ ] **4.** Rebascule à un moment calme (facultatif, pour revenir à la répartition d'origine) : arrêter l'API et PostgreSQL du principal actuel, lancer `basculer-second-serveur.ps1` sur l'autre, puis `remettre-en-second.ps1` sur celui qu'on vient d'arrêter, et remettre l'adresse des postes (Serveur et PC de secours). **GED, dans cet ordre** : (a) avant la bascule, une dernière copie sur le serveur qui va devenir principal (F3, étape 2 bis, après l'arrêt de l'API de l'autre) ; (b) après la bascule, `-Desinstaller` sur lui (F3, étape 3 bis) ; (c) sur celui qui devient second : `partager-ged.ps1 -Retirer` s'il partageait, puis `copier-ged.ps1 -Installer` avec pour source le nouveau principal (étapes 2 et 3 bis ci-dessus). **Une seule tâche de copie à la fois dans l'officine** : toujours sur le serveur qui est second.

### F5. Révocation d'un certificat (PC volé, officine résiliée)

- [ ] **1.** Lister les certificats. **Où** : serveur central. **Compte** : compte d'administration.
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --lister
  ```
- [ ] **2.** Révoquer un certificat :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --revoquer <empreinte> --motif "<motif>"
  ```
  Ou tous les certificats d'un serveur d'officine (PC volé) : `--revoquer-serveur <code officine> <serveur> --motif "<motif>"`. Révoquer un certificat révoque aussi ceux obtenus en le renouvelant.
- [ ] **3.** Effet au prochain appel. Un nouveau code d'activation permet de réactiver l'officine. Le certificat (3 ans) se renouvelle seul 60 jours avant l'échéance.

### F6. Renouveler le certificat HTTPS (825 jours)

Le certificat de chaque PC dure **825 jours** (environ 27 mois). L'autorité dure 10 ans : son renouvellement (nouvelle autorité) est une réinstallation sur tous les postes ; le prévoir **avant** l'échéance. Noter les dates : `Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*PharmaOfficine*" } | Select-Object Subject, NotAfter`. Renouveler **au moins 2 mois avant** l'échéance (le script refait le certificat de lui-même à moins d'un mois de l'échéance).

- [ ] **1.** **PC serveur principal** (administrateur) : relancer la commande de l'étape 4.6 (sans `-FichierSauvegardeAutorite`) :
  ```powershell
  .\scripts\certificat_https.ps1 -DossierApi "C:\Program Files\PharmaOfficine\Api" -DossierExport E:\ -PareFeu
  ```
  - Le certificat est refait seulement s'il expire dans moins d'un mois ou si la liste des noms change. Pour renouveler plus tôt : retirer le certificat `CN=PharmaOfficine Mobile` du magasin à la main (`Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=PharmaOfficine Mobile' | Remove-Item`) puis relancer, en reprenant la **même liste de noms et adresses** que la première fois.
  - Puis redémarrer le service de l'API. **Attendu** : `Certificat du serveur créé`, nouvelle date d'échéance. Les postes ne changent rien (même autorité).
- [ ] **2.** **2e serveur et PC de secours** : sur le PC serveur, refaire l'étape 8a du parcours C (`-CertificatPourAutrePc`, mêmes noms et adresses, **nouveau nom de fichier**, par exemple `E:\certificat-second-AAAAMMJJ.pfx`) ; sur l'autre PC, refaire l'étape 8b (`-Importer`). Le script retire l'ancien certificat **après** avoir mis le nouveau en place. Redémarrer le service de l'API s'il tourne ; sinon rien (il est arrêté jusqu'à la bascule).
- [ ] **3.** Vérifier sur un poste : `https://<serveur>:5443/api/healthcheck` sans avertissement ; pour le 2e serveur et le PC de secours, au prochain essai de bascule.
- [ ] **4.** Détruire les PFX temporaires de la clé USB des postes ; garder le PFX **de l'autorité** au coffre (inchangé).

### F7. Ancienne autorité « une par PC » trouvée

La première version du script créait une autorité « `PharmaOfficine Autorite locale` » **propre à chaque PC** (jamais exécutée en réel en production). Si le script avertit « Ancienne autorité … trouvée sur ce PC », ou si un poste a reçu un ancien `autorite-pharmacie.crt` :

- [ ] **1.** Constater (sans rien supprimer) : `Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite locale" } | Select-Object PSParentPath, Thumbprint, NotAfter`.
- [ ] **2.** Le script **ne la supprime jamais**. La nouvelle autorité porte un nom différent (`PharmaOfficine Autorite pharmacie`) : les deux coexistent le temps de la transition. Le nouveau certificat du serveur est signé par la nouvelle autorité seulement.
- [ ] **3.** Faire (ou refaire) l'étape 4.6 du parcours A, puis installer la **nouvelle** autorité sur les postes et téléphones (étapes 5.3, 5.4, 6.1, 6.2).
- [ ] **4.** Quand **tous** les postes fonctionnent en HTTPS : retirer l'ancienne **à la main**. Sur le PC serveur : `Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=PharmaOfficine Autorite locale" } | Remove-Item`. Sur chaque poste : `certutil -delstore Root "PharmaOfficine Autorite locale"`. Décision du titulaire ; garder une trace de l'empreinte retirée.


### F8. Départ d'une officine (fin de contrat)

**Où** : serveur central. **Compte** : compte d'administration (`PHARMA_CLOUD_ADMIN_CONNEXION`, propriétaire de la base centrale). Scripts `021` et `027` passés.

- [ ] **1.** Enregistrer le départ et écrire l'export complet remis au titulaire :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --depart-officine <code officine> --sortie "<dossier>\officine-<code>.zip" --operateur "<nom de l'opérateur>" --email "<adresse e-mail du titulaire>"
  ```
  - `--email` est **facultatif** : avec lui, le mot de passe de l'archive est envoyé par e-mail au titulaire (compte SMTP de l'éditeur, variables `PHARMA_CLOUD_SMTP_*` de l'étape 3.2, sinon le compte du réglage « Envoi des codes », relu avec les clés du dossier du central ou celles de `--dossier-cles "<dossier des clés des écrans>"` si les écrans utilisent `Ecrans:DossierCles` ; aucun dossier ni clé créé en lecture) ; sans lui, il est seulement affiché. Une adresse de forme invalide est **refusée avant tout départ ou export** (rien n'est fait). L'adresse n'est **pas enregistrée** au central : seule sa forme masquée (ex. `t***@exemple.fr`) est notée au journal des départs avec le résultat de l'envoi.
  - Tous les certificats de l'officine sont révoqués, la date de départ est posée (gardée si la commande est rejouée) : la synchronisation de l'officine est refusée dès son appel suivant. Rien d'autre à faire dans l'officine.
  - L'archive est un **zip chiffré AES-256 (format WinZip AE-2)**, écrit directement chiffré (aucune copie en clair sur le disque ; le fichier provisoire `<fichier>.zip.partiel`, déjà chiffré, est renommé à la fin ou supprimé en cas d'erreur). Elle contient un fichier CSV par table copiée au central et `ligne_remplacee.csv` (UTF-8, séparateur « ; », en-tête), et `empreintes.txt` (SHA-256 de chaque CSV, une fois extrait : `Get-FileHash -Algorithm SHA256 <fichier>`). Les **noms** des fichiers de l'archive restent visibles sans mot de passe (format zip), pas leur contenu. Un fichier existant n'est jamais écrasé. Patients sous code, comme au central.
  - **Mot de passe** : tiré au sort par la commande (24 caractères, sans caractères ambigus), **affiché une seule fois** à la fin de la commande (dans tous les cas, pour le noter), et, avec `--email`, **envoyé par e-mail au titulaire** après la création réussie de l'archive (message : nom de l'officine, nom du fichier, début de l'empreinte SHA-256, mot de passe ; **jamais l'archive ni un lien**). Il n'est écrit dans aucun journal, ni la base, ni un fichier. **Ne pas rediriger la sortie de la commande vers un fichier.**
    - **Échec de l'envoi** (réglages SMTP incomplets, refus du serveur, réseau) : la commande l'affiche avec sa raison et le note au journal des départs ; **l'archive reste valide** : transmettre le mot de passe affiché **par un autre canal que l'archive** (téléphone, papier).
    - **SMS** : non disponible (aucun fournisseur SMS au serveur central ; noté au reste à faire).
    - **Règle des deux canaux** : l'archive et son mot de passe ne passent **jamais par le même canal**. Aujourd'hui : archive sur clé USB, mot de passe par e-mail (et affiché). **Si un jour l'archive est remise par un lien envoyé par e-mail, le mot de passe ne doit plus partir par e-mail.**
    - Mot de passe perdu : l'archive est illisible, relancer la commande avec un autre `--sortie` (nouvel export, nouveau mot de passe, nouvelle empreinte).
  - **Ouvrir l'archive** : avec **7-Zip** (clic droit → 7-Zip → Extraire, saisir le mot de passe). L'Explorateur de Windows **n'ouvre pas** ce format chiffré. **[À VÉRIFIER : ouverture constatée avec 7-Zip sur un poste réel (non faite : 7-Zip absent du poste de développement le 2026-11-20 ; format vérifié par les tests automatiques).]**
  - Le départ, l'export, le chiffrement, l'empreinte SHA-256 de l'**archive chiffrée** (affichée) et le résultat de l'envoi du mot de passe (adresse masquée, envoyé ou raison de l'échec, jamais le mot de passe) sont notés au journal des départs (`journal_depart_officine`, ajout seul) et au journal de sécurité.
  - Remettre l'archive au titulaire **en main propre sur une clé USB**. La remise par **lien de téléchargement à durée limitée** n'existe pas encore (dans aucun lot, noté au reste à faire).
  - **Accusé de réception** (à faire signer avant toute étape d'effacement), à garder avec le procès-verbal : date de remise, nom du titulaire, mode de remise (clé USB), nom du fichier, empreinte SHA-256 de l'**archive chiffrée telle que remise** (celle affichée par la commande et notée au journal des départs), contrôle par le titulaire avec `Get-FileHash -Algorithm SHA256 <fichier>.zip` **sur l'archive avant extraction** (l'empreinte calculée doit être identique), mot de passe reçu par un autre canal que l'archive (e-mail, téléphone ou papier ; **ne jamais l'écrire sur l'accusé**), signature du titulaire.
- [ ] **2.** Ne rien effacer : l'effacement n'est **jamais automatique**. Il est refusé (même en simulation) tant que la durée de conservation après départ (étape 7.8) n'est pas écoulée.
- [ ] **3.** Après la durée de conservation : simulation (nombre de lignes par table, rien n'est effacé) :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --effacer-officine <code officine>
  ```
- [ ] **4.** Effacement réel, **suppression de données : validation du titulaire obligatoire** et sauvegarde de la base centrale juste avant :
  ```powershell
  & "<dossier>\Cloud\Pharma.Cloud.Api.exe" --effacer-officine <code officine> --confirmer <code officine> --operateur "<nom de l'opérateur>"
  ```
  - Efface les tables copiées, `ligne_remplacee` et les lots bruts de l'officine, **et ses éléments techniques** (script `027`), en une seule transaction : les quatre tables d'intégrité du script `022` (`cloud_ligne_divergente`, `cloud_anomalie_classement`, `cloud_integrite_bifurcation`, `cloud_epoque_changement`), puis `cloud_anomalie_integrite`, `cloud_integrite_sceau`, `cloud_integrite_etat`, `cloud_integrite_officine`, les ordres (`ordre_central`), les éléments descendants (`descendant`, `officine_descendant`), les chargements, les volumes du jour (`officine_volume_jour`) et les époques (`officine_epoque`, en dernier). Procès-verbal (date, officine, opérateur, lignes par table, **sous-total des éléments techniques**, script `027`) dans `journal_depart_officine` et `officine_effacee` au journal de sécurité.
  - Restent (décision du titulaire du 2026-11-20) : la fiche de l'officine, ses certificats révoqués, le code d'activation, les réglages de familles de l'officine, le journal de sécurité, le journal des départs, le journal des réglages et le journal des ordres (`ordre_central_journal`, gardé sans clé étrangère vers les ordres). Les sauvegardes de la base centrale gardent les données jusqu'à leur propre expiration. **[À VÉRIFIER : durée de conservation des sauvegardes du central.]**
---

## Annexes

> **API sous Linux** (démonstration cloud) : réglages ajoutés (`Donnees:Racine`, `Protection:*`, `Fiscal:DossierArchives`,
> `CopieSecours:Fichier`, `ProxysConnus`), dossiers, polices et limites dans [demo-cloud.md](demo-cloud.md). Sur un PC serveur
> Windows, **aucun de ces réglages n'est nécessaire** : sans eux, tout reste comme décrit dans ce manuel.

### A1. Dépannage

| Symptôme | Cause probable | Action |
| --- | --- | --- |
| Le service se ferme aussitôt | `Démarrage refusé : l'API écoute en HTTP (non chiffré) sur le réseau` | Parcours A, étape 4.6 non faite, ou `appsettings.Https.json` absent |
| Le service se ferme : message sur `Jwt:Cle` | Variable absente ou de moins de 64 caractères | Parcours A, étape 4.4 |
| Service `Running`, mais rien ne répond | Pare-feu ou mauvais port | `netstat -ano \| findstr :5443`, puis règle `PharmaOfficine API (https 5443)` |
| Avertissement de certificat sur un poste | Autorité non installée, ou nom du serveur absent du certificat | Parcours A, étape 5.4 ; sur le PC serveur, relancer l'étape 4.6 avec `-NomsSupplementaires` / `-AdressesSupplementaires` ; sur le 2e serveur ou le PC de secours, regénérer le certificat (parcours C, étape 8) avec le nom ou l'IP utilisé par les postes |
| Le poste n'arrive pas à basculer sur le PC de secours : bandeau « Le certificat du 2e serveur est refusé par ce poste… » | Autorité absente du poste, ou nom/IP de « PC de secours » absent du certificat du PC de secours | Installer l'autorité (étape 5.4) ou corriger l'adresse ; `https://<adresse de secours>/api/healthcheck` dans le navigateur du poste ; procédure F2, étape 4 |
| Bandeau « Le 2e serveur ne répond pas… » | 2e serveur éteint, service pas démarré (il ne démarre qu'après la bascule), port 5443 fermé, adresse erronée | Vérifier le 2e serveur, le pare-feu, l'adresse saisie |
| Bandeau « Le 2e serveur est joignable mais n'est pas encore basculé… » | API répond mais base pas encore promue | Lancer la bascule sur le 2e serveur (procédure F2) |
| Bandeau « Le certificat du serveur est refusé par ce poste… » (serveur principal) | Autorité absente du poste ou adresse `PHARMA_API` absente du certificat | Étape 5.4 ; vérifier l'adresse (Administration, Imprimantes et étiquettes) |
| `Première exécution : … Préciser -FichierSauvegardeAutorite` | L'autorité n'existe pas encore sur ce PC | Étape 4.6 (nouvelle pharmacie) ou procédure F2 bis (PC remplacé, PFX du coffre) |
| Avertissement d'heure dans le journal au démarrage | Fuseau Windows incorrect | Parcours A, étape 2.3 |
| Le poste dit « Serveur injoignable » | API arrêtée, ou adresse `PHARMA_API` erronée | Vérifier le service, puis la variable |
| Message « base en lecture seule » | Base d'un 2e serveur non basculé | Normal : écrire sur le PC principal |
| `Get-NetFirewallRule` montre une règle sur 5080 | Règle héritée | `Disable-NetFirewallRule -DisplayName '<nom>'` |
| Deux postes partagent la même caisse | Même `PHARMA_POSTE` | Donner un nom unique à chaque poste |

Journal du serveur : `C:\ProgramData\PharmaOfficine\Journaux\pharma-api-AAAAMMJJ.log` (un fichier par jour). Serveur central : `C:\ProgramData\PharmaOfficine\Journaux\Central\central-api-AAAAMMJJ.log` et `central-ecrans-AAAAMMJJ.log`. Dossier, taille maximale et durée de conservation : section `Journaux` du `appsettings.json` de chaque application.

### A2. Clé de scellement des journaux

- Au premier démarrage, l'API crée `C:\ProgramData\PharmaOfficine\cle-integrite.dat`. Elle est chiffrée pour ce PC.
- Elle se **sauvegarde avec le PC serveur** (copie du fichier sur le disque externe). Sans elle, les sceaux déjà posés ne peuvent plus être vérifiés (ils sont signalés « faits avec une autre clé »).
- Elle n'est lisible que sur ce PC. Pour un autre PC (PC de secours, 2e serveur) : **export** par le titulaire (Administration, 2e serveur, Clé de scellement, fichier protégé par mot de passe), puis **import** (`--importer-cle`) : voir parcours C, étape 6, et parcours D, étape 8.
- Contrôle : `Pharma.Api.exe --empreinte-cle` affiche la même empreinte que l'écran du titulaire.
- Ranger la clé USB **au coffre** et le mot de passe **à part**. Le chiffrement porte sur la clé seulement, pas sur les données.

### A3. Archives fiscales

- Les rapports de clôture et mensuels scellés sont archivés dans `C:\ProgramData\PharmaOfficine\archives-fiscales\AAAA\MM`.
- Ce dossier se **sauvegarde avec le PC serveur** et se **conserve 6 ans au moins** (`parametre_fiscal.conservation_annees`).
- Ne **jamais** purger le journal ni les sceaux : la base le refuse.

### A4. Synchronisation : fichiers et règles

- **Ne jamais** copier le dossier `Synchronisation` d'un serveur à l'autre : chaque serveur a sa clé (DPAPI : illisible ailleurs).

### A5. Réglages du poste (`poste.json`)

Fichier `C:\ProgramData\PharmaOfficine\poste.json` du poste. Clés connues : `AdresseApi` (prioritaire sur `PHARMA_API` quand il est rempli), `AdresseApiSecours`, `DelaiServeurInjoignableSecondes` (60 par défaut, entre 10 et 3 600). Les réglages se font dans l'application (Administration, Imprimantes et étiquettes) ; ne pas éditer à la main sans raison.

### A6. Développement (rappel)

- À chaque nouveau script `0xx`, régénérer `database/init/schema.sql` (`pg_dump --schema-only --no-owner --no-privileges` d'une base construite par les scripts) et compléter `seed_referentiel.sql` s'il ajoute des données de référence.
- Le paquet `scripts\paquet\installer.ps1` installe une **base d'essai avec données fictives** : jamais pour une officine réelle.

---

## Checklist avant ouverture

Cocher tout avant le jour J.

- [ ] Onduleur branché, PC serveur allumé en permanence, mise en veille désactivée
- [ ] PostgreSQL : service démarré automatiquement ; 5432 écouté seulement sur `127.0.0.1` (parcours A, étape 2.4)
- [ ] Base : `schema.sql`, `083`, `seed_referentiel.sql`, `111b` faits, `objets_encore_a_pharma_app = 0`
- [ ] Variables de l'API réglées (`ConnectionStrings__Pharmacie`, `Jwt__Cle` ≥ 64, `Chifa__DossierExport`)
- [ ] Certificat HTTPS créé ; `appsettings.Https.json` présent ; règle de pare-feu 5443 active
- [ ] PFX de l'autorité au coffre (hors ligne, seconde copie ailleurs), phrase de passe notée à part ; empreinte de l'autorité notée
- [ ] Service `PharmaOfficine API` en démarrage automatique et `Running`
- [ ] `http://localhost:5080/api/healthcheck` répond ; le réseau ne répond pas sur 5080
- [ ] Tous les postes : autorité installée (empreinte vérifiée), `PHARMA_API` en HTTPS, `PHARMA_POSTE` unique, écran de connexion sans avertissement
- [ ] Téléphones : autorité installée (empreinte vérifiée), scanner mobile testé
- [ ] `cle-integrite.dat` et `super-admin.dat` sauvegardés sur le disque externe, avec le mot de passe de la clé de scellement au coffre
- [ ] Compte titulaire : mot de passe personnel choisi ; comptes des employés créés ; droits vérifiés
- [ ] Administration : identité, RC, NIF, NIS, AI, n° Chifa, organismes, paramétrage (caisse, alertes, emplacements), imprimantes **sur chaque poste**
- [ ] Nomenclature nationale importée, compteurs contrôlés
- [ ] Notifications : message d'essai reçu
- [ ] Compte super admin créé ; au moins un canal vérifié (téléphone ou e-mail)
- [ ] Sauvegarde de l'application réglée, première sauvegarde faite, fichier lisible (ou script planifié, mais **un seul** des deux)
- [ ] Test de restauration dans une base d'essai fait, base d'essai supprimée
- [ ] Reprise de données (si besoin) : totaux contrôlés et rapport signé par le pharmacien
- [ ] Synchronisation : **désactivée** (sauf décision du titulaire)
- [ ] Vérifications avant ouverture (parcours A, étape 14) faites
- [ ] Essai réel sur un vrai poste, en HTTPS (connexion, vente, impression, scanner mobile)

## Checklist après chaque mise à jour

- [ ] Sauvegarde faite avant la mise à jour, et lisible (`pg_restore --list`)
- [ ] Serveur central mis à jour d'abord (si l'officine est synchronisée)
- [ ] Service arrêté puis redémarré, `http://localhost:5080/api/healthcheck` répond
- [ ] Scripts `database\migration` appliqués dans l'ordre, sans erreur ; pas de `schema.sql` ni de `clean.sql`
- [ ] Scripts 112 et suivants lancés avec le rôle propriétaire (`pharma_proprietaire`), jamais avec `pharma_app`
- [ ] API republiée et remplacée ; `appsettings.Https.json` toujours présent
- [ ] Postes republiés ; chaque poste ouvre l'écran de connexion
- [ ] Nomenclature réimportée si elle a changé
- [ ] Une vente test, une clôture, « Vérifier maintenant » de l'intégrité des journaux
- [ ] Si 2e serveur : `verifier-second-serveur.ps1` lancé sur le 2e serveur, versions identiques
- [ ] Si .NET a reçu un correctif de sécurité : republier l'API et les postes avec le SDK à jour
- [ ] Journal du serveur relu : aucune erreur au démarrage
