# Conception : synchronisation officine ↔ serveur central (lot 12)

> **Statut : conception (GO « go conception sync », 2026-11-16) ; lot S1 réalisé (§ 11) ; lot S2 réalisé (2026-11-16, § 12)** : base centrale commune cloisonnée, autorité de certification, API centrale (activation, jeton, dépôt, accusé), envoi depuis l'officine, écran « Admin › Synchronisation ». Serveur central **local** pour l'instant (`pharmacie_cloud_dev`, port 5090), aucune connexion vers l'extérieur. Lots S3 à S7 non lancés.
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
- ~~Numéro de séquence local croissant attribué au commit~~ **Tranché au lot S1** : les **lignes** reçoivent un n° d'identité croissant (trous possibles, transactions annulées) qui ne sert qu'à l'ordre ; ce sont les **lots** qui sont numérotés sans trou, à la constitution (voir § 11). Pas de verrou ajouté sur les ventes.

### 3.2 Identifiants stables
- Identité globale = **(id officine, table, id local)**. Les `bigint` existants ne changent pas (§ 6 bis, point 3) ; les nouvelles tables portent une clé GUID.
- L'id officine est un GUID attribué à l'inscription (§ 9) et stocké dans la base locale ; il est **répliqué** vers le 2e serveur (même officine).

### 3.3 Envoi par lots idempotents
- Lot = lignes de la file pas encore rattachées, par n° croissant, taille plafonnée (réglage : 500 lignes et 1 Mo non compressé par défaut, **[À VÉRIFIER par mesure]**), identifiant de lot = (id officine, époque, n° de lot sans trou), empreinte SHA-256 du lot sérialisé `sync-v1` (révisé au lot S1, § 11).
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
- ~~Attribution sans trou de la séquence d'outbox au commit.~~ Tranché au lot S1 (§ 11 : lots numérotés à la constitution).
- ~~Liste exhaustive des tables à synchroniser.~~ Faite au lot S1 : [synchronisation-inventaire.md](synchronisation-inventaire.md).

## Décisions du titulaire (2026-11-16)

