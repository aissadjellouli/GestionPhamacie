# Conception : synchronisation officine ↔ serveur central (lot 12)

> **Statut : conception seulement (GO « go conception sync », 2026-11-16).** Aucun code, aucun script SQL, aucune modification de base. Rien n'est lancé.
> Les éléments marqués **[À VÉRIFIER]** ne sont pas établis ; les éléments marqués **[DÉCISION]** attendent le titulaire.
> Le chiffrement des données (lot 9) n'est **pas** décidé ici : la conception indique seulement où il pourra s'insérer.

Documents liés : [analyse-ecarts.md](analyse-ecarts.md) (lot 5 § 6 bis, lot 12), [mise-en-production.md](mise-en-production.md) (2e serveur, copie de secours), [regles-metier.md](regles-metier.md) (intégrité des journaux, journal fiscal, registre), [securite-et-droits.md](securite-et-droits.md), [base-de-donnees.md](base-de-donnees.md).

## 0. Décisions déjà prises (titulaire, 2026-11-16)

- **Local d'abord** : l'API et la base de l'officine **font foi**. Les postes ne parlent **jamais** au cloud. Une coupure d'Internet n'a **aucun effet** sur le travail (ventes, Chifa, stock, caisse).
- **Cloud** = API cloud + base centrale, une copie **cloisonnée** par officine, hébergée en Algérie chez un hébergeur agréé ARPCE **[À VÉRIFIER : hébergeur non choisi]**. **Aucun PostgreSQL ouvert sur Internet** : tout passe par l'API cloud.
- Synchronisation **par l'API** (service intégré à l'API locale), **pas** par réplication PostgreSQL (réservée au 2e serveur, en réseau local).
- La sync tolère une officine **en retard d'une version** d'API.
- Le 2e serveur reste facultatif.

## 1. Schéma et rôles

```
 Officine (réseau local)                                     Algérie, hébergeur agréé [À VÉRIFIER]
 ┌───────────────────────────────────────────┐              ┌──────────────────────────────────────┐
 │ Postes WPF/Blazor ──► API locale ◄──► PostgreSQL local    │              │ API cloud (seul point d'entrée)       │
 │                       │   (fait foi)      │  HTTPS sortant│              │   ├─ réception des lots montants      │
 │                       └─ Service de sync ─┼──── TLS ─────►│ ─────────────►   ├─ file des lots descendants        │
 │                          (outbox, envoi,  │ (initié par   │              │   ├─ vérification intégrité/sceaux    │
 │                           application des │  l'officine)  │              │   └─ écrans à distance (titulaire)    │
 │                           ordres descendants)              │              │ Base centrale : un schéma ou une base │
 │ 2e serveur (facultatif) : réplication PG locale            │              │ par officine (cloisonnée)             │
 └───────────────────────────────────────────┘              └──────────────────────────────────────┘
```

