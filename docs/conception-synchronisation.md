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

- **Chargement initial** : **réalisé au lot S6** (§ 13, ordre garanti autrement que par le verrou long décrit ici). Conception : à l'activation, inscrire dans la file une ligne `creation` par ligne existante de chaque table envoyée (même minimisation que le déclencheur), dans un instantané pris **après** la pose des déclencheurs et sous un verrou court qui ordonne l'instantané avant les modifications suivantes (sinon un état ancien pourrait recevoir un n° plus grand qu'une modification plus récente) ; pour les chaînes, depuis le rang 1. Durée et verrou à mesurer sur une vraie base.
- Purge de `sync_sortie` après accusé et délai de garde ; bandeau poste et alertes (S3) ; vérification des chaînes au central (S4) ; ordres descendants (S5) ; nouvelle époque après restauration, bascule (S6) ; écrans à distance (S7).
- Tables miroir des tables **descendantes** : non créées (référentiels diffusés par le central, S5).
- Changement de **type** d'une colonne envoyée : script central à part (le générateur n'ajoute que des colonnes).
- Hébergement réel, certificat HTTPS en production, sauvegarde de la base centrale : **[À VÉRIFIER : hébergeur]**.

### 12.6 Corrections après revue de code et audit de sécurité (2026-11-16)

