# Promotion du 2e serveur : fonctionnement, prérequis, diagnostic

> Document de référence (2026-11-26). Installation pas à pas : [procedure-deux-serveurs.md](procedure-deux-serveurs.md). Procédures de panne
> et de retour (F3, F4) : [mise-en-production.md](mise-en-production.md), parcours C. Essais réels : [second-serveur-local.md](second-serveur-local.md)
> § 15 à 18. Code : `src/Pharma.Application/SecondServeur/` (`DecisionPromotion.cs`, `PromotionAutomatique.cs`, `PresencePostes.cs`,
> `ReglagesBascule.cs`).

## 1. En une phrase

Quand le PC serveur (serveur 1) tombe, le **2e serveur**, qui tient une copie de la base en lecture seule, **se promeut seul** en serveur
principal si **la majorité des postes** confirme qu'elle ne joint plus le serveur 1 ; de son côté, le serveur 1, s'il est seulement coupé du
réseau, **se met lui-même en lecture seule**. Il n'y a ainsi **jamais deux serveurs qui acceptent des ventes** en même temps.

## 2. Vocabulaire

| Mot | Sens |
|---|---|
| **Serveur 1** (principal) | Le PC serveur : sa base PostgreSQL est la seule en écriture. |
| **2e serveur** (veille) | Tient une **copie** de la base (réplication PostgreSQL continue, lecture seule) ; son API tourne « en veille » et ne sert pas les postes. |
| **Promotion** | La copie du 2e serveur devient une base en écriture (`pg_promote`), son API devient principale. **Irréversible** : l'ancien serveur 1 ne peut plus redevenir principal sans la procédure F4. |
| **Postes connus** | Postes vus récemment (3 minutes par défaut) d'après la copie de la base : la base du vote. |
| **Vote** | Rapport d'un poste au 2e serveur (route `/api/vigie/rapport`) disant « je ne joins plus le serveur 1 ». Seuls les rapports **authentifiés** (employé connecté, poste prouvé) comptent pour promouvoir ou pour bloquer. |
| **Isolement** | Le serveur 1 se met lui-même en lecture seule quand il a perdu sa copie ET voit moins de la moitié des postes. |
| **Vigie** | La tâche du 2e serveur qui sonde le serveur 1 toutes les 5 s et rend un **verdict**. |

## 3. Prérequis (sans eux, la promotion reste manuelle)

| # | Prérequis | Où | Contrôle |
|---|---|---|---|
| 1 | Réplication en place (copie en lecture seule à jour) | 2e serveur | Écran Administration › 2e serveur ; `verifier-second-serveur.ps1` |
| 2 | **Script `163b`** : le compte de l'API lit l'identifiant de la base (même « lignée ») | Base du **serveur 1**, compte `postgres` | Pas de `lignee_illisible` au journal du 2e serveur ([procedure-deux-serveurs.md](procedure-deux-serveurs.md) § 2.1) |
| 3 | **Script `166b`** : le compte de l'API (`pharma_app`) a le droit d'exécuter `pg_promote` | Base du **serveur 1**, compte `postgres` (recopié sur la copie par la réplication) | `t` à la fin du script (voir ci-dessous) |
| 4 | Promotion automatique active | Réglage de l'API du 2e serveur `SecondServeur__Bascule__PromotionAutomatique` (vrai par défaut) | Journal de l'API au démarrage |
| 5 | Chaque serveur connaît l'autre | `SecondServeur__AutreServeur` (et l'adresse HTTPS de l'autre API) sur les deux serveurs | Écran « 2e serveur » : autre serveur joint |
| 6 | Postes **autorisés et prouvés** (Administration › Postes autorisés), employés connectés | Postes | Barre d'état « Connecté » |