| Composant | Rôle | Ce qu'il ne fait jamais |
|---|---|---|
| **API locale** | Seule à écrire dans la base de l'officine ; applique toutes les règles métier ; héberge le service de sync. | Dépendre du cloud pour une opération de comptoir. |
| **Service de sync** (tâche de fond de l'API locale) | Lit le journal des modifications (outbox), envoie des lots, reçoit les accusés ; **tire** les modifications descendantes et les fait appliquer par les services métier de l'API locale. | Écrire directement dans les tables métier sans passer par les contrôles ; ouvrir un port entrant. |
| **API cloud** | Authentifie l'officine, reçoit et range les lots, vérifie l'intégrité, prépare les modifications descendantes, sert les écrans à distance. | Modifier une vente, une ligne de journal ou de registre ; exposer la base. |
| **Base centrale** | Copie par officine, en lecture pour les écrans à distance ; file des ordres descendants. | Être la référence d'une donnée fiscale, de registre ou de stock. |
| **Écrans à distance** (titulaire, multi-officines) | Consulter ; préparer des modifications descendantes (prix, réglages). | Agir sur l'officine en direct : tout passe par un ordre descendant appliqué par l'API locale. |

Choix proposé : la connexion est **toujours initiée par l'officine** (HTTPS sortant, interrogation périodique ou connexion longue). Aucun port entrant à ouvrir dans l'officine. **[DÉCISION]** interrogation périodique seule, ou connexion longue (WebSocket/SSE) pour réduire le délai descendant.

## 2. Sens de chaque famille de données

Les noms de tables cités sont ceux constatés dans la documentation ; la liste exhaustive table par table est à établir à partir de [base-de-donnees.md](base-de-donnees.md) au lot S1 **[À VÉRIFIER]**.

| Famille | Exemples | Sens | Référence | Remarque |
|---|---|---|---|---|
| Ventes et paiements | `vente`, lignes, paiements, annulations, retours, avoirs | **Montante seule** | Local | `vente` n'est pas figée (mises à jour Chifa) : on envoie chaque nouvel état, le cloud garde la dernière version reçue. |
| Journal fiscal | `journal_vente`, `cloture_fiscale`, sceaux | **Montante seule** | Local | Recopié tel quel (rang, empreinte, sceau) ; voir § 5. |
| Registre des psychotropes | `registre_psychotrope`, `journal_registre_psychotrope` | **Montante seule** | Local | Noms de patients / porteurs : voir minimisation ci-dessous. |
| Stock | lots, boîtes, `boite_evenement`, mouvements, inventaires | **Montante seule** | Local | Le cloud ne crée ni ne déplace de stock. |
| Caisse | sessions, clôtures, relèves, écarts | **Montante seule** | Local | |
| Journaux d'actions et d'intégrité | `journal_audit`, `verification_integrite` | **Montante seule** | Local | Utile pour l'alerte à distance. |
| Achats et réceptions | commandes, réceptions, factures grossistes | **Montante** ; commande préparée à distance = **descendante** (ordre) | Local | Une commande préparée à distance arrive en brouillon, validée dans l'officine **[DÉCISION]**. |
| Chifa | bordereaux, rejets | **Montante seule** | Local | Les échanges CHIFA-Officine restent locaux. |
| Réglages de l'officine | seuils, remises, paramètres caisse | **Bidirectionnelle** (ordre descendant + état montant) | Local | Modifié à distance = ordre ; l'état réel est celui que l'officine renvoie après application. |
| Prix de vente | prix public, prix boîte | **Descendante** (ordre) + état montant | Local | Appliqué par l'API locale avec ses contrôles (prix > 0, marges réglementées [À VÉRIFIER]). |
| Catalogue commun / nomenclature nationale | fichier MIPH, classes réglementaires | **Descendante** | Cloud (diffusion) | L'officine importe comme aujourd'hui ; produit « à vérifier » si classe sensible, inchangé. |
| Employés, rôles, droits | comptes, droits | **Montante** (état) ; création à distance **[DÉCISION]** | Local | Proposition : jamais de mot de passe ni code comptoir transmis, seulement l'existence et les rôles. |
| Patients | identité, crédit, ordonnances | **Montante, minimisée** **[DÉCISION]** | Local | Voir ci-dessous. |

### Données qui ne quittent pas l'officine (proposition, à valider)

| Donnée | Justification proposée |
|---|---|
| Clé de scellement HMAC | Le cloud ne doit pas pouvoir produire un sceau ; il vérifie autrement (§ 5). |
| Mots de passe, codes comptoir, compte super admin | Aucun besoin à distance ; risque en cas de fuite centrale. |
| Images d'ordonnances, documents GED de santé | Données de santé lourdes ; aucun usage cloud identifié (loi 18-07, minimisation). **[DÉCISION]** |
| Identité des patients et porteurs du registre | Proposition : remplacée par un **pseudonyme stable** (identifiant opaque calculé localement) dans la copie cloud ; le registre en clair reste dans l'officine pour l'inspection. **[DÉCISION]** — limite : la copie cloud ne permettrait plus une restitution complète du registre en cas de perte de l'officine (voir § 9). |
| Journaux fichiers de l'API (`C:\ProgramData\PharmaOfficine\Journaux`) | Techniques, déjà sans données personnelles (lot 1) ; non nécessaires. |

Le choix « pseudonymiser ou envoyer en clair » est structurant pour la sauvegarde hors site et la déclaration ANPDP : **[DÉCISION]** du titulaire, après avis réglementaire **[À VÉRIFIER]**.

## 3. Mécanisme

### 3.1 Journal des modifications local (outbox)
- Une table locale `sync_sortie` (nom indicatif) reçoit **dans la même transaction** que l'opération métier une ligne par modification d'une table synchronisée : table, identifiant stable, type (création / modification / ajout seul), version de contrat, contenu ou référence, horodatage UTC.
- Alimentation proposée : **déclencheurs différés** (même principe que `journal_vente`) pour ne rien oublier quel que soit le chemin d'écriture. Une opération sans sa ligne d'outbox est donc impossible.
- Numéro de séquence local **croissant par officine** (attribué à la validation, verrou court comme le compteur de tickets) : il donne l'ordre d'envoi. Les trous dus aux annulations de transaction sont acceptables ici **à condition** que la séquence soit attribuée au commit ; sinon le cloud ne peut pas détecter un manque **[À VÉRIFIER au lot S1 : mode d'attribution sans trou]**.

### 3.2 Identifiants stables
- Identité globale = **(id officine, table, id local)**. Les `bigint` existants ne changent pas (§ 6 bis, point 3) ; les nouvelles tables portent une clé GUID.
- L'id officine est un GUID attribué à l'inscription (§ 9) et stocké dans la base locale ; il est **répliqué** vers le 2e serveur (même officine).

### 3.3 Envoi par lots idempotents
- Lot = suite contiguë de séquences [n..m], taille plafonnée (proposition : 500 lignes ou 1 Mo compressé, **[À VÉRIFIER par mesure]**), identifiant de lot = (id officine, n, m), empreinte SHA-256 du contenu.
- Le cloud applique un lot **une seule fois** : un lot déjà reçu avec la même empreinte est accusé sans réapplication ; même numéro avec une empreinte différente = **anomalie** (alerte, lot refusé).
- Le cloud n'accepte que le lot qui suit immédiatement le dernier accusé (n = dernier + 1) : **ordre garanti**, aucun manque.

### 3.4 Accusés et reprise
- Accusé = dernier n° de séquence enregistré durablement côté cloud. L'officine garde `dernier_accuse` et ne purge l'outbox qu'au-delà, après un délai de garde (proposition : 30 jours, **[DÉCISION]**).
- Coupure en plein envoi : l'officine renvoie le même lot ; l'idempotence évite le doublon. Aucune perte : rien n'est retiré de l'outbox sans accusé.
- Coupure longue : l'outbox grossit ; le bandeau l'indique (§ 8). Le rattrapage envoie les lots dans l'ordre, débit plafonné pour ne pas saturer la ligne de l'officine.

### 3.5 Descendant
- Le cloud tient une file d'**ordres** par officine (n° croissant, id GUID, auteur, motif). Le service de sync les tire, les fait exécuter **par les services métier de l'API locale** (mêmes contrôles qu'un écran local), inscrit le résultat au `journal_audit` (auteur distant + application locale), puis renvoie un accusé « appliqué » ou « refusé (motif) ».
- Un ordre est idempotent (son id est mémorisé localement ; rejoué = ignoré).

### 3.6 Fréquence
- Proposition : envoi dès qu'il y a des lignes, au plus toutes les 30 s ; interrogation descendante toutes les 1 à 5 min **[DÉCISION]**. Jamais pendant une transaction de caisse : tâche de fond indépendante.

## 4. Conflits

| Famille | Règle |
|---|---|
| Fiscal, registre, stock, caisse, ventes | **Le local fait foi, toujours.** Le cloud ne modifie jamais ces données ; aucun ordre descendant ne les vise. |
| Prix, réglages | Ordre descendant appliqué par l'API locale **seulement si** la valeur locale n'a pas changé depuis la version vue à distance (version ou horodatage transmis avec l'ordre). Sinon : ordre **refusé « modifié dans l'officine »**, signalé à l'auteur distant, qui décide à nouveau. Pas de fusion automatique. |
| Catalogue commun / nomenclature | Diffusé ; l'officine garde ses produits et leurs réglages propres ; un changement de classe réglementaire passe par l'état « à vérifier » existant. |
| Employés / droits (si modifiables à distance) | Même règle que les réglages. |

**Modification des deux côtés** : l'officine gagne (elle a déjà servi des clients avec sa valeur). L'ordre distant est refusé, tracé, et visible à distance. Proposition d'un **délai d'expiration** des ordres non appliqués (ex. 7 jours) **[DÉCISION]**.

## 5. Intégrité : vérification sans réécriture

- Les lignes de `journal_vente`, `journal_registre_psychotrope` (et toute chaîne de la brique du lot 2) sont envoyées **avec leur rang et leur empreinte**, sans recalcul (§ 6 bis, point 4).
- Le cloud **recalcule** chaque empreinte selon le format canonique v1 ([base-de-donnees.md](base-de-donnees.md)) et vérifie le chaînage et la continuité des rangs. Toute rupture : lot mis en anomalie, **alerte titulaire et éditeur**, la donnée reçue est conservée telle quelle (preuve), jamais corrigée.
- Continuité des tickets « T- » : même contrôle que `journal_vente_continuite()`, côté cloud, sur les données reçues.
- **Sceaux HMAC** : la clé ne quitte pas l'officine, donc le cloud **ne peut pas vérifier un HMAC** directement. Options **[DÉCISION]** :
  1. le cloud conserve les sceaux reçus et l'**ancrage** (dernier rang + dernière empreinte) ; il vérifie que les empreintes recalculées correspondent à ce que le sceau couvre ; l'HMAC reste vérifiable dans l'officine et lors d'un contrôle ;
  2. ajouter plus tard une **signature asymétrique** (clé privée dans l'officine, clé publique au cloud) pour permettre une vérification distante sans pouvoir signer — touche à la cryptographie, donc proche du lot 9 : **non décidé**.
- Côté cloud, les tables reçues sont en **ajout seul** (mêmes déclencheurs d'immuabilité qu'en local) : le compte de l'API cloud ne peut ni modifier ni supprimer.
- Le cloud devient aussi une **preuve externe** : une ligne réécrite dans l'officine après envoi serait détectée par comparaison (vérification croisée périodique des derniers ancrages).

## 6. Sécurité

- **Transport** : TLS 1.2 minimum (1.3 de préférence), certificat serveur vérifié par l'officine (épinglage de l'autorité **[DÉCISION]**).
- **Authentification de l'officine** : proposition **certificat client** (mTLS) délivré à l'inscription, stocké sur le PC serveur (magasin machine, même protection que la clé de scellement) + jeton court émis par l'API cloud. Rotation annuelle et révocation depuis le cloud **[DÉCISION durée]**. Le 2e serveur reçoit le certificat avec l'export protégé existant de la clé, ou un certificat propre **[DÉCISION]**.
- **Cloisonnement** : chaque requête est liée à l'id officine du certificat ; jamais à un id fourni dans le corps. Base centrale : une base ou un schéma par officine avec rôle propre **[DÉCISION]** ; tests de non-fuite entre officines obligatoires.
- **Comptes distants** (titulaire, multi-officines, éditeur) : authentification forte (mot de passe + code SMS, comme le super admin) ; droits limités à leurs officines ; l'éditeur (support) n'a pas accès aux données nominatives sans accord tracé **[DÉCISION]**.
- **Journal des accès** côté cloud : qui, quoi, quand, quelle officine ; ajout seul ; consultable par le titulaire.
- **Minimisation (loi 18-07)** : voir § 2 ; journaux techniques sans données personnelles (règle du lot 1 reprise).
- **Lot 9 (non lancé)** : points d'insertion possibles, sans décision — chiffrement des colonnes sensibles avant mise en outbox, chiffrement au repos de la base centrale, chiffrement des sauvegardes cloud.
### 6.1 Protection de l'API cloud, couche par couche (ajouté le 2026-11-16 à la demande du titulaire)

Principe : défense en profondeur ; si une couche cède, les autres tiennent. L'API cloud n'est jamais sur le chemin d'une vente.

1. **Réseau** : hébergement en Algérie chez un hébergeur agréé ARPCE ; **seul le port 443** est exposé ; la base centrale n'est jamais exposée (réseau interne, seule l'API lui parle) ; pare-feu et filtrage anti-saturation (DDoS) de l'hébergeur **[À VÉRIFIER : offre de l'hébergeur]** ; limitation du débit des requêtes par officine, par compte et par adresse (essais de mots de passe en rafale).
2. **Transport** : HTTPS uniquement (TLS 1.2 minimum, 1.3 de préférence), aucun HTTP ; certificat client propre à chaque officine (mTLS, révocable pour une seule officine). **Épinglage** : l'API locale n'accepte que le certificat (ou l'autorité) du cloud attendu, et refuse tout autre serveur, même muni d'un certificat d'apparence valide.
3. **Pas de chiffrement « maison » par secret partagé** : HTTPS chiffre déjà tout le transit avec une clé négociée et jetée à chaque connexion (confidentialité persistante), vérifie l'identité du serveur et détecte toute altération. Un secret unique commun aux officines serait exposé d'un coup en cas de fuite, difficile à distribuer et à renouveler, et un chiffrement écrit à la main est la première source d'erreurs de sécurité.
4. **Identité de l'appelant : JWT** (voir 6.2).
5. **Droits et cloisonnement** : contrôle des actions permises (portées du jeton) ; officine du jeton = officine des données, vérifiée aussi dans la base (sécurité au niveau des lignes ou base/schéma par officine) ; ordres à distance limités à ceux décidés par le titulaire ; ventes, journal fiscal et registre en **ajout seul** dans le cloud.
6. **Signature des envois** : chaque lot montant est signé avec une clé propre à l'officine (paire de clés asymétriques, la clé privée ne quitte pas le serveur de l'officine) ; le cloud prouve l'origine et l'absence d'altération des ventes et du journal fiscal, en plus de la vérification de la chaîne (§ 5).
7. **Données** : patients sous pseudonyme (décision 1), ordonnances et images scannées jamais envoyées (décision 2), clé de scellement jamais envoyée ; chiffrement au repos = lot 9, non lancé (« go chiffrement » exigé).
8. **Surveillance** : journal de chaque accès (chaîné, ajout seul) ; alertes (refus en série, origine inhabituelle, volume de lecture anormal) ; mises à jour de sécurité du serveur, de .NET et de PostgreSQL ; sauvegardes du cloud testées sur un second site en Algérie **[À VÉRIFIER : hébergeur]** ; **test d'intrusion** par un prestataire externe avant mise en service (conseillé).
9. **Dernier rempart** : l'officine ne dépend pas du cloud ; tout ordre reçu est recontrôlé par l'API locale (règles métier, bornes, droits) avant application, un ordre aberrant est refusé et journalisé.

### 6.2 JWT pour valider l'appelant

| Appelant | Preuve préalable | Jeton reçu |
|---|---|---|
| Officine (API locale) | Certificat client (mTLS) | JWT d'accès de 10 min, renouvelé automatiquement |
| Titulaire à distance | Identifiant + mot de passe + second facteur | JWT d'accès de 10 min + jeton de renouvellement à usage unique (renouvelé à chaque usage, durée totale limitée) |
| Support de l'éditeur | Compte séparé + second facteur | JWT limité à l'état technique, sans données nominatives (décision 4) |

- **Contenu** : émetteur (`iss`), destinataire (`aud`), sujet (`sub`), officine, rôle, portées (ex. `sync.envoyer`, `tableau.lire`, `ordre.prix`), expiration (`exp`), identifiant unique (`jti`), empreinte du certificat de l'officine (`cnf`, jeton lié au certificat). **Aucune donnée sensible** : un JWT est signé, pas chiffré, il se lit.
- **Signature asymétrique** (ES256 ou RS256) par le cloud seul ; la vérification n'utilise que la clé publique. Pas de HS256 à secret partagé. Rotation régulière des clés de signature (identifiant de clé `kid`).
- **Vérification à chaque appel** : algorithme imposé côté serveur (refus de `none` et de tout algorithme non prévu) ; signature ; `iss`, `aud`, `exp` (tolérance de quelques secondes) ; officine du jeton = officine demandée ; portées suffisantes ; jeton lié au certificat présenté (un JWT volé est inutilisable sans le certificat).
- **Révocation** : durée courte ; liste des jetons, comptes et officines révoqués (employé bloqué, téléphone perdu, officine résiliée) consultée à chaque appel ; les jetons de renouvellement d'un compte révoqué sont invalidés.
- Le JWT **s'ajoute** à HTTPS, au certificat de l'officine et aux contrôles de l'API locale : il prouve qui appelle, il ne remplace aucune autre couche.

- **[À VÉRIFIER] réglementaires** : déclaration du traitement à l'ANPDP (et qui est responsable / sous-traitant : titulaire ou éditeur) ; agrément ARPCE de l'hébergeur et éventuelle exigence d'hébergeur de données de santé ; durée de conservation côté cloud (6 ans fiscaux minimum, registre psychotropes : durée réglementaire à confirmer) ; contrat de sous-traitance ; droit d'accès / d'effacement des patients face aux obligations de conservation.

## 7. Versions

- **Contrat de sync versionné** (n° entier, ex. `sync-v1`) porté par chaque lot et chaque ordre ; format canonique des empreintes versionné à part (v1 existant).
- Règle **N-1** : l'API cloud accepte la version courante et la précédente ; un champ ajouté est facultatif pour N-1 ; un champ retiré ou changé de sens = nouvelle version, avec conversion côté cloud.
- Une officine en N-2 : envoi **suspendu** (données gardées dans l'outbox, rien de perdu), bandeau « mise à jour nécessaire ». Le travail local continue.
- Mise à jour : le cloud est mis à jour **en premier** (il sait lire N et N-1), puis les officines. Les ordres descendants sont émis dans la version de l'officine destinataire.

## 8. Supervision

- **Bandeau poste** (même emplacement que « Serveur injoignable ») : « Synchronisation en attente depuis … (n opérations) », affiché au titulaire et aux rôles choisis ; seuil d'affichage proposé 1 h **[DÉCISION]**. Jamais bloquant.
- **Alerte titulaire** (SMS / e-mail existants, au plus une fois par heure) : retard au-delà d'un seuil (proposé 24 h), anomalie d'intégrité au cloud, ordre refusé, certificat proche d'expiration, version N-2.
- **Écran d'état** (Administration, titulaire) : dernier envoi et dernier accusé, taille de la file, lots en anomalie, ordres reçus / appliqués / refusés, version de contrat, validité du certificat, bouton « Synchroniser maintenant ».
- **Côté cloud** : tableau par officine (dernier contact, retard, anomalies) pour l'éditeur.

## 9. Cas particuliers

- **2e serveur et bascule** : l'outbox et `dernier_accuse` sont dans la base, donc répliqués. Seule l'API **qui écrit** (principal) fait tourner le service de sync ; l'API de secours refuse déjà de démarrer sur une base en lecture seule. Après bascule, le nouveau principal reprend l'envoi au premier non-accusé ; l'idempotence absorbe les lots renvoyés. Les dernières transactions non répliquées au moment de la panne peuvent manquer : si le cloud a reçu des séquences que le nouveau principal n'a pas, c'est une **anomalie signalée** (le cloud a alors la preuve de lignes perdues) — traitement **[DÉCISION]**.
- **Restauration d'une sauvegarde locale** : la base revient avec un `dernier_accuse` et des séquences antérieures à ce que le cloud a reçu. Proposition : à la reprise, le service de sync **compare** avec le cloud ; les données reçues par le cloud après le point de restauration sont signalées, **jamais renvoyées automatiquement** dans la base locale (elles touchent au fiscal). Une restitution assistée depuis le cloud est à concevoir **[DÉCISION]** ; une **nouvelle époque** de séquence (n° d'époque incrémenté) évite toute collision.
- **Nouvelle officine** : inscription (certificat, id officine), puis **chargement initial** montant : instantané cohérent de toutes les tables synchronisées (lu dans une transaction de lecture), puis outbox à partir de ce point. Pour les chaînes, envoi depuis le rang 1 (ou depuis la ligne « Mise en service »).
- **Officine qui quitte le service** : export complet remis au titulaire (format à définir), révocation du certificat, puis effacement de la copie cloud **après** les durées légales de conservation ou remise au titulaire **[À VÉRIFIER : qui conserve les données fiscales 6 ans]**, procès-verbal d'effacement.

## 10. Découpage en lots (proposition, rien n'est lancé)

| Lot | Contenu | Dépend de |
|---|---|---|
| S0 | Décisions du titulaire ci-dessous ; choix de l'hébergeur ; avis ANPDP | — |
| S1 | Inventaire table par table (sens, minimisation), contrat `sync-v1`, outbox locale + séquence (scripts SQL), sans envoi | S0 |
| S2 | API cloud minimale (réception, idempotence, ordre, accusés), base centrale cloisonnée, authentification par certificat | S0, S1 |
| S3 | Service de sync local (envoi, reprise, bandeau, écran d'état, alertes) | S1, S2 |
| S4 | Vérification d'intégrité côté cloud (chaînes, continuité, ancrages des sceaux) | S2 ; lots 2, 3, 8 (faits) |
| S5 | Ordres descendants (prix, réglages, nomenclature) appliqués par l'API locale, conflits, journalisation | S3 |
| S6 | Cas particuliers : chargement initial, bascule 2e serveur, restauration (époques), départ d'une officine | S3, S4 |
| S7 | Écrans à distance du titulaire, multi-officines, journal des accès | S2, S5 |
| (lot 9) | Chiffrement des données : **non lancé**, s'insérera aux points du § 6 | « go chiffrement » |

### Décisions demandées au titulaire
1. Données patients et porteurs : envoyées en clair ou pseudonymisées (§ 2) ; images d'ordonnances / GED : restent locales ?
2. Sceaux : ancrage seul, ou signature asymétrique future (§ 5).
3. Ordres descendants autorisés : prix, réglages, commandes brouillon, employés ? Délai d'expiration.
4. Interrogation périodique ou connexion longue ; fréquences ; seuils du bandeau et de l'alerte.
5. Certificat client : durée, rotation, certificat partagé ou propre au 2e serveur.
6. Base centrale : une base ou un schéma par officine.
7. Traitement des lignes perdues lors d'une bascule ou d'une restauration (§ 9).
8. Accès de l'éditeur (support) aux données nominatives.

### [À VÉRIFIER]
- Hébergeur agréé ARPCE et éventuelles exigences pour les données de santé.
- Déclaration ANPDP, rôle responsable de traitement / sous-traitant, délégué à la protection des données.
- Durées de conservation côté cloud (fiscal 6 ans ; registre des psychotropes ; données patients).
- Taille réelle des lots et débit des lignes Internet des officines.
- Attribution sans trou de la séquence d'outbox au commit.
- Liste exhaustive des tables à synchroniser.

## Décisions du titulaire (2026-11-16)

| N° | Sujet | Décision |
|---|---|---|
| 1 | Données des patients vers le cloud | **Pseudonyme** : le cloud reçoit un code aléatoire stable par patient (et par porteur), jamais le nom, le n° d'assuré ni le téléphone ; la correspondance code ↔ personne reste uniquement dans l'officine. Conséquences acceptées : la base cloud ne contient aucun nom de patient, et l'API cloud ne permet aucune recherche par nom (montants, crédits et Chifa visibles à distance sous le code) |
| 2 | Images d'ordonnances et documents scannés | **Restent dans l'officine**, jamais envoyés au cloud |
| 3 | Ordres à distance permis | **Prix et marges, réglages, commandes en brouillon, employés et droits** (toujours appliqués par l'API locale avec ses contrôles métier et journalisés) |
| 4 | Accès du support de l'éditeur | **Jamais de données nominatives** : état technique seulement (synchronisation, versions, erreurs) |
| 5 | Certificat de l'officine | **Valable 3 ans**, renouvelé automatiquement avant l'échéance, révocable à tout moment ; **le 2e serveur a son propre certificat** (révocation de l'un sans toucher l'autre) |
| 6 | Mode d'échange | **Interrogation toutes les minutes** environ à l'initiative de l'officine (envoi des nouveautés, relève des ordres) ; un ordre à distance arrive en une minute au plus |
| 7 | Séparation des officines dans le cloud | **Un schéma par officine** dans une base commune, avec un compte PostgreSQL propre à chaque schéma (plus le contrôle de l'officine du jeton dans l'API) |
| 8 | Lignes reçues par le cloud mais absentes de l'officine après une bascule ou une restauration | **Signaler et garder** : le cloud ne les efface jamais, les marque « absentes de l'officine » et alerte le titulaire, qui décide (ressaisie, annulation tracée ou justification pour le contrôle fiscal) |