- Renouvellement borné (60 derniers jours, depuis le certificat le plus récent, 1/jour), successeurs chaînés et révoqués en cascade, révocation par serveur ; quota d'octets par officine et par jour (`officine_volume_jour`, `cloud_parametre`) ; limites de débit par certificat vérifié, globale par adresse, corps limité hors dépôt ; erreurs génériques au client ; refus journalisés une fois par heure ; dossier de la clé de l'officine à accès restreint ; identifiants du schéma miroir validés et entre guillemets.
- Officine : verrou unique (`VerrouSync`) ; reprise automatique d'un trou côté central ; contrôle de l'accusé ; erreurs imprévues notées sans détail ; refus définitif sans renvoi en boucle ; garde `pg_is_in_recovery()` ; réglages dans une transaction ; client HTTP réutilisé par certificat.
- Suppression au central : clé relue depuis l'id `sync-v1` (format S1 inchangé, valeurs jointes par « | ») et convertie au type de la colonne ; pour une clé composée, l'id reconstitué doit être identique : une valeur contenant « | » ne peut jamais retirer une autre ligne (au pire la suppression n'est pas appliquée).
- **Défaut de `officine_id` non remplacé par une constante** : mesuré sur 200 000 insertions, la fonction coûte environ 0,4 s de plus (≈ 2 µs par ligne : 88 ms avec une constante, 430 à 530 ms avec la fonction). Une constante inscrirait le code d'une officine dans le schéma : `init/schema.sql` diffèrerait d'une base à l'autre (comparaison init en échec) et une installation neuve hériterait du code de la base de référence. Gardé tel quel, à reconsidérer si un import massif le justifie **[DÉCISION]**.
- Noms du personnel et coordonnées des fournisseurs et de la pharmacie : **décidé et réalisé le 2026-11-16** (§ 14, familles nominatives réglables).

## 13. Lot S6 : réalisé (2026-11-16, GO du titulaire) — chargement complet

### 13.1 Ordre entre l'instantané et les modifications simultanées (risque du § 12.5)

Retenu : **instantané en deux transactions REPEATABLE READ, repère de n° de file, même époque**.

1. Transaction courte validée : chargement en état « constitution » (`sync_enregistrer_lot` refuse alors tout nouveau lot) et codes
   patients/porteurs créés d'avance (l'instantané n'a presque jamais à en créer, ce qui évite les conflits de sérialisation).
2. **A** : `LOCK sync_sortie IN SHARE MODE` (attente plafonnée à 2 s, `lock_timeout`), puis image exportée (`pg_export_snapshot()`)
   et **repère** = plus grand n° de file visible. Le verrou attend la fin des transactions qui ont déjà inscrit une ligne de file et retient
   les suivantes avant leur insertion (le verrou de table est pris avant l'attribution du n°). Donc : toute modification **visible** dans
   l'image a un n° ≤ repère ; toute modification **non visible** reçoit un n° > repère.
3. **B** importe l'image (`SET TRANSACTION SNAPSHOT`), **A est validée aussitôt** (le verrou ne dure que l'attente des transactions en
   cours : les ventes ne sont pas bloquées pendant la lecture des tables), puis B inscrit pour chaque ligne de chaque table envoyée une
   ligne `instantane` minimisée par `sync_minimiser` (exactement comme le déclencheur), **ordre = repère**, n° de chargement.
4. Les lots sont formés par **(ordre, n°)** : modifications ≤ repère, puis instantané, puis modifications > repère. Le n° envoyé d'une ligne
   d'instantané est le repère : au central, « plus grand (époque, n°) gagne » donne toujours l'état le plus récent, et une suppression
   postérieure ne peut pas être suivie d'une résurrection par l'instantané (elle part après lui).

Écartés : un seul gros verrou pendant toute la lecture (ventes bloquées plusieurs minutes) ; une nouvelle époque à chaque chargement (les
lignes inscrites par une transaction ayant lu l'ancienne époque juste avant le changement seraient restées hors de toute époque envoyée) ;
numéroter l'instantané avec ses propres n° d'identité (une modification non visible pouvait recevoir un n° plus petit qu'une ligne
d'instantané plus ancienne : inversion).

### 13.2 Étapes et reprise

| Étape (`sync_chargement.etat`) | Fait | Coupure |
|---|---|---|
| demande | `sync_demander_chargement` : activation (première), réactivation, bouton « Recharger toutes les données », lots perdus par le central | — |
| constitution | instantané (une transaction) | annulé en entier, repris au passage suivant (nouveau repère) ; base occupée : reporté, erreur non bloquante |
| pret → annonce | `POST /api/v1/chargement/debut` (n°, époque, repère, dernier lot accusé ici, tables) **avant** le premier lot | idempotent |
| annonce | lots ordinaires (500 lignes / 1 Mo, 20 lots par passage), renvoyés à l'identique après coupure | reprise au premier lot non accusé |
| termine | quand toutes les lignes d'instantané sont dans des lots accusés : `POST /api/v1/chargement/fin` | idempotent |

Un seul chargement en cours (`ux_sync_chargement_en_cours`). Une demande pendant l'envoi d'un instantané est refusée ; une réactivation ou
des lots manquants **remplacent** le chargement en cours (état `remplace`, instantané périmé).

### 13.3 Serveur central (`database/cloud/003_chargement.sql`)

- Table `chargement` (RLS), `cloud_debut_chargement`, `cloud_fin_chargement` ; `cloud_appliquer_lot` remplacée : ligne `instantane`
  appliquée comme une création et marquée `sync_chargement` = n° du chargement en cours **si son n° est le repère annoncé** (une ligne
  d'un chargement remplacé ne compte pas), même si une version plus récente est déjà là ; colonnes `sync_chargement` et
  `sync_absente_le` ajoutées par le générateur des tables miroir (`002` régénéré).
- **Lots perdus** : au début, si le dernier lot accusé de l'officine dépasse le dernier lot reçu, le n° attendu saute (lots abandonnés,
  journal `lots_abandonnes`) ; le chargement remplace leur contenu. Remplace la procédure manuelle « lots manquants » du lot S2.
- **Fin** : toute ligne non vue par le chargement et dont (époque, n°) ≤ (époque, repère) est marquée `sync_absente_le` — **jamais
  effacée** (décision 8) ; compte par table renvoyé à l'officine et affiché ; journal `chargement_absentes`. Une ligne reçue de nouveau
  perd sa marque. Lignes créées après le repère : jamais signalées.
- Contrat `sync-v1` : opération `instantane` ajoutée (champ existant, liste des valeurs élargie ; aucun champ ni colonne envoyée changés).

### 13.4 Officine et écran

Script `129_synchronisation_chargement.sql` (`sync_chargement`, classée jamais ; `sync_sortie.ordre` et `.chargement` ; fonctions
`sync_demander_chargement`, `sync_chargement_preparer`, `sync_instantane_table`, `sync_chargement_pret`, `_annonce`, `_accuse`,
`_terminer`, `_progression`, `sync_tables_envoyees` ; `sync_enregistrer_lot` refuse pendant la constitution). Application :
`ChargementCompletSync` (appelé par `EnvoyerLotsSync` sous `VerrouSync`), `RechargerSync` ; `ChargementSyncSql`. Écran
« Admin › Synchronisation › État » : dernier chargement, progression (tables pendant la préparation, puis lignes accusées), lignes absentes
signalées, bouton « Recharger toutes les données » avec confirmation (droit `admin.parametres`, journalisé).

### 13.5 Essais (2026-11-16, copie jetable de `pharmacie_test`, supprimée)

- Activation → chargement 34 025 lignes (100 tables) pendant 4 000 modifications simultanées (stock minimum de 300 produits, emplacements
  créés puis supprimés) et une **coupure du serveur central** au milieu : 0 écart de nombre de lignes sur les 100 tables, `produit`
  identique valeur par valeur, emplacements supprimés absents au central, aucun nom de patient.
- Lots perdus par le central (n° ramené de 77 à 40, lots 1 à 60 retirés de la copie) + une vente fantôme au central : rechargement
  demandé seul, 37 lots abandonnés, vente fantôme signalée absente et gardée ; puis « Recharger toutes les données » (2e demande refusée
  pendant l'envoi) : terminé, 1 absente.
- La limite de 60 lots/min par certificat ralentit un gros chargement (429, nouvel essai) : ~10 000 lignes/min.

### 13.6 Reste à faire

- **Restauration de la base de l'officine** (n° de lots revenus en arrière) : nouvelle époque + chargement, non réalisé.
- Gros volumes : durée de l'instantané et taille de la transaction à mesurer sur une vraie base **[À VÉRIFIER]** ; débit plafonné par la
  limite de dépôts et le quota journalier.
- Purge de `sync_sortie` après accusé (S3) ; écran central de consultation des lignes absentes (S7).
- Sous-rubrique propre « Chargement » de l'écran : demande une modification de `NavAdmin.razor` (non touché ici).

## 14. Réglages du serveur central (2026-11-16, GO du titulaire)

### 14.1 Données nominatives envoyées (familles)

Décision du titulaire : quatre **familles** réglables par case à cocher au serveur central, valeur globale + surcharge par officine.

| Famille | Colonnes | Défaut |
|---|---|---|
| `employes` | `utilisateur.nom_complet`, `utilisateur.identifiant` | **non** |
| `pharmacie` | `pharmacie.titulaire`, `.adresse`, `.telephone`, `parametre_notification.smtp_expediteur` | oui |
| `fournisseurs` | `fournisseur.telephone`, `.email`, `.adresse` | oui |
| `courriel_commandes` | `commande_fournisseur.envoyee_email_a` | oui |
| `pharmacie` (complément, script 133 / central 007) | `pharmacie.nom`, `.nif`, `.nis`, `.rc`, `.article_imposition` (identifiants fiscaux de l'officine, l'article d'imposition étant du même ordre que NIF, NIS et RC) | oui |
| `banque` (script 133 / central 007, élargie : script 135 / central 008) | `compte_tresorerie.numero_compte`, `.banque`, `pret_bancaire.preteur`, `.reference`, `reglement_fournisseur.banque`, `.reference`, `paiement_effet.reference`, `remise_cheques.reference` (aucune dans une clé) | **non** |
| `clients_factures` (script 133 / central 007) | `client_facture.adresse` (seule colonne de contact du client facturé) | **non** |

Colonnes nominatives du personnel cherchées dans toutes les tables envoyées (`002_tables_miroir.sql`) : seules ces deux-là ; `utilisateur.telephone`
et `.email` sont déjà exclues (script 126) ; ailleurs le personnel n'est désigné que par son id (`utilisateur_id`, `traitee_par`, `vu_par`…).
**Code d'un employé = son id local**, gardé : il est déjà la clé de la ligne au central (le changer casserait les liens de toutes les tables) et
ne désigne personne sans la base de l'officine ; un pseudonyme en plus n'apporterait rien. Le rôle (`role_id`) et les colonnes non nominatives partent.
Décision du titulaire (2026-11-16) : `parametre_formulaire_sms.texte` reste toujours envoyé. Colonnes bancaires du même ordre ajoutées le 2026-11-16
(« ok banque », scripts 135 / 008) ; `correspondance_echeancier_preteur.preteur` reste envoyée : c'est la clé de sa ligne (non retirable). Détail : synchronisation-inventaire.md.

**Où filtrer — retenu : à la constitution des lots** (`sync_retirer_familles` dans la lecture des lignes en attente, script 130), et non dans
`sync_minimiser` (déclencheur) : la file `sync_sortie` est en ajout seul (garde du script 126), une ligne déjà inscrite ne peut pas être réécrite ;
filtrer à la lecture applique le réglage **du moment** à tout ce qui n'est pas encore parti, y compris les lignes inscrites avant le changement
et l'instantané d'un chargement complet (S6). La ligne complète reste dans la file, donc dans l'officine.

- **Réglage descendu dans la base locale** (remplace le 2026-11-16 la lecture par `GET /api/v1/reglages`, retirée) : le réglage choisi au central
  est enregistré en base centrale et **descend** par la voie descendante (§ 15) dans la table locale `reglage_central` (clé `familles_envoyees`,
  version). L'API locale le lit **uniquement** dans sa base (`sync_familles_effectives()`) : aucun appel au central pour l'obtenir, central en
  panne = dernière version appliquée, rien bloqué. **Jamais reçu : aucune famille** (rien de nominatif).
- **Lot déjà formé** : `sync_lot.familles` garde les familles appliquées (renvoi à l'identique). Si l'une a été décochée depuis et que le lot n'est
  pas accusé, il est **reformé** sans elle avant l'envoi (`sync_reformer_lot` ; la garde de `sync_lot` ne permet que cette réduction stricte,
  jamais un élargissement). Si le central avait déjà reçu la forme d'origine (accusé perdu), il répond « autre empreinte » : le lot est tenu pour
  accusé (n° propre à l'officine, rien d'autre ne peut l'avoir envoyé) ; une ligne `lot_anomalie` reste au journal central.
- **Constitution** : `sync_enregistrer_lot` (5 arguments) refuse si le réglage a changé entre la lecture des lignes et l'enregistrement
  (reprise au passage suivant).
- **Double sécurité au central** : `cloud_appliquer_lot` (script 004) retire **avant toute écriture** les colonnes d'une famille non autorisée
  pour l'officine du certificat (tables miroir **et** lot brut gardé, marqué `expurge_le` / `colonnes_effacees` ; l'empreinte reçue est gardée
  pour l'idempotence, elle ne couvre plus le contenu) ; journal `familles_effacees_reception` une fois par jour et par officine.
- **Décocher ensuite** ne supprime rien de ce qui a déjà été reçu (une mise à jour de la ligne vide la colonne au central) ; effacement par
  commande séparée `--effacer-famille` : simulation par défaut, `--confirmer <famille>` exigé, refus si la famille est encore envoyée, journal
  `famille_effacee`. **Recocher** ne renvoie pas les valeurs déjà retirées : prochaine modification ou « Recharger toutes les données ».
- Écran de l'officine « Admin › Synchronisation › Réglages » : familles envoyées, date de réception et version, **lecture seule**.

### 14.2 Conservation des lots bruts (`lot_recu`)

Durée globale (`cloud_parametre.conservation_lots_ans`) et par officine (`officine.conservation_lots_ans`) : **6 ans par défaut et minimum**
(contrainte et refus de `cloud_regler_conservation`), pas de maximum. Effacement codé (`cloud_purger_lots`) mais **désactivé**
(`purge_lots_active = false`) ; activé seulement par `--purge-lots activer` ; chaque effacement est journalisé (`lots_purges` : officine,
nombre de lots, première et dernière date de réception, durée).

**Retenu : fonction dédiée contrôlée, ligne par ligne, puis suppression des partitions vides.** Les partitions mensuelles sont communes à
toutes les officines et la durée est propre à chacune : détacher une partition entière n'est possible que si toutes ses lignes ont dépassé la
durée de leur officine, ce que seule une lecture de la partition établit ; le plus simple et sûr est d'effacer les lignes expirées (par officine)
puis de supprimer les partitions **vides** entièrement plus anciennes que 6 ans (`partition_supprimee`). Le déclencheur `cloud_lot_recu_garde`
remplace « ajout seul » mais refuse toujours toute modification ou suppression, sauf : suppression d'un lot dont la durée de **son** officine
est dépassée, effacement activé **et** opération posée par la fonction ; retrait de colonnes nominatives par `cloud_effacer_famille` (le contenu
nouveau doit être exactement l'ancien moins les colonnes listées). Le rôle applicatif n'a ni UPDATE ni DELETE sur `lot_recu`.
Effacement lancé par l'outil d'administration (tâche planifiée `--purger-lots`), pas par l'API : moindre privilège.
Non couvert par un déclencheur : `DROP`/`TRUNCATE` d'une partition par le propriétaire (droit d'administrateur de la base) **[À VÉRIFIER :
séparation des comptes à l'hébergement]**.

### 14.3 Essais (2026-11-16, copie jetable de `pharmacie_test`, supprimée)

De bout en bout (API officine sur 5181, serveur central sur 5090, base centrale recréée ensuite) : avant le premier passage, réglage
inconnu ; activation puis chargement complet (69 lots) : 9 employés au central sans nom ni identifiant (rôle présent), coordonnées des
fournisseurs et de la pharmacie présentes, aucun nom d'employé dans les lots bruts ; « fournisseurs » décochée pour l'officine : ligne
déjà en file envoyée sans téléphone ; `--effacer-famille` : simulation sans effet, mauvaise confirmation refusée, puis 10 + 7 + 5 valeurs et
1 lot brut effacés, refus pour une famille encore envoyée ; central coupé, lot formé avec les fournisseurs, famille décochée entre-temps :
lot reformé et accepté sans téléphone (empreinte identique des deux côtés) ; conservation à 5 ans refusée ; `--purger-lots` sans effet
(désactivé). Tests SQL `130_synchronisation_familles_nominatives_tests.sql` et `cloud/tests/004_reglages_tests.sql`,
tests `EnvoiSynchronisationTests` (réglage jamais lu, ligne déjà en file, lot reformé, lot reformé déjà reçu) et `FamillesNominativesTests`.

### 14.4 Reste à faire

- Durées réglementaires de conservation (registre des psychotropes) avant d'activer l'effacement **[À VÉRIFIER]**.
- Écran central de réglage (S7) : aujourd'hui commandes de l'outil d'administration seulement.
- Données de référence de synchronisation dans `database/init/seed_referentiel.sql` : **corrigé le 2026-11-16** (`sync_table` des scripts 126 à 131,
  ligne `sync_parametre`, `sync_famille_colonne` ; test `Le_seed_contient_le_classement_de_synchronisation_des_scripts`).

## 15. Voie descendante (2026-11-16, GO du titulaire « réglages descendants », première brique de S3)

**Principe** : ce que décide le serveur central est enregistré en base centrale et **descend** dans la base de l'officine ; l'API locale ne lit
que sa base. C'est toujours l'officine qui initie (aucune connexion vers l'officine) ; une panne du central ne bloque rien localement.

**Format (contrat `desc-v1`, `Pharma.Shared.ChangementsDescendantsDto`)** : `{ contrat, versionMax, changements: [{ table, cle, version,
donneesJson }] }`. Générique : table locale de destination, clé, version, objet JSON ; réutilisable en S3 (prix, réglages, commandes en brouillon :
une table locale = un cas de `sync_appliquer_descendant`). Un autre contrat est ignoré par l'officine (journalisé).

| Élément | Central (`database/cloud/005_descendant.sql`) | Officine (`131_synchronisation_descendant.sql`) |
|---|---|---|
| Stockage | `descendant` (officine, table, clé, version, données ; RLS) ; `officine_descendant` (version max, version acquittée) | `reglage_central` (clé, version, valeur ; **classée descendante**) ; `sync_parametre.descendant_version` |
| Version | **monotone par officine** ; nouvelle version seulement si les données changent | jamais écrasée par une version plus ancienne ou égale ; table non prévue refusée |
| Publication | à chaque échange, le réglage effectif (global + surcharge, script 004) est comparé au dernier publié (`cloud_descendants`) | — |
| Transport | **réponse au dépôt d'un lot** (`AccuseLotDto.Descendants`, si l'en-tête `X-Sync-Descendants` donne la version appliquée) et **échange léger** `POST /api/v1/echange` à chaque passage (chaque minute), même sans rien à envoyer | `EnvoyerLotsSync` : échange en tête de passage, puis éléments portés par chaque accusé |
| Acquittement | la version donnée par l'officine (`version_acquittee`) ; seules les versions plus grandes sont renvoyées | la plus grande version appliquée est redonnée à l'échange suivant |

Jeton : la réponse de `POST /api/v1/jeton` ne porte pas d'éléments (le jeton est obtenu à l'intérieur du client HTTP) ; l'échange léger de chaque
passage joue ce rôle. Le central continue d'effacer à la réception ce qui n'est pas autorisé (§ 14.1).

**Essais (2026-11-16, copie jetable de `pharmacie_test`, supprimée)** : activation → réglage version 1 descendu au premier passage, chargement
complet sans nom d'employé ; famille décochée au central → version 2 descendue par un passage **sans rien à envoyer** ; **central coupé** : réglage
local gardé (version 2), lot formé sans le téléphone du fournisseur modifié, réponse 502 avec message ; central relancé : lot accepté sans
téléphone. Installation neuve depuis `database/init` : 0 table non classée, activation possible (transaction annulée).

**Réponses d'erreur « vides » vues pendant l'essai précédent** : examinées le 2026-11-16 : l'API renvoyait bien un ProblemDetails avec un message
(502 « Serveur central injoignable : … », 409 passage simultané). Le vide venait de l'outil d'essai (Windows PowerShell 5.1 ne remplit pas le détail
d'erreur pour `application/problem+json`) ; le poste lit le champ `detail`. Message du 409 précisé (« passage automatique ou autre poste :
réessayez dans un instant »).

## 16. Mutualisation des réglages (prévue en S3, décision du titulaire 2026-11-16)

**Principe** : sans serveur central, tous les réglages se font dans les écrans d'administration de l'officine (aucun script). Avec le serveur
central, un **modèle central par table** ; chaque réglage est marqué **« imposé »** (affiché grisé dans l'écran de l'officine avec la mention
« fixé par le serveur central ») ou **« proposé »** (modifiable localement). Les écrans locaux sont conservés. Transport : la voie descendante (§ 15).
Rien n'est codé ; les classements de `sync_table` ne changent pas maintenant.

- **Groupe 1, référentiels nationaux** (descendants, le central fait foi, diffusion S5) : `wilaya`, `organisme`, `centre_payeur`,
  `type_prestation`, `categorie_produit`, `classe_suivie`, `classe_suivie_dci`, `parametre_gs1_ai`.
- **Groupe 2, mutualisables avec imposé / proposé** : `parametre_fiscal` (imposé), `regle_alerte_psychotrope` (imposé ou proposé : à décider),
  `parametre_tracabilite` (imposé), `parametre_serialisation` (imposé), `parametre_substitution` (proposé), `motif_reglable` (proposé + ajouts
  locaux), `parametre_formulaire_sms` (proposé), `parametre_surveillance` (proposé), `parametre_alerte` (proposé), `parametre_inventaire`
  (proposé), `remise_automatique` (proposé). « Imposé / proposé » est une proposition : obligations légales à confirmer **[À VÉRIFIER]**
  (seuils psychotropes, TVA).
- **Qui décide « imposé » ou « proposé »** (décision du titulaire 2026-11-16) : l'**administrateur du serveur central**, dans son
  administration ; le choix est enregistré au central et descend vers les officines par la voie descendante (§ 15), comme les autres
  réglages. Rien n'est figé dans le logiciel : les classements du groupe 2 ci-dessus ne sont que des **valeurs de départ proposées**,
  y compris pour `regle_alerte_psychotrope` et `parametre_fiscal`. Le groupe 3 n'est jamais proposé à la mutualisation.
  **[À PRÉCISER en S3]** : granularité du choix (par table ou par réglage), traçabilité du changement (qui, quand, avant/après) et
  effet d'un passage de « proposé » à « imposé » sur une valeur déjà modifiée localement.
- **Groupe 3, strictement locaux, jamais mutualisés** : `parametre_caisse`, `parametre_sauvegarde`, `parametre_notification`,
  `parametre_rapport`, `serveur_officine`, `compteur_ticket`, `chaine_integrite`, `parametre_integrite`.

## 17. Débit pendant un chargement complet (2026-11-16, GO du titulaire)

- **Limite de dépôts** : 60 lots/min par certificat en temps normal (`Cloud:LotsParMinute`), **600/min** (`Cloud:LotsParMinuteChargement`)
  tant que l'officine a un chargement annoncé et non terminé. L'API lit l'état en base (`cloud_chargement_en_cours`, script central 006)
  avant la limite de débit, le garde 30 s par certificat (`SuiviChargementCentral`) et l'efface au début et à la fin du chargement ; seau
  distinct, la limite globale par adresse (600/min) reste.
- **Quota d'octets — retenu : plafond par chargement à la place du quota du jour.** Le quota du jour (512 Mio) protège contre un flux anormal
  au quotidien ; un chargement complet est un envoi unique et borné par la taille de l'officine : s'il comptait dans le quota du jour, une
  grosse officine serait arrêtée à mi-chemin et reprendrait le lendemain. Pendant un chargement, ses lots comptent dans
  `chargement.octets_recus`, plafonné par `cloud_parametre.quota_chargement_octets` (8 Gio par défaut, réglable) : un chargement anormal reste
  borné (429, nouvel essai), et le quota du jour n'est pas entamé.
- **Compatibilité de contrat** : voir mise-en-production.md, « Procédure d'activation des officines » (une seule version acceptée aujourd'hui).

## 18. Corrections après revue et audit de sécurité (2026-11-16, scripts officine 136 et central 009)

Chaque point est rejouable (scripts idempotents) ; le script 130 rejoué seul après 131 ne fait plus rien (garde en tête de fichier).

- **Colonnes figées par lot** (`sync_lot.colonnes_retirees`, « table.colonne ») : constituées avec les lignes, elles servent au renvoi à l'identique ;
  une colonne ajoutée ensuite à une famille ne rend plus un lot non renvoyable. La reforme d'un lot non accusé ne peut que retirer davantage
  (garde `sync_lot_garde`). Lots antérieurs (colonne NULL) : liste reconstituée depuis leurs familles (`sync_colonnes_hors_familles`), `{}` si les
  familles sont NULL (avant 130) ; ils sont toujours renvoyés **filtrés** par la liste en vigueur (jamais l'empreinte stockée comme seule référence).
- **Reprise des lots manquants** : `LotEnregistre` expose `Accuse` et `ReformeLe`. Un lot accusé renvoyé après un trou du central n'est jamais
  reformé en base (`sync_reformer_lot_colonnes` refuse un lot accusé) : son contenu filtré est envoyé tel quel ; le central l'accepte comme lot manquant
  (il n'a pas ce numéro, aucune empreinte à comparer). Si le central l'a encore avec une autre empreinte, 409 : le lot est tenu pour reçu
  (numéro propre à l'officine). Lot reformé dont l'accusé s'est perdu : décision par `reforme_le IS NOT NULL`.
- **Voie descendante** : `sync_recevoir_descendant` renvoie un statut (`applique`, `ancien`, `table_inconnue`, `refuse`), n'avance pas la version
  acquittée (`sync_noter_version_descendante`, monotone), refuse une version au-delà de la version maximale annoncée, un saut supérieur à
  `descendant_saut_max` (1000) et une donnée de plus de 64 Kio ; journal local `sync_journal_descendant` (sans donnée personnelle ; familles avant / après).
  Côté application, la version acquittée s'arrête avant le premier élément ignoré ou refusé (redonné à chaque passage) et une erreur d'application n'arrête
  pas l'envoi des lots. Le client HTTP borne la réponse (1 Mio), le nombre d'éléments (1000) et la taille d'une donnée (64 Kio).
- **Central restauré** (`cloud_descendants`) : version annoncée > version maximale (et <= 10^12) : la version maximale est remontée, les éléments de l'officine
  sont republiés au-dessus, événement `descendant_remonte`.
- **Plafonds de chargement** (central 009) : `chargements_max_24h` (5) et `quota_chargements_octets_24h` (16 Gio) par officine sur 24 h glissantes, tous chargements
  (remplacés compris), en plus du plafond par chargement (8 Gio). Refus journalisé `quota_depasse` ; 429 au début d'un chargement.
- **`cloud_chargement_en_cours`** : le `set_config('app.officine_id')` est conservé. La table `chargement` est sous sécurité par ligne forcée, y compris pour
  le propriétaire de la fonction : sans lui la fonction ne voit jamais le chargement (test central 009). Non retiré malgré la demande.
- **Lots reçus** : le rôle applicatif n'a ni UPDATE, ni DELETE, ni TRUNCATE sur `lot_recu` et ses partitions (test central 009).
- **Contenance** (script 136, fonction `contenance_depuis_conditionnement`) : motif sans dépendance à la locale (classes `[EÉeé]` écrites en entier) ; le test
  C# lit ce motif. Nouveau passage borné aux produits encore sans contenance, journal d'audit comme au script 134 (aucune écriture en double au rejeu).
  La contenance reste figée dans le produit (décision du titulaire).
- **Non fait** : lecture en base de `SuiviChargementCentral` avant la limite de débit (point facultatif, la valeur est déjà gardée 30 s par certificat).