**Le script 166b se passe sur la base de la pharmacie, jamais sur la base système `postgres`** (le droit s'enregistre base par base) :

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -p 5432 -U postgres -d <base de la pharmacie> -v ON_ERROR_STOP=1 `
    -f C:\MesAtlassian\pharmaofficine\database\init\166b_droit_promotion_base.sql
```

Sur le PC serveur d'une officine, le fichier est dans `C:\Program Files\PharmaOfficine\Api\database\init\166b_droit_promotion_base.sql`
(le chemin ci-dessus est celui du dépôt de développement).

**Attendu** : la dernière ligne `pharma_app_peut_promouvoir` vaut `t`. À refaire après une restauration sur une base neuve. Contrôle seul :

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -U postgres -d <base de la pharmacie> -Atc "SELECT has_function_privilege('pharma_app', 'pg_promote(boolean, integer)', 'EXECUTE')"
```

État constaté le 2026-11-26 : `pharmacie_dev` **t**, `pharmacie_test` **f** (166b non passé), base `postgres` f (sans objet).

## 4. Comment le 2e serveur décide

Toutes les **5 s**, la vigie sonde le serveur 1 (sa **base** et son **API**), compte les **votes** des postes et rend un verdict. Il est
écrit dans le journal de l'API du 2e serveur sous la forme `Vigie : verdict <Verdict> (<raison>), connus <n>, votes <m>`.

| Situation | Verdict (raison du journal) | Effet |
|---|---|---|
| Serveur 1 joint, aucun poste ne le perd | `Rester` (`serveur1_joint_aucun_vote_authentifie`) | Rien |
| Silence du serveur 1 depuis moins de 30 s | `Attendre` (`injoignable_depuis_peu`) | Rien |
| Un poste authentifié joint encore le serveur 1 | `Attendre` (`poste_authentifie_joint_serveur1`) | **Jamais de promotion** |
| Votes = exactement la moitié des postes connus | `Egalite` (`votes_egaux_moitie`) | Pas de promotion, alerte « égalité » |
| Moins de la moitié des postes votent | `Attendre` (`majorite_non_atteinte`) | Rien |
| Majorité, mais délai total pas encore écoulé | `Attendre` (`delai_total_non_atteint`) | Rien |
| Moins de 2 minutes depuis le démarrage de l'API de veille | `Attendre` (`demarrage_veille`) | Rien |
| **Base ET API du serveur 1 muettes, majorité de votes, délai écoulé** | **`Promotion` (`cas_1_injoignable_majorite`)** | Promotion (cas 1) |
| 2e serveur joint le serveur 1, mais aucun poste ne le joint, majorité de votes | `RetraitPuisPromotion` (`cas_2_serveur1_joint_aucun_poste`) | Mise hors jeu signée du serveur 1, puis promotion (cas 2) |
| Quelques postes seulement perdent le serveur 1 | `Relais` (`cas_3_quelques_postes`) | Le 2e serveur **relaie** ces postes vers le serveur 1, sans promotion (cas 3) |
| Base du serveur 1 vivante mais son API muette | `ApiServeur1Muette` (`base_vivante_api_muette`) | **Jamais de promotion** ; alerte « API du serveur 1 arrêtée » : relancer son service |

La promotion n'est lancée qu'après **2 verdicts consécutifs** identiques.

### Délais (réglages `SecondServeur:Bascule`, valeurs par défaut)

| Réglage | Défaut | Bornes | Rôle |
|---|---|---|---|
| `InjoignableServeur1Secondes` | 30 s | 15 à 300 | Silence minimal du serveur 1 avant de compter |
| `PromotionMinimumSecondes` | 60 s, **relevé à 76 s** | 60 à 600 | Délai total minimal avant promotion ; relevé automatiquement pour que le serveur 1 se soit écarté avant (vu + isolement + sonde + marge) |
| `PosteVuSecondes` | 45 s | 30 à 120 | Un rapport de poste plus vieux ne compte plus |
| `IsolementSecondes` | 20 s | 10 à 120 | Décompte de l'isolement du serveur 1 |
| `SondeSecondes` | 5 s | 2 à 30 | Fréquence de la vigie |
| `PosteActifMinutes` | 3 min | 1 à 10 | Fenêtre des « postes connus » |
| `DemarrageVeilleSansDecisionSecondes` | 120 s | 60 à 600 | Aucune décision juste après le démarrage de la veille |
| `PromotionAutomatique` | vrai | | Faux : jamais de promotion automatique (alerte, bascule manuelle) |

Durée observée d'une panne réelle à la promotion : **1 min 30 à 2 min** (103 s mesurées le 2026-11-26).

## 5. Garde-fous avant l'étape irréversible

Avant `pg_promote`, le 2e serveur vérifie, **dans cet ordre** ; le premier qui échoue arrête tout, écrit `promotion_refusee` au journal et
alerte le titulaire (« promotion manuelle nécessaire ») :

1. promotion automatique active ;
2. ni démonstration ni restauration en cours ;
3. sa copie est lisible et **encore en lecture seule** (sinon : déjà promue, rien à faire) ;
4. **même lignée** de base que le serveur 1 (droit du script 163b ; sinon « lignée illisible ») ;
5. cas 2 seulement : le serveur 1 a confirmé sa **mise hors jeu** par une réponse signée, puis, en 60 s au plus, la ligne de retrait est
   visible dans la copie ET la copie a rejoué toutes les écritures du serveur 1 (LSN final) : les **3 preuves** ;
6. écriture du **fichier de décision** `Journaux\bascule-<horodatage>.json` (postes, votes, sondes : aucune donnée de patient) ;
7. `pg_promote` : **une seule tentative** par démarrage de l'API. Droit absent : « Le droit de promouvoir la base manque (script 166b) ».

Après la promotion : l'API redémarre en **principal**, la décision est enregistrée en base (table `bascule_decision`), au journal d'audit, et
le titulaire est alerté. Les numéros de tickets et de factures **sautent** volontairement (aucun doublon possible avec l'ancien principal).

## 6. Côté serveur 1 : la mise à l'écart

Le serveur 1 évalue toutes les 5 s : s'il a perdu sa copie (réplication coupée) **et** voit **strictement moins de la moitié** des postes
connus pendant `IsolementSecondes`, il passe **en lecture seule** : « Ce serveur est isolé (copie perdue et moins de la moitié des postes
vus) : il passe en lecture seule, titulaire prévenu » (`/api/healthcheck` : rôle `ecarte`). À égalité, il garde la main. La mise à l'écart
se lève seule si la copie revient ou si assez de postes reviennent, **tant que le 2e serveur ne s'est pas promu**. Règle non appliquée
pendant 2 minutes après le démarrage de son API.

## 7. Côté postes

- Chaque poste rapporte au 2e serveur s'il joint le serveur 1 ; il reçoit une **consigne** : rester, attendre, relais, basculer.
- Pendant l'attente : « Le serveur principal ne répond plus. Vérification avec le 2e serveur en cours », barre « Serveur injoignable ».
- Après la promotion : « Le 2e serveur est devenu le serveur principal », barre **« Connecté · 2e serveur »**, sans redémarrage ni
  reconnexion. Une vente interrompue par la panne est renvoyée seule, jamais encaissée deux fois.

## 8. Quand la promotion reste manuelle

| Cause (journal ou alerte) | Que faire |
|---|---|
| `droit_pg_promote_absent` (« script 166b ») | Bascule manuelle F3 tout de suite ; passer 166b sur la base du principal ensuite |
| `lignee_illisible` (« script 163b ») | Bascule manuelle F3 ; passer 163b ensuite |
| `promotion_automatique_desactivee` | Décision du titulaire, bascule manuelle F3 |
| `Egalite` (moitié exacte des postes) | Vérifier physiquement le serveur 1 ; décision du titulaire, F3 |
| `ApiServeur1Muette` | **Ne pas basculer** : relancer le service de l'API du serveur 1 |
| `retrait_sans_reponse_fiable`, preuve manquante (cas 2) | Vérifier le serveur 1 ; décision du titulaire, F3 |
| `pg_promote_echec` | Vérifier l'état de la base du 2e serveur ; F3 |

**Bascule manuelle (F3)** : sur le 2e serveur, `scripts\second-serveur\basculer-second-serveur.ps1 -AncienPrincipal <IP> -Schema https
-PortApi 5443`, d'abord avec `-Essai`, puis pour de bon (taper `BASCULER`) ; sur un poste resté sur le bandeau : bouton « Basculer sur le
2e serveur ». Détail : [procedure-deux-serveurs.md](procedure-deux-serveurs.md) § 8.

## 9. Après une promotion

1. **Ne jamais redémarrer l'API de l'ancien serveur 1** tant que la procédure F4 n'est pas faite : l'isoler (arrêter ses services ou
   débrancher son câble).
2. Faire les étapes de F3 restantes : dernière copie de la GED si l'ancien est joignable, arrêt de la copie de la GED sur le nouveau principal,
   écran « 2e serveur », synchronisation avec le central (nouvelle époque si demandée).
3. Retour du PC réparé : **F4** ([procedure-deux-serveurs.md](procedure-deux-serveurs.md) § 9) ; il redevient **2e serveur** (copie). Les
   réglages de réplication synchrone sont à reposer sur le nouveau principal (ils ne suivent pas la réplication).

## 10. Diagnostic rapide

| Question | Où regarder |
|---|---|
| Qui est principal ? | `https://<serveur>:5443/api/healthcheck` : `role` = `principal`, `veille` ou `ecarte` |
| Pourquoi pas de promotion ? | Journal de l'API du 2e serveur : dernières lignes `Vigie : verdict …` et `promotion_refusee` |
| Le droit 166b est-il là ? | Requête de contrôle du § 3 (`t` attendu) |
| La copie est-elle en lecture seule ? | `SELECT pg_is_in_recovery();` sur le 2e serveur : `t` avant promotion, `f` après |
| Décisions passées | Table `bascule_decision`, fichiers `Journaux\bascule-*.json` du 2e serveur |

## 11. Essais réels faits

| Date | Cas | Résultat | Détail |
|---|---|---|---|
| 2026-11-25 | Panne complète du serveur 1 | Promotion automatique, poste prouvé basculé | second-serveur-local.md § 15 |
| 2026-11-25 | Relais (cas 3) et retrait (cas 2) | Conformes | § 16 |
| 2026-11-25 | Réplication synchrone, panier pendant la bascule | 92 ventes sur 92 retrouvées, renvoi sans doublon | § 17 |
| 2026-11-26 | Égalité (1 poste de chaque côté) | Pas de promotion, serveur 1 garde la main | § 18 |
| 2026-11-26 | Isolement complet du serveur 1 | Serveur 1 écarté à 72 s, 2e serveur principal à 103 s, postes « Connecté · 2e serveur » | § 18 |

**Avant toute mise en service avec deux serveurs, un essai de bascule réel est obligatoire** ([procedure-deux-serveurs.md](procedure-deux-serveurs.md) § 7 bis).