| N° | Sujet | Décision |
|---|---|---|
| 1 | Données des patients vers le cloud | **Pseudonyme** : le cloud reçoit un code aléatoire stable par patient (et par porteur), jamais le nom, le n° d'assuré ni le téléphone ; la correspondance code ↔ personne reste uniquement dans l'officine. Conséquences acceptées : la base cloud ne contient aucun nom de patient, et l'API cloud ne permet aucune recherche par nom (montants, crédits et Chifa visibles à distance sous le code) |
| 2 | Images d'ordonnances et documents scannés | **Restent dans l'officine**, jamais envoyés au cloud |
| 3 | Ordres à distance permis | **Prix et marges, réglages, commandes en brouillon, employés et droits** (toujours appliqués par l'API locale avec ses contrôles métier et journalisés) |
| 4 | Accès du support de l'éditeur | **Jamais de données nominatives** : état technique seulement (synchronisation, versions, erreurs) |
| 5 | Certificat de l'officine | **Valable 3 ans**, renouvelé automatiquement avant l'échéance, révocable à tout moment ; **le 2e serveur a son propre certificat** (révocation de l'un sans toucher l'autre) |
| 6 | Mode d'échange | **Interrogation toutes les minutes** environ à l'initiative de l'officine (envoi des nouveautés, relève des ordres) ; un ordre à distance arrive en une minute au plus |
| 7 | Séparation des officines dans le cloud | ~~Un schéma par officine~~ **Remplacée le 2026-11-16 (lot S2)** : **tables communes** dans une seule base centrale, mêmes tables et colonnes que la base locale, chaque ligne portant le code officine (`officine_id`, dans la clé primaire côté central) ; cloisonnement par **Row Level Security** PostgreSQL (`FORCE ROW LEVEL SECURITY`, rôle applicatif non propriétaire, officine posée par `SET LOCAL app.officine_id` dans chaque transaction, déduite du certificat) ; plus le contrôle de l'officine du jeton dans l'API. Voir § 12 |
| 8 | Lignes reçues par le cloud mais absentes de l'officine après une bascule ou une restauration | **Signaler et garder** : le cloud ne les efface jamais, les marque « absentes de l'officine » et alerte le titulaire, qui décide (ressaisie, annulation tracée ou justification pour le contrôle fiscal) |

## 11. Lot S1 : réalisé (2026-11-16, GO « go S1 »)

Côté officine seulement : **aucun appel réseau, aucune adresse de serveur central, aucun envoi, aucun chiffrement**.

| Élément | Réalisation |
|---|---|
| Inventaire | `sync_table` (script `126_synchronisation_sortie.sql`) et [synchronisation-inventaire.md](synchronisation-inventaire.md) : 142 tables du schéma public — 78 montantes, 22 bidirectionnelles, 17 descendantes, 25 jamais. Test d'architecture `SynchronisationInventaireTests` (table oubliée, document en écart, patients et porteurs sous code) ; `sync_definir_active(true)` refuse d'activer tant qu'une table n'est pas classée. |
| Contrat `sync-v1` | `Pharma.Shared/DtosSynchronisation.cs` : `EnTeteLotSyncDto` (version, officine, époque, n° de lot, nombre de lignes, empreinte), `LigneSyncDto` (n°, table, id stable, opération `creation`/`modification`/`suppression`, horodatage UTC, données), `SerialisationSyncV1` : JSON UTF-8 compact, ordre des propriétés fixe, données canoniques (clés triées), dates à la microseconde ; empreinte = SHA-256 du lot sérialisé avec une empreinte vide. Même bibliothèque prévue côté serveur central. |
| File locale (outbox) | `sync_sortie`, alimentée par le déclencheur `zz_sync_sortie` (AFTER INSERT/UPDATE/DELETE, **même transaction**) des tables montantes et bidirectionnelles ; données minimisées **avant** inscription (patients et porteurs sous code, colonnes exclues) ; ajout seul ; purge seulement après accusé (prévue, non lancée). |
| Pseudonymes | `sync_pseudonyme` : patient ↔ `P-` + 24 hex, porteur ↔ `R-` + 24 hex (clé locale = empreinte du nom et de la pièce), créés au premier besoin, stables, jamais envoyés. |
| Désactivée par défaut | `sync_parametre.active = false` : **aucun déclencheur posé**, rien n'est inscrit, aucun coût sur les ventes. `sync_definir_active(bool)` pose ou retire les déclencheurs. `officine_id` reste vide jusqu'à l'inscription (S2/S6). |
| Constitution des lots | `Pharma.Application/Synchronisation/ConstitutionLots.cs` (`ConstitutionLotsSync`, ports `ISortieSyncDepot` — `SortieSyncSql` — et `IFormatLotSync` — `FormatLotSyncV1` dans l'API) : lit les lignes en attente, forme des lots bornés (lignes et octets ; une ligne plus grosse que la borne part seule), calcule l'empreinte, enregistre par `sync_enregistrer_lot`. Refuse si désactivée, officine non inscrite ou version inconnue. **Aucune tâche de fond ne l'appelle encore.** |

**Numérotation (point [À VÉRIFIER] du § 3.1, tranché)** : attribuer aux *lignes* un n° sans trou au commit imposerait un verrou sérialisant toutes les écritures (comme le compteur de tickets) sur chaque vente. Retenu : les lignes ont un n° d'identité croissant **avec trous possibles** (transactions annulées), utilisé seulement pour l'ordre ; les **lots** sont numérotés **sans trou** par époque au moment de la constitution (`sync_enregistrer_lot` : n° = dernier + 1 sous verrou de la ligne `sync_parametre`, insertion du lot et rattachement des lignes dans la même transaction ; une constitution simultanée reçoit NULL et reprend plus tard). Une ligne n'est jamais oubliée : la sélection se fait sur « pas encore rattachée », pas sur un intervalle de n°. Une transaction longue validée après un lot part dans un lot suivant ; comme deux modifications d'une même ligne se suivent toujours dans l'ordre des n° (verrou de ligne PostgreSQL), le serveur central retient **l'état de plus grand n° de ligne**. Le serveur central détecte un manque par la continuité des n° de lot.

**Ce qui reste (S2 et suivants)** :
- S2 : API cloud (réception, idempotence par (officine, époque, n° de lot, empreinte), ordre n = dernier + 1, accusés), base centrale par schéma, certificat client et JWT ; inscription de l'officine (remplit `officine_id`).
- S3 : tâche de fond d'envoi (appel de `ConstitutionLotsSync`, renvoi d'un lot à l'identique depuis `sync_lot`/`sync_sortie`, enregistrement de l'accusé dans `sync_lot.accuse_le` et `sync_parametre.dernier_accuse`, purge après le délai de garde), bandeau « n opérations en attente », écran d'état.
- S4 : vérification des chaînes côté cloud ; **[DÉCISION]** pour `journal_registre_psychotrope`, dont l'empreinte ne peut pas être recalculée sans les colonnes du porteur.
- S6 : chargement initial (instantané des tables montantes), nouvelle époque après restauration.
- Points ouverts de l'inventaire : textes libres envoyés, `medecin` jamais, volume de `catalogue_grossiste` (voir l'inventaire).
- `init/schema.sql` à régénérer (tables, fonctions et déclencheurs du script 126).

