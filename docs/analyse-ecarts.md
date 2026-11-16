# Analyse des écarts : cible produit / dépôt actuel

> Réponse au § 0 de `docs/pharma-officine-differenciation.md`. Analyse en lecture seule, au 16/11/2026 : aucun code, script ni réglage n'a été modifié.
> Notation : **[Constaté]** = vu dans le dépôt (fichier cité) ; **[Supposé]** = déduction à confirmer ; **[À VÉRIFIER]** = point réglementaire du document de cadrage.
> Statuts : **Fait** / **Partiel** / **Absent**.

---

## 1. Écarts de cadrage (à trancher avant tout lot)

| Sujet | Document de cadrage | Dépôt réel [Constaté] | Conséquence |
|---|---|---|---|
| Front | Angular | Poste **WPF + Blazor** (`src/Pharma.Desktop`, composants `.razor`) ; page mobile HTML/JS servie par l'API (`src/Pharma.Api/wwwroot/mobile/`) | Aucun Angular. Les lots « front » se font en Blazor. L'agent `developpeur-frontend` (Angular) n'est pas adapté tel quel. |
| Base | PostgreSQL **et** SQL Server | **PostgreSQL seul** : paquet `Npgsql.EntityFrameworkCore.PostgreSQL` (`Directory.Packages.props`), aucun `SqlClient`/SQL Server dans `src`, `database`, `docs` | La « double compatibilité » ne s'applique pas. La réintroduire serait un chantier L (110 scripts en PL/pgSQL, déclencheurs, `\gset`/`\if` psql). Proposition : **retirer cette exigence** du cadrage. |
| Migrations | Migrations (EF) réversibles | **Scripts SQL numérotés idempotents** `database/migration/001…110_*.sql` + `database/init/schema.sql` (vidage psql) ; `PharmaDbContext` sans migrations (`docs/architecture.md`) | Chaque lot = un nouveau script `111_…`, `112_…` + mise à jour de `database/init`. Pas de « down » : la réversibilité se fait par script de retour explicite si besoin. |
| Déploiement | Cloud algérien (VPS API + PostgreSQL primaire/réplica), postes avec base locale | **Tout dans l'officine** : un PC serveur (PostgreSQL + API en service Windows), postes en HTTP local + JWT (`docs/architecture.md`) | Le « hors ligne d'abord » est déjà obtenu vis-à-vis d'Internet, mais il n'y a ni cloud, ni base locale par poste, ni synchronisation. Le schéma d'hébergement du § 4 bis ne concerne aujourd'hui que des fonctions futures (multi-officines, mobile hors pharmacie, réseau). |
| Identifiants | GUID, horodatage UTC | `bigint GENERATED ALWAYS AS IDENTITY` (87 séquences/identités dans `schema.sql`) ; `timestamp with time zone` partout (108 colonnes, aucune `without time zone`) ; un seul `uuid` (`vente.cle_encaissement`, idempotence d'encaissement) | UTC : conforme. GUID : absent, à introduire seulement si une synchronisation multi-sites est décidée. |
| Outillage | `/equipe` et agents | Agents existants orientés EF Core / Angular / SQL Server | Adapter les consignes des agents (Blazor, scripts SQL, PostgreSQL seul) avant d'utiliser `/equipe`. |

### Principes du § 4 au regard du dépôt

| Principe | État | Constat |
|---|---|---|
| Couche d'abstraction par système externe | Partiel | Ports existants : `IChifaConnecteur` (`src/Pharma.Application/Chifa/IChifaConnecteur.cs`, adaptateur fichiers `src/Pharma.Infrastructure/Chifa/ChifaFichiersConnecteur.cs`), `IConnecteurSource` (reprise, `src/Pharma.Application/Reprise/IConnecteurSource.cs`, adaptateurs ODBC/CSV). Absents : `ISerialisationGateway`, `IRepertoireOrdonnancesGateway`, `IPlateformeOfficinesGateway`, `IGrossisteConnector`. Pas de tests de contrat. Nommage : le dépôt dit « Connecteur », le document « Connector/Gateway » : garder la convention française du dépôt. |
| Journaux inaltérables | Partiel | Déclencheurs anti UPDATE/DELETE et anti TRUNCATE sur `registre_psychotrope`, `boite_evenement`, `journal_audit`, `journal_sauvegarde`, `credit_mouvement`, `fournisseur_mouvement`, `avoir_mouvement`, `correction_lot`, `regularisation_part_organisme` (`schema.sql`, scripts 001, 006, 014, 076, 079). **Pas de chaînage par hachage** (aucun `digest`/`sha256` en SQL) ; **la table `vente` n'est pas protégée** ; le compte applicatif garde les droits UPDATE/DELETE (protection par déclencheur seulement : le propriétaire peut les désactiver, limite déjà écrite dans `docs/regles-metier.md`, « Journal et audit »). |
| Double compatibilité PG / SQL Server | Sans objet | Voir ci-dessus. |
| Hors ligne | Partiel | Aucun service cloud (`docs/architecture.md` § Hors ligne) ; mais si le PC serveur tombe, les postes s'arrêtent (pas de base locale au poste). |
| Paramétrage réglementaire en données | Partiel | En données : classes et listes via nomenclature nationale (`034`, Administration › Nomenclature), seuils d'alerte (`AdminSeuils`), `parametre_tracabilite`, `parametre_rapport` (TVA reversée, `061`), motifs réglables (`094`). En code : règles de délivrance des psychotropes, format GS1 (`src/Pharma.Domain/Commun/Gs1DataMatrix.cs`), numérotation `T-000001`. |
| Données de santé | Partiel | Masquage à l'écran (registre : initiales hors titulaire ; reprise : exemples masqués ; parcours de boîte : nom selon droit). Aucune notification ne porte de donnée patient (`docs/regles-metier.md` § Notifications › Confidentialité). **Absents** : chiffrement au repos des colonnes sensibles (pas de `pgcrypto`), journal des consultations de fiches patient, export/rectification/anonymisation « droits des personnes ». |
| Arabe / RTL | Absent (préparé) | Sélecteur refusé tant que les traductions manquent (`src/Pharma.Application/Administration/Pharmacie.cs` l. 25-31) ; règle CSS `[dir="rtl"]` seule (`src/Pharma.Desktop/wwwroot/css/app.css` l. 43) ; maquettes `docs/maquettes/AdminLangue.html`. |

---

## 2. Écarts par module

### M1 — Sérialisation DataMatrix

| Fonction / critère | Statut | Constat |
|---|---|---|
| Parsing GS1 (01, 21, 10, 17) | **Fait** | `src/Pharma.Domain/Commun/Gs1DataMatrix.cs` ; règles « Data Matrix GS1 » de `docs/regles-metier.md`. Tout autre identifiant d'application rend le code illisible (rigide si l'IANOR ajoute des AI). |
| Lecture à la réception | **Fait** | Mobile (`MobileEndpoints.cs` `/reception/scans`), poste (`OngletReceptions.razor`, `EtiquetagerLots.razor`) ; `docs/scanner-mobile.md`. |
| Lecture à la dispensation | **Fait** | Caisse : boîte identifiée scannée servie (`Caisse.razor`, fonction SQL `vendre_boite`). |
| Stock à la boîte | **Fait** (étendu) | Table `boite`, étiquette interne EAN-13 « 20… » pour les boîtes sans Data Matrix (`creer_boites_internes`), mode strict `parametre_tracabilite.boite_obligatoire` ; `docs/tracabilite.md`. |
| Une boîte ne se vend pas deux fois | **Fait** | `vendre_boite` refuse (« contrefaçon ou double scan possible »). |
| Vérification / décommissionnement national (`ISerialisationGateway`) | **Absent** | Aucun port, aucune file. |
| Vente hors ligne + vérifications rejouées | **Absent** | Dépend du port ci-dessus (file d'attente à créer). |
| Alertes boîte vendue / rappelée / expirée | **Fait** ; « inconnue » nationale : Absent | Contrôle mobile et caisse ; « inconnue de la base nationale » demande le port. |
| Rappel de lot en un clic (stock + vendues) | **Fait** | `rappel_lot`, `ventes_lot`, `src/Pharma.Application/Tracabilite/RappelerLot.cs`, `RappelLotSql.cs`, `Tracabilite.razor`, scripts 051, 075. |
| Retours fournisseur traçables | **Fait** | `OngletRetours.razor`, événement `retour_grossiste` dans `boite_evenement`. |
| Normes paramétrables (IANOR/GS1) [À VÉRIFIER] | **Absent** | Format en code. |

**Bilan M1 : Partiel avancé** — il manque le port national, la file hors ligne et le paramétrage des AI.

### M2 — Psychotropes et stupéfiants

| Fonction / critère | Statut | Constat |
|---|---|---|
| Registre en ajout seul | **Fait** | `registre_psychotrope`, `trg_registre_immuable`, `trg_registre_non_vidable` (001, 024, 076) ; numérotation continue `numeroter_registre`. |
| Chaînage par hachage, détection de rupture | **Absent** | Pas de colonnes `hash`/`hash_precedent`, pas de vérification planifiée. |
| Tentative de modification tracée | **Partiel** | Refusée par déclencheur ; le refus est journalisé côté serveur (`Pharma.Refus`) mais pas dans une table d'audit. |
| Corrections par écriture d'annulation | **Partiel** | Mouvements inverses existants (retour client, écart ±) ; pas de type « annulation d'écriture » référant la ligne corrigée. |
| Saisie patient, prescripteur, ordonnance, porteur | **Fait** | `Encaisser.cs`, `Vente.cs`, contraintes `ck_registre_porteur_complet`, `registre_psychotrope_check`. **Quantités et durée de traitement** : non constaté (durée absente du registre). |
| Contrôles automatiques (patient servi plusieurs fois, ordonnance réutilisée, quantités anormales, prescripteur inhabituel) | **Absent** | Le contrôle interne (`067`, `SurveillanceSql.cs`) vise caisse et stock, pas ces cas. |
| Alertes paramétrables, forçage avec utilisateur et motif | **Partiel** | Mécanisme d'autorisation tracée existant (`FenetreAutorisation.razor`, `journal_audit` « autorisé par ») réutilisable ; alertes M2 à créer. |
| Inventaire avec écarts justifiés | **Fait** | Écarts d'inventaire inscrits au registre (`inscrire_mouvement_registre`), validation des écarts. |
| Exports inspection PDF + Excel par période/produit/patient/prescripteur | **Partiel** | PDF et CSV par période et balance mensuelle (`RegistrePsychotropes.cs`, `RegistreSql.cs`, `DocumentsPdf.cs`) ; pas d'Excel natif ni de filtre patient/prescripteur à l'export. |
| `IRepertoireOrdonnancesGateway` | **Absent** | |
| Suivi des antibiotiques | **Absent** | « antibiot » n'apparaît que comme exemple de rayon (`016_emplacements.sql`). |
| Liste des molécules/classes paramétrable [À VÉRIFIER] | **Partiel** | Classe issue de la nomenclature nationale + confirmation titulaire (`080_classe_confirmee_titulaire.sql`). |

**Bilan M2 : Partiel** (base solide, manque hachage, alertes, répertoire, antibiotiques).

### M3 — Ruptures et réseau

| Fonction / critère | Statut | Constat |
|---|---|---|
| Commandes fournisseurs | **Fait** (mono-grossiste) | `commande_fournisseur`, `src/Pharma.Application/Achats/Achats.cs`, `AchatsSql.cs`, `OngletCommandes.razor` ; un brouillon par grossiste. |
| Écran unique multi-grossistes, comparaison prix/délais | **Absent** | Seul le dernier prix par grossiste est connu (`AchatsDuProduit`). |
| Catalogue/disponibilité par grossiste (fichier, e-mail, API) | **Absent** | Pas de table catalogue grossiste ; pas d'`IGrossisteConnector`. |
| Répartir une commande entre grossistes | **Partiel** | Possible à la main (un bon par grossiste) ; pas de répartition assistée. |
| Envoi du bon de commande | **Partiel** | PDF produit à l'émission ; pas d'envoi automatique. |
| Suggestions « à commander » | **Fait** | Ruptures + stock critique, dernier prix et grossiste (règle « suggestions de commande »). |
| Historique des ruptures chez les grossistes, retour en stock | **Absent** | Les ruptures connues sont celles de l'officine seulement. |
| File des patients en attente + rappel SMS/WhatsApp [À VÉRIFIER] | **Absent** | Les SMS existants ne vont qu'au titulaire et à ses destinataires vérifiés (modem GSM). |
| Substitution même DCI/dosage/forme | **Fait, mais écart** | `ListerAlternatives.cs`, `RechercherAuComptoir.cs`, `EquivalentsComptoirSql.cs`. **Le dépôt propose aussi un autre dosage/forme « à valider »**, contraire au critère « uniquement ». À trancher (paramètre). |
| Mise en avant des génériques locaux | **Absent** | Pas de champ « fabriqué en Algérie » constaté sur `produit` [Supposé : déductible de la nomenclature, à vérifier]. |
| Liste blanche du pharmacien | **Absent** | |
| Réseau inter-officines opt-in, application patient | **Absent** | Nécessite un serveur central (§ 4 bis). |

**Bilan M3 : Partiel** (achats et substitution présents ; multi-grossistes, ruptures externes, réseau absents).

### M4 — Frein au changement

| Fonction | Statut | Constat |
|---|---|---|
| Assistant de reprise (produits, stock, lots, fournisseurs, clients, crédits) | **Fait** (générique) | Schéma `import` (`048`, `052`, `092`), `src/Pharma.Application/Reprise/Reprise.cs`, `RepriseSql.cs`, sources ODBC (Access, DBF, Firebird…) et CSV (`SourceOdbc.cs`, `LecteurCsv.cs`), correspondance proposée puis confirmée, anomalies bloquantes, tout ou rien, contrôle des totaux, doublons d'import (`AdminDoublonsImport.razor`, `093`). Règles : `docs/regles-metier.md` § Reprise. |
| Profils par concurrent (Chifa Plus, XpertPharm, PharmaPro, Profficine) | **Absent** | Aucun profil nommé ; détection générique (`DetectionReprise.cs`). Formats réels [À VÉRIFIER]. |
| Historique Chifa | **Absent** | Non repris. |
| Excel (.xlsx) direct | **Absent** | CSV et ODBC seulement [Supposé : export CSV demandé à l'utilisateur]. |
| Rapport d'import avec anomalies | **Fait** | Compte rendu avant chargement, contrôle après chargement. |
| Hors ligne d'abord | **Partiel** | Pas d'Internet requis ; pas de résilience à la panne du PC serveur, pas de synchronisation cloud ni gestion de conflits. |
| Multi-postes | **Fait** | Postes, sessions de caisse, relève (`session_caisse`, `Sessions.cs`). |
| Multi-officines | **Absent** | Aucun `officine_id` ; `pharmacie` est une ligne unique. |
| Pilotage mobile du titulaire | **Partiel** | `GET /api/pilotage` (`PilotageEndpoints.cs`, `src/Pharma.Application/Pilotage/Pilotage.cs`, `PilotageSql.cs`) : CA, caisses, alertes, pertes ; accessible hors pharmacie via VPN seulement (`docs/acces-distant.md`). Activité par employé : partielle (écarts par employé). |

**Bilan M4 : Partiel** (reprise générique solide ; profils concurrents, xlsx, Chifa, résilience et multi-officines absents).

### M5 — Pilotage financier

| Fonction | Statut | Constat |
|---|---|---|
| Marge | **Partiel** | Marge brute et marge après pertes (`Rapports.cs`, `RapportsSql.cs`, `061`) ; par catégorie seulement, **pas par laboratoire ni produit**, pas d'évolution dans le temps. |
| Pertes sur périmés (valeur) | **Fait** | `PertesEtDons` (Pilotage), sorties de stock (`SortiesStockSql.cs`). |
| Proches péremption + suggestion de retour/échange | **Partiel** | Alertes de péremption (`002`, `066`) ; pas de suggestion de retour. |
| Stock dormant | **Partiel** | Alerte « dormant » (`002_alertes_stock.sql`, `Alertes.cs`, seuil dans `AdminSeuils`) ; valeur immobilisée non constatée en rapport. |
| Rejets Chifa (liste, motif, montant, suite, taux par motif) | **Partiel** | Bordereaux, rejets, suites redéposer/perte/patient (`110_bordereaux_chifa.sql`, `Bordereaux.cs`, `BordereauxChifa.razor`) ; **taux de rejet par motif absent**. |
| Suggestions de commande (saisonnalité) | **Partiel** | Ruptures/stock critique uniquement, sans historique ni saisonnalité. |

**Bilan M5 : Partiel.**

### M6 — Conformité fiscale

| Fonction | Statut | Constat |
|---|---|---|
| Journal des ventes inaltérable (hachage) | **Absent** | `vente` sans déclencheur d'immuabilité ; annulation tracée (`annulation_vente`) mais une vente peut être modifiée en SQL. |
| Numérotation continue des tickets | **Partiel** | `prochain_numero_vente()` = `T-` + `nextval('seq_numero_vente')` : unique, **pas garanti sans trou** (une transaction annulée consomme un numéro). |
| Clôtures journalières signées / mensuelles | **Partiel** | Clôture de caisse par session (`Cloture.razor`, `064`, `085`) ; ni clôture journalière d'officine, ni mensuelle, ni signature. |
| Archivage 6 ans, export d'audit DGI [À VÉRIFIER] | **Absent** | Sauvegardes locales (14 par défaut) ; pas d'archive scellée. |
| TVA par produit et récapitulatifs | **Partiel** | Taux par produit/prestation, HT ligne à ligne dans les rapports, TVA à reverser paramétrable (`061`) ; pas de récapitulatif fiscal dédié. |

**Bilan M6 : Partiel faible.**

### Synthèse

| Module | Statut global |
|---|---|
| M1 DataMatrix | Partiel avancé (lecture, stock à la boîte, rappel faits ; port national absent) |
| M2 Psychotropes | Partiel (registre immuable fait ; hachage, alertes, répertoire, antibiotiques absents) |
| M3 Ruptures/réseau | Partiel (mono-grossiste, substitution) ; réseau absent |
| M4 Changement | Partiel (reprise générique, multi-postes) ; multi-officines, sync absents |
| M5 Pilotage financier | Partiel |
| M6 Fiscal | Partiel faible (aucun chaînage, numérotation avec trous possibles) |

---

## 3. Contrainte § 4 bis : dépendances externes

| Dépendance | Constat | Ce qui part | Conformité |
|---|---|---|---|
| E-mail (MailKit) | SMTP **de la pharmacie**, réglé par le titulaire (`src/Pharma.Infrastructure/Notifications/Envois.cs`, `013`) | Rapports et alertes **sans donnée patient** (règle « Confidentialité ») ; contient noms d'employés, adresses e-mail des destinataires, montants | Conforme côté contenu. **Risque** : si le fournisseur SMTP/boîte est étranger (Gmail, Outlook…), noms d'employés et chiffres partent à l'étranger [Supposé : dépend du réglage, non vérifié]. À exiger : fournisseur algérien, à documenter. |
| SMS | Modem GSM local (`System.IO.Ports`), opérateur algérien | Messages titulaire sans donnée patient | Conforme. |
| Code super admin | Envoyé par SMS/e-mail (`SuperAdminEndpoints.cs`) | Code de validation | Même réserve SMTP. |
| Télémétrie / suivi d'erreurs | Aucun Application Insights, Sentry, Analytics constaté ; journal fichier local `src/Pharma.Api/Journalisation/JournalFichier.cs` | — | Conforme. |
| CDN / scripts distants | Aucune URL externe dans `src` (hors `0.0.0.0` de `appsettings.json`) ; polices locales (`wwwroot/fonts`), ZXing servi localement | — | Conforme. |
| WebView2 (Blazor dans WPF) | Moteur Edge installé sur le poste | [Supposé] télémétrie de diagnostic Microsoft du runtime, sans contenu applicatif | À vérifier et, si besoin, désactiver par stratégie Windows. |
| Paquets NuGet | Feed `nuget.org` (`NuGet.Config`) | Code seulement, à la compilation | Pas de donnée personnelle. |
| IA | Aucun service d'IA externe ; l'essai TrOCR était local et non intégré (`docs/fonctionnalites.md` l. 80) | — | Conforme. |
| Sauvegardes | Disque externe + GED du serveur, local (`Sauvegardes.cs`) | — | Conforme ; ni chiffrement des sauvegardes ni copie hors site (pour la cible cloud : second hébergeur algérien). |
| Accès distant | VPN à choisir (`docs/acces-distant.md`) | Tout le trafic du titulaire | **À contrôler** : service VPN étranger avec relais = transfert possible. Privilégier un VPN auto-hébergé. |
| Chifa_Officine | Lecture de fichiers exportés (`ChifaFichiersConnecteur.cs`) | Rien n'est envoyé par notre logiciel | Conforme. |

**Aucune dépendance non conforme certaine.** Deux points à confirmer : fournisseur SMTP réel et solution VPN.

### Données personnelles dans les journaux applicatifs [Constaté, `C:\ProgramData\PharmaOfficine\Journaux`, 5 fichiers, 11 au 15/11/2026]
- Aucun e-mail. Une occurrence d'un motif ressemblant à un n° de mobile (fichier du 12/11, non qualifiée).
- **Des messages de refus métier sont journalisés tels qu'affichés** (catégorie `Pharma.Refus`, `src/Pharma.Api/Securite/ErreursMetier.cs`) : l'un contient le **nom d'un patient** (message « N° d'assuré déjà utilisé par … », données de recette). Toute règle métier dont le message cite un nom finit dans le journal.
- `SuperAdminEndpoints.cs` l. 40 journalise le **destinataire** (téléphone ou e-mail) du code super admin.
- Mise en conformité : masquage des noms, n° d'assuré, n° de pièce et coordonnées avant écriture (filtre dans `JournalFichier`/`ErreursMetier`), test automatique associé ; règle à ajouter à l'agent `securite`.

---

## 4. Impacts sur le modèle de données

| Lot | Tables / scripts à prévoir (numéros indicatifs) |
|---|---|
| Hachage (M6, M2) | Colonnes `hash`, `hash_precedent` sur `registre_psychotrope` et un nouveau **journal des ventes** (`journal_vente` en ajout seul) ; fonction de calcul (`pgcrypto` `digest`, extension à installer) ; table `verification_integrite` ; amorçage du chaînage sur l'existant (ligne « genèse »). |
| Numérotation (M6) | Compteur sans trou (table compteur verrouillée) au lieu de la séquence ; table `cloture_periode` (jour, mois, totaux, hash). |
| Ventes inaltérables (M6) | Déclencheur interdisant UPDATE des colonnes financières de `vente`/`ligne_vente` après encaissement : **à auditer**, car de nombreux scripts (Chifa : `chifa_rattache_le`, `bordereau_id`, exclusions…) mettent `vente` à jour après coup → séparer données fiscales figées et données de suivi. |
| Multi-grossistes (M3) | `catalogue_grossiste` (grossiste, produit, prix, dispo, date), `import_catalogue`, `rupture_grossiste` (historique), `demande_patient` (attente), `substitution_liste_blanche`, `produit.fabrication_locale`. |
| Psychotropes (M2) | `parametre_alerte_psychotrope`, `alerte_psychotrope` (forçage : utilisateur, motif), durée de traitement sur ordonnance, `classe_suivie` (antibiotiques). |
| Sérialisation (M1) | `verification_serialisation` (file : boîte, opération, statut, essais), paramétrage des AI GS1. |
| Données de santé | Colonnes chiffrées (patient : identité, n° d'assuré, pièce ; registre : porteur) → impact fort sur les recherches et index (`ux_patient_num_assure`), sur la reprise et sur les vues ; `journal_acces_patient`. |
| Multi-officines | `officine_id` sur la quasi-totalité des tables, ou une base par officine + agrégation centrale [à décider]. |

---

## 5. Risques techniques et dépendances

1. **Chiffrement des colonnes** : casse la recherche par nom/n° d'assuré et l'unicité ; nécessite un index aveugle (hash à clé) — risque élevé, à concevoir avant M2 étendu.
2. **Immuabilité de `vente`** : contradiction avec les mises à jour Chifa existantes ; il faut un journal de ventes séparé plutôt que figer `vente`.
3. **Hachage et concurrence** : le chaînage impose une écriture sérialisée (verrou) ; à mesurer sur plusieurs caisses.
4. **Restauration** (`RestaurationPostgres.cs`) et reprise : doivent conserver et revérifier la chaîne.
5. **Droits PostgreSQL** : retirer UPDATE/DELETE au compte `pharma_app` sur les journaux impose que les scripts soient exécutés par un autre rôle (propriétaire).
6. **Multi-officines / sync** : changement d'architecture (bigint → GUID, conflits) ; à ne lancer qu'après décision d'hébergement § 4 bis.
7. Dépendances : M6 et M2 partagent la brique hachage (à faire une fois) ; M1 hors ligne réutilise la file d'attente générique ; M3 réseau et application patient dépendent du serveur central ; M5 suggestions dépend de l'historique des ruptures M3.

---

## 6. Plan de livraison proposé (à valider, rien n'est lancé)

| Ordre | Lot | Contenu | Effort | Scripts | Dépend de | Paramétrable [À VÉRIFIER] |
|---|---|---|---|---|---|---|
| 0 | Cadrage | Valider les écarts du § 1 (Blazor, PostgreSQL seul, scripts SQL, hébergement local) ; adapter les agents | S | — | — | — |
| 1 | Journaux sans données personnelles | Masquage dans `Pharma.Refus` et super admin, tests | S | — | — | — |
| 2 | P1 · M6a Brique d'intégrité | Fonction de hachage, chaînage générique, vérification planifiée + alerte titulaire, rôle propriétaire séparé | M | 111 | 0 | Algorithme, fréquence |
| 3 | P1 · M6b Journal des ventes | `journal_vente` ajout seul chaîné, numérotation sans trou, clôtures journalières/mensuelles scellées, récap TVA | L | 112-113 | 2 | Format d'export DGI, durée 6 ans, TVA médicaments/parapharmacie |
| 4 | P1 · M4a Reprise concurrents | Profils Chifa Plus / XpertPharm / PharmaPro / Profficine, import xlsx, historique Chifa | M par profil | 114 | exports réels | Format de chaque logiciel |
| 5 | P1 · M4b Résilience | Mode dégradé si PC serveur indisponible (à définir : caisse locale minimale ou serveur de secours) | L | — | décision | — |
| 6 | P1 · M3a Multi-grossistes | `IGrossisteConnecteur` (fichier, e-mail), catalogue et dispo, comparaison, répartition de commande | L | 115 | — | Formats grossistes |
| 7 | P1 · M3b Ruptures et substitution | Historique ruptures, file patients en attente, liste blanche, génériques locaux, paramètre « équivalent exact seulement » | M | 116 | 6 | Consentement SMS/WhatsApp |
| 8 | P2 · M2 Registre renforcé | Chaînage du registre, écritures d'annulation, alertes paramétrables avec forçage tracé, exports Excel et filtres, antibiotiques, `IRepertoireOrdonnancesConnecteur` factice | L | 117-118 | 2 | Nomenclature des molécules, seuils |
| 9 | P2 · Données de santé | Journal des accès patient, chiffrement des colonnes sensibles, export/anonymisation | L | 119 | 8 | Loi 18-07, durée de conservation |
| 10 | P2 · M1 Sérialisation | `ISerialisationConnecteur` factice, file de vérification hors ligne, décommissionnement, AI GS1 paramétrables | M | 120 | file générique | API MIPH, normes IANOR |
| 11 | P3 · M5 Pilotage | Marge par produit/labo/temps, valeur dormante, suggestions de retour, taux de rejet Chifa par motif, suggestions saisonnières | M | 121 | 7 | — |
| 12 | P3 · Réseau | Serveur central en Algérie, multi-officines, partage opt-in agrégé, puis application patient | L+ | 122+ | hébergement ARPCE | Règles CNOP |

---

## 7. Points [À VÉRIFIER] et traitement actuel

| Point | Dans le code aujourd'hui |
|---|---|
| Normes IANOR / GS1 (M1) | GS1 figé en code (`Gs1DataMatrix.cs`). |
| API base nationale de sérialisation | Rien. |
| Nomenclature des molécules et classes (M2) | Nomenclature nationale importée + confirmation du titulaire (paramétrable). |
| Répertoire national des ordonnances | Rien. |
| Consentement SMS/WhatsApp patients (M3) | Aucun message aux patients. |
| CNOP : application patient, partage entre officines | Rien. |
| Formats des logiciels concurrents (M4) | Reprise générique ODBC/CSV avec correspondance confirmée par une personne. |
| Article 51 bis, TVA médicaments/parapharmacie (M6) | Taux par produit ; TVA reversée sur pertes paramétrable (`061`, « règle fiscale à confirmer », `docs/a-faire.md`). |
| Format d'export DGI | Rien. |
| Loi 18-07, déclaration ANPDP, DPO | Patient anonyme par défaut hors ordonnance (`parametre_tracabilite.patient_obligatoire`) ; pas de registre des traitements. |
| Durée de conservation / anonymisation du registre | Registre jamais supprimable ; pas d'anonymisation. |
| Liste des hébergeurs ARPCE | Sans objet aujourd'hui (tout est local). |
| Statut du projet de loi 18-07 (DPO) | Rien. |