## 12. Lot S2 : réalisé (2026-11-16, GO du titulaire)

Décisions nouvelles du titulaire (2026-11-16) : **tables communes + RLS** (remplace la décision 7 « un schéma par officine ») ; **serveur central local** sur le PC de développement pour l'instant (base `pharmacie_cloud_dev`, API `src/Pharma.Cloud.Api`, port 5090, aucune connexion vers l'extérieur) ; **même schéma que la base locale** (mêmes tables et colonnes pour les tables envoyées, plus le code officine) ; **code officine sur les tables locales** (option B : colonne `officine_id`, hors clé primaire locale).

### 12.1 Base centrale (`database/cloud`, voir son README)

| Élément | Réalisation |
|---|---|
| Tables miroir | `002_tables_miroir.sql` **généré** depuis `database/init/schema.sql` et le classement `sync_table` (`Pharma.Cloud.Api.exe --generer-miroir`) : 101 tables (78 montantes, 22 bidirectionnelles, `medecin` en option). Colonnes locales moins les colonnes exclues ; patients sous code (`P-…`, donc `text`), porteur remplacé par `porteur_code` ; clé `(officine_id, clé locale)` ; `sync_epoque`, `sync_numero`, `sync_recu_le`. Test `SchemaMiroirTests` : échoue si une table envoyée diverge, si une table envoyée ou reçue n'a pas `officine_id` localement, ou si une colonne de nom de patient apparaît. |
| État final | `cloud_appliquer_lot` : lignes groupées par table, dernier état par donnée dans le lot (plus grand n°), `INSERT … ON CONFLICT DO UPDATE … WHERE (sync_epoque, sync_numero) < nouveau` (une ligne ancienne ne remplace jamais une récente), suppression = ligne retirée. Seul l'état final est gardé, pas chaque modification. |
| Preuve | `lot_recu` : lot brut canonique (octets couverts par l'empreinte), **partitionné par mois** de réception, ajout seul. |
| Cloisonnement | RLS `ENABLE` + `FORCE` sur `officine_epoque`, `lot_recu` et chaque table miroir ; politique `officine_id = cloud_officine_courante()` ; rôle `pharma_cloud_api` non propriétaire, sans accès direct aux tables d'administration (fonctions `SECURITY DEFINER`). Test SQL : l'officine B ne lit, ne modifie, ne supprime ni n'écrit au nom de A, même sans filtre ; sans officine posée, rien n'est visible, propriétaire compris. |
| Administration | `officine`, `certificat` (empreinte SHA-256, échéance, révocation), `code_activation` (seul le condensé du code ; usage unique ; 24 h), `journal_securite` (ajout seul, sans donnée personnelle). |

**Partitionnement des tables miroir** : non fait. Une table d'état final mise à jour par clé ne peut pas être partitionnée **par mois** dans PostgreSQL (la clé unique doit contenir la colonne de partition, et le mois d'une donnée qui change n'est pas stable). Seule `lot_recu` est partitionnée par mois. Pour 12 000 officines, la voie prévue est un partitionnement des grosses tables miroir (ventes, lignes, mouvements, journaux) **par hachage de `officine_id`** — **Décision du titulaire (2026-11-16) : découpage par officine** (hachage de `officine_id`, par ex. 32 partitions, nombre à fixer par mesure) pour les grosses tables miroir ; `lot_recu` reste découpée par mois. À réaliser avant l'arrivée d'un grand nombre d'officines (non codé).

**Répartition future sur plusieurs serveurs** (conçue, non codée) : toutes les clés commencent par `officine_id` et aucune requête ne croise deux officines ; une table d'aiguillage `officine → serveur de base` (tenue par l'API centrale) permettra de placer chaque officine sur une base parmi N, l'API ouvrant la connexion de la base de l'officine du certificat. Déplacer une officine = copier ses lignes (toutes filtrées par `officine_id`) puis changer l'aiguillage.

### 12.2 Autorité de certification et API centrale (`src/Pharma.Cloud.Api`)

- **Autorité propre** (ECDSA P-256, 10 ans) dans un dossier **hors dépôt** (`--dossier` ou `PHARMA_CLOUD_DOSSIER` ; refus d'un dossier situé dans un dépôt de sources) ; clé chiffrée PKCS#8 AES-256 par `PHARMA_CLOUD_MOT_DE_PASSE` (16 caractères au moins), jamais dans appsettings ni dans git. Commandes : `--initialiser-autorite`, `--certificat-serveur`, `--creer-officine`, `--code-activation <officine> --serveur <nom>`, `--revoquer <empreinte>`, `--lister`, `--partitions`, `--generer-miroir`.
- **Code d'activation** : `XXXXX-XXXXX-XXXXX-XXXXX.<32 hex>` ; la seconde partie est le début de l'empreinte SHA-256 de l'autorité : l'officine épingle le serveur central **dès la première connexion**. Usage unique, 24 h. Le 2e serveur reçoit son propre code et son propre certificat.
- **Certificat client** : signé à partir de la demande (CSR) de l'officine, sujet imposé (`CN=<code officine>, OU=<serveur>`), 3 ans, authentification client seulement ; renouvellement par `POST /api/v1/certificat/renouveler` (jeton et certificat valables), l'ancien restant valable jusqu'à son échéance.
- **Routes** : `POST /api/v1/activation` (code + CSR + code officine → certificat et autorité), `POST /api/v1/jeton` (certificat → JWT ES256 10 min, `cnf.x5t#S256`, portée `sync.envoyer`), `POST /api/v1/lots` (corps = lot `sync-v1` ; accusé `accepte`, `deja_recu`, `trou` + dernier reçu ; refus `empreinte_differente`), `GET /api/v1/etat?epoque=` (dernier lot reçu). HTTPS seul (TLS 1.2/1.3), certificat client vérifié contre l'autorité **et** la base (révocation, échéance, officine résiliée) à chaque appel ; limites de débit (§ 12.6), 10 activations / 10 min par adresse ; ProblemDetails ; journal structuré (officine, n° de lot) sans donnée personnelle.
- **Première activation** : l'officine créée par l'outil (code provisoire) prend le **code de l'officine installée** (`sync_parametre.officine_id`) ; ensuite, un autre code est refusé.
- **Clé de l'autorité dans l'API** : l'API centrale charge la clé de l'autorité pour signer à l'activation. En production, une autorité intermédiaire dédiée à l'API (racine hors ligne) est conseillée **[DÉCISION]**.

### 12.3 Côté officine

| Élément | Réalisation |
|---|---|
| Script 128 | Code officine généré une fois (`sync_parametre.officine_id`, ne se modifie plus), `officine_courante()`, colonne `officine_id` (NOT NULL, défaut `officine_courante()`) sur les **118** tables montantes, bidirectionnelles, descendantes et `medecin`, ajoutée sans UPDATE (défaut constant puis défaut fonction) : journaux chaînés vérifiés identiques avant et après (`integrite_verifier`, `journal_vente_continuite`), gardes intactes. Réglages : `adresse_serveur` (https seulement), `inscrite_le`, `dernier_envoi_le`, `dernier_accuse_le`, `derniere_erreur(_le)` ; fonctions `sync_enregistrer_accuse`, `sync_noter_envoi`, `sync_enregistrer_inscription`, `sync_regler_adresse`. |
| Clé et certificat | `CertificatSyncFichier` : comme la clé de scellement (DPAPI portée machine, fichier restreint), dossier `C:\ProgramData\PharmaOfficine\Synchronisation` (réglage `Synchronisation:Dossier`) ; **hors base** (le 2e serveur a les siens). |
| Envoi | `EnvoyerLotsSync` + tâche `EnvoiSynchronisation` (chaque minute, attente 1, 2, 4… 30 min en erreur) : constitue les lots (S1), renvoie chaque lot non accusé **à l'identique** (vérifie que le contenu redonne l'empreinte enregistrée), enregistre l'accusé (`sync_lot.accuse_le`, `dernier_accuse`). Client `ClientServeurCentralHttp` : certificat client, autorité épinglée (`EpinglageServeur`), jeton redemandé avant expiration. |
| Écran | « Admin › Synchronisation » (droit `admin.parametres`) : État, Réglages, Activation. |

### 12.4 Essais réalisés (2026-11-16, poste de développement)

- De bout en bout sur une **copie jetable** de `pharmacie_test` (base d'essai supprimée ensuite) : activation (mauvais code refusé, code réutilisé refusé), code officine adopté par le serveur central, 718 lignes en 4 lots accusés, patients sous code, suppression propagée, réactivation par un nouveau code, certificat révoqué → envoi refusé (401) et lot gardé en attente.
- **Test de charge** (`tests/Pharma.Cloud.Charge`, lancé à la main ; client et serveur sur le même PC, PostgreSQL 18 local) : 200 officines × 5 lots × 200 lignes : 310 lots/s, 62 059 lignes/s, médiane 52 ms par dépôt ; 1 000 officines × 5 × 200 : 342 lots/s, 68 320 lignes/s, 95 % sous 155 ms, 1 échec de connexion TLS sur 1 000 ; 5 000 officines × 2 × 200 : 304 lots/s, 60 706 lignes/s, 3 échecs de connexion TLS sur 5 000 (cause non examinée **[À VÉRIFIER]**). Place : environ 275 Ko pour 1 000 lignes « vente » par officine (lot brut compris). L'objectif de 60 M lignes/jour représente environ 700 lignes/s en moyenne ; les pointes, la volumétrie disque et la durée de conservation des lots bruts restent **[À VÉRIFIER / DÉCISION]**.

### 12.5 Reste à faire

- **Chargement initial** (première émission de l'existant) : **non codé** (S6). Conception : à l'activation, inscrire dans la file une ligne `creation` par ligne existante de chaque table envoyée (même minimisation que le déclencheur), dans un instantané pris **après** la pose des déclencheurs et sous un verrou court qui ordonne l'instantané avant les modifications suivantes (sinon un état ancien pourrait recevoir un n° plus grand qu'une modification plus récente) ; pour les chaînes, depuis le rang 1. Durée et verrou à mesurer sur une vraie base.
- Purge de `sync_sortie` après accusé et délai de garde ; bandeau poste et alertes (S3) ; vérification des chaînes au central (S4) ; ordres descendants (S5) ; nouvelle époque après restauration, bascule (S6) ; écrans à distance (S7).
- Tables miroir des tables **descendantes** : non créées (référentiels diffusés par le central, S5).
- Changement de **type** d'une colonne envoyée : script central à part (le générateur n'ajoute que des colonnes).
- Hébergement réel, certificat HTTPS en production, sauvegarde de la base centrale : **[À VÉRIFIER : hébergeur]**.

### 12.6 Corrections après revue de code et audit de sécurité (2026-11-16)

- Renouvellement borné (60 derniers jours, depuis le certificat le plus récent, 1/jour), successeurs chaînés et révoqués en cascade, révocation par serveur ; quota d'octets par officine et par jour (`officine_volume_jour`, `cloud_parametre`) ; limites de débit par certificat vérifié, globale par adresse, corps limité hors dépôt ; erreurs génériques au client ; refus journalisés une fois par heure ; dossier de la clé de l'officine à accès restreint ; identifiants du schéma miroir validés et entre guillemets.
- Officine : verrou unique (`VerrouSync`) ; reprise automatique d'un trou côté central ; contrôle de l'accusé ; erreurs imprévues notées sans détail ; refus définitif sans renvoi en boucle ; garde `pg_is_in_recovery()` ; réglages dans une transaction ; client HTTP réutilisé par certificat.
- Suppression au central : clé relue depuis l'id `sync-v1` (format S1 inchangé, valeurs jointes par « | ») et convertie au type de la colonne ; pour une clé composée, l'id reconstitué doit être identique : une valeur contenant « | » ne peut jamais retirer une autre ligne (au pire la suppression n'est pas appliquée).
- **Défaut de `officine_id` non remplacé par une constante** : mesuré sur 200 000 insertions, la fonction coûte environ 0,4 s de plus (≈ 2 µs par ligne : 88 ms avec une constante, 430 à 530 ms avec la fonction). Une constante inscrirait le code d'une officine dans le schéma : `init/schema.sql` diffèrerait d'une base à l'autre (comparaison init en échec) et une installation neuve hériterait du code de la base de référence. Gardé tel quel, à reconsidérer si un import massif le justifie **[DÉCISION]**.
- Non traité, décision du titulaire en attente : noms du personnel et coordonnées des fournisseurs et de la pharmacie envoyés au central.
