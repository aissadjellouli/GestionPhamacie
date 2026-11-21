# Conception : synchronisation officine ↔ serveur central (lot 12)

> **Statut : conception (GO « go conception sync », 2026-11-16) ; lot S1 réalisé (§ 11) ; lot S2 réalisé (2026-11-16, § 12)** : base centrale commune cloisonnée, autorité de certification, API centrale (activation, jeton, dépôt, accusé), envoi depuis l'officine, écran « Admin › Synchronisation » ; **lot S6 (chargement complet, § 13), réglages descendants (§ 14, § 15) et corrections (§ 18) réalisés ; lot S3 réalisé le 2026-11-16 pour la supervision et la purge (§ 20 : purge de `sync_sortie`, bandeau des postes, alertes au titulaire, écran Supervision et purge)**. Serveur central **local** pour l'instant (`pharmacie_cloud_dev`, port 5090), aucune connexion vers l'extérieur. **Restent : la mutualisation imposé / proposé des réglages (§ 16, attend des décisions du titulaire), les ordres descendants (S5), l'intégrité au central (S4), les écrans à distance (S7).** ; **lot S4 réalisé le 2026-11-17 (§ 22 : vérification d'intégrité au serveur central, ajout seul des journaux reçus)** ; **lots S5.1 et S5.5 réalisés le 2026-11-18 (§ 24 : ordres du central, employés et droits, confirmation du titulaire)** ; **lot S5.2 réalisé le 2026-11-18 (§ 25 : prix d'un produit, confirmation au-delà de 20 %)** ; **lot S5.3 réalisé le 2026-11-18 (§ 26 : nomenclature nationale envoyée par le central)** ; **lot S5.4 réalisé le 2026-11-18 (§ 27 : commandes en brouillon envoyées par le central)** ; **référentiels (antibiotiques suivis, organismes et centres payeurs) réalisés le 2026-11-18 (§ 28)** ; **lot S7.1 réalisé en local le 2026-11-21 (§ 32 : écrans de l'éditeur, [ecrans-central.md](ecrans-central.md))**
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

**Modification des deux côtés** : l'officine gagne (elle a déjà servi des clients avec sa valeur). L'ordre distant est refusé, tracé, et visible à distance. **Délai d'expiration** des ordres non appliqués : **7 jours** (décision S5-D1 du 2026-11-18, § 23).

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
- **Comptes distants** (titulaire, multi-officines, éditeur) : authentification forte (mot de passe + code SMS, comme le super admin) ; droits limités à leurs officines ; l'éditeur (support) n'a **jamais** accès aux données nominatives (**corrigé le 2026-11-21**, décision n° 4 du § 6.2 : la mention antérieure « sans accord tracé » est abandonnée ; écrans de l'éditeur limités à l'état technique, § 32).
- **Journal des accès** côté cloud : qui, quoi, quand, quelle officine ; ajout seul ; consultable par le titulaire.
- **Minimisation (loi 18-07)** : voir § 2 ; journaux techniques sans données personnelles (règle du lot 1 reprise).
- **Lot 9 (non lancé)** : points d'insertion possibles, sans décision — chiffrement des colonnes sensibles avant mise en outbox, chiffrement au repos de la base centrale, chiffrement des sauvegardes cloud. **Option C retenue par le titulaire le 2026-11-17** (copie chiffrée des données nominatives au central, ouverture à deux titulaire + éditeur) : conception et décisions dans [chiffrement-data.md](chiffrement-data.md), non lancée.
### 6.1 Protection de l'API cloud, couche par couche (ajouté le 2026-11-16 à la demande du titulaire)

Principe : défense en profondeur ; si une couche cède, les autres tiennent. L'API cloud n'est jamais sur le chemin d'une vente.

1. **Réseau** : hébergement en Algérie chez un hébergeur agréé ARPCE ; **seul le port 443** est exposé ; la base centrale n'est jamais exposée (réseau interne, seule l'API lui parle) ; pare-feu et filtrage anti-saturation (DDoS) de l'hébergeur **[À VÉRIFIER : offre de l'hébergeur]** ; limitation du débit des requêtes par officine, par compte et par adresse (essais de mots de passe en rafale).
2. **Transport** : HTTPS uniquement (TLS 1.2 minimum, 1.3 de préférence), aucun HTTP ; certificat client propre à chaque officine (mTLS, révocable pour une seule officine). **Épinglage** : l'API locale n'accepte que le certificat (ou l'autorité) du cloud attendu, et refuse tout autre serveur, même muni d'un certificat d'apparence valide.
3. **Pas de chiffrement « maison » par secret partagé** : HTTPS chiffre déjà tout le transit avec une clé négociée et jetée à chaque connexion (confidentialité persistante), vérifie l'identité du serveur et détecte toute altération. Un secret unique commun aux officines serait exposé d'un coup en cas de fuite, difficile à distribuer et à renouveler, et un chiffrement écrit à la main est la première source d'erreurs de sécurité.
4. **Identité de l'appelant : JWT** (voir 6.2).
5. **Droits et cloisonnement** : contrôle des actions permises (portées du jeton) ; officine du jeton = officine des données, vérifiée aussi dans la base (sécurité au niveau des lignes ou base/schéma par officine) ; ordres à distance limités à ceux décidés par le titulaire ; ventes, journal fiscal et registre en **ajout seul** dans le cloud.
6. **Signature des envois** : chaque lot montant est signé avec une clé propre à l'officine (paire de clés asymétriques, la clé privée ne quitte pas le serveur de l'officine) ; le cloud prouve l'origine et l'absence d'altération des ventes et du journal fiscal, en plus de la vérification de la chaîne (§ 5). Résistance au quantique (signature hybride ML-DSA + ECDSA, échange de clé TLS hybride) : [chiffrement-data.md](chiffrement-data.md) § 3 bis.
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
| S4 | Vérification d'intégrité côté cloud (chaînes, continuité, ancrages des sceaux) — **réalisé le 2026-11-17 (§ 22)** | S2 ; lots 2, 3, 8 (faits) |
| S5 | Ordres descendants (prix, réglages, nomenclature) appliqués par l'API locale, conflits, journalisation — **analysé, décisions S5-D1 à D5 du 2026-11-18 (§ 23) ; S5.1 (socle des ordres) et S5.5 (employés et droits) réalisés le 2026-11-18 (§ 24) ; S5.2 (prix) réalisé le 2026-11-18 (§ 25) ; S5.3 (nomenclature) réalisé le 2026-11-18 (§ 26) ; S5.4 (commandes en brouillon) réalisé le 2026-11-18 (§ 27) ; référentiels : antibiotiques suivis, organismes et centres payeurs, prestations, indications, DCI réalisés le 2026-11-18 (§ 28)** | S3 |
| S6 | Cas particuliers : chargement initial, bascule 2e serveur, restauration (époques), départ d'une officine | S3, S4 |
| S7 | **S7.1 réalisé le 2026-11-21 en local (§ 32 : écrans de l'éditeur, comptes distants, journal des accès)** ; S7.2 : écrans à distance du titulaire, multi-officines ; journal des accès (la page d'état de développement `http://127.0.0.1:5092` du 2026-11-21 n'est **pas** un écran S7 : outil de développement en lecture seule ; seul son rôle `pharma_cloud_etat`, script central 029, servira à S7) | S2, S5 |
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
- ~~Purge de `sync_sortie` après accusé et délai de garde ; bandeau poste et alertes (S3)~~ **réalisés (§ 20)** ; vérification des chaînes au central (S4) ; ordres descendants (S5) ; nouvelle époque après restauration, bascule (S6) ; écrans à distance (S7).
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

### 13.3 Serveur central (`database/cloud/003_chargement_complet_officine.sql`)

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

- ~~**Restauration de la base de l'officine** (n° de lots revenus en arrière) : nouvelle époque + chargement~~ **réalisé** (lot S6, § 30.5 à 30.8 ; intégrité après restauration : § 31).
- Gros volumes : durée de l'instantané et taille de la transaction à mesurer sur une vraie base **[À VÉRIFIER]** ; débit plafonné par la
  limite de dépôts et le quota journalier.
- ~~Purge de `sync_sortie` après accusé (S3)~~ **réalisée (§ 20)** ; écran central de consultation des lignes absentes (S7).
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
(désactivé). Tests SQL `130_synchronisation_familles_nominatives_tests.sql` et `cloud/tests/004_reglage_familles_envoyees_tests.sql`,
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

| Élément | Central (`database/cloud/005_voie_descendante_elements.sql`) | Officine (`131_synchronisation_descendant.sql`) |
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
  **Précisé par le titulaire le 2026-11-16** : (1) le choix se fait **par table** (tout un groupe de réglages imposé ou proposé
  ensemble) ; (2) chaque changement est **tracé au central et dans l'officine** (qui, quand, valeur avant / après) ; (3) au passage
  de « proposé » à « imposé », la valeur du central **remplace** la valeur modifiée localement, l'ancienne valeur est gardée au
  journal et le titulaire de l'officine est **prévenu** par message. Réalisation : à lancer (reste du lot S3).
- **Groupe 3, strictement locaux, jamais mutualisés** : `parametre_caisse`, `parametre_sauvegarde`, `parametre_notification`,
  `parametre_rapport`, `serveur_officine`, `compteur_ticket`, `chaine_integrite`, `parametre_integrite`.

## 16 bis. Mutualisation des réglages : conception détaillée (2026-11-16, « go mutualisation », reste du lot S3)

**Décisions D1 à D14 (§ 16 bis) : toutes les recommandations sont acceptées par le titulaire le 2026-11-16 (« ok recommandations mutualisation »).** Développement par lots M1 à M9.

**Statut : réalisé, lots M1 à M9 clos le 2026-11-17 (§ 16 bis.12 et § 16 bis.13).** Le texte qui suit est la conception d'origine ; les écarts retenus pendant la réalisation sont au § 16 bis.12. Les décisions du titulaire du § 16 et de `regles-metier.md` (2026-11-16) s'appliquent telles quelles.
Les points **[À DÉCIDER]** sont regroupés au § 16 bis.9 avec une recommandation ; les points **[À VÉRIFIER]** ne sont pas établis.
« Constaté » = lu dans le code ou les scripts à la date de cette conception.

### 16 bis.1 Résumé

- **Au central** : un **modèle par table** du groupe 2 (`cloud_reglage_modele`) : mode **imposé / proposé**, valeur (JSON, format `mutu-v1`),
  version du modèle, auteur. Tant que l'administrateur n'a **rien publié** pour une table, rien ne descend : l'officine reste maître de cette table.
  Chaque action de l'administrateur est inscrite dans un journal central en ajout seul (`cloud_journal_reglage` : qui, quand, avant / après).
- **Transport** : la voie descendante existante, **sans nouveau contrat** : un élément `reglage_central` par table, clé `mutu_<table>`
  (mêmes protections que § 15 et § 18 : version monotone, version maximale, saut borné, 64 Kio, élément inconnu ou mal formé journalisé, jamais bloquant).
- **À l'officine** : le modèle reçu est **rangé** (`reglage_central`) puis **appliqué en C#** par un cas d'usage qui passe la valeur par les
  **mêmes règles métier que la saisie locale** (validateurs extraits dans `Pharma.Domain`) ; une valeur invalide est **rejetée**, journalisée,
  signalée au central par l'état montant, et la synchronisation continue. L'état de chaque table est tenu dans `reglage_mutualise` (montante).
- **Table imposée** : refus côté serveur (exception métier, 409) **et** garde dans la base (déclencheur), grisé dans l'écran avec la mention
  « fixé par le serveur central ». Le passage proposé → imposé remplace la valeur locale, garde l'ancienne au journal et prévient le titulaire.
- **Sans serveur central** (synchronisation jamais activée ou désactivée) : mode effectif « local » pour les 11 tables, aucun changement de comportement.

### 16 bis.2 Constats dans le code (11 tables)

| Table | Forme | Écran local (constaté) | Route d'écriture | Contrôles métier (constatés) | Droit |
|---|---|---|---|---|---|
| `parametre_fiscal` | ligne unique (`id = 1`) : conservation, format, séparateur, fuseau | **aucun écran de modification** (lecture dans `JournalFiscal.razor`) | **aucune** | contraintes SQL seulement : conservation 6 à 30 ans, format `csv-v1`, séparateur `;` `,` tabulation | — |
| `regle_alerte_psychotrope` | 5 lignes à code fixe | `AdminPsychotropes.razor` | `PUT /api/psychotropes/regles` | `ParametrerAlertesPsychotropes` : code connu, seuil 1–1000, période 1–3650, unités 1–100 ou vide | titulaire |
| `parametre_tracabilite` | ligne unique : boîte obligatoire, patient obligatoire | **aucun écran de modification** (lu par la caisse et par le déclencheur du script 007) | **aucune** | contrainte SQL (`jamais`, `psychotrope`, `ordonnance`, `toujours`) | — |
| `parametre_serialisation` | ligne unique : actif, connecteur, factice hors ligne | `AdminSerialisation.razor` | `PUT /api/serialisation/parametres` | connecteur fixé à `factice` (contrainte SQL) | voir `SerialisationEndpoints` |
| `parametre_substitution` | ligne unique : équivalent exact seulement | `Achats › OngletSubstitution.razor` | `PUT /api/substitution/parametre` | aucun hors type | `admin.parametres` |
| `motif_reglable` | lignes, clé métier `(liste, code)` unique, id local | `AdminMotifs.razor` | `POST/PUT /api/motifs` | `EnregistrerMotifReglable` + `RegleMotifs` : liste connue, libellé 1–80 unique dans la liste (C# seulement), ajout seulement dans 8 listes, motif système jamais désactivé, effet du tiroir fixé à la création, **jamais de suppression** | `admin.parametres` |
| `parametre_formulaire_sms` | ligne unique : texte (1–4000 ou vide) | `AdminNotifications.razor` | `PUT /api/parametres/formulaire-sms` | `FormulaireAccordSmsPatient` | voir le cas d'usage |
| `parametre_surveillance` | ligne unique, 23 colonnes dont **horaires d'ouverture** | `AdminControleInterne.razor` | `PUT /api/surveillance/parametres` | `ReglerSurveillance` : seuils ≥ 1, bornes de jours et minutes, horaires tous deux vides ou différents | titulaire |
| `parametre_alerte` | ligne unique | `AdminSeuils.razor` (même enregistrement que seuils par grossiste et exceptions par produit) | `PUT /api/admin/seuils` | `EnregistrerSeuils` + `RegleSeuils` : rouge < orange, positifs ; **recalcule les alertes** après enregistrement | `admin.parametres` |
| `parametre_inventaire` | ligne unique : seuils boîtes, montant | `Inventaire.razor` | `PUT /api/inventaires/parametres` | `ModifierParametresInventaire` : non négatifs | titulaire |
| `remise_automatique` | lignes : **`produit_id` local** ou `categorie` (code de `categorie_produit`), `cree_par` **NOT NULL** (clé étrangère vers `utilisateur`) | `Mise/RemisesAutomatiques.razor` | `POST/PUT /api/remises-automatiques`, `…/arreter` | `GererRemisesAutomatiques` : produit **ou** catégorie, entier 1–100, fin ≥ début et ≥ aujourd'hui, motif ≤ 200, jamais sur un produit remboursable, **pas de chevauchement** sur la même cible | `caisse.remises_auto` |

Autres constats utiles :
- Les 11 tables sont classées **bidirectionnelles** (script 126) : leur état part déjà au central (tables miroir), donc le central connaît la valeur réelle de chaque officine.
- **TVA** : aucune des 11 tables ne porte de taux ni de réglage de TVA ; les réglages « TVA reversée » sont dans `parametre_rapport` (groupe 3, local).
  La mention « TVA » du § 16 ne correspond donc à aucune table du groupe 2 **[À VÉRIFIER avec le titulaire]**.
- Journal local : `journal_audit` (`utilisateur_id` facultatif) ; certains cas d'usage n'y écrivent que la valeur **après** (inventaire, substitution, psychotropes).
- Erreurs de l'API locale : `RegleMetierException` → 422, `DroitRefuseException` → 403, erreur `P0001` de la base → 422, corps `ErreurDto(Message, Champ)` lu par le poste (`Program.cs`) : c'est le format du projet (pas un `ProblemDetails` au sens strict).
- Voie descendante (scripts 131, 136) : `sync_recevoir_descendant` n'accepte que la table `reglage_central`, garde la plus grande version, journalise (`sync_journal_descendant`) ;
  la version acquittée s'arrête **avant** un élément refusé (redonné à chaque passage). Central (005, 009) : `cloud_publier_descendant` ne crée une version que si les données changent ;
  `cloud_descendants` publie à chaque échange le réglage effectif puis renvoie les éléments plus récents.
- Notifications : `IAlertesTitulaire` (sujets dans la contrainte `destinataire_notification_types_check`, dernier ajout : `synchronisation`, script 140).
- `journal_securite` au central : détail limité à 500 caractères, insuffisant pour une valeur avant / après.
- Aucun cache en mémoire de ces réglages repéré dans l'API locale (recherche ciblée, non exhaustive **[À VÉRIFIER au lot M4]**).

### 16 bis.3 Modèle de données

#### Au central (script `database/cloud/010_mutualisation_reglages.sql`)

| Élément | Contenu | Accès |
|---|---|---|
| `cloud_reglage_table` | une ligne par table du groupe 2 (liste fermée de 11 noms, contrainte) : `nom_table`, `libelle`, `mode_depart` (valeur de départ du § 16, pour information) | lecture par l'outil |
| `cloud_reglage_modele` | `perimetre` (voir D1), `nom_table`, `mode` (`propose` / `impose`), `valeur jsonb` (**NULL = rien publié**), `version_modele int` (+1 à chaque publication ou changement de mode), `ajouts_locaux boolean` (motifs, remises), `modifie_le`, `modifie_par`. Clé `(perimetre, nom_table)`. Créée **sans valeur** pour les 11 tables, mode = valeur de départ du § 16. | propriétaire seul (outil d'administration) ; aucun droit du rôle `pharma_cloud_api` |
| `officine.mutualisation_liberee_le` | officine sortie de la mutualisation (départ, D3) : toutes ses tables publiées en mode `local` | outil |
| `cloud_journal_reglage` | **ajout seul** (`cloud_ajout_seul`) : `le`, `perimetre`, `officine_id` (si action propre à une officine), `nom_table`, `action` (`publication`, `mode`, `liberation`, `retour`), `mode_avant`, `mode_apres`, `version_modele`, `valeur_avant`, `valeur_apres`, `auteur` (obligatoire), `compte_systeme`, `poste`, `motif` | outil (écriture et lecture) |
| Miroir `reglage_mutualise` | état montant de chaque officine (appliqué, rejeté, non adopté, origine…) : table miroir générée comme les autres (`--generer-miroir`) | RLS comme toutes les tables miroir |

**Où vit la valeur « modèle »** : au central seulement (`cloud_reglage_modele.valeur`) ; l'officine n'en garde que la **dernière version reçue**
(`reglage_central`, clé `mutu_<table>`) pour pouvoir la réappliquer sans le central. **L'état imposé / proposé** : au central (décision), recopié dans
l'élément descendant. **La version** : `version_modele` (propre à la table, lisible par l'administrateur) ; la version de transport reste celle de la
voie descendante (monotone par officine, § 15). **L'historique** : `cloud_journal_reglage` au central ; `journal_audit` à l'officine (§ 16 bis.6).

**Publication vers les officines (retenu : à l'échange, comme les familles)** : `cloud_descendants` (remplacée par 010, en reprenant **à l'identique**
la logique de 009 : central restauré, acquittement) appelle `cloud_publier_descendant('reglage_central', 'mutu_' || nom_table, élément)` pour chaque table
**publiée** du périmètre de l'officine ; `cloud_publier_descendant` ne crée une nouvelle version que si l'élément a changé. Une officine libérée reçoit
l'élément `{ "mode": "local" }` pour chaque table déjà publiée. Écarté : diffuser à toutes les officines au moment de la commande (des milliers d'écritures
d'un coup, officines inscrites plus tard oubliées). Coût : 11 comparaisons au plus par échange et par officine **[À VÉRIFIER par mesure, test de charge
§ 12.4 à rejouer]** ; si nécessaire, comparer d'abord `version_modele` (entier) avant le JSON.

#### Format de l'élément descendant (`mutu-v1`, dans le champ `donneesJson` du contrat `desc-v1`, inchangé)

```json
{ "format": "mutu-v1", "mode": "impose", "version_modele": 4, "ajouts_locaux": true,
  "publie_le": "2026-11-20T09:12:00Z", "auteur": "admin-central-1",
  "valeur": { "…": "selon la table" } }
```

| Table | `valeur` |
|---|---|
| tables à ligne unique | objet des colonnes métier, noms de la base (sans `id`, `officine_id`, `modifie_le`, `modifie_par`) ; `parametre_surveillance` **sans** `heure_ouverture` / `heure_fermeture` (D7) ; `parametre_serialisation` sans `connecteur` |
| `regle_alerte_psychotrope` | `{ "regles": [ { code, active, seuil, periode_jours, exiger_valideur, unites_par_jour } ] }` : les 5 codes, ni `libelle` ni `ordre` (affichage local) |
| `motif_reglable` | `{ "motifs": [ { liste, code, libelle, actif, ordre, effet, detail } ] }` : clé `(liste, code)` ; un motif nouveau du central a un code préfixé `c_` |
| `remise_automatique` | `{ "remises": [ { cle, categorie, pourcentage, date_debut, date_fin, motif } ] }` : `cle` = identifiant stable du central (`c_` + 12 caractères) ; **catégories seulement** (D6) |

Mode `local` : `{ "format": "mutu-v1", "mode": "local", "version_modele": n }`, sans valeur (libération).
Taille : un élément reste sous 64 Kio (borne existante) ; l'outil refuse de publier au-delà (environ 300 motifs ou 400 remises **[À VÉRIFIER par mesure]**).

#### À l'officine (script `database/migration/141_mutualisation_reglages.sql`)

| Élément | Contenu |
|---|---|
| `reglage_mutualise` (classée **montante**, colonne `modele` exclue de l'envoi) | une ligne par table (11 lignes créées, `mode = 'local'`) : `nom_table`, `mode` (`local`/`propose`/`impose`, **reçu** du central), `version_modele_recue`, `recu_le`, `version_modele_appliquee`, `applique_le`, `etat` (`aucun`, `applique`, `non_adopte`, `rejete`), `motif_rejet` (≤ 500, sans donnée personnelle), `origine` (`locale` / `central` : la valeur en vigueur vient-elle du modèle), `modifie_localement_le`, `ajouts_locaux` |
| `reglage_mode_effectif(table)` | `CASE WHEN sync_parametre.active AND officine inscrite THEN mode ELSE 'local' END` : la désactivation libère **sans rien effacer** (D3) |
| Déclencheur `zz_reglage_garde` sur les 11 tables (BEFORE INSERT/UPDATE/DELETE) | si `current_setting('pharma.reglage_central', true) = 'on'` (posé par `SET LOCAL` dans la transaction d'application) : laisse passer ; sinon, mode effectif `impose` : **refus** `P0001` « Réglage fixé par le serveur central : … » (cas particuliers des motifs et remises ci-dessous) ; sinon : note `origine = 'locale'`, `modifie_localement_le = now()` dans `reglage_mutualise` (même transaction). Prend un verrou partagé sur la ligne de `reglage_mutualise` (l'application prend un verrou exclusif : § 16 bis.5) |
| `motif_reglable.modele_code_central boolean` (défaut false) | vrai pour un motif créé ou réglé par le modèle ; le déclencheur refuse en mode imposé toute modification d'un motif central, et l'ajout d'un motif local si `ajouts_locaux = false` |
| `remise_automatique.modele_cle text` (NULL = remise locale) ; `cree_par` devient facultatif **seulement** pour une remise du central (contrainte `cree_par IS NOT NULL OR modele_cle IS NOT NULL`) | une remise du central n'a pas d'auteur local ; aucune donnée existante modifiée (ajout de colonne, NOT NULL remplacé par une contrainte équivalente pour les lignes existantes) |
| Sujet de notification `reglages_centraux` (D4) | ajouté aux contraintes `destinataire_notification_types_check` et `message_sortant_type_check` (même garde rejouable qu'au script 140) |
| `sync_journal_descendant` | pour une clé `mutu_*`, `avant` / `apres` = version de modèle et mode (pas la valeur entière : elle est dans `journal_audit`) |

Le script est idempotent, ne modifie aucune valeur de réglage, met à jour `database/init` (schéma, `seed_referentiel.sql` : `sync_table` et les 11 lignes) dans la
même modification. Les scripts futurs qui écrivent dans une de ces 11 tables (par exemple un nouveau motif système) devront poser `SET LOCAL pharma.reglage_central = 'on'`
(règle à inscrire dans `base-de-donnees.md`).

**Cas de `motif_reglable` et des ajouts locaux.** Identité commune = `(liste, code)` (contrainte unique existante) ; l'id local n'est jamais transmis.
- Motif du modèle dont le code existe déjà (motifs système, codes fixes de `sortie_stock` et `retour_fournisseur`) : libellé, activation et ordre réglés, marqué `modele_code_central`.
- Motif nouveau du central : code `c_…` ; créé seulement dans une liste qui accepte l'ajout (`RegleMotifs.AjoutPermis`), effet du tiroir contrôlé (`RegleTiroir.VerifierEffet`).
  Le générateur de codes locaux (`RegleMotifs.CodeDepuisLibelle`) ne doit plus produire de code commençant par `c_` (règle ajoutée, codes existants inchangés).
- Ajout local : toujours un motif ordinaire (`modele_code_central = false`) ; permis en mode proposé, et en mode imposé si `ajouts_locaux` (vrai par défaut, D5).
- Libellé d'un motif du central identique à celui d'un motif local de la même liste : **rejet** du modèle (la règle d'unicité locale reste vraie), motif donné au central.
- Motif retiré du modèle : **jamais supprimé** (règle « jamais de suppression », motifs cités par des ventes) ; il redevient local, inchangé (D5).

### 16 bis.4 Application d'un modèle à l'officine

Cas d'usage `AppliquerReglagesMutualises` (Application), appelé dans le passage d'envoi (`EnvoyerLotsSync`, sous `VerrouSync`) **après** les éléments descendants,
**même si l'échange avec le central a échoué** (les modèles déjà reçus sont dans la base) ; jamais sur une base en lecture seule (2e serveur : tout se réplique).
Pour chaque table dont le modèle reçu n'est pas appliqué, ou qui est imposée et dont la valeur locale a changé depuis (`modifie_localement_le > applique_le`) :

1. Transaction ; `SELECT … FROM reglage_mutualise WHERE nom_table = … FOR UPDATE` ; `SET LOCAL pharma.reglage_central = 'on'`.
2. Lecture du modèle (`reglage_central`) : format inconnu → `rejete` (« format non pris en charge »).
3. **Validation** par les validateurs du domaine (ceux de la saisie locale, lot M1), puis contrôles qui demandent la base
   (catégorie existante, chevauchement de remises, libellé de motif en double, liste qui accepte l'ajout). Une erreur → `rejete` avec le message métier ;
   **tout ou rien par table** (D9) ; la valeur en vigueur et le mode précédent restent.
4. Décision selon le mode reçu :

| Mode reçu | Valeur locale = modèle | Valeur locale différente, `origine = central` | Valeur locale différente, `origine = locale` |
|---|---|---|---|
| `local` | rien ; `etat = aucun`, mode local (libération, journal) | idem | idem |
| `propose` | `origine = central`, `applique` | **remplacée** par le modèle (personne ne l'a modifiée ici) | **gardée** : `etat = non_adopte` ; l'écran montre la valeur proposée et le bouton « Adopter la valeur du serveur central » (D2) |
| `impose` | `origine = central`, `applique` | remplacée | **remplacée** ; ancienne valeur au journal ; titulaire prévenu (§ 16 bis.6) |

5. Écriture de la table **par le dépôt de l'infrastructure** (`ReglagesMutualisesSql`, une seule transaction : valeur, `reglage_mutualise`, `journal_audit`),
   contraintes SQL en dernier rempart ; effets de bord des cas d'usage reproduits : `parametre_alerte` → recalcul des alertes (`IMoteurAlertes.GenererAsync`) après validation.
   Remises imposées : les remises **locales** en cours ou à venir sont **arrêtées** (`arretee_le`, `arretee_par` vide, journal), jamais supprimées (D6).
6. Validation de la transaction, puis notification (hors transaction, jamais bloquante).

Un rejet n'est **pas** un refus de transport : l'élément `reglage_central` est rangé et acquitté (la voie descendante continue, rien n'est redonné en boucle) ;
le rejet est un **état** (`reglage_mutualise.etat = rejete`, `motif_rejet`) qui monte au central par la file. Écarté : faire valider dans `sync_recevoir_descendant`
(SQL) : les règles métier sont en C# et seraient dupliquées ; un refus de transport bloquerait l'acquittement de tous les éléments suivants (§ 18).

### 16 bis.5 Lecture, refus et conflits à l'officine

**Lecture** : l'API locale ne lit que sa base ; le central en panne ne bloque rien (dernier modèle appliqué).
`GET /api/reglages-mutualises` (tout employé connecté, aucune donnée sensible) : pour les 11 tables, mode effectif, version, état, origine, date de réception, motif de rejet.
`GET /api/reglages-mutualises/{table}/modele` (droit de la table) : valeur proposée, pour comparaison. `POST /api/reglages-mutualises/{table}/adopter` (droit de la table,
corps `{ versionModele }`) : applique le modèle proposé (mêmes étapes), 409 si la version a changé entre-temps, 422 si la valeur est rejetée.

**Refus côté serveur (table imposée)**, deux couches :
1. **Cas d'usage** : chacun des cas d'usage d'écriture (11 tables, tableau du § 16 bis.2) appelle d'abord `IGardeReglagesCentraux.ExigerModifiableAsync(table, cible)` ;
   refus = `ReglageImposeException` (dérivée de `RegleMetierException`), **409**, `ErreurDto("Ce réglage est fixé par le serveur central : il ne peut pas être modifié dans l'officine.", "reglage_impose")`.
   Cas particuliers : `PUT /api/admin/seuils` reste permis pour les seuils par grossiste et les exceptions par produit, refusé seulement si la partie générale change ;
   motifs : refus sur un motif central, ajout local selon `ajouts_locaux` ; remises : refus sur une remise du central, création locale refusée en mode imposé.
2. **Base** : déclencheur `zz_reglage_garde` (même règle), pour tout autre chemin d'écriture ; message `P0001` → 422 (correspondance existante).

**Conflits (modification locale pendant que le central publie)** :
- Les deux écritures se sérialisent sur la ligne de `reglage_mutualise` (verrou partagé du déclencheur, exclusif de l'application).
- Écriture locale validée **avant** l'application : en mode proposé, `origine = locale` → la valeur locale gagne (§ 4, « l'officine gagne ») ; en mode imposé, elle est remplacée et gardée au journal.
- Application validée **avant** l'écriture locale : en mode imposé, l'écriture est refusée (409 ou 422) ; en mode proposé, elle passe et devient la valeur locale.
- Deux publications rapprochées : seule la dernière version est rangée (versions monotones) ; le journal central garde les deux.
- Désactivation pendant une application : sans effet (le mode effectif est relu dans la transaction).
- Valeur imposée modifiée malgré tout (script, restauration d'une sauvegarde) : remise au modèle au passage suivant (étape « valeur locale changée »), journalisée.

### 16 bis.6 Traçabilité et message au titulaire

- **Central** : chaque commande d'écriture inscrit une ligne de `cloud_journal_reglage` (auteur obligatoire, compte Windows et poste relevés par l'outil, valeur et mode avant / après, version).
  L'état de chaque officine (appliqué, rejeté, non adopté) se lit dans le miroir `reglage_mutualise` (lecture par une fonction de l'outil qui parcourt les officines en posant `app.officine_id`, sécurité par ligne oblige).
- **Officine** : `journal_audit`, `utilisateur_id` vide, actions `reglage_central_applique`, `reglage_central_remplace` (valeur locale écrasée), `reglage_central_rejete`,
  `reglage_central_adopte` (bouton, avec l'employé), `reglage_central_libere` ; détails : table, mode avant / après, version du modèle, auteur au central, **valeur avant et après**.
  Ces actions rejoignent la catégorie « modifications » du journal (`CategoriesJournal`). Les cas d'usage locaux des 11 tables écrivent aussi **avant / après** (aujourd'hui l'après seul pour l'inventaire, la substitution et les psychotropes ; D13).
- **Message** au titulaire par `IAlertesTitulaire` (nouvelle méthode `ReglagesImposesAsync`, sujet `reglages_centraux`, D4) à chaque table qui **devient imposée**, en précisant
  si la valeur locale a été remplacée (« Seuils d'alerte de péremption : fixés par le serveur central le … ; votre valeur a été remplacée, l'ancienne est au journal »).
  Un seul message par passage pour toutes les tables concernées ; pas de limite « une par heure » (chaque événement est distinct). Sans destinataire : bandeau de l'accueil (mécanisme existant `alerte_non_envoyee`).
  Aucune donnée de patient. Rejet d'un modèle : pas de message (problème du central), visible dans l'écran et au central (D9).

### 16 bis.7 Première activation, rien de publié, désactivation, départ

- **Première activation** : les 11 lignes de `reglage_mutualise` sont en `local` ; rien ne change tant que le central n'a rien publié. L'échange suivant apporte les modèles
  **déjà publiés** du périmètre ; ils sont appliqués selon le tableau du § 16 bis.4 (en mode proposé, une valeur locale **modifiée sur place** n'est **pas** écrasée, D2 ; à la première réception, une table **jamais modifiée sur place depuis l'installation** reçoit tout de suite la valeur du central, script 142).
- **Rien publié pour une table** : aucun élément ne descend ; table locale. Le central ne publie **jamais** de lui-même les valeurs de départ (D12) : l'administrateur publie
  explicitement, après une simulation qui montre, officine par officine, les valeurs qui seraient remplacées.
- **Désactivation de la synchronisation** (proposition, **[À DÉCIDER par le titulaire]**, D3) : mode effectif `local` pour toutes les tables, immédiatement ; valeurs gardées ; journal `reglage_central_libere`.
  À la réactivation, le mode reçu redevient effectif ; une table imposée modifiée entre-temps est remise au modèle, journalisée, titulaire prévenu.
- **Départ d'une officine** (proposition, **[À DÉCIDER par le titulaire]**, D3) : commande `--liberer-reglages --officine <code>` (journal central), qui publie le mode `local` pour toutes ses tables ; puis la révocation habituelle.
  Si l'officine ne se reconnecte plus, la désactivation locale la libère (point précédent).
- **2e serveur et bascule** : tout est en base, donc répliqué ; l'application ne tourne que sur le principal.

### 16 bis.8 Sécurité

- **Seul l'administrateur du central publie** : fonctions `cloud_publier_reglage`, `cloud_regler_mode_reglage`, `cloud_liberer_reglages` sans `EXECUTE` pour `PUBLIC` ni pour `pharma_cloud_api` ;
  appelées par l'outil d'administration (propriétaire de la base), comme `cloud_regler_famille`. L'API centrale ne fait que lire (`cloud_descendants`, `SECURITY DEFINER`). Aucune route nouvelle au central.
- **Validation à la publication** (outil) : mêmes validateurs du domaine (`Pharma.Cloud.Api` référencera `Pharma.Domain`, qui n'a aucune dépendance) ;
  liste fermée des 11 tables (le groupe 3 est refusé, contrainte SQL et validateur) ; taille ≤ 64 Kio.
- **Validation à la réception** (officine) : de nouveau, toutes les règles métier (§ 16 bis.4) ; valeur invalide rejetée, journalisée, synchronisation poursuivie.
- **Garde locale** : refus dans les cas d'usage et dans la base (§ 16 bis.5). Le drapeau `pharma.reglage_central` protège contre une erreur de l'application, pas contre le compte
  de la base de l'API (de confiance par construction) ; il n'est posé que par `ReglagesMutualisesSql`.
- **Données** : aucune donnée personnelle dans les modèles ; `auteur` = identifiant de l'administrateur du central (pas de nom de patient, pas de mot de passe).
  `parametre_formulaire_sms.texte` part déjà au central (décision du § 14.1).

### 16 bis.9 Points à décider par le titulaire

| N° | Question | Recommandation |
|---|---|---|
| D1 | **Périmètre d'un modèle** : un seul modèle pour toutes les officines du serveur central, ou un modèle par groupe d'officines (réseau d'un même titulaire) ? Un serveur central prévu pour des milliers d'officines rend un modèle unique discutable (remises, motifs). | Clé `(perimetre, nom_table)` dès le script 010, avec un seul périmètre `tous` créé ; l'option `--perimetre` et le rattachement des officines à un groupe viendront avec S7, sans migration de schéma. |
| D2 | **Mode proposé, valeur locale différente** (première réception ou modification locale) : écraser ou garder ? | **Garder** la valeur locale, montrer la valeur proposée et un bouton « Adopter » (conforme au § 4 : l'officine gagne). Une valeur jamais modifiée ici suit le modèle, **dès la première réception** (décision du 2026-11-17, D-M8-2 : table jamais modifiée sur place depuis l'installation = valeur appliquée toute seule ; modifiée = gardée, « Adopter »). |
| D3 | **Désactivation de la synchronisation, départ d'une officine** : les tables imposées redeviennent-elles modifiables ? | **Oui** : mode effectif local dès la désactivation (valeurs gardées, journal) ; à la réactivation, l'imposition revient (valeur remise, message). Départ : `--liberer-reglages` avant la révocation. Sinon le titulaire resterait sans moyen de régler sa propre officine. |
| D4 | **Sujet du message** quand une table devient imposée | Nouveau sujet « Réglages fixés par le serveur central » (`reglages_centraux`), abonnable dans Administration › Notifications ; premiers abonnés : ceux du sujet « synchronisation » (une seule fois, comme au script 140). |
| D5 | **Motifs en mode imposé** : ajouts locaux permis ? Motif retiré du modèle ? | Ajouts locaux **permis par défaut** (`ajouts_locaux`, réglable par table au central) ; motifs du central verrouillés ; motif retiré du modèle : **gardé, redevient local**, jamais supprimé ni désactivé d'office. |
| D6 | **Remises automatiques du central** : `produit_id` est un identifiant local, inconnu du central. | **Catégories seulement** dans cette version ; produits par n° d'enregistrement plus tard si besoin. En mode imposé, les remises locales en cours sont **arrêtées** (jamais supprimées), journalisées, titulaire prévenu. |
| D7 | **Colonnes propres à chaque officine** dans une table mutualisée | `parametre_surveillance.heure_ouverture` / `heure_fermeture` **toujours locales** (hors modèle, modifiables même si la table est imposée). |
| D8 | `parametre_fiscal` et `parametre_tracabilite` **n'ont pas d'écran de modification** (constaté), alors que la décision veut tous les réglages dans l'administration de l'officine. | Créer deux écrans simples (lot M7) : fiscal (conservation 6 à 30 ans, séparateur, fuseau ; format fixe), traçabilité (boîte obligatoire, patient obligatoire), droit titulaire, journal avant / après. |
| D9 | **Valeur rejetée par une officine** | Tout ou rien par table ; état « rejeté » + motif visible à l'écran et au central (`--reglages-mutualises`) ; pas de SMS. |
| D10 | **« Qui » au central avant les écrans S7** | `--auteur <identifiant>` obligatoire sur chaque commande d'écriture, plus le compte Windows et le poste relevés par l'outil. |
| D11 | **Format du refus local** | `ErreurDto` (format actuel de l'API locale, lu par le poste), code **409**, champ `reglage_impose` ; un passage général à `ProblemDetails` serait un chantier séparé. |
| D12 | **Valeurs de départ du § 16** (fiscal, traçabilité, sérialisation imposés ; autres proposés) | Enregistrées comme mode de départ **sans valeur** : rien ne descend tant que l'administrateur n'a pas publié (simulation d'abord). |
| D13 | **Modifications locales des tables mutualisées** tracées avant / après | Oui, aligner les 11 cas d'usage (petit travail, même lot que la garde). |
| D14 | **TVA** citée au § 16 : aucune table du groupe 2 ne la porte (constaté) | Confirmer qu'il n'y a rien à mutualiser pour la TVA dans ce lot ; sinon désigner la table (`parametre_rapport` est du groupe 3). |

### 16 bis.10 Lots de développement

| Lot | Contenu | Agent | Dépend de | Taille |
|---|---|---|---|---|
| **M1. Contrat et règles** | `Pharma.Shared/DtosMutualisation.cs` (élément `mutu-v1`, valeurs par table, état, DTO des routes locales) ; `Pharma.Domain/Reglages/` : liste fermée des 11 tables, **validateurs purs** extraits des cas d'usage existants (seuils, surveillance, inventaire, psychotropes, motifs, remises, fiscal, traçabilité, sérialisation, formulaire, substitution) ; les cas d'usage existants les appellent (comportement inchangé) ; règle « code local jamais `c_` » | backend | décisions D1, D2, D5–D7 | M |
| **M2. Script officine 141** `141_mutualisation_reglages.sql` | `reglage_mutualise` (montante), `reglage_mode_effectif`, déclencheur `zz_reglage_garde`, colonnes `motif_reglable.modele_code_central`, `remise_automatique.modele_cle` + contrainte `cree_par`, sujet `reglages_centraux`, journal descendant ; `database/tests/141_mutualisation_reglages_tests.sql` ; `database/init` (schéma, seed) dans la même modification | base-de-donnees | décisions D3, D4, D6 | M |
| **M3. Script central 010** `010_mutualisation_reglages.sql` | `cloud_reglage_table`, `cloud_reglage_modele` (11 lignes sans valeur), `cloud_journal_reglage`, `officine.mutualisation_liberee_le`, fonctions d'administration, `cloud_descendants` remplacée (logique 009 conservée), lecture de l'état des officines ; `database/cloud/tests/010_mutualisation_reglages_tests.sql` ; `002_tables_miroir.sql` **régénéré** pour la nouvelle table montante (à coordonner : un autre travail modifie d'anciens scripts) | base-de-donnees | D1, D10, D12 ; M2 pour le miroir | M |
| **M4. Application et infrastructure officine** | `AppliquerReglagesMutualises`, `AdopterReglageCentral`, `IGardeReglagesCentraux` et sa mise en place dans les 11 cas d'usage (cas particuliers seuils, motifs, remises), journal avant / après (D13), `ReglagesMutualisesSql` (transaction unique, `SET LOCAL`), appel dans `EnvoyerLotsSync`, `IAlertesTitulaire.ReglagesImposesAsync` | backend | M1, M2 | L |
| **M5. API locale** | `GET /api/reglages-mutualises`, `GET …/{table}/modele`, `POST …/{table}/adopter` ; `ReglageImposeException` → 409 dans `Program.cs` ; champ `mutualisation` dans `GET /api/admin/synchronisation` | backend | M4 (peut commencer sur M1) | S |
| **M6. Outil d'administration central** (`Pharma.Cloud.Api`) | `--reglages-mutualises [--table] [--officine]` (modèles, modes, état par officine, rejets), `--publier-reglage <table> --fichier <json> --auteur <id> [--simulation]`, `--mode-reglage <table> impose\|propose --auteur <id>`, `--modele-depuis-officine <table> --officine <code> --auteur <id>` (prépare un fichier depuis le miroir d'une officine de référence), `--liberer-reglages --officine <code> --auteur <id>`, `--journal-reglages` ; validation par M1 | backend | M1, M3 | M |
| **M7. Écrans du poste** | composant commun « fixé par le serveur central » (mention, champs grisés, bouton Adopter, valeur proposée) dans les 9 écrans existants ; deux écrans nouveaux fiscal et traçabilité (D8) ; Administration › Synchronisation : sous-rubrique « Réglages mutualisés » (état des 11 tables, rejets) ; charte graphique, sans défilement, captures clair et sombre | frontend (poste) | M5 (routes fixées ici : peut commencer en parallèle) | L |
| **M8. Essais de bout en bout** | copies jetables, les deux API réelles : publication proposée puis imposée (valeur locale remplacée, journal des deux côtés, message), refus 409 et refus de la base, valeur invalide rejetée sans arrêt de l'envoi, central coupé (réglage gardé, rien bloqué), conflit simultané, désactivation puis réactivation, libération, 2e serveur en lecture seule ; tests C# (`Pharma.Application.Tests`, `Pharma.Cloud.Tests`, test d'architecture : même liste de 11 tables dans le domaine, le script 141, le script 010 et `sync_table`) | testeur | M2–M7 | M |
| **M9. Documentation** (réalisé le 2026-11-17, § 16 bis.13) | `regles-metier.md` (chaque règle, même modification que le code), ce document (réalisé), `base-de-donnees.md`, `securite-et-droits.md`, `mise-en-production.md` (scripts 141 et 010), `a-faire.md` ; scripts passés sur dev et test | documentaliste | au fil des lots | S |

**Ordre et parallélisme** :
1. Décisions D1 à D14 (au moins D1, D2, D3, D5, D6, D8 avant de coder).
2. **En parallèle** : M1, M2, M3 (le format `mutu-v1` et les noms sont fixés ici).
3. **En parallèle** : M4 (après M1 + M2) et M6 (après M1 + M3) ; M7 peut démarrer sur les routes du § 16 bis.5.
4. M5 (après M4), fin de M7.
5. M8, puis clôture de M9.

### 16 bis.11 Risques

- **Déclencheur de garde et scripts futurs** : un script qui écrit dans une table imposée échouera s'il ne pose pas le drapeau ; à documenter et à tester.
- **Coût de la publication à l'échange** pour un très grand nombre d'officines : à mesurer (test de charge).
- **Régénération de `002_tables_miroir.sql`** pendant qu'un autre travail touche d'anciens scripts : à faire en dernier dans M3, après coordination.
- **Extraction des validateurs** : risque de changer un comportement existant ; les tests actuels des cas d'usage doivent passer sans modification.
- **Remises par catégorie imposées** : arrêter des remises locales en cours change les prix au comptoir ; le message au titulaire est indispensable.
- **Obligations légales** (seuils psychotropes, conservation fiscale) qui justifieraient d'imposer : **[À VÉRIFIER]** (déjà noté au § 16).

### 16 bis.12 Lot M1 : réalisé (2026-11-16) — format `mutu-v1` et règles extraites

**Où** : `Pharma.Shared/DtosMutualisation.cs` (forme et sérialisation), `Pharma.Domain/Reglages/` (règles et validation). `Pharma.Shared` et `Pharma.Domain`
ne dépendent de rien (test d'architecture existant) : le format ne porte donc que la **forme** ; les règles métier et la validation d'un modèle complet sont dans
le domaine, qui lit le JSON lui-même (`System.Text.Json`) pour servir à la fois `Pharma.Application` (réception, lot M4) et l'outil central (lot M6). Un test vérifie
que ce que le format écrit passe la validation du domaine, et que les deux listes de 11 tables sont identiques.

**Format** (champ `donneesJson` du contrat `desc-v1`, table `reglage_central`, clé `mutu_<table>`) :
- Enveloppe : `format` (`mutu-v1`), `mode` (`impose`, `propose`, `local`), `version_modele` (entier ≥ 1) ; sauf en mode `local` : `publie_le` (UTC, `AAAA-MM-JJTHH:MM:SSZ`),
  `auteur` (1 à 100 caractères), `valeur`, et `ajouts_locaux` (booléen, **motifs et remises seulement**, interdit ailleurs). Mode `local` : les trois premiers champs, rien d'autre.
- **Lecture stricte** : champ inconnu, manquant ou mal typé refusé. Aucun identifiant propre à une officine : `id`, `officine_id`, `produit_id`, `cree_par`, `cree_le`,
  `modifie_*`, `arretee_*`, `heure_ouverture`, `heure_fermeture` sont refusés avec un message explicite ; le connecteur de sérialisation (fixé à `factice`) n'est pas dans le modèle.
- **Déterministe** : JSON UTF-8 compact, clés triées par ordinal, listes triées par clé métier (règles par `code`, motifs par `liste` puis `code`, remises par `cle`), nombres sans zéro final
  (`5000.00` s'écrit `5000`), dates `AAAA-MM-JJ`, accents écrits tels quels. Deux sérialisations d'un même modèle donnent les mêmes octets.
- **Borné** : 64 Kio (octets UTF-8), refusé à l'écriture comme à la lecture ; en pratique environ 340 motifs de 70 à 80 caractères (mesuré : 400 dépassent la borne).

Exemple (`parametre_inventaire`, impose) :

```json
{"auteur":"admin-central-1","format":"mutu-v1","mode":"impose","publie_le":"2026-11-20T09:12:00Z","valeur":{"seuil_boites":2,"seuil_montant":2000.5},"version_modele":4}
```

| Table | `valeur` (noms de colonnes de la base) |
|---|---|
| `parametre_fiscal` | `conservation_annees`, `format_export_dgi`, `separateur_csv`, `fuseau` |
| `regle_alerte_psychotrope` | `regles[]` : `code`, `active`, `seuil`, `periode_jours`, `exiger_valideur`, `unites_par_jour` (entier ou `null`, clé obligatoire) ; les 5 codes exactement |
| `parametre_tracabilite` | `boite_obligatoire`, `patient_obligatoire` |
| `parametre_serialisation` | `actif`, `factice_hors_ligne` |
| `parametre_substitution` | `equivalent_exact_seulement` |
| `motif_reglable` | `motifs[]` : `liste`, `code`, `libelle`, `actif`, `ordre`, `effet`, `detail` |
| `parametre_formulaire_sms` | `texte` (texte ou `null` = texte par défaut, clé obligatoire) |
| `parametre_surveillance` | les 21 seuils, **sans** les horaires d'ouverture (D7) |
| `parametre_alerte` | `jours_orange`, `jours_rouge`, `horizon_risque_jours`, `delai_fournisseur_jours`, `jours_securite`, `jours_cible_commande`, `jours_dormant`, `seuil_lot_delaisse`, `seuil_lot_delaisse_collectif` |
| `parametre_inventaire` | `seuil_boites`, `seuil_montant` |
| `remise_automatique` | `remises[]` : `cle`, `categorie`, `pourcentage`, `date_debut`, `date_fin`, `motif` (texte ou `null`) ; catégories seulement (D6) |

**Écarts avec la conception ci-dessus** (à valider) :
- `parametre_alerte` : seules les 9 colonnes que l'écran des seuils règle sont dans le modèle ; `fenetre_conso_jours` et `jours_surstock` existent dans la base mais aucun code ne les lit ni ne les règle (constaté par recherche dans le code C#).
- `publie_le` est à la seconde (le § 16 bis.3 donnait déjà ce niveau) ; `ajouts_locaux` n'est présent que pour les motifs et les remises (l'exemple générique du § 16 bis.3 le montrait toujours).
- Remises : `date_fin ≥ aujourd'hui` n'est exigé qu'à la **publication** (paramètre « aujourd'hui » de la validation) ; à la réception une remise qui arrive à son terme ne fait pas rejeter toute la table.
- Règles qui n'existaient qu'en SQL, reprises comme fonctions du domaine : fiscal (conservation 6 à 30, format `csv-v1`, séparateur `;` `,` tabulation) et traçabilité (4 valeurs). **Nouvelles** (pas de contrainte SQL aujourd'hui) : fuseau (identifiant de 64 caractères au plus, forme `Zone/Ville`), `ordre` d'un motif de 0 à 10 000, montants à deux décimales et moins de 10 chiffres entiers (`numeric(12,2)`). **[À VÉRIFIER]** par le titulaire.
- Contrôles qui demandent la base, **laissés au lot M4** : catégorie de remise existante, remise locale qui chevauche, libellé de motif déjà pris par un motif local, motif système non désactivable (`RegleMotifs.VerifierModification`), code existant d'un motif système.
- Aucune règle n'était enfouie dans `Pharma.Cloud.Api` ni dans `Pharma.Infrastructure` (seul le chevauchement de remises est en SQL : sa définition, bornes incluses, est reprise dans `RegleRemisesAutomatiques.Chevauche`, utilisée par la validation du modèle ; la requête SQL de la saisie locale reste en place).
- Les nouvelles règles fiscales et de traçabilité n'ont pas de nom de champ d'écran (le test `ChampsObligatoiresTests` exige un `data-champ` sur l'écran pour tout champ nommé) : à ajouter avec les écrans du lot M7.

**Règles extraites dans `Pharma.Domain` (comportement des cas d'usage inchangé, tests existants non modifiés)** :

| Table | Règles (classe de `Pharma.Domain.Reglages` sauf mention) | Cas d'usage qui les appelle |
|---|---|---|
| `parametre_fiscal` | `RegleFiscale` : conservation, format, séparateur, fuseau | aucun aujourd'hui (pas d'écran, lot M7) |
| `regle_alerte_psychotrope` | `RegleAlertesPsychotropes` : code connu, seuil 1–1000, période 1–3650, unités 1–100 ou vide ; codes (`CodesAlertePsychotrope` y renvoie) | `ParametrerAlertesPsychotropes` |
| `parametre_tracabilite` | `RegleTracabilite` : patient obligatoire | aucun aujourd'hui (lot M7) |
| `parametre_serialisation` | aucune règle de valeur (deux booléens) | — |
| `parametre_substitution` | aucune règle de valeur (un booléen) | — |
| `motif_reglable` | `RegleMotifs` (existante, complétée) : `ExigerListe`, `VerifierLibelle`, `VerifierCode`, `ExigerLibelleUnique`, `ExigerAjoutPermis`, `ExigerEffetAutorise`, `EstCodeCentral` ; `RegleTiroir.VerifierEffet` ; **un code local ne commence plus jamais par `c_`** (préfixe `local_`, codes existants inchangés) | `EnregistrerMotifReglable` |
| `parametre_formulaire_sms` | `RegleFormulaireSms` : texte rogné, 4000 caractères au plus, texte par défaut = vide | `FormulaireAccordSmsPatient.ModifierTexteAsync` |
| `parametre_surveillance` | `RegleSurveillance.VerifierSeuils` (21 seuils, mêmes messages et même ordre) ; `VerifierHoraires` (locaux, hors modèle) | `ReglerSurveillance` |
| `parametre_alerte` | `RegleSeuils.VerifierReglageGeneral` (+ `VerifierGeneral` et `VerifierParticulier` existantes) | `EnregistrerSeuils` |
| `parametre_inventaire` | `RegleInventaire.VerifierSeuils` | `ModifierParametresInventaire` |
| `remise_automatique` | `RegleRemisesAutomatiques` : cible exclusive, pourcentage entier 1–100, période, motif ≤ 200, produit non remboursable, chevauchement | `GererRemisesAutomatiques` |

`TablesMutualisables` (liste fermée des 11 tables, clé `mutu_<table>`) ; `ValidationModele.ValiderElement(table, json, aujourdhui?)` et `ValiderValeur` renvoient **la liste des erreurs** en français
(liste vide = valide), tout ou rien par table (D9). Tests : `ReglesReglagesTests`, `ValidationModeleTests` (Pharma.Domain.Tests), `FormatMutuV1Tests` (Pharma.Application.Tests).

### 16 bis.12 Réalisation des lots M2 (officine) et M3 (central) (2026-11-16)

**Fait : les deux scripts de base de données, rien n'est imposé ni publié.** À la fin des essais toutes les tables sont « locales » sans modèle ; le comportement de l'officine est inchangé.

**M2, officine : `database/migration/141_mutualisation_reglages.sql`** (tests `database/tests/141_mutualisation_reglages_tests.sql`) :
- `reglage_mutualise` (11 lignes, mode `local`, classée **montante**, colonne `modele` non envoyée, `motif_rejet` filtré comme les textes libres) ; colonnes ajoutées par rapport au § 16 bis.3 : `mode_recu` (mode reçu, distinct du mode **en vigueur** `mode` : un modèle rejeté ne change pas le mode précédent) et `version_modele_rejetee` (une version déjà rejetée n'est pas réexaminée à chaque passage) ; `modele` garde l'élément `mutu-v1` reçu.
- `reglage_tables_mutualisables()` (liste fermée des 11 tables, utilisée par la contrainte), `reglage_mode_effectif(table)` (mode en vigueur si la synchronisation est active **et** l'officine inscrite, sinon `local`).
- Garde `zz_reglage_garde` (BEFORE INSERT/UPDATE/DELETE, 11 tables) : refus `P0001` « Réglage fixé par le serveur central : … » sur une table imposée, sauf marqueur `pharma.reglage_central = on`. Cas particuliers : horaires de `parametre_surveillance` toujours locaux (D7) ; motifs : ajout local selon `ajouts_locaux`, motif du central verrouillé (D5) ; remises : aucune création locale en mode imposé, remise du central verrouillée (D6) ; le repérage `modele_code_central` / `modele_cle` ne se pose pas hors application d'un modèle ; écriture sans effet permise. Hors mode imposé, la modification locale est notée (`origine = locale`, `modifie_localement_le`) par une écriture dans la ligne de `reglage_mutualise` (ce verrou de ligne sérialise avec `SELECT … FOR UPDATE` de l'application ; remplace le « verrou partagé » du § 16 bis.3, sans risque d'interblocage entre deux écritures locales) ; aucune écriture quand la table est locale et sans valeur venue du central.
- `reglage_journaliser(…)` : une ligne du journal d'audit (actions `reglage_central_applique`, `_remplace`, `_rejete`, `_adopte`, `_libere`), valeurs avant et après.
- `motif_reglable.modele_code_central`, `remise_automatique.modele_cle` (catégorie seulement, `c_` + 12 caractères, unique), `cree_par` facultatif seulement pour une remise du central.
- Voie descendante : `sync_recevoir_descendant` (définition de 136 gardée sur la base à jour, plus recréée) reconnaît `mutu_<table>`. Table non mutualisable : journal `invalide`, élément **acquitté** (le statut `invalide` ne bloque pas l'acquittement, contrairement à `refuse` et `table_inconnue`). Table connue mais élément mal formé (format, mode, version, valeur) : acquitté, table `rejete` avec motif. Élément valide : rangé dans `reglage_central` et `reglage_mutualise` (`mode_recu`, `version_modele_recue`, `recu_le`, `modele`) ; c'est le cas d'usage C# (lot M4) qui l'applique. Protections de transport inchangées (version maximale, saut, 64 Kio) ; une clé inutilisable est maintenant refusée au lieu de lever une erreur.
- Sujet de notification `reglages_centraux` (les abonnés du sujet « synchronisation » y sont abonnés une fois). `init/schema.sql` et `seed_referentiel.sql` mis à jour (en-têtes « 001 à 141 »).
- **Point d'attention** : une table imposée refuse aussi les scripts. Les outils qui rejouent toute la suite (`mettre_a_jour_base_officine.ps1`, restauration depuis l'application) devront poser `PGOPTIONS="-c pharma.reglage_central=on"` (ou `SET LOCAL`) avant que la première table soit imposée ; à traiter avec le lot M4.

**M3, central : `database/cloud/010_mutualisation_reglages.sql`** (tests `database/cloud/tests/010_mutualisation_reglages_tests.sql`) :
- `cloud_reglage_table` (11 tables, mode de départ du § 16 ; `regle_alerte_psychotrope` démarre en « proposé »), `cloud_reglage_modele` (clé `(perimetre, nom_table)`, un seul périmètre `tous`, D1 ; 11 modèles créés **sans valeur**, version 0, D12), `cloud_journal_reglage` (ajout seul, auteur obligatoire, compte Windows et poste), `officine.mutualisation_liberee_le`.
- Fonctions d'administration (sans `EXECUTE` pour `PUBLIC` ni `pharma_cloud_api`) : `cloud_publier_reglage`, `cloud_regler_mode_reglage`, `cloud_liberer_reglages`, `cloud_retablir_reglages`, `cloud_etat_reglages` (état des officines lu dans le miroir, officine posée une par une). La validation métier de la valeur reste celle de l'outil (lot M6) ; la base contrôle la table, l'auteur, la forme et la taille.
- `cloud_descendants` remplacée : logique de 009 à l'identique, plus un élément `mutu_<table>` par table publiée (nouvelle version descendante seulement si l'élément change ; `publie_le` est la date de la dernière modification du modèle, donc stable). Officine libérée : élément `{ mode: local }`.
- Droits : `pharma_cloud_api` ne fait que **lire** les modèles (sécurité par ligne : politique de lecture seule), aucun droit sur le journal.
- `002_tables_miroir.sql` régénéré (`--generer-miroir`) : miroir de `reglage_mutualise` (sans `modele`) et colonnes `motif_reglable.modele_code_central`, `remise_automatique.modele_cle`. `101b` (suppression de `vente.num_facture_chifa`, jamais lancée par les scripts) n'est pas appliqué dans `schema.sql` : le miroir garde donc la colonne `vente.num_facture_chifa`, qui restera simplement vide pour une officine où `101b` a été passé.

**Essais (2026-11-16)** : sauvegardes `pg_dump -Fc` avant ; 141 passé deux fois sur dev et test (empreinte du schéma identique entre les deux passages et entre dev et test, comptages inchangés), `rejeu_complet.ps1` sur une copie jetable (001 à 141 deux fois : structure, données, comptages identiques) ; base centrale jetable créée par `creer_base_centrale.ps1` (001 à 010, tests 001 à 010 réussis), 010 rejoué trois fois sans changement ; `pharmacie_cloud_dev` : 002 puis 010 deux fois (dump du **schéma seul** et des tables sans sécurité par ligne : le dump complet est impossible, les tables de données ont la sécurité par ligne forcée ; la base ne contenait aucune donnée d'officine), tests centraux 001 à 010 réussis.

### 16 bis.12 Réalisation du lot M6 : outil d'administration du central (2026-11-16)

**Où** : `src/Pharma.Cloud.Api/Reglages/` (`ArgumentsReglages`, `ModelePublication`, `ModeleDepuisOfficine`, `AdministrationReglages`), branché dans `ConsoleAdministration` (7 commandes). `Pharma.Cloud.Api` référence maintenant `Pharma.Domain` (validation du lot M1) ; aucun changement dans `Pharma.Shared` ni `Pharma.Domain`. Tout passe par les fonctions du script 010 avec `PHARMA_CLOUD_ADMIN_CONNEXION` ; messages en français ; code de sortie 1 en cas d'erreur.

| Commande | Effet |
|---|---|
| `--reglages-mutualises [table]` | 11 tables : mode, version, auteur, date, officines qui ont appliqué (dont à la dernière version), rejeté (avec motif), non adopté, sans information (`cloud_etat_reglages`) ; avec une table, détail par officine |
| `--publier-reglage <table> --fichier <json> --auteur <nom> [--motif] [--simulation] [--oui]` | validation complète (`ValidationModele.ValiderElement` avec la date du jour, puis forme canonique `SerialisationMutuV1`), puis `cloud_publier_reglage` ; simulation : mode, version avant / après, erreurs, rien d'écrit ; modèle identique : aucune version ; mode imposé : confirmation |
| `--mode-reglage <table> <impose ou propose> --auteur <nom> [--motif] [--oui]` | `cloud_regler_mode_reglage` ; « imposé » : avertissement (valeurs locales remplacées, message au titulaire) et confirmation « oui » sauf `--oui` ; sans saisie possible : refus |
| `--modele-depuis-officine <code> <table> --fichier <sortie> [--mode] [--ecraser]` | lit le miroir de l'officine (officine posée par transaction), écrit un fichier `mutu-v1` sans identifiant propre à l'officine ; ne publie rien ; signale ce qui est écarté et les erreurs que la publication refuserait |
| `--liberer-reglages <code>` et `--retablir-reglages <code>` (avec `--auteur <nom> [--motif]`) | `cloud_liberer_reglages` et `cloud_retablir_reglages` (D3) ; déjà libérée ou pas libérée : message, rien d'écrit |
| `--journal-reglages [table] [--depuis AAAA-MM-JJ] [--limite n]` | lecture de `cloud_journal_reglage` (200 entrées par défaut) |

**Écarts et choix à valider** :
- **Mode « local » d'une table** : `--mode-reglage <table> local` est **refusé** avec explication, car `cloud_regler_mode_reglage` (script 010) ne connaît que `propose` et `impose` ; le retour d'une officine en « local » se fait par `--liberer-reglages`. Si le titulaire veut un « local » par table pour toutes les officines, c'est une évolution du script 010.
- La conception (§ 16 bis.10) écrivait `--modele-depuis-officine <table> --officine <code>` et `--reglages-mutualises [--table] [--officine]` ; la forme retenue suit la demande du lot (code d'officine puis table en positions, table seule pour le détail).
- Une **publication en mode imposé** demande aussi la confirmation (pas seulement le changement de mode) : elle remplace de la même façon les valeurs locales.
- Dans le fichier à publier, `version_modele`, `publie_le` et `auteur` sont exigés par le format mais **remplacés** par le central (version +1, auteur de `--auteur`) ; `ajouts_locaux` du fichier est repris (motifs et remises).
- Le modèle depuis une officine reprend tout le miroir de la table : pour les motifs, les motifs locaux de l'officine s'y trouvent (à retirer à la main) ; les remises sur produit, arrêtées ou terminées sont écartées ; la clé d'une remise sans clé du central est dérivée (`c_` + 12 caractères d'une empreinte de l'officine et de la remise), donc stable d'un essai à l'autre.

**Essais (2026-11-16)** : tests `ReglagesMutualisesAdminTests` (analyse des arguments, simulation, refus d'un modèle invalide, auteur obligatoire, confirmation, modèle depuis le miroir ; sans base). Essai réel sur une base centrale **jetable** `pharmacie_cloud_jetable_m6_essai` créée par `creer_base_centrale.ps1` (001 à 010, tests 001 à 010 réussis) **dans une instance PostgreSQL temporaire du poste de développement (port 6231, compte d'essai propre, arrêtée et base supprimée à la fin)**, `pharmacie_cloud_dev` non touchée : modèle invalide refusé (journal vide), simulation sans écriture, publication imposée refusée sans confirmation puis acceptée, modèle identique sans nouvelle version, changement de mode (version 3), état et journal lus, `cloud_descendants` renvoie l'élément `mutu_parametre_inventaire` (mode, version, valeur ; mode `local` après libération, de nouveau imposé après rétablissement), commandes lancées aussi en ligne (`Pharma.Cloud.Api.dll`). Le test réel est ignoré sauf si `PHARMA_ESSAI_M6_CONNEXION` désigne une base jetable **neuve** `pharmacie_cloud_jetable_m6_*`.

### 16 bis.12 Réalisation du lot M4 : application à l'officine (2026-11-16)

**Fait** : application des modèles reçus, « Adopter », garde des 11 cas d'usage de saisie locale, journal avant / après (D13), sujet de notification, marqueur des outils de rejeu. Pas de route d'API (lot M5) ni d'écran (lot M7). Aucun script SQL nouveau : le script 141 a suffi (aucun défaut constaté).

**Où** : `Pharma.Application/Reglages/ReglagesMutualises.cs` (`AppliquerReglagesMutualises`, `AdopterReglageCentral`, `LibererReglagesMutualises`, `GardeReglagesCentraux` / `IGardeReglagesCentraux`, `ReglageImposeException`, `ModeleReglageChangeException`, ports `IReglagesMutualisesDepot` et `ITransactionReglage`), `Pharma.Infrastructure/Reglages/ReglagesMutualisesSql.cs` (une transaction par table : `SELECT … FOR UPDATE` de la ligne d'état, `set_config('pharma.reglage_central', 'on', true)`, valeur, état, journal). Application et infrastructure ne connaissent pas `Pharma.Shared` (test d'architecture) : elles lisent la valeur du modèle en JSON, la validation est celle de `Pharma.Domain.Reglages.ValidationModele` (lot M1).

**Appels** : `EnvoyerLotsSync` applique les modèles après l'échange descendant (même s'il a échoué) et, si des éléments sont arrivés avec l'accusé d'un lot, une seconde fois en fin de passage ; une panne n'arrête jamais l'envoi (note « Réglages mutualisés non appliqués »). `EnvoiSynchronisation` (tâche de fond) l'applique aussi au démarrage. Sans synchronisation active, officine inscrite et base en écriture, rien n'est appliqué. `ReglerSync` écrit la libération au journal quand la synchronisation est désactivée (D3).

**Examen d'une table** : à examiner si la version reçue n'est ni appliquée ni rejetée, si l'état est « non adopté » (réévalué : une valeur redevenue égale au modèle est appliquée), ou si la table est imposée et modifiée localement depuis son application (`modifie_localement_le > applique_le`). Décision : voir § 16 bis.4 ; comparaison des valeurs par écriture canonique (clés et listes triées, nombres sans zéro final) ; motifs : seulement les motifs du modèle comptent dans la valeur locale ; remises : les remises du serveur central en vigueur.

| Cas | Résultat |
|---|---|
| imposé, valeur différente | remplacée ; journal `reglage_central_remplace` (avant / après) ; un message par passage (`ReglagesImposesAsync`) |
| imposé, valeur égale | `reglage_central_applique` ; message « fixé par le serveur central » |
| proposé, jamais modifiée sur place (origine central, ou origine locale sans date de modification locale : première réception) | suit le modèle |
| proposé, modifiée localement (origine locale avec une date de modification locale) | gardée, état `non_adopte`, rien écrit, rien journalisé |
| modèle invalide (domaine ou base) | `rejete` + motif (500 caractères) + `version_modele_rejetee`, mode et valeur gardés, journal `reglage_central_rejete`, aucun message |
| mode `local` reçu | libération, `reglage_central_libere` |
| Adopter | droit de saisie locale de la table (`admin.parametres` : motifs, seuils, substitution, texte SMS ; `caisse.remises_auto` : remises ; titulaire : les autres) ; 409 si la version a changé (`ModeleReglageChangeException`) ; `reglage_central_adopte` avec l'employé |

**Contrôles qui demandent la base** (`ControlerAsync`) : catégorie de remise existante, remise locale qui chevauche (mode proposé seulement : en imposé elle est arrêtée), libellé de motif déjà pris par un motif de l'officine hors modèle, motif système non désactivable, effet du tiroir d'un motif existant inchangé, code de motif inconnu qui n'est pas « c_ ».

**Refus de saisie locale** : `ReglageImposeException` → 409, `ErreurDto("Ce réglage est fixé par le serveur central.", "reglage_impose")`, levé AVANT d'écrire dans `EnregistrerSeuils` (partie générale seulement), `EnregistrerMotifReglable` (ajout selon `ajouts_locaux`, motif du central), `GererRemisesAutomatiques` (création, remise du central), `ReglerSurveillance` (un seuil change ; horaires jamais refusés, D7), `ModifierParametreSubstitution`, `ModifierParametresInventaire`, `ParametrerAlertesPsychotropes`, `FormulaireAccordSmsPatient.ModifierTexteAsync`, `PiloterFileSerialisation.EnregistrerAsync`. Fiscal et traçabilité n'ont pas de cas d'usage (lot M7). Correspondance HTTP : `Program.cs` et `ErreursMetier.Filtrer` (nécessaire pour que le 409 existe, le reste de l'API est le lot M5).

**Journal avant / après (D13)** : clés `avant` et `apres` ajoutées aux journaux des 11 cas d'usage (inventaire, substitution, psychotropes, texte SMS, sérialisation, motifs, remises, en plus des seuils et du contrôle interne qui les avaient).

**Notifications** : sujet `reglages_centraux` dans `TypeNotification` (`Abonnables`), `IAlertesTitulaire.ReglagesImposesAsync`, abonnement possible dans l'écran Administration › Notifications (deux chaînes ajoutées au poste, sans autre changement d'écran). `StatutsDescendant.Invalide` ajouté (n'arrête pas l'acquittement).

**Marqueur des outils de rejeu** : `RestaurationPostgres.OptionsScripts`, `scripts/mep/outils_mep.ps1` (donc `mettre_a_jour_base_officine.ps1`), `database/tests/rejeu_complet.ps1` (option `-SansMarqueurReglages`, preuve seulement), `scripts/initialiser_base_locale.ps1`, `scripts/paquet/installer.ps1`. Test : `MarqueurRejeuReglagesTests`. Preuve manuelle (copie de `pharmacie_dev`, 11 tables imposées, synchronisation active, déclencheurs installés) : `rejeu_complet.ps1 -SansMarqueurReglages` échoue (`002_alertes_stock.sql : Réglage fixé par le serveur central`), sans l'option les 139 scripts passent deux fois et structure, données et comptages restent identiques.

**Hypothèses du lot (à confirmer)** : (1) une remise du serveur central retirée du modèle est **arrêtée** (jamais supprimée) ; (2) les règles d'alerte des psychotropes absentes de la base sont créées à l'application (`database/init` n'en sème qu'une) ; (3) la valeur locale d'une table « non adoptée » est réévaluée à chaque passage (pas de colonne de version dédiée dans `reglage_mutualise`, script 141 inchangé) ; (4) en mode proposé, un chevauchement avec une remise locale rejette la table (en imposé, la remise locale est arrêtée).

### 16 bis.12 Réalisation des lots M5 (routes de l'API locale) et M7 (écrans du poste) (2026-11-16)

**Fait** : routes de lecture et d'adoption, saisie locale du réglage fiscal et de la traçabilité (D8), bandeau commun « serveur central » dans les 9 écrans de réglage, deux nouveaux écrans d'administration, sous-rubrique Synchronisation › Réglages mutualisés. **Aucun script SQL nouveau** (les scripts 141 et 010 ont suffi). Aucune librairie ajoutée.

**M5, routes** (`Pharma.Api/Endpoints/ReglagesMutualisesEndpoints.cs`, cas d'usage dans `Pharma.Application/Reglages/ConsultationReglages.cs` et `ParametresFiscalTracabilite.cs`) :

| Route | Droit | Effet et codes |
|---|---|---|
| `GET /api/reglages-mutualises` | `admin.parametres` (le titulaire l'a) | les 11 tables dans l'ordre de la liste fermée : mode en vigueur (« local » tant que la synchronisation est désactivée), mode et version reçus, version appliquée, dates de réception et d'application, état (`aucun`, `applique`, `non_adopte`, `rejete`), motif du refus, origine, auteur au central ; **pour une valeur proposée et non adoptée seulement** : `Ecarts` = valeur de l'officine à côté de celle du modèle (libellé, valeur locale, valeur du central ; 500 au plus). Lecture seule de la base de l'officine, sans verrou ni marqueur (`ILectureValeurReglage`) |
| `GET /api/reglages-mutualises/{table}` | celui de la saisie locale de la table (comme « Adopter ») | même contenu pour une table (utilisé par le bandeau d'un écran) ; 404 si la table n'est pas mutualisable |
| `POST /api/reglages-mutualises/{table}/adopter` | idem | corps `{ versionModele }` ; `AdopterReglageCentral` ; 204 ; **409** si la version a changé ou si la valeur n'est plus à adopter, **409** `reglage_impose` si la table est imposée, **422** si la valeur est refusée par les règles, 403 sans le droit |
| `GET`, `PUT /api/fiscal/parametres` | titulaire | `ModifierParametreFiscal` : `RegleFiscale` (conservation 6 à 30, format `csv-v1`, séparateur, fuseau), fuseau aussi contrôlé par la base (`pg_timezone_names`), garde des tables imposées (`ReglageImposeException` → 409), journal `parametre_fiscal` avant / après ; 204 |
| `GET`, `PUT /api/tracabilite/parametres` | titulaire | `ModifierParametreTracabilite` : `RegleTracabilite`, même garde, journal `parametre_tracabilite` avant / après ; 204 |

**Les réponses des routes existantes indiquent si la table est imposée : un en-tête de réponse, pas un champ.** Choix : les 9 routes de lecture des écrans de réglage (et les deux nouvelles) répondent aussi par l'en-tête `X-Reglage-Central: table=…; mode=impose; etat=applique; version=4; ajouts=1` (filtre `AvecEtatReglageCentral(table)`, `Pharma.Api/Securite/EtatReglageCentral.cs`, format dans `Pharma.Shared/DtosReglagesLocaux.cs`) ; le poste le retient à chaque lecture (`ApiClient.ReglageCentral(table)`) et le bandeau commun grise l'écran sans appel de plus. Pourquoi plutôt qu'un champ dans chaque réponse : trois des neuf routes renvoient une liste ou une page (motifs, remises, règles des psychotropes), qu'il aurait fallu emballer (contrats JSON et tests existants changés), et l'état est le même quelle que soit la forme de la réponse. Seul ajout au JSON : `DuCentral` (fin de `MotifReglableDto` et de `RemiseAutomatiqueDto`, valeur par défaut `false`) pour verrouiller les lignes venues du serveur central. Le détail (écarts, motif de refus) n'est lu (`GET …/{table}`) que pour une valeur proposée non adoptée ou une table refusée. Une lecture impossible de l'état n'empêche jamais la route (journal d'avertissement, réponse sans en-tête : l'écran se comporte comme sans serveur central, le refus 409 reste la garde). Test d'architecture : chacune des 11 tables a son bandeau et son en-tête (`ReglagesCentrauxEcransTests`).

**M7, écrans** (`Pharma.Desktop/Composants`) :
- `Mise/ReglageCentral.razor` : bandeau commun, paramètres `Table`, `Detail`, `OnAdopte`. Imposé : « **Fixé par le serveur central.** » + précision ; champs grisés par un `fieldset disabled` (classe `panneau` conservée) et Enregistrer désactivé. Proposé et différent : « **Proposé par le serveur central** (version n) : n écart(s) avec la valeur de l'officine », boutons « Comparer » (fenêtre : réglage, valeur de l'officine, valeur du serveur central ; écarts seulement, filtrés et paginés) et « Adopter » (409 : message du serveur) ; proposé et égal : rien. Refusé : « Valeur du serveur central refusée par l'officine » + motif. `data-champ="reglage_impose"` sur le bandeau : un refus 409 le met en évidence et le message dit d'actualiser l'écran.
- 9 écrans : Alertes et seuils (bandeau sur Péremption et Stock seulement ; seuils par grossiste et exceptions par produit restent modifiables, Enregistrer actif), Motifs et catégories (motifs du central « Fixé par le serveur central », ajout selon `ajouts_locaux`, D5), Remises automatiques (remises du central verrouillées, aucune création en mode imposé, remises locales arrêtées « Arrêtée par le serveur central », D6), Contrôle interne (seuils grisés, **horaires et Enregistrer toujours actifs**, D7), Substitution (seulement « équivalent exact seulement »), Inventaire › Seuils, Psychotropes › Alertes du registre (zone de l'officine locale), Notifications › Formulaire d'accord SMS, Sérialisation.
- Nouveaux écrans, titulaire : **Réglages fiscaux** (`/admin/reglage-fiscal`, sous Contrôle interne dans `NavAdmin`) et **Traçabilité** (`/admin/reglage-tracabilite`, sous Sérialisation DataMatrix). Fuseau en saisie contrôlée (liste de plusieurs centaines de valeurs : pas de liste déroulante), séparateur et patient obligatoire en liste (valeurs bornées).
- Administration › Synchronisation › **Réglages mutualisés** : tableau des 11 tables (mode, versions, état, reçu le, motif du refus), lien vers l'écran de chaque réglage ; liste fermée donc non paginée (exception inscrite dans `ListesTests`).
- Règles nommées (`conservation_annees`, `format_export_dgi`, `separateur_csv`, `fuseau`, `patient_obligatoire`) : les messages de `RegleFiscale` et `RegleTracabilite` désignent de nouveau leur champ (retiré au lot M1 faute d'écran) ; `ChampsObligatoiresTests` passe, chacun a son `data-champ`.

**Écarts avec la conception et choix à valider** :
1. En-tête de réponse plutôt que champ `mutualisation` dans `GET /api/admin/synchronisation` (non fait : la sous-rubrique lit `GET /api/reglages-mutualises`) et plutôt qu'un champ dans chaque réponse (ci-dessus).
2. `GET …/{table}` remplace `GET …/{table}/modele` : il rend l'état et les écarts déjà calculés côté serveur (valeur de l'officine et du modèle comparées), pas la valeur brute du modèle.
3. Valeur proposée **égale** à celle de l'officine : aucun bandeau (rien à décider) ; l'état « appliqué » se lit dans la sous-rubrique.
4. Le droit de `GET /api/reglages-mutualises` est `admin.parametres` (le titulaire l'a), comme demandé ; la sous-rubrique est dans l'écran Synchronisation (même droit).
5. Mise en page : pour que les écrans tiennent sans défilement avec le bandeau à 1 366 × 768 et 1 536 × 890, `poste.js` accepte `data-hauteur-min` sur un cadre défilant (240 px par défaut, inchangé), Contrôle interne place ses trois panneaux dans un cadre défilant, Psychotropes garde son bouton à côté du titre (`titre-actions-cote`), le texte du formulaire SMS passe de 12 à 8 lignes (il défile dans sa zone).
6. Remise arrêtée sans auteur : libellé « Arrêtée par le serveur central » (une remise arrêtée par un employé garde son nom).

**Essais (2026-11-16)** :
- Tests : `ReglagesMutualisesLocauxTests` (18 : état des 11 tables, écarts d'une valeur proposée, table refusée, comparaison des motifs, des remises et des règles des psychotropes, en-tête, réglage fiscal et traçabilité : valeurs refusées avec leur champ, droit du titulaire, table imposée → 409, journal avant / après) et `ReglagesCentrauxEcransTests` (les 11 tables ont leur bandeau et leur en-tête, routes fiscal et traçabilité réservées au titulaire). Les règles de saisie locale et de modèle n'ont pas changé : les tests des lots M1 et M4 passent sans modification.
- À l'écran, sur **deux copies jetables de `pharmacie_dev`** (`pharmacie_jetable_m7_a` et `_b`, supprimées ensuite ; `pharmacie_dev` et `pharmacie_test` non touchées, synchronisation jamais activée dessus) : synchronisation activée dans la copie (adresse vide : rien n'est envoyé), puis les modèles **reçus comme par la voie descendante** (`sync_recevoir_descendant`, éléments `mutu-v1`), appliqués par l'API au démarrage (aucun serveur central lancé). Copie « a » : les 11 tables **imposées** (valeurs remplacées, remise locale arrêtée, motif local gardé). Copie « b » : les 11 tables **proposées** avec valeurs différentes (`non_adopte`), puis « Comparer », « Adopter » (inventaire : valeur appliquée, `reglage_central_adopte` au journal avec les valeurs avant et après), refus 409 (écran fiscal ouvert, table devenue imposée entre-temps : message « Ce réglage est fixé par le serveur central. Actualisez l'écran… »), puis états **mélangés** (fiscal imposé, inventaire refusé avec son motif, sérialisation libérée, les autres proposées) pour la sous-rubrique.
- Mesures (poste à 1 536 × 890 et 1 366 × 768, clair et sombre, titulaire) : 133 vues, **page à 0 px, aucun élément coupé, aucun chevauchement, aucun élément hors fenêtre** après corrections. Dépassements trouvés puis corrigés : Alertes et seuils › Par grossiste à 1 366 (23 px : bandeau retiré des rubriques sans réglage général), Contrôle interne (22 px à 1 536, 179 px à 1 366 : cadre défilant), Psychotropes (27 / 170 px, puis 24 : bouton à côté du titre, `data-hauteur-min`), Formulaire SMS (65 px à 1 366 : 8 lignes), Motifs en mode proposé (41 px : actions du bandeau en `.lien-bouton`). Captures : `scratchpad\verif-mutualisation\captures` (écran, état, thème, taille dans le nom).
- Non vérifié : le serveur central réel (aucun serveur central lancé ; le lot M8 le fera), le poste d'un employé sans le droit du réglage (Adopter masqué faute de détail lisible : codé, non essayé à l'écran), un modèle de plus de 500 écarts.

### 16 bis.12 Lot M8 : essai de bout en bout avec le vrai serveur central (2026-11-17) — premier passage partiel, repris et clos (voir « Résultat final »)

**Dispositif** : essai local `scripts\dev\essai_cloud_officine.ps1` (central sur 5190, API officine sur 5191, bases jetables `pharmacie_cloud_essai` et `pharmacie_essai_officine`). Compte d'essai `essai_m8` (rôle titulaire) créé dans la base d'essai seulement, mot de passe tiré au hasard en mémoire. Les lectures et écritures de contrôle sont faites par psql (compte `pharma_app`) sur ces deux bases.

**Constats à l'ouverture** : (1) l'officine d'essai n'était **pas activée** (`-Demarrer` s'était arrêté à la demande du mot de passe du titulaire) : l'activation a été faite à la main avec `essai_m8` et les mêmes appels que le script (création de l'officine au central, `PUT …/reglages`, `POST …/activation`) ; (2) vers 00 h 56, **l'essai entier a disparu** (bases, dossier d'essai et processus absents ; cause inconnue, aucune commande d'arrêt n'a été lancée par cet essai). Les scénarios 9 à 13 n'ont donc pas pu être joués ; le scénario 8 est fait à 90 % (voir ci-dessous).

**Défaut bloquant constaté (D-M8-1)** : le script central `010_mutualisation_reglages.sql`, fonction `cloud_descendants` (ligne `'ajouts_locaux', m.ajouts_locaux`), écrit `ajouts_locaux` dans l'élément descendant de **toutes** les tables. Le format `mutu-v1` du domaine l'**interdit** hors motifs et remises (§ 16 bis.12, lot M1 : « interdit ailleurs »). Résultat : l'officine **rejette** tout modèle publié pour les 9 autres tables (`etat = rejete`, motif « « ajouts_locaux » n'existe que pour les motifs et les remises automatiques. »), la table reste **locale** et modifiable alors que le central la croit imposée (PUT local de l'inventaire en « imposé » : 204). Les essais des lots M1 à M7 n'ont pas vu le défaut parce qu'ils fabriquaient leurs éléments eux-mêmes. **Correctif fait (2026-11-17, script central `011_descendant_ajouts_locaux_motifs_remises.sql`)** : dans `cloud_descendants`, n'ajouter `ajouts_locaux` que pour `motif_reglable` et `remise_automatique` (`… || CASE WHEN m.nom_table IN ('motif_reglable','remise_automatique') THEN jsonb_build_object('ajouts_locaux', m.ajouts_locaux) ELSE '{}'::jsonb END`) ; test SQL `database/cloud/tests/011_…_tests.sql` (11 tables, officine libérée comprise) et test C# `DescendantsMutuV1Tests` (base centrale jetable : chaque élément `mutu_<table>` produit par `cloud_descendants`, modèle publié en « propose » puis « impose », passe `ValidationModele.ValiderElement`). Le script `010` a une garde : rejoué après `011`, il garde la fonction corrigée. Passé sur une base centrale jetable (001 à 011, tests 001 à 011) et sur `pharmacie_cloud_dev` (sauvegarde avant, deux passages sans écart, tests réussis) ; un élément déjà publié avec l'ancien format est republié une fois. Contrôle sur un essai neuf (central 001 à 011) : `parametre_inventaire` publié en « propose » puis reçu et appliqué à l'officine (`etat = applique`, version 1). La règle de première réception d'un modèle proposé (D-M8-2) n'est pas touchée. Seul le côté officine C# aurait pu tolérer le champ, mais cela contredit le format strict choisi au lot M1.

**Décision D-M8-2 (titulaire, 2026-11-17) — réalisée (script officine 142)** : à la **première réception** d'un modèle « proposé » pour une table, la valeur du central est **appliquée automatiquement** si la table **n'a jamais été modifiée sur place depuis l'installation** ; si elle l'a été, la valeur locale est gardée et le central se propose par « Adopter » (`non_adopte`). Ensuite D2 s'applique (une valeur jamais modifiée localement suit le central). Un modèle **imposé** est inchangé (toujours appliqué).

**Réalisation (2026-11-17, script `142_mutualisation_reglages_modification_locale.sql`)** : constat : la garde `zz_reglage_garde` (141) ne notait une modification locale que si la table avait déjà un mode ou une valeur du central ; tant qu'aucun modèle n'était reçu, une table modifiée et une table jamais touchée étaient indiscernables (`origine = 'locale'`, pas de date). **Critère retenu** : « modifiée sur place » = `origine = 'locale'` **et** `modifie_localement_le` renseigné ; « jamais modifiée » = `origine = 'central'` ou `origine = 'locale'` sans date. (1) La garde note désormais **toute modification locale réelle** (ajout, suppression, ou mise à jour qui change une valeur de réglage), même en mode `local` et sans modèle reçu ; ne comptent pas : une mise à jour sans changement de valeur, les colonnes `modifie_le` / `modifie_par` / `modifiee_le` / `modifiee_par`, les horaires d'ouverture de `parametre_surveillance` (restent locaux, D7, hors modèle). Les **valeurs d'installation ne comptent pas** : scripts, init (`schema.sql`, `seed_referentiel.sql`), rejeu, restauration et données de démo écrivent avec le marqueur `pharma.reglage_central = on`, jamais noté. (2) **Modifications faites avant 141** : reprise du journal d'audit (dernière écriture du cas d'usage de saisie : `parametre_fiscal`, `parametre_tracabilite`, `seuils_alerte`, `parametre_substitution`, `formulaire_sms_texte_modifie`, `reglages_alertes_psychotropes`, `motif_reglable`, `seuils_inventaire`, `reglages_surveillance` hors horaires seuls, `serialisation_parametres`, `remise_automatique_creee` / `_modifiee` / `_arretee`) pour les tables jamais reçues, d'origine locale et sans date ; rejouable (une date posée n'est jamais écrasée). **Limite** : une modification faite hors application (SQL direct) n'a pas de trace ; elle n'est pas reprise (la table est alors vue comme jamais modifiée). Côté C# : `AppliquerReglagesMutualises.ModifieeLocalement` (remplace le test sur la seule origine). Sur dev et test : la reprise a daté `parametre_alerte` et `parametre_inventaire` (dev), `motif_reglable`, `parametre_alerte` et `remise_automatique` (test) ; les autres tables sont « jamais modifiées ». Preuves : tests SQL 141 et 142 (dev et test), tests C# (première réception appliquée / gardée avant ou après 141, imposé inchangé, deuxième réception D2, version avec base réelle), rejeu complet sur copie jetable (2 passages, structure et données identiques).

| N° | Scénario | Résultat |
|---|---|---|
| 1 | 11 tables locales, rien publié | OK : `--reglages-mutualises` « NON PUBLIÉ » ×11 ; `GET /api/reglages-mutualises` : 11 tables `mode = local`, `etat = aucun` ; `cloud_reglage_modele.valeur` vide ×11 |
| 2 | Modèle proposé, simulation, publication | **KO sur `parametre_inventaire`** (D-M8-1). **OK sur `motif_reglable`** : `--modele-depuis-officine` (49 motifs), simulation sans écriture, publication ; reçu en 13 à 60 s ; en-tête `X-Reglage-Central: table=motif_reglable; mode=propose; etat=applique; version=3; ajouts=1` ; première réception différente : `non_adopte` (D-M8-2, constat avant le script 142 : table jamais modifiée mais d'origine locale ; depuis le 142 elle est appliquée toute seule), après adoption la version 3 est suivie toute seule |
| 3 | Modification locale puis nouveau modèle proposé ; Adopter | OK (motifs) : valeur locale gardée, `non_adopte`, 2 écarts listés ; `POST …/adopter` 204, valeur du central appliquée, journal `reglage_central_adopte` avec valeur avant (locale) et après (central) et l'employé |
| 4 | `--mode-reglage … impose --oui` | OK (motifs) : valeur locale remplacée, `reglage_central_remplace` avant/après, alerte `reglages_centraux` « Réglages fixés par le serveur central » (en file d'alerte faute de destinataire), `PUT /api/motifs/{id}` 409 `reglage_impose`, `UPDATE` et `DELETE` SQL refusés par la garde (« Réglage fixé par le serveur central »). **KO sur `parametre_inventaire`** : rejeté, table non protégée (D-M8-1) |
| 5 | Motifs (D5) | OK : motif du central verrouillé (409), ajout local permis (200), motif retiré du modèle : gardé, actif, modifiable (redevenu local) |
| 6 | Remises (D6) | OK : remise locale « hygiène » arrêtée (`arretee_le` posé, `arretee_par` vide, ligne conservée), remise du central créée (`modele_cle`), message au titulaire, création locale refusée en imposé (409) |
| 7 | Contrôle interne (D7) | **KO** (D-M8-1) : modèle rejeté, table non imposée ; les horaires restent modifiables mais le refus des seuils n'a pas pu être vérifié de bout en bout |
| 8 | Modèle invalide | Refus par `--publier-reglage` : OK (« Les seuils ne peuvent pas être négatifs. », code 1, journal et version inchangés). Voie descendante (élément injecté dans `pharmacie_cloud_essai` par `cloud_publier_descendant`, table `parametre_alerte` jamais publiée pour que le central ne le réécrive pas) : rejet en bloc OK, motif lisible (« l'alerte rouge (20 j) doit être plus courte que l'orange (10 j) »), valeur 180/90 gardée, `reglage_central_rejete` écrit une fois. **Non vérifié** : poursuite de la synchronisation et version non réexaminée (essai disparu pendant l'attente) |
| 9 à 13 | Libération et rétablissement, désactivation, central arrêté, rejeu complet, journal | **Non joués** (essai disparu) |

Ce qui restait pour clore M8 : correctif D-M8-1 fait (voir ci-dessus), puis rejouer les scénarios sur un essai neuf : fait, voir « Résultat final de M8 » ci-dessous.

### 16 bis.12 Lot M8 : résultat final (2026-11-17, essai neuf central 5192 / API 5193, scripts central 001 à 011 et officine jusqu'au 142)

**Dispositif** : essai relancé après le script 142 (`-Arreter`, `-Demarrer -PortCentral 5192 -PortApi 5193`, `TEMP` dédié) ; activation par le compte temporaire du script, chargement complet terminé (lots accusés) avant les scénarios. Compte d'essai `essai_m8` (rôle titulaire) créé dans `pharmacie_essai_officine` seulement, mot de passe tiré au hasard en mémoire, désactivé après chaque série. Attention : `--modele-depuis-officine` lit le **miroir du central**, en retard d'un envoi sur l'officine ; un modèle fabriqué juste après une modification locale ne la contient pas.

**Défaut D-M8-3 (trouvé et corrigé)** : `--liberer-reglages` (et `--retablir-reglages`) republient chaque modèle avec la **même `version_modele`** et un autre mode. `AppliquerReglagesMutualises.AAppliquer` ne retenait une table que si la version reçue était nouvelle, « non adoptée » ou imposée et modifiée localement : une table dont la version courante était déjà appliquée **restait imposée** (409 en saisie, garde SQL active) alors que le central l'avait libérée (`mode_recu = local`, mais `mode = impose`, constat sur 6 tables pendant 6 minutes ; seule la table « non adoptée » avait été libérée). Même défaut au rétablissement. Les tests du lot M4 donnaient une version plus récente à l'élément local, ce que le central ne fait pas. **Correctif** : `AAppliquer` retient aussi une table dont le mode reçu diffère du mode en vigueur (une version rejetée n'est jamais réexaminée) ; tests `Liberation_puis_retablissement_avec_la_meme_version_que_celle_appliquee_sont_executes` et `Examen_d_une_table_selon_son_etat`. Vérifié ensuite sur l'essai recompilé (`-Recompiler`) : libération puis rétablissement effectifs.

| N° | Scénario | Attendu | Constaté (preuve) | Résultat |
|---|---|---|---|---|
| 1 | 11 tables locales ; état du central | 11 `local` à l'officine ; au central rien d'imposé | `reglage_mutualise` : 11 `local`/`aucun` ; `GET /api/reglages-mutualises` : idem ; central : 11 modèles « NON PUBLIÉ », 0 ligne de journal. (Sur cet essai neuf `parametre_inventaire` n'était pas publié : l'ancien contrôle du correctif appartenait à l'essai précédent.) | OK |
| 2 | Première réception (D-M8-2) | table jamais modifiée : appliquée ; table modifiée avant : valeur gardée, `non_adopte` ; en-tête `X-Reglage-Central` | `parametre_serialisation` (jamais modifiée) : modèle différent appliqué en 16 s, `reglage_central_applique` avant/après, en-tête `table=parametre_serialisation; mode=propose; etat=applique; version=1; ajouts=1`. `parametre_formulaire_sms` modifié par `PUT` local avant réception : texte local gardé, `non_adopte`, un écart listé, en-tête `etat=non_adopte`. `parametre_inventaire` (daté par la reprise du 142) : `non_adopte`. `motif_reglable` jamais modifiée : appliquée toute seule | OK |
| 3 | `parametre_inventaire` : modification locale, nouvelle version proposée, Adopter | valeur gardée, `non_adopte` ; Adopter applique ; journal avant/après | v1 : `non_adopte` (9/3100 gardée) ; `POST …/adopter` 204 : 5/2500 ; v2 suivie seule (D2) ; `PUT` local 9/3100 puis v3 : gardée, `non_adopte`, 2 écarts ; Adopter 204 : 7/2700 ; `reglage_central_adopte` : `avant` 9/3100, `apres` 7/2700, employé 6 | OK |
| 4 | `--mode-reglage parametre_inventaire impose --oui` | valeur remplacée, ancienne au journal, message, `PUT` 409, SQL refusé | local 4/1500 remplacé par 7/2700 en 47 s ; `reglage_central_remplace` (avant 4/1500, après 7/2700) ; alerte `reglages_centraux` « Réglages fixés par le serveur central » (en file : aucun destinataire d'essai) ; `PUT /api/inventaires/parametres` : 409 `reglage_impose` ; `UPDATE` et `DELETE` SQL : « Réglage fixé par le serveur central … ne peut pas être modifié » (`reglage_garde()`) | OK |
| 5 | Motifs (D5) | motif du central verrouillé, ajout local permis, motif retiré gardé et modifiable | v1 proposée appliquée d'office ; v2 imposée : `PUT` du motif `c_essai_m8` 409, `POST` d'un motif local 200, `UPDATE` SQL refusé ; v3 sans le motif : `c_essai_m8` gardé, actif, `modele_code_central = f`, `PUT` 200 | OK |
| 6 | Remises (D6) | remise locale arrêtée (jamais supprimée), remise du central créée, création locale refusée | modèle imposé : remise du central `c_m8remise0001` créée (`duCentral`), message au titulaire, `POST` local 409. Remise locale « hygiène » créée pendant la libération puis rétablissement : `arretee_le` posé, `arretee_par` vide, ligne conservée, `GET` : `etat = arretee`, `reglage_central_remplace` | OK |
| 7 | Contrôle interne (D7) | table imposée, seuils refusés, horaires modifiables | `parametre_surveillance` imposée v1 (21 champs, sans horaires) : `PUT` d'un seuil 409 ; `PUT` des seuls horaires 204 (08:15 / 19:45 relus en base) | OK |
| 8 | Modèle invalide | refusé par `--publier-reglage` ; voie descendante : table rejetée en bloc, valeur gardée, synchronisation continue, version non réexaminée | `--publier-reglage` : « Les seuils ne peuvent pas être négatifs. », code 1, journal central (12) et version (4) inchangés. Élément invalide injecté pour `parametre_alerte` : `rejete`, « l'alerte rouge (20 j) doit être plus courte que l'orange (10 j) », 180/90 gardée, un seul `reglage_central_rejete` ; après 140 s toujours un seul rejet, lots 91 → 93 **tous accusés**, `derniere_erreur` vide | OK |
| 9 | `--liberer-reglages` puis `--retablir-reglages` | tables modifiables et valeurs gardées, puis imposition de retour | **KO avant correctif (D-M8-3)**. Après correctif : 7 tables `local` (7 `reglage_central_libere`), `PUT` inventaire 204, remise locale 200, valeurs gardées ; rétablissement : inventaire remis à 7/2700 (`reglage_central_remplace`), remise locale arrêtée, message au titulaire | OK après correctif |
| 10 | Désactiver puis réactiver la synchronisation (D3) | tables modifiables aussitôt, valeurs gardées ; imposition de retour | `PUT …/reglages active=false` 204 : `GET /api/reglages-mutualises` : `mode = local` pour les tables imposées, 6 `reglage_central_libere` (employé 6), `PUT` inventaire 204 (12/3600) ; réactivation : 7/2700 remis (`reglage_central_remplace`), message, `PUT` 409, synchronisation active sans erreur | OK |
| 11 | Central arrêté (`-ArreterCentral`) puis relancé | officine fonctionnelle, refus local actif, envoi repris | port 5192 fermé, 5193 ouvert ; `GET` 200 avec l'en-tête ; `PUT` inventaire 409 et `UPDATE` SQL refusé ; une modification permise (`PUT` traçabilité) passe ; lots non accusés 20 → 30, erreur « Serveur central injoignable » ; `-Demarrer` relance le central seul (PID 82512), 30 → 0 lot non accusé en 4 minutes | OK |
| 12 | Rejeu complet sur copie jetable avec tables imposées | rejeu OK | copie `pharmacie_jetable_rejeu_m8b` (4 tables imposées) : `rejeu_complet.ps1` 2 passages, « structure, données et comptages identiques ». Sans marqueur (`-SansMarqueurReglages`) : échec attendu (garde 141). Constat : la copie de l'essai ne contenait pas le script 143 (autre chantier, absent de `pharmacie_essai_officine` créée avant lui) : le premier rejeu montrait donc un écart de structure sans lien avec la mutualisation ; 143 appliqué une fois à la copie, rejeu identique. Copie supprimée | OK |
| 13 | `--journal-reglages` | qui, quand, avant/après, compte Windows et poste | 40 entrées : date UTC, action (publication, mode, libération, retour), table, officine, mode avant → après, version, auteur, compte Windows et poste (`ADH\aissa sur ADH`), motif | OK |

**Limites constatées** : (1) le journal central porte les modes et versions, pas les valeurs (les valeurs avant/après sont au journal de l'officine) ; (2) un modèle injecté à la main avec un numéro de version que le central n'a pas encore donné (version 1) fait que la première vraie publication portant le même numéro n'est pas réexaminée (version rejetée) : sans objet en production, le compteur du central étant la seule source de version ; contourné dans l'essai par une version suivante ; (3) `-Arreter` répond parfois « droits insuffisants » quand le processus met quelques secondes à se terminer : relancer la commande ; (4) la copie `pharmacie_essai_officine` est faite au démarrage : un script de migration ajouté à `pharmacie_dev` ensuite n'y est pas (écart au rejeu complet).

**État final de l'essai** : à la fin de M8, l'essai a été arrêté et entièrement supprimé (`essai_cloud_officine.ps1 -Arreter` : bases `pharmacie_cloud_essai` et `pharmacie_essai_officine`, rôle `pharma_cloud_essai` et dossier d'essai supprimés ; vérifié : aucun processus, aucun port d'essai, aucune base d'essai). M8 est clos. Le chiffrement (lot 9 du plan général) ne fait **pas** partie de la mutualisation : il reste en dernier, sur « go chiffrement » du titulaire.


### 16 bis.13 Lot M9 : documentation finale (2026-11-17)

Lot clos sans la tâche de chiffrement (décision du titulaire : le chiffrement des données reste le lot 9 du plan général, sur « go chiffrement »).

| Document | Contenu à jour |
|---|---|
| Ce document | Statut « réalisé », résultats de M8 (13 scénarios, défauts D-M8-1 à D-M8-3 corrigés), état final de l'essai. |
| `regles-metier.md` | Règles de la mutualisation (modes local / proposé / imposé, première réception, Adopter, motifs et remises, horaires toujours locaux, libération, désactivation, modèle rejeté), écrites avec le code de chaque lot. |
| `base-de-donnees.md` | Tableau des scripts remis en ordre (001 à 143, dont 101b, 111b et 127 à 140 qui manquaient), scripts 141 à 143 décrits. |
| `securite-et-droits.md` | Rubrique « Réglages mutualisés » : qui publie, qui consulte et adopte, refus local (409 et garde de la base), marqueur `pharma.reglage_central`, traçabilité. |
| `mise-en-production.md` | Scripts centraux 010 et 011 (parcours E, étapes 2.1 et 10.2), étape 9 « Réglages mutualisés » ; scripts officine 141 et 142 passés par la mise à jour de l'officine. |
| `database/cloud/README.md` | Scripts centraux 010 et 011 et leurs tests. |
| `essai-cloud-officine.md` | Essai local des deux serveurs, § 6.9 (mutualisation), suivi en direct et arrêt. |
| `a-faire.md`, `reste-a-faire-synthese.md` | Mutualisation close ; restent les points à décider listés au § 16 bis.12 (limites constatées). |

**Scripts passés** (contrôlé le 2026-11-17) : `pharmacie_dev` et `pharmacie_test` à jour jusqu'à 143 (garde `reglage_garde` du script 142, colonne `ged_copie_minutes` du script 143), les 11 réglages en mode `local`, synchronisation désactivée ; `pharmacie_cloud_dev` : `cloud_descendants` du script 011.

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

## 19. Essai de bout en bout avec les vraies API (2026-11-16, copies jetables, supprimées)

Pharma.Api (officine) et Pharma.Cloud.Api (central) réellement lancés, central créé par `database/cloud` 001 à 009, officine copiée de `pharmacie_dev` avec les scripts jusqu'au 136, autorité et certificat d'essai en fichiers (aucun magasin Windows), adresses locales seulement.

- Activation (officine créée, code adopté, certificat), envoi d'une vente, familles (réglage descendant qui ajoute puis retire les noms des employés), chargements complets (37 060 lignes, 100 tables, 5 en 24 h puis refus 429 au début, plafond d'octets sur 24 h refusé au premier lot, `quota_depasse` au journal du central), lots perdus par le central (lots accusés renvoyés sans reforme, filtrés par la liste en vigueur), colonne ajoutée à une famille après constitution d'un lot, réglage descendant d'une table inconnue (ignoré, journal local, l'envoi continue) : conformes à ce qui est décrit aux § 13, 15 et 18.
- **Défaut trouvé et corrigé (script officine 138)** : les messages d'alerte (`alerte_surveillance.message`, `alerte.message`) citent le nom complet d'un employé ; le filtre des textes libres ne masquait que les patients et les porteurs, donc ce nom partait au central même famille « employes » décochée. `sync_filtrer_texte` remplace désormais tout nom complet d'utilisateur par `[PERSONNEL]` tant que la famille « employes » n'est pas envoyée. Seules les nouvelles lignes de file sont concernées (la file est en ajout seul) ; sur une officine déjà active, un chargement complet renvoie l'état minimisé avec le nouveau filtre.
- Constats sans correction : `sync_sortie` reste en ajout seul (purge après accusé non réalisée à ce stade ; réalisée ensuite, § 20) ; le « en attente » de l'écran tombe à 0 mais la file n'est pas vidée. Par défaut partent aussi les familles « pharmacie », « fournisseurs » et « courriel_commandes » (décision du § 14).

## 20. Lot S3 : supervision et purge de la file (2026-11-16, GO « go S3 », script 140)

Hors périmètre de ce lot : la mutualisation imposé / proposé des réglages (§ 16), qui attend des décisions du titulaire. Aucune connexion nouvelle vers le serveur central ; tout est lu et écrit dans la base de l'officine.

### 20.1 Purge de `sync_sortie` après accusé (§ 12.5, § 13.6)

**Règle** : `sync_purger(délai, n)` retire les lignes de `sync_sortie` des lots **accusés depuis plus longtemps que le délai de conservation**, par **lots entiers**, dans l'ordre des numéros et **sans trou** à partir du dernier lot déjà purgé (`sync_parametre.purge_epoque`, `purge_dernier_lot`). Jamais une ligne hors lot, jamais une ligne d'un lot non accusé, jamais un lot qui contient des lignes d'un chargement complet non terminé (la purge s'arrête avant lui). Les gardes ne changent pas : celle de `sync_sortie` (script 126) n'autorise toujours la suppression d'une ligne qu'après l'accusé de son lot (une erreur de l'application ne peut donc pas purger une ligne non accusée) ; celle de `sync_lot` (126, 130, 136) interdit toujours de supprimer ou de modifier un lot : **la ligne du lot reste** (numéro, empreinte, `colonnes_retirees` et `familles` figées, accusé). Le renvoi à l'identique d'un lot (§ 18, point 1) reste donc possible tant que ses lignes sont là, et impossible ensuite, ce que l'application sait (`LotEnregistre.Purge`).

**Ce que l'officine garde pour la reprise après une restauration du central — décision : une durée (30 jours), puis le chargement complet.** Trois choix ont été comparés :

| Choix | Verdict |
|---|---|
| Ne jamais purger | La file grossit sans borne (une ligne par modification de chaque table envoyée) : écarté. |
| Garder le dernier chargement complet (ses lignes d'instantané) | Revient à garder en permanence une copie minimisée de toute la base, pour un cas rare ; et inutile, car la base de l'officine **est** la source de cet état : écarté. |
| **Garder chaque lot accusé N jours ; au-delà, recourir au chargement complet** | **Retenu.** |

Justification : (1) la file n'est qu'une copie de ce que la base de l'officine contient déjà ; (2) le serveur central ne garde que **l'état final de chaque donnée** (§ 12.1) : un chargement complet lui redonne exactement l'état qu'il aurait eu en recevant tous les lots ; la seule chose qu'il ne retrouve pas est la **preuve brute** des lots perdus dans sa propre restauration (`lot_recu`), et la base de l'officine, qui fait foi, garde les journaux et leurs chaînes ; (3) le délai couvre le cas courant, une restauration du central depuis une sauvegarde récente : les lots accusés depuis sont renvoyés **à l'identique**, sans recharger toute l'officine ; (4) 30 jours est le délai de garde déjà proposé au § 3.4. **[À VÉRIFIER]** : la fréquence et la durée des sauvegardes de la base centrale chez l'hébergeur ; si l'hébergeur garde ses sauvegardes plus de 30 jours, allonger le délai ou accepter le chargement complet.

**Après la purge, côté envoi** (`EnvoyerLotsSync`) : si le serveur central répond « trou » et que parmi les lots manquants l'un est **purgé** (ou absent), l'officine ne renvoie rien d'incomplet : elle demande un **chargement complet** (motif « lots perdus par le serveur central », § 13.3 : le central abandonne les lots perdus, reprend après le dernier lot accusé ici, et reçoit l'état actuel). Un chargement complet déjà tenté sans succès arrête l'envoi avec la procédure de `mise-en-production.md`. Un lot purgé n'est jamais envoyé (arrêt avec message). Coût d'un chargement complet : limites du § 17 et du § 18 (5 chargements et 16 Gio sur 24 h, environ 10 000 lignes par minute mesurées au § 13.5).

**Réglages** (`sync_parametre`, écran Supervision et purge) : purge active (**oui**), conservation **30 jours** (1 à 365). **Valeurs de départ proposées, non décidées par le titulaire.** Désactivée : tout est gardé.

**Exécution** : tâche de fond `PurgeSynchronisation` (5 minutes après le démarrage, puis toutes les heures), `PurgerSortieSync` : paquets de 20 lots par transaction (jusqu'à 10 000 lignes), 40 lots au plus par passage ; elle prend le **verrou d'envoi** (`VerrouSync`) et passe son tour si un passage d'envoi est en cours (un renvoi de lots manquants ne lit jamais des lignes en train d'être purgées) ; rien sur une base en lecture seule (2e serveur en veille : la purge se réplique). Journal du serveur : nombre de lots et de lignes, aucune donnée ; une erreur est mémorisée sans détail (`derniere_purge_erreur`) et affichée à l'écran. L'espace libéré est réutilisé par PostgreSQL (nettoyage automatique) ; l'écran montre la taille de la file.

**Limites** : la purge ne traite que l'**époque courante** (le repère est par époque) ; à la création d'une nouvelle époque après restauration de la base de l'officine (§ 13.6, réalisée : § 30.6), les lots de l'ancienne époque sont gardés (jamais purgés) et la purge repart sur la nouvelle époque (`purge_epoque` à NULL, `purge_dernier_lot` à 0). `sync_lot` n'est jamais purgée (environ 100 octets par lot).

### 20.2 Bandeau sur les postes (§ 8)

« **Synchronisation en attente depuis hh:mm (n opérations).** » en bas de la fenêtre, au même emplacement que « Serveur injoignable » (il le remplace quand le serveur est injoignable), style `bandeau-attention`, sans bouton : **jamais bloquant**. Affiché seulement si la synchronisation est **activée** (`sync_parametre.active`), si la base n'est pas en lecture seule et si la plus ancienne opération non accusée a au moins **le seuil** (60 minutes, **valeur de départ de la conception, § 8**, réglable de 1 minute à 7 jours). « En attente » = lignes hors lot + lignes des lots non accusés ; « depuis » = plus ancienne de ces lignes. Réservé au **titulaire et aux rôles qui ont le droit `sync.suivre`** (« Voir le bandeau de synchronisation », script 140, donné au titulaire ; le titulaire l'attribue aux rôles choisis dans Administration › Droits par rôle ; droit non sensible). Route `GET /api/synchronisation/attente` (tout employé connecté ; la réponse est vide sans le droit) ; état mémorisé 30 secondes côté API pour ne pas interroger la base à chaque poste ; la barre d'état l'actualise une fois par minute ; une erreur de lecture masque le bandeau sans rien bloquer.

### 20.3 Alertes au titulaire (§ 8)

Par le mécanisme de notification existant (`IAlertesTitulaire`, nouveau sujet **« synchronisation »**, abonnable dans Administration › Notifications ; SMS court sans accents si l'option « SMS sauvegarde » est cochée et un modem réglé, sinon e-mail). **Au plus une alerte par heure et par sujet** tant que le problème dure (date de la dernière alerte par sujet dans `sync_parametre`, jamais remise à zéro : un problème qui revient dans l'heure ne rappelle pas) ; tâche `SurveillanceSynchronisation` chaque minute ; rien si la synchronisation est désactivée ou la base en lecture seule.

| Sujet | Condition | Seuil |
|---|---|---|
| `retard` | plus ancienne opération non accusée au-delà du seuil | **24 h** (valeur de départ, réglable 1 à 720 h) |
| `erreur` | passages d'envoi en erreur sans interruption (début gardé par `sync_noter_passage`) | **60 min** (valeur de départ, réglable 1 à 10 080 min) ; l'erreur de quota est traitée à part |
| `certificat` | certificat à moins de **30 jours** de l'échéance, ou échu | 30 jours, fixe. **Vérifié dans le code** : le renouvellement automatique démarre 60 jours avant l'échéance (`EnvoyerLotsSync.RenouvelerAvant`) et le serveur central ne le permet que dans les 60 derniers jours (§ 12.6) : une alerte à 30 jours signale un renouvellement qui échoue depuis 30 jours |
| `quota` | le serveur central refuse l'envoi pour quota ou plafond (quota du jour, plafond d'un chargement, chargements sur 24 h) | immédiat |

Comme les autres alertes : dédoublonnage par sujet, **destinataires vérifiés** seulement (`Destinataire.Recoit`), alerte qui ne part vers personne ou mise en file impossible **signalée au bandeau de l'accueil du titulaire** (`alerte_non_envoyee`), jamais bloquante. Contenu : nombres, dates, durée et dernier message technique de l'envoi (e-mail seulement) ; **aucune donnée de patient**. **Détection du quota** : un refus 429 **accompagné d'un motif** (ProblemDetails `detail`) est un quota ou un plafond ; la limite de débit seule répond 429 sans motif et n'alerte pas **[À VÉRIFIER avec le serveur central réel : un répartiteur d'hébergement qui répondrait 429 avec un motif ferait alerter à tort ; un champ explicite dans la réponse du central serait plus sûr]**. Premier abonnement : les destinataires déjà abonnés aux alertes de sauvegarde (alertes techniques) sont abonnés au nouveau sujet par le script 140, une seule fois **(choix du développement, à confirmer)**.

### 20.4 Réglages et écran

Administration › Synchronisation › **Supervision et purge** (droit `admin.parametres`, réglages journalisés `reglages_supervision_synchronisation`) : seuils du bandeau, du retard et de l'erreur persistante ; purge active et conservation ; lecture : opérations en attente et depuis quand, erreur en cours, dernière purge (date, lignes), lots purgés (jusqu'au lot n°, total de lignes), taille de la file (lignes estimées, octets), dernière erreur de purge. Route `PUT /api/admin/synchronisation/supervision` ; ces données figurent aussi dans `GET /api/admin/synchronisation` (champ `supervision`). Vérifié sur copie jetable à 1 366 × 768 et 1 536 × 890, clair et sombre : la page ne défile pas ; à 1 536 × 890 rien ne défile (hors le menu de gauche de l'Administration, commun à tous ses écrans) ; à 1 366 × 768 le cadre des deux panneaux défile de 97 px (le bandeau du bas en recouvre 40). La sous-rubrique « Chargement » propre (§ 13.6) reste facultative et non faite.

### 20.5 Essais (2026-11-16, copie jetable de `pharmacie_dev`, supprimée ; `pharmacie_dev` et `pharmacie_test` intactes : synchronisation inactive, 0 lot, 0 ligne de file)

API et poste réellement lancés (ports 5921 et 5922), file alimentée par les vrais déclencheurs (25 modifications), deux lots accusés (il y a 40 et 5 jours), 5 opérations en attente depuis 3 h, un destinataire vérifié sur le sujet « synchronisation » (aucun serveur SMTP ni modem : les messages restent en file) : bandeau visible au titulaire (mesures et captures en clair et en sombre) ; alerte d'erreur persistante envoyée une fois (rien de plus en plus de 2 minutes), alerte de retard après abaissement du seuil à 2 h ; **tâche de purge réelle** : le lot accusé il y a 40 jours purgé (10 lignes), celui de 5 jours et les lignes en attente intacts, journal sans donnée ; réglages enregistrés depuis l'écran (valeur hors bornes refusée avec message), audit écrit. Tests : SQL `140_synchronisation_supervision_purge_tests.sql` (purge, gardes, chargement en cours, bornes, alertes, droit) ; C# `SupervisionSynchronisationTests` (bandeau, alertes, une par heure, purge) et `ChargementSynchronisationTests` (lot purgé : chargement complet sans renvoi, lot conservé : renvoi à l'identique, passage noté pour la supervision). **Non essayé** : un vrai refus 429 du serveur central, une restauration réelle du central après purge, un certificat à moins de 30 jours (cas couverts par les tests unitaires seulement).

## 21. Chiffrement des données nominatives (option C, lot 9)

Conception et décisions DC1 à DC7 (acceptées le 2026-11-17) : [chiffrement-data.md](chiffrement-data.md). Rien n'est codé : lots C1 à C5 sur « go chiffrement » seulement.

## 22. Lot S4 : réalisé (2026-11-17, GO « go S4 option A ») — vérification d'intégrité au serveur central

Décisions du titulaire : option A pour le registre des psychotropes (rangs et chaînage seulement, déjà validée le 2026-11-16) ; plan
validé avec les recommandations S4-D1 à S4-D5 ; « filtré, jamais vidé » pour les textes libres des journaux chaînés (question posée
pendant le lot, voir 22.2).

### 22.1 Ce qui est vérifié, et comment

| Contrôle | Où | Règle |
|---|---|---|
| Rangs sans trou, origine, chaînage | `journal_vente`, `journal_registre_psychotrope` | Rang n+1 après n ; rang 1 chaîné sur l'origine (octets nuls, longueur de l'algorithme déduite de l'empreinte : 32 = SHA-256, 64 = SHA-512, S4-D2) ; empreinte précédente = empreinte de la ligne d'avant |
| Empreintes recalculées | `journal_vente` (mode « complet ») | Même forme canonique v1 que l'officine (`cloud_integrite_expression`, copie de `integrite_expression`, référentiel `cloud_chaine_reference` = colonnes des scripts officine 112 et 117) |
| Registre | `journal_registre_psychotrope` (mode « liaison ») | Rangs et chaînage seulement : les colonnes du porteur ne partent pas (option A) |
| Ancrage des sceaux | `sceau_integrite` | La ligne scellée (rang, id) existe au central avec l'empreinte du sceau ; le HMAC reste vérifié dans l'officine (clé jamais envoyée) |
| Continuité des tickets « T- » | `compteur_ticket` (montante depuis le script 144, S4-D1), `vente`, `journal_vente`, `annulation_vente` | Même contrôle que `journal_vente_continuite()` : numéro manquant, ticket au-delà du compteur, vente ou annulation sans ligne au journal |
| Vérification croisée | `verification_integrite.dernier_rang`, `dernier_hash` (script 144) | La fin de chaîne constatée par la vérification de l'officine doit être la ligne reçue au même rang : sinon « réécrite après envoi » ; une ligne reçue au-delà, enregistrée avant la vérification, signale des lignes perdues par l'officine |
| Ajout seul au central | déclencheur `trg_cloud_integrite_ajout_seul` sur `journal_vente`, `journal_registre_psychotrope`, `sceau_integrite`, `cloture_fiscale`, `verification_integrite` | Une ligne reçue avec un autre contenu n'est **pas** appliquée (l'original reste, preuve ; anomalie « modification refusée ») ; une suppression reçue non plus ; renvoi identique (chargement complet) accepté ; seul un texte filtré différent (listes de l'officine changées) : original gardé sans anomalie |

- **Délai d'attente** (S4-D4) : un rang, un ticket, une ligne scellée ou une fin de chaîne absents ne sont une anomalie qu'après
  `cloud_parametre.integrite_delai_heures` (24 h) : le temps que tout arrive.
- **Points de reprise** : `cloud_integrite_etat` (par chaîne), `cloud_integrite_sceau` (par sceau), `cloud_integrite_officine`
  (tickets, ventes contrôlées, dernière vérification de l'officine lue). Une chaîne en rupture n'est plus relue au passage
  incrémental ; la vérification complète repart du rang 1 et retrouve aussi une ligne modifiée **directement dans la base centrale**.
- **Anomalies** : `cloud_anomalie_integrite`, ajout seul, cloisonnée par officine, notée **une seule fois** (index unique), inscrite au
  journal de sécurité (`integrite_anomalie`, sans valeur de donnée). L'API n'a aucun droit de lancer la vérification ni de modifier
  une anomalie ; elle n'en écrit que par le déclencheur d'ajout seul.

### 22.2 Textes libres des journaux chaînés : filtrés, jamais vidés (script 144)

Le motif de `journal_vente` et du registre est un texte libre filtré avant l'envoi (script 127). Le central ne peut pas recalculer
l'empreinte d'une ligne dont le motif a été changé par le filtre. Décision du titulaire (2026-11-17) : pour les **journaux chaînés**
(`chaine_integrite`), le texte est **toujours filtré, jamais vidé**, même avec l'option « textes libres retirés » (`sync_minimiser`
redéfinie). Ainsi un motif vide au central est vraiment vide ; un motif qui porte un marqueur du filtre (`[COURRIEL]`, `[TÉL]`,
`[N°]`, `[PERSONNEL]`, `[NOM]`, code `P-…`) rend la ligne « non vérifiable » (comptée à part, chaînage vérifié, aucune alerte) ;
toute autre différence est une anomalie. Les autres tables gardent l'option « retirés ».

### 22.3 Résultat vers l'officine, alerte, écran

- `cloud_verifier_integrite` publie un résumé (`integrite-v1` : statut ok / attente / anomalie, chaînes, sceaux, tickets, nombre
  d'anomalies, 20 dernières) par la voie descendante existante : `reglage_central`, clé `integrite_centrale`. **Aucun changement
  du contrat ni des échanges.**
- Officine : une alerte au titulaire (sujet « Synchronisation ») **par nouvelle anomalie**, sans délai d'une heure ; la dernière
  signalée est gardée dans `sync_parametre.integrite_anomalie_signalee` (`sync_noter_integrite_signalee`, script 144).
- Écran : Administration › Synchronisation › **Intégrité au central** (droit `admin.parametres`) : état, dernières vérifications,
  sceaux, tickets, chaque journal (lignes vérifiées, mode, texte filtré), tableau des 20 dernières anomalies. Lecture seule.

### 22.4 Administration du central (S4-D3, S4-D5)

- `Pharma.Cloud.Api.exe --verifier-integrite` : tâche planifiée **toutes les 15 minutes** (incrémental) ;
  `--verifier-integrite --complete` **chaque semaine** (depuis le rang 1) ; `--officine <code>` pour une seule officine.
  Une officine en erreur n'arrête pas les autres (code de sortie 1).
- `Pharma.Cloud.Api.exe --anomalies-integrite [--officine <code>] [--nombre <n>]` : anomalies, les plus récentes d'abord.
- Écran des anomalies au central : plus tard, avec les écrans du lot S7.

### 22.5 Scripts, code, essais

- Central `012_verification_integrite_central.sql` (+ tests `tests/012_…`) ; officine `144_synchronisation_verification_integrite_centrale.sql`
  (+ tests `database/tests/144_…`), `database/init` à jour ; tables miroir régénérées (`compteur_ticket`, colonnes de
  `verification_integrite`) ; le générateur des tables miroir prend désormais en compte un reclassement
  (`ON CONFLICT … DO UPDATE SET sens`).
- Les deux scripts posent `SET client_encoding = 'UTF8'` : constaté le 2026-11-17, `psql` sous Windows avec une sortie redirigée
  lit le fichier en WIN1252 et abîme les accents (ici les marqueurs du filtre).
- **Essai de bout en bout** (2026-11-17, copies jetables de `pharmacie_test` et d'un central neuf, supprimées) : lot construit avec la
  vraie minimisation de l'officine (7 lignes de journal dont un motif filtré, registre, 2 sceaux réels, vérification locale,
  compteur, 3 330 ventes) : empreintes recalculées **identiques** à celles de l'officine, ligne au motif filtré « non vérifiable »,
  sceaux ancrés, tickets vérifiés, aucune anomalie ; résumé accepté et relu par l'officine ; commandes essayées, ligne altérée dans
  la base centrale trouvée par la vérification complète. Écran mesuré à 1 536 × 890 (aucun défilement de page), captures en
  clair et sombre.
- **Non essayé** : volume réel d'une officine (durée d'une vérification complète sur des centaines de milliers de lignes)
  **[À VÉRIFIER]** ; tâche planifiée réelle sur le serveur central.

## 23. Lot S5 : analyse et décisions (2026-11-18, « ok recommandations S5 »), rien n'est codé

**Déjà fait** : la voie descendante générique (§ 15) et la mutualisation des 11 tables de réglages (§ 16 bis, lots M1 à M9), qui
couvrent les « réglages » de la décision 3. **Reste** : prix, commandes en brouillon, employés et droits, référentiels nationaux et
nomenclature.

### 23.1 Ordres restants

| Ordre | État actuel | Règle (§ 4) | Point dur |
|---|---|---|---|
| Prix (`produit.prix_public`, `tarif_reference`) | `produit` remonte (bidirectionnelle) | Appliqué par l'API locale avec les contrôles existants (`ModifierProduit` : prix > 0, produit remboursable ⇒ tarif de référence) **seulement si la valeur locale n'a pas changé** depuis la version vue au central ; sinon refusé « modifié dans l'officine », sans fusion | Désignation du produit (S5-D2) ; marges réglementées **[À VÉRIFIER]** |
| Commandes en brouillon (`commande_fournisseur`, `ligne_commande`) | Statut `brouillon` existant | Créées en brouillon, envoyées au grossiste depuis l'officine seulement | Le central ne choisit pas les n° locaux : identifiant central (uuid) porté par l'ordre, lignes créées par l'officine |
| Employés et droits (`utilisateur`, `role`, `role_permission`) | Remontent sans mot de passe | Même règle que les réglages | Jamais de mot de passe ni de code comptoir envoyé ; compte créé à distance activé dans l'officine ; titulaire et super admin jamais modifiables à distance |
| Référentiels nationaux et nomenclature (18 tables descendantes, groupe 1 du § 16) | Scripts ou import local | Le central fait foi ; l'officine garde ses produits ; un changement de classe passe par l'état « à vérifier » existant | Volume : `nomenclature_produit` = 9 555 lignes ; un élément descendant est limité à 64 Kio : envoi découpé |

### 23.2 Socle commun (S5.1)

- Central : table des ordres (identifiant, officine, type, cible, version vue, données, auteur, créé le, expire le, statut
  `en_attente` / `applique` / `refuse` / `expire`, motif) ; ordres publiés par la voie descendante (`desc-v1`).
- Officine : ordres reçus rangés, appliqués par l'API locale **en C#** (mêmes cas d'usage et contrôles que les écrans) ; un ordre
  aberrant est refusé et journalisé (§ 6.1, point 9) ; résultat remonté au central par une table montante ; trace des deux côtés
  (qui, quand, valeur avant / après) ; alerte au titulaire.
- Avant les écrans S7 : commandes d'administration du central avec `--auteur` obligatoire (D10).

### 23.3 Décisions du titulaire (2026-11-18, recommandations acceptées)

| N° | Sujet | Décision |
|---|---|---|
| S5-D1 | Délai d'expiration d'un ordre non appliqué | **7 jours**, puis « expiré » (tracé, visible au central) |
| S5-D2 | Désignation du produit d'un ordre de prix | **Par officine** (n° local du produit) ; le prix « toutes les officines » par code national viendra avec S7 |
| S5-D3 | Employés et droits | **Confirmation du titulaire** dans l'officine avant application ; exception : le **blocage** d'un employé s'applique tout de suite (protecteur) |
| S5-D4 | Commande en brouillon reçue | Alerte au titulaire ; envoi au grossiste uniquement depuis l'officine |
| S5-D5 | Nomenclature | Diffusée par le central en plusieurs morceaux ; l'import local par fichier reste possible |

### 23.4 Découpage (rien n'est lancé)

S5.1 socle des ordres → S5.2 prix → S5.3 référentiels et nomenclature → S5.4 commandes en brouillon → S5.5 employés et droits.
Chaque lot sur « go » du titulaire, plan présenté avant tout code.

## 24. Lots S5.1 et S5.5 : réalisés (2026-11-18, « ok plan S5.1 + S5.5 ») — ordres du central, employés et droits

Décisions : S5.1-D1 alerte au titulaire pour un ordre à confirmer, refusé ou expiré, et pour un blocage appliqué (un ordre appliqué
est seulement tracé et visible) ; S5.1-D2 l'ordre transporte la **valeur attendue** ; S5.1-D3 date limite du central (7 jours),
contrôlée aussi avec l'horloge de l'officine ; S5.1-D4 type « essai » ; S5.5-D1 don et remise par employé exclus ; S5.5-D2 employé
désigné par son n° local, rôle par son code ; S5.5-D3 blocage immédiat accepté.

### 24.1 Parcours d'un ordre

1. **Central** : `cloud_creer_ordre` (commande `--ordre-…`, auteur obligatoire) contrôle l'ordre, calcule la valeur attendue depuis
   les tables miroir (rôle par code, état actif, nom s'il a été reçu ; droits actuels du rôle triés), l'écrit dans `ordre_central`
   (journal `ordre_central_journal`, ajout seul ; journal de sécurité `ordre_cree`) et le publie par la voie descendante (élément
   `ordre_central`, clé = identifiant, format `ordre-v1`). Refusés dès la création : compte ou rôle de titulaire, employé ou rôle
   inconnu, employé déjà bloqué ou déjà actif, type inconnu, auteur absent. Jamais de mot de passe ni de code comptoir (champ retiré).
2. **Officine, réception** (`sync_recevoir_descendant`, script 145) : la version du script 141 est renommée
   `sync_recevoir_descendant_reglages` et un aiguillage la remplace ; un ordre bien formé est rangé dans `sync_ordre_recu`
   (« à appliquer ») ; un élément mal formé est refusé et journalisé (`sync_journal_descendant`) ; un ordre annulé ou expiré au
   central avant d'être traité passe à l'état correspondant, sans rien appliquer.
3. **Officine, passage automatique** (`AppliquerOrdresCentraux`, après chaque passage d'envoi, aussi quand la synchronisation est
   désactivée, jamais sur une base en lecture seule) : ordre périmé → « expiré » ; type inconnu de cette version → « refusé » ;
   création, modification, déblocage, droits → « à confirmer » ; essai et blocage → appliqués tout de suite.
4. **Titulaire** (Administration › Synchronisation › Ordres du central) : **Confirmer** (avec le mot de passe provisoire, saisi deux
   fois, pour une création ; l'employé le change à sa première connexion) ou **Refuser** (motif de 3 à 300 caractères, envoyé au
   central après filtrage des textes libres). Une erreur de saisie laisse l'ordre à confirmer ; une valeur changée dans l'officine,
   une cible interdite ou introuvable le refuse définitivement.
5. **Application** par les cas d'usage existants (`CreerUtilisateur`, `ModifierUtilisateur`, `EnregistrerDroits`) **au nom du
   titulaire qui confirme** ; le blocage change seulement l'état actif (les sessions de l'employé tombent à l'appel suivant).
6. **Trace et retour** : `sync_ordre_noter` change l'état, écrit `sync_ordre_resultat` (montante : état, motif, date) et une ligne au
   journal des actions (`ordre_central_<état>`, utilisateur NULL si automatique, auteur au central, valeurs attendues et voulues) ;
   au central, le résultat reçu met l'ordre à jour (déclencheur `trg_ordre_resultat_recu`, journal « officine »).
7. **Expiration au central** : `--expirer-ordres` (tâche planifiée, chaque heure) passe les ordres non traités à « expiré » et les
   republie.

### 24.2 Types d'ordres

| Type | Données | Valeur attendue | Application |
|---|---|---|---|
| `essai` | — | — | Tout de suite, rien ne change |
| `employe_creer` | nom, identifiant, rôle (code) | — | Après confirmation, avec mot de passe provisoire |
| `employe_modifier` | nom et/ou rôle | rôle, actif, nom (s'il était reçu) | Après confirmation |
| `employe_bloquer` | — | rôle, actif | Tout de suite (alerte au titulaire) |
| `employe_debloquer` | — | rôle, actif | Après confirmation |
| `droits_role` | liste complète des permissions | permissions actuelles du rôle | Après confirmation |

**Si le central était piraté** : il ne peut que **proposer** (confirmation du titulaire), sauf **bloquer** un employé, jamais un
titulaire, ce que le titulaire corrige sur place.

### 24.3 Scripts, code, essais

- Central `013_ordres_vers_officines.sql` (+ tests) ; officine `145_synchronisation_ordres_central.sql` (+ tests), `database/init`
  régénéré depuis `pharmacie_test` ; `002` régénéré (`sync_ordre_resultat`).
- Officine : `OrdresCentraux.cs` (types, application, confirmation, refus, résumé), `OrdresCentrauxSql`, routes
  `GET /api/admin/synchronisation/ordres`, `POST …/ordres/{id}/confirmer`, `POST …/ordres/{id}/refuser` ; écran « Ordres du central »
  (paginé, filtré par état, fenêtre de confirmation). Central : `OrdresAdministration` (commandes).
- **Essai de bout en bout** (2026-11-18, copies jetables de `pharmacie_test` et d'un central neuf, supprimées) : rôles, employés et
  droits envoyés au central ; cinq ordres créés et descendus ; la vraie API locale (port 5085) applique l'essai et le blocage, met les
  trois autres à confirmer ; confirmation par l'API (création avec mot de passe provisoire, modification de rôle), refus des droits
  avec motif, mot de passe trop court refusé (l'ordre reste à confirmer) ; résultats remontés : le central passe les ordres à
  « appliqué » / « refusé » ; déblocage d'un titulaire refusé dès la création au central. Écran mesuré à 1 536 × 890 (aucun
  défilement de page), captures en clair et sombre, fenêtre de confirmation comprise.
- **Non essayé** : passage réel par l'échange HTTPS avec certificat (la descente a été rejouée par SQL) **[À VÉRIFIER avec
  `scripts/dev/essai_cloud_officine.ps1`]** ; tâche planifiée réelle `--expirer-ordres`.
- **Reste de S5** : S5.2 prix, S5.3 référentiels et nomenclature, S5.4 commandes en brouillon (§ 23.4).

## 25. Lot S5.2 : réalisé (2026-11-18, « ok plan S5.2 ») — prix d'un produit

Décisions : S5.2-D1 application automatique si l'écart reste dans **20 %**, au-delà confirmation du titulaire ; S5.2-D2 seuls le prix
public et le tarif de référence changent par ordre (jamais le remboursement ni la TVA) ; S5.2-D3 prix appliqué tracé et visible, sans
alerte ; S5.2-D4 aucun contrôle de marge tant que les règles algériennes (prix public algérien de la vignette, marges) ne sont pas
établies **[À VÉRIFIER : comptable ou juriste]**.

- **Central** (script `014`) : type `prix_produit` (cible = n° local du produit) ; données = nouveau prix et/ou tarif (DA, > 0, deux
  décimales au plus) et nom du produit (affichage) ; valeur attendue = prix et tarif actuels du miroir ; refusé à la création si le
  produit est inconnu, si un montant est invalide, ou si rien ne change. `cloud_creer_ordre` redéfinie (version 013 + cette branche),
  contrainte des types élargie avec garde. Commande `--ordre-prix --officine <code> --produit <n°> [--prix <DA>] [--tarif <DA>] --auteur <nom>`.
- **Officine** (aucun nouveau script : socle du § 24) : écart de plus de 20 % sur le prix ou le tarif, ou valeur actuelle absente ou
  nulle → « à confirmer » ; sinon appliqué tout de suite. Écriture limitée au prix et au tarif (`ModifierPrixAsync`) : **seulement si
  les valeurs actuelles sont encore celles attendues**, vérifié sous verrou de la ligne ; mêmes contrôles que la fiche produit (prix et
  tarif > 0) ; journal `prix_modifie` (avant, après, tarif avant / après, source « ordre du serveur central », auteur, n° d'ordre), au
  nom du titulaire qui confirme, sans auteur local sinon.
- **Écart avec le plan** : une baisse de prix venue du central **ne compte pas** dans la surveillance des baisses de prix des employés
  (elle ne concerne que les employés autres que le titulaire) ; elle reste visible dans « Ordres du central » et au journal.
- **Journal des actions** (corrigé le 2026-11-18, « ok journal ») : l'écran montrait déjà les lignes sans employé, mais avec « — » ;
  il affiche désormais « Serveur central (auteur) » pour une action faite par un ordre sans auteur local, le filtre « Employé »
  les retrouve (« central » ou le nom de l'auteur), et les actions `ordre_central_…` ont un libellé et un détail lisibles (type,
  cible, auteur, motif).
- **Essai de bout en bout** (2026-11-18, copies jetables, supprimées) : +10 % appliqué tout de suite (120 → 132 DA, journal sans
  auteur local) ; +50 % mis à confirmer puis confirmé par le titulaire (350 → 525 DA, journal à son nom) ; prix changé dans l'officine
  avant l'arrivée de l'ordre → refusé, rien écrit ; résultats remontés au central (appliqué, appliqué, refusé). Écran « Ordres du
  central » mesuré à 1 536 × 890, captures en clair et sombre.

## 26. Lot S5.3 : réalisé (2026-11-18, « ok plan S5.3 ») — nomenclature nationale envoyée par le central

Décisions : S5.3-D1 la nomenclature d'abord, les autres référentiels (wilayas, organismes, substances surveillées…) plus tard, un
par un ; S5.3-D2 confirmation du titulaire ; S5.3-D3 fichier officiel gardé une fois au central et téléchargé par l'officine,
empreinte SHA-256 vérifiée ; S5.3-D4 l'import manuel reste possible ; S5.3-D5 version déjà chargée : rien ne change.

- **Central** (script `015`) : table `referentiel_fichier` (classeur du MIPH gardé une fois pour toutes les officines : libellé,
  nom, contenu, empreinte SHA-256 unique, taille de 20 Mo au plus **[À VÉRIFIER : plafond à confirmer avec les prochains fichiers
  du MIPH ; celui de juin 2026 fait 1,4 Mo]**, auteur) ; contrôle à l'enregistrement : archive .xlsx, non vide. Type d'ordre
  `nomenclature` (données : n° du fichier, libellé, nom, empreinte, taille). `cloud_referentiel_contenu` remet le contenu à l'API
  **seulement pour l'officine de la transaction et seulement si un de ses ordres désigne ce fichier** ; l'API n'a aucun droit sur
  la table. Commandes `--importer-nomenclature <fichier.xlsx> --libelle <libellé> --auteur <nom>` puis
  `--publier-nomenclature <n°> (--officine <code> | --toutes) --auteur <nom>` (un ordre par officine en service).
- **Route** `GET /api/v1/referentiels/{n°}` : mêmes protections que les autres (certificat de l'officine, jeton, limite de débit) ;
  404 si le fichier n'est pas destiné à l'officine ; téléchargement et refus inscrits au journal de sécurité.
- **Officine** (aucun nouveau script) : l'ordre attend toujours le titulaire. À la confirmation, l'API télécharge le fichier (lu en
  flux, arrêté au-delà de la taille annoncée), vérifie taille et empreinte, puis l'importe **par le même chemin que l'import
  manuel** (`ImporterNomenclature` : contrôles, remplacement en une transaction, journal `nomenclature_importee`, produits « à
  vérifier »). Version chargée de même libellé et de même nombre de lignes : rien ne change, journal `nomenclature_deja_a_jour`.
  Central injoignable : l'ordre reste à confirmer (nouvel essai) ; fichier inconnu (404), différent (empreinte) ou refusé à la
  lecture : ordre refusé, rien d'importé.
- **Essai de bout en bout** (2026-11-18, `scripts\dev\essai_cloud_officine.ps1`, HTTPS avec certificats, bases jetables
  supprimées ensuite) : vrai fichier « NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx » (1 454 522 octets) enregistré puis publié ;
  1er ordre confirmé sur une officine qui avait déjà cette version → « déjà à jour », rien remplacé ; 2e ordre après avoir fait
  croire à une version plus ancienne → importé en 1 s environ au nom du titulaire (5 381 en vigueur, 1 495 non renouvelés,
  2 679 retraits, 9 555 lignes) ; deux téléchargements tracés au central ; résultats remontés (appliqué, appliqué). Écran « Ordres
  du central » mesuré à 1 536 × 890 sans défilement, captures en clair et sombre.
- **Point relevé** (outil d'essai) : `essai_cloud_officine.ps1 -Arreter` a annoncé deux fois « n'a pas pu être arrêté » alors que
  le processus s'arrêtait un peu plus tard ; relancé, il a tout supprimé. **Corrigé le 2026-11-18** (« ok arrêt essai ») : attente jusqu'à 60 s au lieu de 15 s, second
  arrêt forcé (avec les processus enfants) à mi-parcours, pour `-Arreter` et `-ArreterCentral` ; essai démarrer puis arrêter : un seul
  passage, tout supprimé en 3 s.

## 27. Lot S5.4 : réalisé (2026-11-18, « go S5.4 ») — commandes en brouillon envoyées par le central

Décisions (recommandations acceptées) : S5.4-D1 grossiste et produits désignés par leur n° local dans l'officine ; S5.4-D2 un bon
**séparé**, jamais mélangé au brouillon en cours de l'officine ; S5.4-D3 **sans confirmation** : le bon est créé tout de suite et le
titulaire est alerté (rien ne part au grossiste sans lui, décision S5-D4) ; S5.4-D4 une seule ligne refusée → tout l'ordre est
refusé, avec le motif de chaque ligne, aucun bon à moitié créé ; S5.4-D5 200 lignes au plus **[À DÉCIDER : plafond à confirmer à
l'usage]** ; S5.4-D6 prix facultatif : à défaut, le dernier prix d'achat (même règle qu'à l'écran), sinon refus.

- **Central** (script `016`) : type d'ordre `commande` ; cible = n° du grossiste ; données = 1 à 200 lignes (n° du produit,
  quantité entière de 1 à 999 999, prix d'achat HT facultatif, 0 ou plus, deux décimales), un produit une seule fois. Grossiste
  (connu, actif) et produits (connus) contrôlés dans les tables miroir de l'officine ; noms ajoutés pour l'affichage. Commande
  `--ordre-commande --officine <code> --grossiste <n°> --ligne <produit>:<qté>[:<prix HT>] … --auteur <nom>`.
- **Officine** (script `146`) : table locale `commande_ordre_central` (jamais envoyée) qui relie le bon à l'ordre (n° d'ordre
  unique : un même ordre ne crée jamais deux bons) ; `commande_fournisseur` inchangée, donc la remontée des commandes aussi. À
  l'application, mêmes contrôles que l'ajout d'un produit à l'écran (`AjouterACommande`) : grossiste actif, produit connu et non
  bloqué à l'achat, prix, quantité ; puis bon `brouillon` créé avec toutes ses lignes en une transaction, sans auteur local, journal
  `commande_preparee_central` (« Serveur central (auteur) » au journal des actions) et alerte au titulaire (« À voir : Achats ›
  Commandes »). L'ajout d'un produit depuis l'écran ne complète jamais un bon du central (`BrouillonAsync` l'exclut). Écran
  « Commandes » : « · serveur central » sous le n° du bon, et dans le détail « Préparé par le serveur central (auteur) : vérifiez-le,
  puis émettez-le vers le grossiste. ». L'émission, la modification et l'annulation restent celles de l'écran.
- **Essai de bout en bout** (2026-11-18, `scripts\dev\essai_cloud_officine.ps1`, HTTPS avec certificats, bases jetables supprimées
  ensuite) : trois ordres pour le grossiste n° 42. 1er ordre (3 lignes dont une sans prix) → bon C-000220 en brouillon, sans auteur
  local, prix manquant repris du dernier achat ; 2e ordre → bon séparé C-000219 ; 3e ordre avec un produit bloqué à l'achat → refusé
  « Rien n'a été créé : … bloqué à l'achat », aucun bon. Produit inconnu refusé dès le central. C-000220 émis depuis l'officine (PDF,
  « envoyée ») ; un produit ajouté ensuite depuis l'écran a ouvert un nouveau brouillon (C-000221), pas le bon du central. Résultats
  remontés au central (appliqué, appliqué, refusé avec motif). Écran « Bons de commande » mesuré à 1 536 × 890 sans défilement de
  la page, captures en clair et sombre. Arrêt de l'outil d'essai : même défaut qu'en S5.3 (deux annonces d'échec avant succès). Corrigé ensuite (voir § 26).

## 28. Référentiels envoyés par le central : lots 1 et 2 réalisés (2026-11-18, « go référentiels ») — antibiotiques suivis, organismes et centres payeurs

**Analyse des 15 tables « descendantes » restantes** (hors nomenclature, § 26, et réglages) et décisions (recommandations acceptées) :

| Groupe | Tables | Décision |
|---|---|---|
| A. Listes fixes livrées par nos scripts | `wilaya`, `categorie_produit`, `substance_surveillee`, `parametre_gs1_ai`, `classe_suivie` | R-D1 : **pas d'envoi par le central** ; les scripts de mise à jour suffisent (à revoir si un changement doit arriver entre deux versions) |
| B. Listes nationales que l'officine complète | `classe_suivie_dci`, `organisme`, `centre_payeur`, `type_prestation`, `indication`, `dci` | R-D2 / R-D3 : un lot par table, lignes désignées par leur clé naturelle (nom, sigle, code), jamais par le n° local, confirmation du titulaire avec l'aperçu des changements. **Toutes faites : `classe_suivie_dci` (lot 1), `organisme` + `centre_payeur` (lot 2), `type_prestation` (lot 3), `indication` (lot 4), `dci` (lot 5, § 28.1).** |
| C. Liens vers les produits de l'officine | `chifa_code_produit`, `produit_indication` | R-D4 : **reclassées montantes le 2026-11-18 (§ 29)** |
| D. À clarifier | `laboratoire` (vide, rien ne l'écrit), `zone_psychotrope_wilaya` (réglée par l'officine) | R-D5 **[À VÉRIFIER]** |

Correction de l'analyse : `classe_suivie_dci` est la liste des **antibiotiques suivis** (rapport des quantités délivrées), amorcée
par le script 117 sur des morceaux de noms de DCI **[À VÉRIFIER : nomenclature]**, pas une liste de psychotropes (ceux-ci passent
par `substance_surveillee`).

- **Central** (script `017`) : types d'ordre `classe_suivie` (cible = code de la classe, données = liste COMPLÈTE des noms de DCI,
  1 à 2 000, nettoyés, doublons retirés sans tenir compte de la casse) et `organismes` (1 à 50 organismes par sigle, mis en
  majuscules ; nom, coordonnées et délai de paiement facultatifs ; centres payeurs seulement pour la CNAS et la CASNOS, par code) ;
  taille d'un ordre limitée à 60 000 octets (élément descendant de 64 Kio au plus). Commandes
  `--ordre-classe-suivie --officine <code> --classe antibiotiques --fichier <dci.txt> --auteur <nom>` (un nom par ligne : un nom de
  DCI peut contenir une virgule) et `--ordre-organismes --officine <code> --fichier <organismes.json> --auteur <nom>`.
- **Officine** (aucun nouveau script) : les deux types attendent la confirmation du titulaire ; à l'arrivée, l'**aperçu des
  changements** est calculé et affiché sous le résumé (tableau et fenêtre de confirmation) ; une donnée invalide refuse l'ordre dès
  l'arrivée.
  - Antibiotiques : noms comparés sans tenir compte de la casse à la table des DCI de l'officine ; ajouts et retraits en une
    transaction sous verrou de la classe, journal `classe_suivie_mise_a_jour` (listes des ajouts et retraits) ; noms absents de
    l'officine ignorés et listés ; classe inconnue ou **aucun nom reconnu → refus** (garde-fou : la classe ne se vide jamais).
  - Organismes et centres : **ajouts et corrections seulement**, rien n'est supprimé ni désactivé, l'état actif d'une ligne
    existante n'est jamais changé ; une valeur absente de l'ordre garde celle de l'officine ; écrits par les cas d'usage des écrans
    (`Facturation.EnregistrerOrganismeAsync`, `CentresPayeurs.EnregistrerAsync` : mêmes contrôles, journaux `organisme_cree` /
    `organisme_modifie` / `centre_payeur_cree` / `centre_payeur_modifie`) ; tous les contrôles sont faits avant la première écriture
    (pas de transaction unique sur l'ensemble **[À VÉRIFIER]** : une erreur de base au milieu laisserait une partie appliquée).
- **Essai de bout en bout** (2026-11-18, `scripts\dev\essai_cloud_officine.ps1`, HTTPS, bases jetables supprimées ensuite) : liste de
  213 DCI (les 214 de l'officine moins deux associations trétinoïne + érythromycine, plus la daptomycine et un nom inconnu) →
  aperçu « 1 ajout, 2 retraits, 1 DCI absente, ignorée », confirmé depuis l'écran, appliqué ; organismes (CNAS : délai 45 jours et
  deux centres ; DGSN nouveau) → aperçu « ajouté : DGSN ; corrigé : CNAS ; 2 centres ajoutés », confirmé, appliqué, CASNOS et MDN
  inchangés ; journaux au nom du titulaire ; résultats remontés au central (appliqué, appliqué). Écran « Ordres du central » avec la
  fenêtre de confirmation : sans défilement, captures en clair et sombre. Arrêt de l'outil d'essai : un seul passage, 3 s.

### 28.1 Référentiels, lots 3 à 5 : prestations, indications, DCI (2026-11-18, « go » du titulaire, les trois tables)

- **Central** (script `018`) : types d'ordre `prestations` (1 à 200 par code, mis en majuscules ; libellé, prix, TVA, remboursable
  et tarif de référence ; tarif donné ⇒ « remboursable : vrai » exigé), `indications` (1 à 500 libellés de 80 caractères au plus) et
  `dci` (1 à 2 000 noms de 200 caractères au plus) ; textes nettoyés, doublons retirés sans tenir compte de la casse. Commandes
  `--ordre-prestations --fichier <prestations.json>`, `--ordre-indications --fichier <liste.txt>`, `--ordre-dci --fichier <liste.txt>`
  (avec `--officine <code> --auteur <nom>`).
- **Officine** (aucun nouveau script) : les trois types attendent la confirmation du titulaire, avec l'aperçu des changements.
  - Prestations (`ListesCentrales`, par `TypesPrestation.EnregistrerAsync` : mêmes contrôles, journaux `type_prestation_cree` /
    `_modifie`) : ajouts et corrections ; une valeur absente garde celle de l'officine ; l'état actif n'est jamais changé ; une
    prestation nouvelle sans prix est créée à 0 DA (jamais vendue tant que son prix n'est pas fixé, garde-fou existant).
  - Indications (`Indications.EnregistrerAsync`, journal `indication_creee`) : **ajouts seulement** ; une indication existante, même
    désactivée, n'est jamais recréée ni réactivée.
  - DCI (dépôt des ordres, une transaction sous verrou de la table, journal `dci_ajoutees`) : **ajouts seulement**, jamais de
    renommage ni de suppression.
  - Prestations et indications : tous les contrôles avant la première écriture, sans transaction unique **[À VÉRIFIER]** (comme les
    organismes).
- **Essai de bout en bout** (2026-11-18, HTTPS, bases jetables supprimées ensuite) : aperçus « 2 ajouts (INJ, TENSION) »,
  « 1 ajout (Toux sèche) » (Fièvre et Mal de gorge déjà là), « 1 ajout (TEDIZOLIDE) » (daptomycine et amoxicilline déjà là, casse
  ignorée) ; confirmés, appliqués au nom du titulaire, journaux écrits, résultats remontés au central (3 appliqués). Arrêt de l'outil
  d'essai : un seul passage.

## 29. Reclassement de deux tables liées aux produits : réalisé (2026-11-18, « go reclassement »)

`chifa_code_produit` (code CHIFA relié à un produit local) et `produit_indication` (produit local relié à une indication) étaient
classées « descendantes » à tort : ce sont des associations faites dans l'officine, qui désignent les produits par leur n° local ;
le central ne peut pas les fournir. Effet du mauvais classement : rien n'était copié au central (associations perdues en cas de
restauration d'une officine depuis le central).

Décisions (recommandations acceptées) : RC-D1 **montantes** (copie au central) ; RC-D2 lignes déjà présentes envoyées **une fois**
au reclassement, si la synchronisation est active ; RC-D3 `associe_par` (n° d'employé) envoyé comme les autres n° d'employés.

- **Central** (script `019`) : tables miroir `chifa_code_produit` (clé `code`) et `produit_indication` (clé `produit_id`,
  `indication_id`), cloisonnées par officine (RLS forcée), droits de l'API, déclarées dans `cloud_table_miroir`. **À passer AVANT le
  script 147 des officines** : un lot qui contient une table inconnue du central est refusé en entier.
- **Officine** (script `147`) : sens « montante » et clés ; seulement pour les tables encore « descendantes » (le rejeu ne change
  rien). Synchronisation active : déclencheurs posés, puis lignes existantes inscrites une fois dans la file (« creation »,
  minimisées comme par le déclencheur). Synchronisation inactive : seul le classement change ; l'activation fera le chargement
  initial comme pour les autres tables.
- **Essais** : tests SQL `019` (réception, suppression, cloisonnement) et `147` (classement, rejeu sans effet, envoi unique des
  lignes existantes) ; `147` passé deux fois sur `pharmacie_dev` et `pharmacie_test` (synchronisation inactive sur les deux : seul le
  classement a changé), `019` deux fois sur `pharmacie_cloud_dev`.

## 30. Lot S6, fin : restauration, bascule, départ d'une officine (analyse du 2026-11-18, « go S6 », à valider avant le code)

### 30.1 Décisions du titulaire (2026-11-18)

| N° | Sujet | Décision |
|---|---|---|
| S6-D1 | Officine revenue en arrière (restauration d'une sauvegarde, bascule sur le 2e serveur) | **Validée par le titulaire** : envoi suspendu, alerte SMS et e-mail, bouton « Repartir sur une nouvelle époque » (titulaire) dans Synchronisation, puis chargement complet ; lignes du central absentes de l'officine signalées et gardées (décision n° 8) |
| S6-D2 | Reconstruire la base d'une officine depuis le central | **Non** : la restauration se fait toujours depuis les sauvegardes locales (disque externe, copie de secours, 2e serveur) ; le central sert de preuve et signale les lignes manquantes |
| S6-D3 | Départ d'une officine | **Maintenant, piloté par les réglages du central** : durée de conservation après départ réglée au central ; effacement jamais automatique (simulation par défaut, puis commande explicite après le délai) |

### 30.2 Constat : ce qui casse aujourd'hui

- Après une restauration (ou une bascule avec retard de réplication), la base locale revient avec `dernier_lot` plus petit que le dernier
  lot reçu par le central pour l'époque en cours. Les lots suivants, reformés avec un autre contenu, reçoivent un refus définitif
  (409 « autre contenu ») : la synchronisation s'arrête en erreur, sans explication pour le titulaire.
- **Collision des identifiants** : les séquences locales reviennent aussi en arrière. Les nouvelles ventes peuvent reprendre des n°
  (`id`) déjà reçus par le central pour des ventes perdues par la restauration. Avec la règle « plus grand (époque, n°) gagne »,
  la ligne de la nouvelle époque **écraserait** la ligne perdue dans la table miroir (elle ne resterait que dans le lot brut
  `lot_recu`) : contraire à la décision n° 8 (« signaler et garder »).

### 30.3 Proposition technique

**Officine (script 149)**
1. Détection, à chaque passage d'envoi, avant de former un lot : `GET /api/v1/etat?epoque=` (déjà existant) ; si le dernier lot
   reçu par le central est **plus grand** que le dernier lot formé ici pour la même époque, l'officine est « revenue en arrière ».
   `sync_parametre.retour_arriere_le`, `retour_arriere_central` (n° vu au central) notés ; rien n'est formé ni envoyé ; message clair à
   l'écran Synchronisation et dans le bandeau ; une alerte au titulaire (SMS et e-mail) par détection.
2. **Protection immédiate des identifiants (sans attendre la validation, rien n'est supprimé)** : à la détection, les séquences des
   tables montantes à clé `id` sont avancées au-delà du plus grand `id` connu du central pour cette officine (nouvelle route
   `GET /api/v1/maximums`, réservée à l'officine appelante, aucune donnée de ligne). Les ventes faites ensuite ne reprennent plus
   les n° perdus ; il reste un risque pour celles faites entre le redémarrage et la première détection (une minute au plus).
3. « Repartir sur une nouvelle époque » (titulaire, mot de passe du titulaire ressaisi, seul facteur ; ni code SMS ni e-mail (décision du 2026-11-19) ; journal des actions) : `sync_nouvelle_epoque()` :
   époque + 1, `dernier_lot` et `dernier_accuse` à 0, lots non accusés de l'ancienne époque gardés et signalés (comme aujourd'hui),
   puis chargement complet demandé (mécanisme du § 13).

**Central (script 020)**
4. **Archive avant remplacement** : dans `cloud_appliquer_lot`, quand une ligne d'une époque plus récente remplace une ligne d'une
   époque plus ancienne, l'ancienne version est d'abord copiée dans `ligne_remplacee` (table, clé, ancienne ligne en jsonb, époque
   et n°, date), cloisonnée par officine. Rien n'est perdu même en cas de collision restante.
5. Fin du chargement de la nouvelle époque : lignes absentes marquées (déjà fait, § 13) ; en plus, les lignes archivées par
   `ligne_remplacee` sont comptées dans le compte rendu et l'alerte « lignes absentes ».
6. Contrôle d'intégrité (S4) : une nouvelle époque redémarre les chaînes à leur rang restauré ; les rangs déjà reçus de l'ancienne
   époque sont signalés « remplacés après restauration », pas « falsifiés » **[À VÉRIFIER en codant : forme exacte du compte rendu]**.

**Départ d'une officine (central, console d'administration)**
7. Réglage central `cloud_parametre.conservation_depart_ans` (6 par défaut, 6 minimum **[À VÉRIFIER : avis du juriste, loi 18-07 et
   durée fiscale]**), surchargeable par officine comme `conservation_lots_ans`.
8. `--depart-officine <id>` : révocation de tous ses certificats, `officine.resiliee_le`, export complet remis au titulaire
   (un fichier CSV par table miroir dans une archive zip chiffrée AES-256 depuis le 2026-11-20 — mot de passe tiré au sort, affiché une seule fois et envoyé par e-mail au titulaire avec `--email`, voir `regles-metier.md` —, avec un fichier d'empreintes SHA-256 ; patients sous code, comme au central),
   journal. `--effacer-officine <id>` : **simulation par défaut** (nombre de lignes par table), refusée avant la fin de la durée de
   conservation, puis effacement réel sur `--confirmer`, procès-verbal écrit (journal ajout seul).
9. Côté officine : synchronisation désactivée par la révocation (certificat refusé), rien d'autre à faire.

### 30.4 Découpage proposé

| Lot | Contenu |
|---|---|
| S6.1 | Central 020 : `ligne_remplacee`, archive avant remplacement, route `maximums` ; tests SQL |
| S6.2 | Officine 149 : détection, avance des séquences, `sync_nouvelle_epoque`, alerte ; écran Synchronisation (bouton titulaire) ; tests |
| S6.3 | Départ : réglage de conservation, `--depart-officine`, export zip, `--effacer-officine` (simulation, confirmation, procès-verbal) |
| S6.4 | Essai de bout en bout sur bases jetables : restauration simulée, nouvelle époque, chargement, lignes archivées, départ |

### 30.5 Réalisation S6.1 et S6.3 (2026-11-18, « go » du titulaire sur le plan § 30)

**Fait (central seulement ; l'officine, S6.2, est réalisée à part)**

| Élément | Réalisation |
|---|---|
| `ligne_remplacee` | Script `020` : officine, table, clé (jsonb), ancienne ligne (jsonb), ancienne et nouvelle époque et n°, opération (`remplacement` / `suppression`), date. RLS forcée (politique `cloisonnement`), ajout seul (déclencheurs ligne et TRUNCATE). `pharma_cloud_api` : `INSERT` seulement (aucune lecture directe). |
| Archive avant remplacement | `cloud_appliquer_lot` reprise de sa dernière définition (script `009`) ; seul ajout : `RETURNING old/new` (PostgreSQL 18) sur l'insertion et la suppression, copie dans `ligne_remplacee` quand l'ancienne époque est **plus petite**. Même époque : inchangé. **Précision** : une ligne renvoyée avec le même contenu (colonnes `sync_*` exclues), cas de tout chargement complet d'une nouvelle époque, n'est pas archivée (rien n'est perdu, sinon chaque nouvelle époque recopierait toute l'officine). Colonnes des familles nominatives non autorisées retirées de la copie. Une modification ou suppression refusée par les journaux chaînés (§ 22) n'est pas archivée : l'original reste en place. |
| Route `GET /api/v1/maximums` | `cloud_maximums_officine()` (`SECURITY DEFINER`, cloisonnée) : tables de `cloud_table_miroir` à clé `{id}` dont `id` est `smallint`, `integer` ou `bigint` ; `max(id)` de l'officine (toutes époques, lignes absentes comprises) et plus grand `id` de `ligne_remplacee` ; table sans ligne absente de la réponse. Même authentification que `/etat` (certificat client + jeton lié, officine du certificat), limite `officine`. Réponse `MaximumsCentralDto` (contrat inchangé). |
| Fin de chargement | `cloud_fin_chargement` (dernière définition : `003`) : nombre de lignes archivées depuis le début de l'époque (`cloud_lignes_remplacees`) dans `chargement.lignes_remplacees` et dans le journal ; l'alerte `chargement_absentes` est levée aussi quand ce nombre n'est pas nul. **Écart** : la réponse à l'officine (`ReponseFinChargementDto`) ne porte pas ce nombre (contrat partagé non modifié) ; il est au central (journal, table `chargement`). |
| Conservation après départ | Script `021` : `cloud_parametre.conservation_depart_ans` (6, minimum 6), `officine.conservation_depart_ans` (surcharge), `cloud_regler_conservation_depart`, console `--conservation-depart`, affichage dans `--reglages` (officines parties et date d'effacement possible). |
| `--depart-officine <code> --sortie <fichier.zip> --operateur <nom>` | `cloud_depart_officine` : tous les certificats révoqués, `resiliee_le` posée (gardée si rejoué), journaux. Export (`src/Pharma.Cloud.Api/Depart/DepartOfficine.cs`) en une image cohérente (`REPEATABLE READ`, lecture seule) : un CSV par table miroir et `ligne_remplacee.csv` (UTF-8 sans BOM, « ; », en-tête, `COPY … TO STDOUT`, trié par clé), `empreintes.txt` (« empreinte  fichier », SHA-256), fichier `.partiel` renommé à la fin, jamais d'écrasement ; empreinte de l'archive notée (`cloud_noter_export_depart`). |
| `--effacer-officine <code> [--confirmer <code> --operateur <nom>]` | `cloud_effacer_officine` : refusée (même en simulation) si l'officine n'est pas partie ou si la durée n'est pas écoulée ; simulation par défaut (lignes par table) ; confirmée : tables miroir, `ligne_remplacee`, `lot_recu`, en une transaction, une ligne restée annule tout ; procès-verbal dans `journal_depart_officine` (ajout seul : date, officine, opérateur, lignes par table) et `officine_effacee` au journal de sécurité. La confirmation répète le code de l'officine (même règle que `--effacer-famille`). |
| Gardes d'ajout seul | Ouvertes seulement si l'opération est posée dans la transaction (`cloud.effacement_officine`) **et** que l'officine est partie avec sa durée écoulée (`cloud_effacement_officine_en_cours`, `SECURITY DEFINER`) : `cloud_lot_recu_garde` (004), `cloud_ligne_remplacee_garde` (020), `cloud_integrite_ajout_seul` (012) redéfinies dans `021`. |
| Droits | Aucune fonction d'administration du départ n'est exécutable par `pharma_cloud_api` ; `journal_depart_officine` lui est fermé. |

**Essais (2026-11-18)**

- Base centrale de dev `pharmacie_cloud_dev` : sauvegarde `pg_dump -Fc --enable-row-security` avant (la RLS forcée empêche un `pg_dump` ordinaire du propriétaire ; base sans officine, les deux tables cloisonnées non vides — `cloud_reglage_table`, `cloud_reglage_modele` — sont bien dans la sauvegarde) ; `020` puis `021` passés **deux fois** chacun (idempotence), tests `020` et `021` réussis, toute la suite `database/cloud/tests` (001 à 021) réussie après coup.
- `tests/Pharma.Cloud.Tests` : 78 tests, 77 réussis, 1 ignoré (essai réel M6 existant, variable d'environnement absente). Nouveaux : `DepartOfficineTests` (empreintes, chemin de sortie, et sur base centrale jetable : maximums cloisonnés, départ, contenu de l'export et empreintes recalculées, CSV avec « ; », guillemets, retour à la ligne et accents, refus avant départ et avant le délai, simulation puis effacement, procès-verbal).

**Limites et points ouverts**

- La route `/maximums` n'est pas essayée en HTTP (pas d'hôte de test dans le projet) : la lecture (`DepotCentral.MaximumsAsync`) et la fonction SQL le sont ; l'authentification est celle de `/etat` (même code).
- `cloud_effacer_famille` (004) ne touchait pas `ligne_remplacee` : **décision du titulaire du 2026-11-20, réalisée par le script 028** (§ 30.9).
- L'effacement d'une officine gardait sa fiche, ses certificats, les journaux et ses éléments techniques : **décision du titulaire du 2026-11-20, réalisée par le script 027** (§ 30.9) ; restent gardés la fiche, les certificats, le code d'activation, les réglages de familles et les journaux (dont celui des ordres).
- Durée de conservation après départ (6 ans minimum) : **[À VÉRIFIER : avis du juriste, loi 18-07 et durée fiscale]** ; durée de conservation des sauvegardes de la base centrale (les données effacées y restent jusqu'à leur expiration) : **[À VÉRIFIER]**.
- Rejouer `004` ou `012` après `021` referme les gardes (l'effacement échoue sur « Effacement incomplet », rien n'est perdu) : repasser `021`. `020` ne remplace pas la garde de `ligne_remplacee` posée par `021`.
- Contrôle d'intégrité (§ 30.3 point 6, « remplacés après restauration ») : non traité ici (hors S6.1 / S6.3) **[À VÉRIFIER : lot à prévoir]**.
- `creer_base_centrale.ps1 -AvecTests` exige que chaque test finisse par « tous réussis » : les tests `012` à `019` ne le disent pas (défaut antérieur, non corrigé ici) ; `020` et `021` le disent.

### 30.6 Réalisation S6.2 (2026-11-18, « go » du titulaire sur le plan § 30) — officine

**Script `149_synchronisation_retour_arriere_nouvelle_epoque.sql`** (à passer après le script central `020`) : colonnes de
`sync_parametre` `retour_arriere_le`, `retour_arriere_central` (dernier lot vu au central), `retour_arriere_sequences_le` (avance des n°
faite), `retour_arriere_alerte_le` (alerte envoyée), contrainte `sync_parametre_retour_arriere_check` ; fonctions `sync_noter_retour_arriere`,
`sync_avancer_sequences(jsonb)`, `sync_noter_alerte_retour_arriere`, `sync_nouvelle_epoque`. Test SQL en transaction annulée
(`database/tests/149_…_tests.sql`, séquences consommées remises à leur valeur de départ, avance essayée sur une table créée dans la transaction).

**Détection** (`EnvoyerLotsSync`, chaque passage, après les contrôles d'activation, l'échange descendant et l'application des réglages, **avant**
le chargement complet et la constitution des lots) : `GET /api/v1/etat?epoque=` ; dernier lot reçu par le central **>** `dernier_lot` local →
détection notée une fois, `GET /api/v1/maximums` et avance des séquences, rien n'est formé, annoncé ni envoyé, message clair (`derniere_erreur`).
Tant que la détection est posée : le central n'est plus interrogé sur `/etat`, l'envoi reste suspendu à chaque passage, l'avance des n° est
réessayée tant qu'elle n'a pas réussi (route absente en 404, central injoignable). Central injoignable pour `/etat` (ou réponse d'une autre
époque) : aucun contrôle à ce passage, l'envoi suit son cours comme avant ce lot. La suspension est notée comme passage **sans** erreur
persistante (`sync_noter_passage(NULL)`) : l'alerte propre à la détection remplace l'alerte horaire « erreur d'envoi ».

**Alerte** : `SurveillerSynchronisation`, nature `retour_arriere`, une par détection (`retour_arriere_alerte_le < retour_arriere_le`), notée
avant l'envoi comme l'alerte d'intégrité ; SMS sans accents, e-mail avec les n° de lots et la marche à suivre.

**Nouvelle époque** : route `POST /api/admin/synchronisation/nouvelle-epoque` (groupe `admin.parametres`, puis **titulaire** exigé par
`RepartirNouvelleEpoqueSync`), corps `{ epoqueAffichee, motDePasse }` (confirmation : refus si l'époque a changé ; mot de passe du titulaire
vérifié par `VerificationMotDePasseActuel`, même vérification que « Changer mon mot de passe », erreur sur le champ `mot_de_passe` ; ni code
SMS ni e-mail), verrou d'envoi, journal `synchronisation_nouvelle_epoque` (avec `validation = mot_de_passe`). `sync_nouvelle_epoque` : époque + 1, `dernier_lot`/`dernier_accuse` à 0, `purge_epoque` à NULL et
`purge_dernier_lot` à 0 (sinon la contrainte `purge_dernier_lot <= dernier_accuse` refuserait la remise à zéro ; la purge ne traite que
l'époque courante, § 20.1), détection effacée, chargement en cours de l'ancienne époque passé « remplacé » (ses lignes d'instantané sont de
l'ancienne époque et ne partiraient jamais), puis `sync_demander_chargement('demande', titulaire)` : motif existant, le contrat `DebutChargementDto`
et le central ne sont pas changés.

**Écran** (Administration › Synchronisation › État) : bandeau d'attention (remplace « Dernière erreur », même message) avec les n° de lots,
l'état de l'avance des n° et le bouton titulaire « Repartir sur une nouvelle époque » → « Repartir sur une nouvelle époque (après l'époque N) ? » avec le champ « Votre mot de passe »,
Confirmer (grisé tant que le mot de passe est vide) / Annuler ;
autres comptes : « Seul le titulaire peut repartir sur une nouvelle époque ». « Recharger toutes les données » est grisé pendant la détection.
Nouvelle règle CSS `.bandeau-attention .actions` (boutons sous le texte du bandeau).

**Tests** : `RetourArriereSynchronisationTests` (9 tests : central en avance → suspendu, détection et avance une seule fois ; central égal ou
injoignable → envoi normal ; maximums en 404 → détection quand même puis avance au passage suivant ; noms de table invalides filtrés ; une alerte
par détection ; nouvelle époque refusée sans détection, réservée au titulaire, refusée sur écran périmé ou pendant un envoi, autorisée et
journalisée, puis lots de l'époque suivante acceptés). Suites `Pharma.Application.Tests` (1 600) et `Pharma.Architecture.Tests` (227, dont le
rejeu de toute la suite des scripts) vertes ; `SchemaMiroirTests` du central inchangé et vert.

**Écarts au plan** : (1) l'alerte n'est pas envoyée par le passage d'envoi lui-même mais par la tâche de supervision (chaque minute), avec la
règle « une par détection » ; (2) le message apparaît à l'écran Synchronisation, **pas** dans le bandeau « Synchronisation en attente » des
postes (§ 20.2), qui n'a pas été modifié ; (3) les opérations non envoyées de l'ancienne époque (hors lot) restent dans la file sans jamais
partir ni être purgées (la garde de `sync_sortie` n'autorise la suppression qu'après accusé) : leur état part avec le chargement complet.

**[À VÉRIFIER]** : rendu de l'écran (captures clair et sombre, mesure du défilement à 1 536 × 890 et 1 366 × 768) non fait : poste non lancé ;
essai de bout en bout avec le vrai serveur central (lot S6.4) ; risque restant pour les opérations faites entre le redémarrage de la base
restaurée et la première détection (une minute environ) : leurs n° peuvent reprendre des n° déjà reçus par le central (archivées par
`ligne_remplacee`, script central 020) ; séquences `integer` : une valeur du central au-delà de 2 147 483 647 ferait échouer l'avance
(non rencontré ; l'envoi reste suspendu avec le message « Avance des n° non faite (erreur de la base…) », nouvel essai à chaque passage).

### 30.7 Lot S6.4 : essai de bout en bout (2026-11-18)

**Environnement** : `scripts/dev/essai_cloud_officine.ps1` avec un `TEMP` dédié (hors dépôt), central 5194 (HTTPS) et API 5195 (HTTP) sur
127.0.0.1, exécutables compilés à part (`dotnet build --artifacts-path`, dossier temporaire hors dépôt). Bases jetables
`pharmacie_cloud_essai` (scripts central 001 à 021 par `creer_base_centrale.ps1`) et `pharmacie_essai_officine` (copie de
`pharmacie_dev`, qui contenait déjà le script 149). Comptes d'essai (titulaire et assistant) créés dans la copie jetable seulement.
Les lignes d'essai sont des fournisseurs et des patients écrits en SQL dans la copie jetable (journalisés comme une saisie : file
`sync_sortie`) ; aucune vente faite à la caisse. Fin d'essai : `-Arreter` (processus arrêtés par leur PID, deux bases, rôle et dossier
supprimés ; vérifié : aucune base d'essai, aucun port 5194 / 5195 / 9222 en écoute). `pharmacie_dev`, `pharmacie_test`,
`pharmacie_cloud_dev` non modifiées.

| N° | Étape | Constaté | Résultat |
|---|---|---|---|
| 1 | Activation, chargement complet, lots | officine activée ; chargement 52 terminé (37 886 lignes, 10 lots par minute observés) ; fournisseurs A1 (44), A2 (45), patient P1 envoyés ; lots 1 à 77 accusés | OK |
| 2 | Point de restauration, puis suite | `pg_dump -Fc` à `dernier_lot` 77 ; ensuite fournisseurs B1 (46), B2 (47), B3 (48), patient P2, modifications de A1 (téléphone) et A2 (adresse) : lots 78 à 80 accusés | OK |
| 3 | Restauration et redémarrage | API arrêtée, base jetable recréée depuis la sauvegarde (`dernier_lot` 77) ; avant redémarrage, fournisseur C1 créé (reprend le n° 46 de B1 : collision voulue) et patient P3 ; API relancée : en 31 s `retour_arriere_le` posé, `retour_arriere_central` = 80, `retour_arriere_sequences_le` posé ; séquences égales aux maximums du central (fournisseur 48, vente 17 916, journal_audit 12 917) ; fournisseur créé ensuite : n° 49 ; rien formé ni envoyé (`dernier_lot` 77, 121 opérations hors lot) ; une seule alerte « Synchronisation suspendue : base de l'officine revenue en arrière » (en `alerte_non_envoyee` : aucun destinataire d'essai), toujours une seule après plus de 5 minutes. La route `GET /api/v1/maximums` n'a pas été appelée directement : sa réponse est constatée par l'avance des séquences, seule source possible | OK |
| 4 | Nouvelle époque | route refusée à un compte assistant (403) et avec une époque périmée (422) ; confirmée depuis le poste (bouton titulaire) : époque 2, compteurs à 0, détection effacée, journal `synchronisation_nouvelle_epoque` ; chargement 53 terminé. Central : B2 et B3 gardés et marqués `sync_absente_le`, patient P2 gardé et marqué absent (P3 a reçu un autre code patient : pas de collision sur `patient`), 3 connexions perdues marquées ; `ligne_remplacee` : B1 (écrasé par C1), A1 et A2 (versions modifiées après la sauvegarde), 107 alertes et 4 alertes de surveillance (recalculées au redémarrage), 1 utilisateur : 115 lignes, compte rendu `chargement_absentes` (6 absentes, 115 archivées). Comparaison des 106 tables miroir avant / après : aucune table en baisse | OK |
| 5 | Départ | `--depart-officine` : 1 certificat révoqué, zip de 107 CSV (106 tables + `ligne_remplacee`, 38 024 lignes), 107 empreintes SHA-256 recalculées identiques, empreinte de l'archive conforme au journal, patients sous code ; envoi suivant refusé (« 401 Certificat révoqué », `certificat_refuse` au journal du central) ; `--effacer-officine` refusé avant départ puis avant le délai (6 ans), simulation comprise ; `--conservation-depart 1` refusé (6 ans minimum, global et par officine) : **délai court impossible par le réglage** ; date de départ reculée de 6 ans et 1 jour par SQL dans la base centrale jetable pour la suite ; simulation (38 180 lignes), confirmation refusée si le code n'est pas répété ou sans opérateur, puis effacement confirmé : tables miroir, `ligne_remplacee` et `lot_recu` vides, fiche officine gardée, procès-verbal `journal_depart_officine` (total 38 180) et `officine_effacee` au journal de sécurité | OK |
| 6 | Écran Synchronisation › État | voir ci-dessous | OK |

**Rendu de l'écran** (poste compilé à part, `PHARMA_API` vers l'API d'essai, piloté par CDP ; compte titulaire d'essai) : trois états
mesurés — bandeau de retour en arrière avec « Repartir sur une nouvelle époque », confirmation « Repartir sur l'époque 2 ? » Confirmer /
Annuler (captures antérieures à l'ajout du mot de passe : la confirmation actuelle est « Repartir sur une nouvelle époque (après
l'époque N) ? » avec le champ « Votre mot de passe »), puis message de réussite après confirmation — en thèmes sombre et clair, à 1 536 × 890 et 1 366 × 768 : `scrollHeight -
innerHeight` = 0 et `scrollWidth - innerWidth` = 0 dans les 12 vues, aucun libellé tronqué. Captures regardées : bandeau d'attention
au-dessus, boutons de 44 px sous le texte (`.bandeau-attention .actions`), panneaux État et Chargement complet dans le cadre défilant
(seul à défiler à 1 366 × 768), « Recharger toutes les données » grisé pendant la détection, en-tête sur une ligne (nom de la
pharmacie masqué à 1 366), couleurs du thème respectées. Remarque : à 1 366 × 768 avec le bandeau, environ 60 px restent libres sous le cadre
défilant (hauteur du cadre non recalculée à l'apparition du bandeau ou au changement de taille simulé) : sans défilement de la page, non
corrigé.

**Constats sans correction (aucun code modifié)**
- « Synchroniser maintenant » pendant la suspension répond **502** (`SynchronisationEndpoints.cs`, route `/envoyer`, ligne 92 : le
  passage suspendu rend son message dans `Erreur`) : le poste affiche le message comme une erreur du serveur central ; un statut 409 ou un
  résultat 200 avec l'arrêt serait plus juste. Gêne mineure.
- `derniere_erreur` perd, dès le passage suivant, la phrase sur l'avance des n° (`EnvoiLots.cs`, `RetourArriereAsync`, lignes 394 à 397 :
  le complément n'est ajouté que le passage où l'avance est faite) ; l'écran, lui, l'affiche d'après `retour_arriere_sequences_le`.
- Toute nouvelle époque archive dans `ligne_remplacee` les lignes recalculées au démarrage (alertes : 111 lignes ici) : attendu par la
  règle « contenu différent », à garder en tête pour le volume.
- Contrôle d'intégrité « remplacés après restauration » (§ 30.3 point 6) : non essayé (`--anomalies-integrite` : aucune anomalie, sans
  vérification lancée).

### 30.8 Corrections après revue et essai (2026-11-18)

Corrections validées par le titulaire (relecture du lot S6 et essai S6.4), réalisées le 2026-11-18 sur le code non commité.

| N° | Point | Ce qui a changé |
|---|---|---|
| 1 | `/etat` injoignable, refus « empreinte différente » | `EnvoyerLotsSync` : si `/etat` ne répond pas (ou répond pour une autre époque), **rien n'est formé ni envoyé** à ce passage (« Serveur central injoignable pour le contrôle de l'état… », erreur non bloquante, nouvel essai au passage suivant). Un lot jamais reformé (`ReformeLe` nul) refusé « empreinte différente » alors que le central a reçu au moins ce n° (`DernierRecu >= n°`) pose la détection par `sync_noter_retour_arriere_refus` (script 149, sans comparaison à `dernier_lot`), puis même suspension (avance des n°, message). Test : `/etat` injoignable puis dépôt refusé. |
| 2 | Avance des séquences jamais en arrière | `sync_avancer_sequences` : `LOCK TABLE public.%I IN SHARE ROW EXCLUSIVE MODE` avant la lecture de la séquence et `setval` (aucune insertion ne peut s'intercaler), `lock_timeout` 5 s. Aucun `nextval` explicite sur ces séquences dans l'application (seulement des séquences de numérotation, vérifié par recherche dans `src` et `database/migration`). |
| 3 | Échec par table | Un bloc `BEGIN … EXCEPTION WHEN others` par table : la table en échec est rendue (nouvelle colonne `erreur`, type de retour changé : l'ancienne version est retirée par le script) et listée dans le message (« Avance des n° incomplète : … ») ; les autres sont avancées ; `retour_arriere_sequences_le` n'est posée que si aucune table n'a échoué (nouvel essai au passage suivant, sans effet sur les tables déjà avancées). |
| 4 | Nouvelle époque au-delà du central | Contrat : champ **facultatif** `EtatCentralDto.EpoqueMax` (défaut null : une officine ou un central antérieur restent compatibles) ; central : `cloud_epoque_max()` (script 020, plus grande époque de `officine_epoque` pour l'officine courante), lue par `/etat` avec le dernier lot (`DepotCentral.EtatAsync`). Officine : `IClientServeurCentral.EpoqueMaxCentralAsync` ; `sync_nouvelle_epoque(titulaire, époque du central, époque attendue)` refuse si l'époque a changé et pose `greatest(locale, central) + 1` ; ancienne signature `(bigint)` retirée. Central injoignable au moment de confirmer : refus, rien ne change. |
| 5 | Rejeu des scripts centraux | Les anciennes définitions sont sous condition (`DO $rejeu$ … EXECUTE $def$`) : non recréées si le marqueur du script suivant existe. `cloud_appliquer_lot` (001, 003, 004, 006, 009), `cloud_debut_chargement` et `cloud_fin_chargement` (003), `cloud_lot_recu_garde` (004, marqueur `journal_depart_officine` de 021), `cloud_integrite_ajout_seul` (012, même marqueur) ; en plus `cloud_journaliser` (001) et `cloud_descendants` (005, 009) : le rejeu de 009 demandé ici ramenait `cloud_descendants` à sa version de 009 (constaté : tests 010 et 011 en échec, corrigé par la garde et le rejeu de 010 et 011). Liste des marqueurs : `database/cloud/README.md`. **Non protégés à l'époque** : `cloud_descendants` 010 → 011, `cloud_creer_ordre` 013 → 018 ; **protégés le 2026-11-20** (§ 30.9, point 5 : marqueur `cloud_chaine_reference` dans 010 ; `cloud_creer_ordre` 013 à 017 par la contrainte `ck_ordre_central_type` ; `018` est la dernière définition). |
| 6 | PostgreSQL 18 | Refus clair en tête de `020` et `021` (`server_version_num < 180000`) et dans `scripts/mep/creer_base_centrale.ps1` (avant toute création). |
| 7 | Intégrité (décision du titulaire) | L'alerte « retour en arrière » (e-mail, et SMS sans accents en version courte), le message de suspension et le bandeau de l'écran Synchronisation disent : « Des anomalies d'intégrité sont attendues au serveur central pour les rangs restaurés (journal des ventes et autres journaux chaînés) : elles ne signifient pas une falsification. » Traitement de fond : futur lot S6.5 (non codé). |
| 8 | Validation de la nouvelle époque | **Constat** : dans l'officine, les autres actions sensibles du titulaire (confirmer un ordre du central, payer une traite, clôturer un inventaire…) n'exigent que la session du titulaire **connecté par mot de passe** (`ExigerTitulaireAsync` : jamais par code comptoir, ni avec un mot de passe provisoire) ; le code reçu par SMS ou e-mail n'existe que pour le **super admin** (hors base), et la validation sur place par code comptoir exclut le rôle de titulaire. **Retenu** : la ressaisie du **mot de passe du titulaire**, mécanisme existant de « Changer mon mot de passe » extrait dans `VerificationMotDePasseActuel` (même compteur et même blocage que la connexion, refus au journal ; `ChangerMonMotDePasse` l'utilise désormais), en plus de l'époque affichée. Route `POST /nouvelle-epoque` : champ `MotDePasse` ; écran : champ « Votre mot de passe » dans la confirmation (vidé après chaque essai). **[À VÉRIFIER avec le titulaire : ce choix lui convient-il, ou faut-il un code reçu par SMS ou e-mail (mécanisme à créer pour le titulaire) ?]** |
| 9 | Fichier `.partiel` | `DepartOfficine.ExporterAsync` ne supprime `…zip.partiel` en cas d'erreur que si ce passage l'a créé. |
| 10 | Fin de chargement | `cloud_fin_chargement` compte les lignes archivées depuis le **début du chargement** (`cloud_lignes_remplacees(époque, chargement.debut_le)`, `remplacee_le >= debut`). |
| 11 | Coût du cas courant | `cloud_appliquer_lot` : si l'officine n'a aucune époque antérieure à celle du lot (`officine_epoque`), chemin de 009 sans `RETURNING old/new` ni `to_jsonb` (aucune archive possible). |
| 12 | Alerte chaque minute | `sync_noter_alerte_retour_arriere` pose `greatest(p_quand, retour_arriere_le)`. |
| 13 | Journal et changement d'époque | `EnvoiSyncSql.NouvelleEpoqueAsync` : `sync_nouvelle_epoque` et l'entrée `synchronisation_nouvelle_epoque` du journal des actions dans **une** transaction (`JournalSql.EcrireAsync` sur la même connexion, mécanisme déjà utilisé ailleurs). |
| 14 | Implémentations par défaut | `IClientServeurCentral.DernierLotCentralAsync`, `MaximumsAsync` et `EpoqueMaxCentralAsync` lèvent `NotSupportedException` ; doublures adaptées (`EnvoiSynchronisationTests`, `ChargementSynchronisationTests` : `DernierLotCentralAsync` du central simulé). |
| 15 | « Synchroniser maintenant » pendant la suspension | `ResultatEnvoi.Suspendu` ; route `/envoyer` : 200 avec `ResultatEnvoiSynchronisationDto.Avertissement` (champ facultatif) au lieu de 502 ; l'écran l'affiche en `.bandeau-attention`. |
| 16 | Message gardé | `derniere_erreur` garde « N° des nouvelles opérations avancés au-delà de ceux du serveur central. » tant que `retour_arriere_sequences_le` est posée. |
| 17 | Archive des lignes recalculées | **Règle inchangée.** Chaque nouvelle époque archive aussi dans `ligne_remplacee` les lignes **recalculées au démarrage** de l'officine (à l'essai S6.4 : 111 alertes et alertes de surveillance) : leur contenu diffère de la version de l'ancienne époque, la règle « autre contenu » les archive. **Décision du titulaire du 2026-11-20, réalisée par le script 026** (§ 30.9) : colonne réglable `cloud_table_miroir.archive_remplacee`, mise à faux pour `alerte` et `alerte_surveillance`. |

**Bases (2026-11-18)** : sauvegardes `pg_dump -Fc` de `pharmacie_dev`, `pharmacie_test` et `pharmacie_cloud_dev` (`--enable-row-security`) dans `C:\MesAtlassian\sauvegardes-bd\*_avant_corr_s6_*.dump` ; `149` passé deux fois sur dev et test (dev et test au même schéma, `init/schema.sql` régénéré) ; au central `003`, `004`, `006`, `009`, `012`, `020`, `021` puis `020`, `021`, définitions finales vérifiées (`pg_get_functiondef`) ; toute la suite `database/cloud/tests` réussie. **Incident constaté et corrigé** : un premier rejeu sans `PGCLIENTENCODING=UTF8` a enregistré mal encodés les messages accentués des fonctions des scripts `003` à `011` (ils ne posent pas `client_encoding`) ; rejeu avec `PGCLIENTENCODING=UTF8`, aucun texte mal encodé restant (vérifié dans `pg_proc` et `pg_description`) ; README central complété.

**Écran** : rendu (charte, captures clair et sombre, défilement à 1 536 × 890 et 1 366 × 768) **non revérifié** après l'ajout du champ mot de passe, de la phrase d'intégrité et du bandeau d'avertissement **[À VÉRIFIER]**.

### 30.9 Décisions du titulaire du 2026-11-20 : réalisation (scripts centraux 026 à 028)

Plan validé : `docs/taches/central-decisions-2026-11-19/plan.md`. Aucun script d'officine. Sauvegarde `pg_dump -Fc --enable-row-security` de `pharmacie_cloud_dev` faite et vérifiée avant les scripts.

| N° | Décision | Réalisation |
|---|---|---|
| 1 | Tables sans archive : colonne réglable | `026` : `cloud_table_miroir.archive_remplacee boolean NOT NULL DEFAULT true`, mise à faux **une fois** (à la création de la colonne) pour `alerte` et `alerte_surveillance` ; `cloud_appliquer_lot` redéfinie (copie de 020, seul changement `IF v_archive AND m.archive_remplacee`) ; garde de rejeu dans 020. Le miroir est mis à jour comme avant. |
| 2 | Alertes déjà archivées gardées | Aucune fonction de retrait, rien n'est supprimé. |
| 3 | Départ : journal des ordres gardé (option B) | `027` : clé étrangère `ordre_central_journal` → `ordre_central` retirée (nom lu dans `pg_constraint`) ; les ordres sont effacés, le journal reste (ajout seul). |
| 4 | Départ : éléments techniques effacés | `027` : `cloud_effacement_tables_techniques()` (liste ordonnée, marqueur de rejeu) ; `cloud_effacer_officine` redéfinie, même signature : tables miroir, `ligne_remplacee`, `lot_recu`, les quatre tables du 022, puis `cloud_anomalie_integrite`, `cloud_integrite_sceau`, `cloud_integrite_etat`, `cloud_integrite_officine`, `ordre_central`, `descendant`, `officine_descendant`, `chargement`, `officine_volume_jour`, `officine_epoque` (en dernier). Déclencheur des anomalies rebranché sur `cloud_integrite_restauration_garde()`. Procès-verbal : `techniques` (sous-total) et `script` = 027. Gardés : fiche, certificats, code d'activation, réglages de familles, journaux de sécurité, des départs, des réglages et des ordres. **Constaté** : une officine résiliée ne peut pas être réactivée (`cloud_code_valable` et `cloud_activer` refusent l'officine dont `resiliee_le` est posée ; aucun script ne la remet à NULL). |
| 5 | Rejeu : `cloud_creer_ordre` et `cloud_descendants` | Blocs `DO $rejeu$` dans 013 à 017 (marqueur : `ck_ordre_central_type` contient le type posé par le script suivant) ; 010 : marqueur `cloud_chaine_reference` ajouté à la garde par contenu ; gardes aussi dans 004 (`cloud_effacer_famille`), 012 (déclencheur des anomalies), 021 (garde de `ligne_remplacee`), 022 (`cloud_effacer_officine`). Test `RejeuScriptsCentrauxTests` : empreinte (définitions des fonctions, déclencheurs, contraintes, droits d'exécution) avant et après le rejeu de 001 à 028 : aucun écart. **Constaté, hors périmètre** : rejouer `001` seul remet le déclencheur de `lot_recu` sur `cloud_ajout_seul()` jusqu'au rejeu de `004` (côté strict, l'état final du rejeu complet est identique). |
| 6 | Zip chiffré pour la remise par lien | **Hors de ce lot** : fonction séparée, à réaliser plus tard (`docs/mise-en-production.md` F8). |
| 7 | `cloud_descendants` : garde de 010 | Garde par contenu conservée, plus `OR to_regclass('public.cloud_chaine_reference') IS NOT NULL`. |
| 8 | Effacement d'une famille dans les lignes archivées | `028` : `ligne_remplacee.expurgee_le` et `colonnes_effacees` ; garde `cloud_ligne_remplacee_garde` : DELETE inchangé, UPDATE permis seulement si l'opération `expurger` est posée, que les autres colonnes sont identiques, que `ancienne` perd exactement les colonnes déclarées et que celles-ci appartiennent à une famille de la table ; `cloud_effacer_famille` redéfinie, même signature (lignes `nom_table = 'ligne_remplacee'`, `colonne = '<table>.<colonne>'`), simulation puis confirmation, journal de sécurité complété. |

## 31. Lot S6.5 : intégrité après restauration (analyse du 2026-11-18 ; décisions S6.5-D1 à D9 validées le 2026-11-18 ; **réalisé** du 2026-11-18 au 2026-11-19, S6.5.0 à S6.5.6)

**État (2026-11-19)** : réalisé et essayé de bout en bout (§ 31.6 à 31.8) ; synthèse et points ouverts au § 31.9. Les § 31.1 à 31.5 sont l'analyse d'origine, gardée telle quelle ; ses « [À VÉRIFIER par essai] » sont tranchés au § 31.9.

Analyse d'origine (avant tout code) : analyse faite **par lecture du code** (scripts central 002, 012, 020, 021 ; officine 111, 112, 144, 149 ;
`IntegriteCentrale.cs`, `Supervision.cs`, `RetourArriere.cs`) : aucun essai n'a été lancé. Tout comportement décrit ci-dessous
qui n'a pas été observé en essai est à confirmer par le premier test du lot (S6.5.5) **[À VÉRIFIER par essai]**.

Rappel des décisions : décision n° 8 (signaler et garder, jamais effacer) ; S6-D1 (nouvelle époque puis chargement complet) ;
titulaire, 2026-11-18 : mention à court terme dans l'alerte de retour en arrière, puis S6.5 : les rangs remplacés après une
nouvelle époque sont classés « remplacés après restauration », pas « falsifiés ».

### 31.1 Constat : ce qui se passe aujourd'hui, rang par rang

**Faits établis par le code**

- Au central, la table miroir `journal_vente` (et `journal_registre_psychotrope`) a pour clé `(officine_id, id)` seulement
  (script 002) : **aucune unicité sur `(cle_chaine, rang)`**, contrairement à l'officine (`ux_<journal>_chaine` sur
  `(cle_chaine, rang)`, script 111). `cle_chaine` vaut « officine » pour toutes les lignes.
- À l'officine, le rang d'une nouvelle ligne est `max(rang) + 1` de la base **restaurée** (`integrite_chainer`, script 111) ;
  l'`id` vient de la séquence d'identité, avancée par `sync_avancer_sequences` (script 149) au-delà du plus grand `id` reçu par
  le central (`cloud_maximums_officine`, script 020) dès la détection, pas avant.
- `cloud_integrite_ajout_seul` (012, dernière définition 021) sur `journal_vente`, `journal_registre_psychotrope`,
  `sceau_integrite`, `cloture_fiscale`, `verification_integrite` : mise à jour au contenu identique (hors colonnes `sync_*`)
  acceptée ; contenu différent : **non appliquée** (`RETURN NULL`), anomalie `modification_refusee`, sans regarder l'époque.
  La version reçue n'est gardée que dans le lot brut `lot_recu` : ni dans la table miroir, ni dans `ligne_remplacee` (020 n'archive
  que ce qui a été réellement remplacé ; une mise à jour annulée par le déclencheur ne remonte pas dans `RETURNING`).
- `cloud_appliquer_lot` (020) marque « vue par le chargement » (`sync_chargement`, `sync_absente_le = NULL`) toute ligne de
  l'instantané **par sa clé**, même si son contenu a été refusé : la mise à jour ne touche que des colonnes `sync_*`, que le
  déclencheur laisse passer.
- Le central n'a **aucune trace d'un changement d'époque déclaré** : une époque naît au premier lot reçu
  (`INSERT INTO officine_epoque … ON CONFLICT DO NOTHING`) ; `chargement.motif` reste « demande » (S6.2 n'a pas changé le
  contrat). Seule trace indirecte : la ligne `journal_audit` « synchronisation_nouvelle_epoque » écrite par
  `RepartirNouvelleEpoqueSync`, si `journal_audit` part bien au central **[À VÉRIFIER : sens et colonnes envoyées de journal_audit]**.

**Exemple déroulé.** Le central a reçu, en époque 1, les rangs 1 à 100 (`id` 1 à 100). La base est restaurée à l'état du rang 80.
Avant la première détection (une minute au plus, § 30.6), deux ventes sont enregistrées : rangs 81 et 82, avec les `id` 81 et 82
(séquence pas encore avancée). Détection, avance des séquences (prochain `id` : 101), titulaire « Repartir sur une nouvelle
époque » (époque 2), chargement complet ; d'autres ventes suivent : rangs 83, 84… avec les `id` 101, 102…

| Rang(s) | Officine (après restauration) | Au central, après le chargement de l'époque 2 | Anomalie notée aujourd'hui |
|---|---|---|---|
| 1 à 80 | lignes d'origine | renvoyées à l'identique : acceptées, `sync_epoque` passe à 2 | aucune |
| 81, 82 | **nouvelles** ventes, `id` 81 et 82 | `id` 81 et 82 reçus avec un autre contenu : **refusés**, version de l'époque 1 gardée, marquée « vue » ; la nouvelle version n'existe que dans `lot_recu` | `modification_refusee` (×2), alerte au titulaire « Ligne renvoyée avec un autre contenu » |
| 83 à 100 (époque 1) | absentes | `id` 83 à 100 gardés, `sync_absente_le` posée à la fin du chargement (§ 13, 020) | aucune (alerte « lignes absentes » du chargement) |
| 83 et suivants (époque 2) | `id` 101, 102… | insérés : **deux lignes par rang** de 83 à 100 | voir vérification ci-dessous |

Vérification d'intégrité (`cloud_integrite_verifier_chaine`, 012) :

- **Incrémentale** : le point de reprise (`cloud_integrite_etat.dernier_rang`) est à 100 ; les rangs 83 à 100 de l'époque 2 ne sont
  **jamais relus** (`rang > 100`). Au rang 101 de l'époque 2 : `hash_precedent` (empreinte du nouveau rang 100) ≠ empreinte de
  l'ancien rang 100 → `chainage_rompu`, statut `rupture`, et la chaîne **n'est plus relue** aux passages incrémentaux.
- **Complète** (hebdomadaire, depuis le rang 1) : rangs 1 à 82 vérifiés sur les lignes de l'époque 1 ; au rang 83, deux lignes,
  ordre indéterminé (`ORDER BY r.rang` seul). Si l'ancienne passe d'abord, la nouvelle (même rang) est prise pour un **trou** :
  `attente`, puis après 24 h `ligne_manquante` au rang 84 (fausse) ; si la nouvelle passe d'abord : `chainage_rompu` au rang 83
  (elle s'enchaîne sur le nouveau rang 82, absent du miroir). Dans les deux cas, statut `rupture`.
- Sans ligne en collision (aucune vente avant la détection), même effet dès le rang 81 (deux lignes chaînées sur le rang 80).

Effets de bord sur les autres contrôles :

- **Sceaux** (`cloud_integrite_verifier_sceaux`) : la ligne scellée est cherchée par `(cle_chaine, rang)` seulement
  (`EXECUTE … INTO` sans `STRICT` ni ordre) ; pour un rang en double, la ligne lue peut être celle de l'autre époque →
  `sceau_incoherent`, pour d'anciens comme pour de nouveaux sceaux.
- **Vérification croisée** (`cloud_integrite_verifier_croisee`) : même recherche par rang → `reecriture_apres_envoi` possible à tort ;
  et `chaine_officine_plus_courte` est notée dès qu'une vérification de l'officine s'arrête à un rang M < 100 alors que l'ancien
  rang M + 1 a été enregistré avant : constat **exact** (lignes perdues), mais classé comme une falsification.
- **Tickets** (`cloud_integrite_verifier_tickets`) : `compteur_ticket` revient en arrière (il n'est pas dans l'ajout seul : il est
  remplacé et archivé dans `ligne_remplacee`) et n'est pas avancé par S6.2 ; les n° « T- » perdus sont **réattribués** à de
  nouvelles ventes (`vente.id` différent, même `numero`) : aucun contrôle ne le voit ; la vérification complète note
  `ticket_hors_compteur` pour les anciennes ventes au-delà du compteur restauré, jusqu'à ce qu'il les rattrape.
- **Registre des psychotropes** (mode « liaison ») : mêmes effets sur les rangs et le chaînage.
- **Officine** : une alerte par nouvelle anomalie (`Supervision.cs`, libellés de `IntegriteCentrale.LibelleNature`) ; le résumé
  `integrite-v1` reste au statut `anomalie` **pour toujours** (`v_total > 0` : une anomalie n'est jamais close) et la chaîne en
  `rupture` : **la surveillance du journal par le central est aveugle** pour la suite, au-delà de la fausse alerte relevée en revue.
- La mention à court terme dans l'alerte de retour en arrière n'est pas encore dans `Supervision.cs` (texte lu le 2026-11-18)
  **[À VÉRIFIER : faite à part ou à faire]**.

### 31.2 Proposition

**Principe.** Le central ne peut pas prouver qu'une restauration est innocente : effacer des ventes en restaurant une sauvegarde
est justement une manière de frauder. Il peut en revanche **prouver la forme** de ce qui s'est passé (une chaîne commune jusqu'au
rang R, puis deux suites intactes) et **garder les deux suites**. S6.5 change le **classement** et le ton de l'alerte, jamais la
preuve : l'ancienne suite reste en place, vérifiée, comptée (lignes, période, montants) et exportable.

**1. Déclaration du changement d'époque (preuve que l'époque a changé).** À `sync_nouvelle_epoque`, l'officine envoie au central,
**avant tout lot de la nouvelle époque**, une déclaration : ancienne et nouvelle époque, dernier lot local et dernier lot vu au
central à la détection (`retour_arriere_central`), dates de détection et de décision, titulaire (n° d'utilisateur, pas de nom),
et pour chaque journal de `chaine_integrite` la **fin de chaîne locale** (rang, `id`, empreinte). Le central la range dans une
table ajout seul `cloud_epoque_changement` avec sa propre date de réception (horloge du central, digne de foi). Sans déclaration,
rien ne change : les anomalies sont notées comme aujourd'hui (S6.5-D1).

**2. Lignes en collision dans les tables à ajout seul.** `cloud_integrite_ajout_seul` est redéfinie : contenu différent **et**
époque reçue plus grande que celle de la ligne **et** changement d'époque déclaré → l'original reste en place (règle S4 inchangée),
la version reçue est copiée dans une nouvelle table ajout seul `cloud_ligne_divergente` (table, clé, ligne jsonb, époque, n° de
lot, date), **sans anomalie à ce moment** : c'est la vérification qui classe (point 4). Sinon : `modification_refusee` comme
aujourd'hui. La condition « effacement d'une officine partie » de 021 est gardée telle quelle (S6.5-D2).

**3. Vérification par branches.** `cloud_integrite_verifier_chaine` ne suit plus « le rang suivant » mais **le lien d'empreinte** :
la ligne suivante de la branche est celle du rang r + 1 dont `hash_precedent` = empreinte de la ligne courante, prise dans la
table miroir **et** `cloud_ligne_divergente`. Deux lignes du même rang chaînées sur la même ligne = **bifurcation** au rang R.
- Branche vivante : celle qui porte les lignes de l'époque la plus récente non absentes ; son point de reprise reste dans
  `cloud_integrite_etat` (une colonne `epoque_branche` en plus).
- Ancienne branche (R + 1 à N) : vérifiée **de bout en bout** une fois (empreintes recalculées en mode « complet », chaînage),
  puis figée : plus aucune ligne ne doit y arriver.
- Les recherches par rang des sceaux et de la vérification croisée passent par `(rang, id)` (`dernier_id` du sceau) ou par la
  branche de l'époque de la ligne, jamais par le rang seul : correction utile même sans restauration (requête indéterminée).

**4. Constat de restauration : conditions, toutes exigées.** Une bifurcation est classée « remplacée après restauration »
seulement si :
1. un changement d'époque E → E' est **déclaré** (point 1) et reçu **avant** le premier lot de E' ;
2. le chargement complet de E' est **terminé** (`chargement.etat = 'termine'`, époque E') : avant, rien n'est classé (attente) ;
3. le rang R est le dernier rang **commun** : lignes 1 à R reçues en E puis renvoyées **identiques** en E' ; toute ligne de rang ≤ R
   reçue différente reste une `modification_refusee` (falsification possible) ;
4. R ≤ fin de chaîne déclarée par l'officine ≤ R + (lignes en collision), et ces lignes en collision sont celles de
   `cloud_ligne_divergente` ;
5. toutes les lignes de l'ancienne branche ont été **reçues en époque < E' et avant la déclaration** (`sync_recu_le`, horloge du
   central) ; une ligne d'ancienne époque reçue après est une anomalie ;
6. toutes les lignes de la nouvelle branche sont **postérieures** à la dernière réception de l'ancienne branche
   (`enregistre_le` / `cree_le` ≥ dernier `sync_recu_le` de l'ancienne branche − tolérance d'horloge, S6.5-D7), et celles d'`id`
   non en collision ont un `id` > plus grand `id` connu du central au moment de la déclaration ; une nouvelle branche qui
   **réécrit le passé** (dates anciennes, mêmes tickets) n'est pas une restauration ;
7. les deux branches se vérifient de bout en bout (point 3) ;
8. les sceaux reçus avant la déclaration restent tous vérifiés : ceux de rang ≤ R ancrés dans la partie commune ; ceux de rang > R
   ancrés dans l'ancienne branche (`(rang, id, empreinte)`) et **signalés à part** (point 5).

Si une condition manque : anomalie d'aujourd'hui (`modification_refusee`, `chainage_rompu`…), avec dans le détail la condition
non remplie.

**5. Rangement et natures d'anomalies.**
- Table ajout seul `cloud_integrite_bifurcation` : officine, journal, `cle_chaine`, ancienne et nouvelle époque, référence de la
  déclaration, rang commun R et son empreinte, fin de l'ancienne branche (rang N, `id`, empreinte), nombre de lignes de l'ancienne
  branche, période (première et dernière date), pour `journal_vente` montant TTC des événements « vente » et « annulation » de
  l'ancienne branche, sceaux de l'ancienne branche, classe (`restauration_constatee` / `restauration_a_justifier`), date du constat.
- Nouvelles natures dans `ck_cloud_anomalie_integrite_nature` : `remplacee_apres_restauration` (une par bifurcation et par
  journal, pas une par ligne : le détail est dans la table), `sceau_perdu_restauration` (sceau d'une ligne de l'ancienne branche),
  `ticket_reattribue_restauration` (selon S6.5-D6). Elles **restent** des anomalies (ajout seul, journal de sécurité
  `integrite_anomalie`) : seul leur classement change.
- Anomalies déjà notées à tort (restauration antérieure à S6.5, ou notées avant la fin du chargement) : `cloud_anomalie_integrite`
  est en ajout seul, on ne les modifie pas ; une table ajout seul `cloud_anomalie_classement` (anomalie, bifurcation, classe, date)
  les rattache à la bifurcation. L'anomalie d'origine reste visible, avec la mention « classée : remplacée après restauration »
  (S6.5-D5).

**6. Ce que voit le titulaire.**
- **Alerte** (une par bifurcation constatée, nature d'alerte nouvelle, sujet « Synchronisation ») : « Après le retour à l'époque N
  le JJ/MM, X ligne(s) du journal des ventes enregistrées du … au … (montant TTC …) n'existent plus dans l'officine. Elles sont
  gardées au serveur central comme preuve et classées « remplacées après restauration ». La nouvelle chaîne a été vérifiée de bout
  en bout. À faire : ne rien corriger ; garder la sauvegarde utilisée et la cause de la restauration ; prévenir l'éditeur. »
  Classe « à justifier » (S6.5-D3) : ton d'anomalie, « à justifier auprès de l'éditeur ».
- **Compte rendu** `integrite-v1` : champ en plus `restaurations` (liste des bifurcations : journal, époques, rang commun, lignes,
  période, montant, sceaux perdus, classe) ; les natures nouvelles ont un libellé dans `IntegriteCentrale.LibelleNature`. Une
  officine non mise à jour ignore le champ et affiche la nature brute (comportement actuel de `Lire` et du `switch`) : compatible.
  Statut : selon S6.5-D4.
- **Écran** Administration › Synchronisation › Intégrité au central : rubrique « Restaurations constatées » (tableau paginé, lecture
  seule, droit `admin.parametres`) ; chaque chaîne affiche « vérifiée depuis la restauration du … » au lieu de « rupture ». Règle des
  écrans sans défilement et charte graphique (captures clair et sombre).

**7. Ce que doit garder un contrôle fiscal (rien n'est effacé ni masqué).**
- Au central : les lignes de l'ancienne branche dans la table miroir (avec `sync_absente_le`), `ligne_remplacee`,
  `cloud_ligne_divergente`, les lots bruts `lot_recu` (durée `conservation_lots_ans`), la déclaration, les bifurcations, les
  anomalies et leur classement, le journal de sécurité ; dates de réception du central.
- Un export « restauration » par officine et par bifurcation (CSV des deux branches + empreintes SHA-256, même forme que l'export
  de départ, `DepartOfficine.cs`) ; l'export de départ inclut les nouvelles tables.
- À l'officine : la ligne `journal_audit` de la nouvelle époque, l'alerte, et ce que le titulaire a noté de la cause
  (S6.5-D4). La base locale ne contient plus les lignes perdues (S6-D2 : pas de reconstruction depuis le central) : seul l'export
  du central les présente (S6.5-D9).

### 31.3 Risques

| Risque | Parade proposée |
|---|---|
| **Falsification déguisée en restauration** (réécrire le journal à partir d'un rang r, puis « repartir sur une nouvelle époque ») | (1) L'ancienne branche n'est jamais perdue : les ventes « effacées » restent au central, comptées et chiffrées dans l'alerte, l'écran et l'export ; (2) condition 6 : une nouvelle branche qui réécrit des événements passés (dates antérieures à la dernière réception, mêmes tickets) n'est pas classée « restauration » ; (3) condition 1 : déclaration par le titulaire avant tout lot, horodatée par le central ; (4) bifurcation sous un sceau ou une clôture fiscale : classe « à justifier » (S6.5-D3) ; (5) fréquence : au-delà d'un seuil de restaurations, alerte éditeur (S6.5-D7, D8) |
| Horloge de l'officine faussée pour satisfaire la condition 6 | La référence est `sync_recu_le` (horloge du central) ; une nouvelle ligne datée avant la dernière réception est refusée au classement ; une datation dans le futur est déjà visible (`survenu_le`, tickets) **[À VÉRIFIER : contrôle de vraisemblance des dates existant ou non]** |
| Lot tardif de l'ancienne époque reçu après la déclaration | Condition 5 : anomalie ; l'officine ne renvoie jamais seule ces lots (§ 30.6) |
| Lignes en collision dans `sceau_integrite`, `cloture_fiscale`, `verification_integrite` (fenêtre d'une minute) | Même mécanisme `cloud_ligne_divergente` pour les cinq tables à ajout seul |
| Double vie (deux serveurs actifs en même temps après une bascule) | Hors S6.5 : même époque, deux suites → refus de lot 409 (§ 30.2) ; une 2e bifurcation sans déclaration reste une anomalie |
| Restaurations successives (E → E' → E'') | Bifurcations multiples, chacune avec sa déclaration ; algorithme par liens d'empreinte, sans limite de profondeur |
| Durée de la vérification par branches sur gros volume | Index au central sur `(officine_id, cle_chaine, rang)` (non unique) des journaux miroir (aucun aujourd'hui) ; ancienne branche vérifiée une fois puis figée **[À VÉRIFIER : volume réel, déjà ouvert au § 22.5]** |
| Ordre de passage des scripts | 021 porte la dernière définition de `cloud_integrite_ajout_seul` : le nouveau script la redéfinit en gardant la condition d'effacement ; rejouer 012 ou 021 après lui la rétrécirait (même piège qu'au § 30.5) : à écrire dans l'en-tête |
| Contrat partagé modifié (déclaration) | Nouvelle route versionnée ; officine ancienne = pas de déclaration = comportement d'aujourd'hui (aucune régression) |

### 31.4 Décisions à demander au titulaire

| N° | Question | Recommandation |
|---|---|---|
| S6.5-D1 | Le changement d'époque doit-il être **déclaré** au central (nouvelle route, contrat modifié) ou **déduit** (premier lot d'une époque plus grande + ligne `journal_audit`) ? | **Déclaré**, avant tout lot de la nouvelle époque, avec les fins de chaîne locales ; sans déclaration, aucune indulgence |
| S6.5-D2 | Ligne d'un journal chaîné reçue d'une époque plus récente avec un autre contenu : (a) original en place, nouvelle version dans `cloud_ligne_divergente` ; (b) nouvelle version appliquée, ancienne dans `ligne_remplacee` (comme les autres tables depuis S6.1) | **(a)** : la règle S4 « l'original reste » ne change pas ; la table miroir garde la première version reçue |
| S6.5-D3 | Restauration qui fait disparaître des lignes **déjà scellées** (clôture journalière ou mensuelle) | Classe **« restauration à justifier »** (rouge, alerte titulaire et éditeur), jamais « remplacée » simple |
| S6.5-D4 | Statut du compte rendu après une restauration constatée | Nouveau statut **« restauration »** (orange) tant que le titulaire n'a pas **pris acte** avec la cause (texte court, journal ajout seul envoyé au central) ; ensuite « ok » avec la restauration toujours listée ; une vraie anomalie garde « anomalie » |
| S6.5-D5 | Anomalies déjà notées à tort (avant S6.5 ou avant la fin du chargement) | Les **rattacher** par une table de classement ajout seul ; ne jamais les modifier ni les cacher |
| S6.5-D6 | N° de tickets « T- » après restauration : (a) avancer `compteur_ticket` au-delà du plus grand n° reçu par le central (pas de doublon ; trou à expliquer dans le contrôle local de continuité) ; (b) laisser réattribuer (doublons de n° au central, signalés `ticket_reattribue_restauration`) | **(a)**, avec la plage sautée notée à l'officine et reconnue par `journal_vente_continuite` **[À VÉRIFIER : avis du comptable / de l'administration fiscale sur un trou ou un doublon de n°]** |
| S6.5-D7 | Seuils de vraisemblance | Tolérance d'horloge **10 minutes** (condition 6) ; plus de **2 restaurations sur 12 mois** : alerte éditeur et classe « à justifier » |
| S6.5-D8 | L'éditeur est-il prévenu de chaque restauration constatée ? | **Oui** (journal de sécurité du central + console `--restaurations-integrite`), conformément au § 5 (« alerte titulaire et éditeur ») ; écran central avec S7 |
| S6.5-D9 | Faut-il redescendre à l'officine les lignes perdues (pour un contrôle sur place) ? | **Non** (S6-D2) ; export du central remis sur demande (console), inclus dans l'export de départ |

### 31.5 Découpage (proposé le 2026-11-18, réalisé : § 31.6 à 31.8)

| Lot | Contenu |
|---|---|
| S6.5.0 | Mention à court terme dans l'alerte de retour en arrière (décidée) **[À VÉRIFIER : état]** |
| S6.5.1 | Central `022_integrite_restauration_tables.sql` : `cloud_epoque_changement`, `cloud_ligne_divergente`, `cloud_integrite_bifurcation`, `cloud_anomalie_classement` (RLS forcée, ajout seul, gardes d'effacement de départ comme 021) ; natures nouvelles ; index `(officine_id, cle_chaine, rang)` ; redéfinition de `cloud_integrite_ajout_seul` (point 2) ; tests SQL |
| S6.5.2 | Central `023_integrite_verification_branches.sql` : vérification par liens d'empreinte, constat de bifurcation (conditions 1 à 8), sceaux et croisée par `(rang, id)`, tickets (selon D6), résumé `integrite-v1` étendu (`restaurations`, statut D4) ; tests SQL : restauration sans collision, avec collisions, falsification déguisée (dates passées), bifurcation sous sceau, lot tardif d'ancienne époque, deux restaurations successives, registre en mode « liaison », anomalies antérieures rattachées |
| S6.5.3 | API centrale : route de déclaration (certificat de l'officine, cloisonnée), DTO partagé ; console `--restaurations-integrite` et export « restauration » ; export de départ complété ; tests `Pharma.Cloud.Tests` |
| S6.5.4 | Officine `150_…` (devenu `151`, le n° 150 ayant été pris entre-temps) : déclaration envoyée par `sync_nouvelle_epoque` / `EnvoyerLotsSync` avant le premier lot (et réessayée) ; compteur de tickets (D6) ; prise d'acte du titulaire (D4) ; `IntegriteCentrale` (libellés, `restaurations`), alerte, écran ; `database/init` à jour ; tests |
| S6.5.5 | Essai de bout en bout sur bases jetables (avec S6.4) : restauration simulée, ventes pendant la fenêtre, nouvelle époque, chargement, vérification incrémentale puis complète, alerte et écran (captures clair et sombre, mesures) |
| S6.5.6 | Documentation : ce § 31 complété, `docs/regles-metier.md` (règles de classement), README de `database/cloud` (ordre de passage 021 → 022 → 023) |

**[À VÉRIFIER]** : comportement réel du § 31.1 par un essai (lecture du code seulement) ; `journal_audit` envoyé au central et avec
quelles colonnes ; existence d'un contrôle de vraisemblance des dates à l'officine ; règle locale de `compteur_ticket` (peut-il
être avancé sans casser `journal_vente_continuite`) ; durée de la vérification par branches sur le volume réel d'une officine ;
avis du juriste sur la présentation des lignes perdues lors d'un contrôle fiscal.

### 31.6 Réalisation S6.5.0 à S6.5.3 (2026-11-18, « go S6.5 ») — serveur central

| Lot | Réalisé |
|---|---|
| S6.5.0 | Déjà fait au § 30.8 (point 7) : phrase « Des anomalies d'intégrité sont attendues… » dans le message de suspension, l'alerte (SMS et e-mail) et le bandeau de l'écran (`EnvoyerLotsSync.PhraseIntegrite`, `Supervision.cs`). |
| S6.5.1 | Script central `022_integrite_restauration_tables.sql` : `cloud_epoque_changement` (déclaration, `avant_premier_lot` = aucun lot de la nouvelle époque reçu avant, date de réception du central ; une par nouvelle époque, renvoi identique accepté, autre contenu refusé 23505), `cloud_ligne_divergente` (une seule fois par version : index unique sur l'empreinte md5 de la ligne), `cloud_integrite_bifurcation`, `cloud_anomalie_classement` : RLS forcée, ajout seul (garde `cloud_integrite_restauration_garde`, effacement de départ permis), aucun droit direct de `pharma_cloud_api` ; `cloud_declarer_changement_epoque` (SECURITY DEFINER, seule fonction ouverte à l'API, contrôles des époques, n° et fins de chaîne, journal `changement_epoque_declare`) ; natures `remplacee_apres_restauration`, `sceau_perdu_restauration`, `ticket_reattribue_restauration` ; index `(officine_id, cle_chaine, rang)` des deux journaux ; `cloud_integrite_ajout_seul` : contenu différent d'une époque **plus récente déclarée** → version gardée dans `cloud_ligne_divergente`, sans anomalie ; sinon comme avant ; `cloud_effacer_officine` étendue aux quatre tables ; gardes de rejeu ajoutées dans `021`. |
| S6.5.2 | Script central `023_integrite_verification_branches.sql` : aiguillage `cloud_integrite_verifier_chaine` — sans déclaration pour l'officine, ou sans rang en double ni version divergente : **vérification d'origine** (copie exacte du 012, `cloud_integrite_verifier_chaine_lineaire`) ; sinon `cloud_integrite_verifier_branches` (toujours depuis le rang 1, nœuds = miroir + versions divergentes dans une table temporaire, liens d'empreinte, empreintes recalculées en mode complet). Bifurcation : `cloud_integrite_constater_bifurcation`, conditions 1 à 8 du § 31.2 (déclaration la plus proche dont l'ancienne époque couvre l'ancienne suite : restaurations successives traitées) ; chargement non terminé → `attente` ; condition manquante → `chainage_rompu` avec la condition dans le détail ; reconnue → `cloud_integrite_bifurcation` (lignes, période, montant TTC, sceaux perdus, conditions en jsonb), une anomalie `remplacee_apres_restauration`, une `sceau_perdu_restauration` par sceau, rattachement des anomalies déjà notées sur la suite remplacée (`cloud_anomalie_classement`), vérification poursuivie sur la suite vivante (`cloud_integrite_etat.epoque_branche`). Classe « à justifier » : sceau perdu (D3) ou plus de 2 déclarations sur 12 mois (D7). Sceaux et vérification croisée : recherche par `(rang, n°, empreinte)` dans les deux suites (`cloud_integrite_ligne_existe`, `cloud_integrite_ligne_vivante`). Résumé `integrite-v1` : `restaurations` (avec `prise_acte`), `anomalies_classees`, `classee` par anomalie, statut `restauration` (D4) ; une officine non mise à jour ignore ces champs. Gardes de rejeu ajoutées dans `012`. |
| S6.5.3 | API centrale : `POST /api/v1/epoque` (certificat, limite `officine` ; 200 avec `ReponseDeclarationEpoqueDto`, 400 refus, 409 autre contenu), contrat partagé `DeclarationEpoqueDto` / `FinChaineDto` (`Refus()`, mêmes bornes que le SQL), `DepotCentral.DeclarerEpoqueAsync` ; console `--restaurations-integrite [--officine <code>]` (D8) ; export de départ : quatre nouvelles tables en CSV. |

**Essais** : copie jetable de `pharmacie_cloud_dev` (supprimée) : `022` et `023` passés deux fois ; rejeu de `021` puis `012` après eux : définitions de 022 et 023 gardées (NOTICE) ; toute la suite `database/cloud/tests` (001 à 023) réussie. `pharmacie_cloud_dev` : sauvegarde `C:\MesAtlassian\sauvegardes-bd\pharmacie_cloud_dev_avant_s65_20261118_2338.dump` (`--enable-row-security`), puis `021`, `022`, `023` et tests 022 / 023 réussis. Tests `023` : restauration avec collision (attente avant la fin du chargement, reconnue après, anomalie unique, suite vivante prolongée, repassage sans doublon), réécriture du passé refusée (condition 6), ligne scellée perdue (« à justifier », sceau toujours ancré), cloisonnement. `Pharma.Cloud.Tests` : 78 réussis, 1 ignoré (déclaration : première, renvoi identique, autre contenu 23505 ; bornes du contrat ; déclaration présente dans l'export). Solution compilée, `Pharma.Architecture.Tests` 243/243.

**Reste (S6.5.4 à S6.5.6)**, fait depuis (§ 31.7 à 31.9) : officine — script `151` (table montante `integrite_prise_acte` : nouvelle époque, cause, titulaire ; compteur de tickets avancé au-delà du plus grand n° reçu par le central et plage sautée reconnue par `journal_vente_continuite`, D6), envoi de la déclaration par `EnvoyerLotsSync` avant le premier lot de la nouvelle époque (réessayée ; route absente en 404 : lots envoyés sans déclaration, comme avant), fins de chaîne lues dans `chaine_integrite`, `IntegriteCentrale` (statut `restauration`, libellés des trois natures, `restaurations`), prise d'acte du titulaire et écran Synchronisation › Intégrité au central (rubrique « Restaurations constatées »), alerte ; puis régénération du `002` (`--generer-miroir`) pour `integrite_prise_acte` ; essai de bout en bout (S6.5.5) ; documentation finale (S6.5.6). **[À VÉRIFIER]** : volume réel (vérification par branches toujours depuis le rang 1 quand une restauration est déclarée) ; bifurcation à trois suites ou bifurcation dans l'ancienne suite : refusée (anomalie), non rencontrée.

### 31.7 Réalisation S6.5.4 (2026-11-19, « go S6.5.4 ») — officine

| Élément | Réalisé |
|---|---|
| Tickets (S6.5-D6 a, choix du titulaire le 2026-11-19) | Central `024` : `cloud_dernier_ticket()` (plus grand n° reçu, préfixe du compteur reçu, ventes absentes comprises), renvoyé par `/maximums` sous l'entrée réservée `#ticket` (une officine antérieure l'ignore). Officine `151` : `sync_avancer_compteur_ticket` (jamais en arrière, saut de plus de 10 millions refusé), plage notée dans `compteur_ticket_saut` (ajout seul, motif « restauration », époque), `compteur_ticket_proteger` n'admet ce saut qu'annoncé par la transaction (`pharma.saut_ticket`) et noté, `journal_vente_continuite` ne compte pas la plage comme n° manquant. `EnvoyerLotsSync` avance le compteur **avant** les séquences (un échec empêche « avance faite » : tout est réessayé). **[À VÉRIFIER : avis du comptable sur une plage sautée.]** |
| Déclaration (S6.5-D1) | `151` : `sync_declaration_epoque` remplie par `sync_nouvelle_epoque` (redéfinie : ancienne et nouvelle époque, dernier lot local, dernier lot vu au central, date de détection, titulaire, `sync_fins_chaine()` : dernier rang, n° et empreinte de chaque journal chaîné, rang 0 si vide) ; `sync_noter_declaration`. `EnvoyerLotsSync.DeclarerEpoqueAsync` : après le contrôle du retour en arrière, **avant** le chargement complet et tout lot ; central injoignable : rien n'est envoyé (nouvel essai) ; 404, 409, 400 : résultat noté, lots envoyés ; époque dépassée : notée non envoyée. Client : `POST /api/v1/epoque`. |
| Prise d'acte (S6.5-D4) | `151` : `integrite_prise_acte` (une par époque, cause 3 à 500 caractères, ajout seul, montante, cause filtrée) ; miroir central (`002` régénéré). `PrendreActeRestaurationSync` (titulaire), route `POST /api/admin/synchronisation/integrite/prise-acte`, journal `integrite_prise_acte`. Au central, `cloud_integrite_prise_acte` la lit : statut « ok » si rien d'autre n'est en anomalie (test `023` complété). |
| Officine, affichage | `IntegriteCentrale` : statut `restauration`, `restaurations` (classe, prise d'acte), `anomalies_classees`, `classee` par anomalie, libellés des trois natures. Administration › Synchronisation : rubrique **« Restaurations constatées »** (tableau, « Prendre acte » pour le titulaire), lien depuis « Intégrité au central » ; anomalies rattachées marquées. |
| Gardes de rejeu | `112` (`compteur_ticket_proteger`, `journal_vente_continuite`) et `149` (`sync_nouvelle_epoque`) ne ramènent plus en arrière les définitions du `151`. |

**Essais** : copie jetable de `pharmacie_test` : `151` passé deux fois, tests `151` et `149` réussis, rejeu de `112` et `149` sans retour en arrière. `pharmacie_dev` et `pharmacie_test` : sauvegardes `*_avant_151_20261119_0042.dump`, `151` passé deux fois, tests réussis ; `database/init/schema.sql` régénéré (001 à 151) et `seed_referentiel.sql` (classement des trois tables). Central : `002` régénéré (`integrite_prise_acte`) et `024` passés sur une copie jetable puis sur `pharmacie_cloud_dev` (sauvegarde `pharmacie_cloud_dev_avant_024_20261119_0044.dump`) ; suite `database/cloud/tests` 001 à 024 réussie. Tests : `Pharma.Application.Tests` 1 646 (9 nouveaux : déclaration avant tout lot et jamais renvoyée, central injoignable, 404 et 409, époque dépassée, compteur avancé et réessayé, prise d'acte, lecture du résumé), `Pharma.Architecture.Tests` 243, `Pharma.Cloud.Tests` 78. Écran : rendu mesuré et regardé (charte-graphique.md), prise d'acte essayée dans le poste sur une copie jetable (refus de la cause courte, champ en rouge, enregistrement, journal).

**Reste**, fait depuis (§ 31.8 et 31.9) : S6.5.5, essai de bout en bout avec le vrai serveur central (restauration simulée, ventes pendant la fenêtre, nouvelle époque, déclaration, chargement, vérification incrémentale puis complète, prise d'acte, tickets) ; S6.5.6, documentation finale. **[À VÉRIFIER]** : volume de la vérification par branches sur une vraie officine ; avis du comptable (plage de n° sautée) et du juriste (présentation des lignes perdues lors d'un contrôle).

### 31.8 Lot S6.5.5 : essai de bout en bout (2026-11-19, « go S6.5.5 »)

**Environnement** : `scripts/dev/essai_cloud_officine.ps1` avec un `TEMP` dédié (hors dépôt) et des ports à part (central 5196, API 5197),
bases jetables `pharmacie_cloud_essai` (scripts centraux 001 à 024) et `pharmacie_essai_officine` (copie de `pharmacie_dev`). Ventes faites
**par l'API de la caisse** (`/api/caisse/encaisser`, compte titulaire de démonstration, Paracétamol, espèces) : tickets, journal des ventes
chaîné et envoi réels. Restauration simulée : API d'essai arrêtée par son PID, base recréée depuis un `pg_dump` pris avant les ventes
perdues, API relancée, puis 2 ventes dans les 30 s qui précèdent le premier passage d'envoi (fenêtre avant la détection). Fin d'essai :
`-Arreter` (bases, rôle, dossier supprimés ; aucun port 5196, 5197, 9223 ou 9333 en écoute) ; sauvegardes d'essai supprimées.

**Premier passage : deux défauts trouvés**

| N° | Constat | Cause | Correction |
|---|---|---|---|
| 1 | Déclaration de la nouvelle époque **refusée (400)** par le central ; lots envoyés sans elle (chemin de repli : rangs 5 et 6 notés `modification_refusee`, versions d'origine gardées, aucune restauration classée — comportement prévu sans déclaration) | `sync_fins_chaine()` (script 151) déclarait toutes les chaînes, y compris `integrite_essai` (table interne de la brique, jamais envoyée, présente dans toute base neuve) ; le central refuse toute déclaration qui cite un journal qu'il ne connaît pas. **Toute officine aurait été touchée.** | `151` : seulement les journaux envoyés au central (`sync_table` montante ou bidirectionnelle) ; test du `151` complété. Central (`PointsEntree.cs`) : raison d'un refus écrite au journal du central (rien n'était écrit). |
| 2 | Restauration déclarée, nouvelle suite **plus courte** que l'ancienne (rangs 5 et 6 contre 5 à 7) : bifurcation bien classée, mais l'ancienne ligne 7 notée « chaînage rompu », chaîne en « rupture », statut « anomalie » (la prise d'acte n'aurait pas ramené « ok ») | `cloud_integrite_verifier_branches` (023) : au bout de la suite vivante, la ligne suivante de l'**ancienne** branche était prise pour la suite. Les tests du 023 n'avaient que des nouvelles suites au moins aussi longues que l'ancienne ; le cas réel (restauration suivie de peu de ventes) est l'inverse | `023` : `cloud_integrite_ancienne_branche()` écarte les lignes d'une ancienne branche reconnue (au-delà du rang commun, jusqu'à sa fin, reçues avant la nouvelle époque) ; cas **D** ajouté aux tests du 023 (suite plus courte, puis vente suivante au même rang que l'ancienne ligne). L'anomalie déjà notée à tort est reclassée (`cloud_anomalie_classement`) au passage suivant. |

**Passage complet, après correction (nouvel environnement propre)**

| N° | Étape | Constaté | Résultat |
|---|---|---|---|
| 1 | Activation, chargement, 3 ventes (T-017617 à 619) | lots 1 à 76 accusés ; vérification complète : ok, journal des ventes ok au rang 4 | OK |
| 2 | Point de restauration, 3 ventes (T-017620 à 622) envoyées | lot 77 ; vérification incrémentale : ok au rang 7 | OK |
| 3 | Restauration, 2 ventes dans la fenêtre | n° T-017620 et 621 **repris** (contenu différent) ; détection en 27 s (central au lot 77, base au lot 76) ; compteur avancé à 17622 (plus grand n° reçu par le central), plage 17622 notée (`compteur_ticket_saut`, époque 1) ; contrôle local : 0 n° manquant | OK |
| 4 | Nouvelle époque (titulaire) | époque 2, chargement 53 ; déclaration **reçue par le central avant le premier lot** (fins de chaîne : psychotropes rang 1, ventes rang 6) | OK |
| 5 | Vérification incrémentale puis complète | restauration constatée époque 1 → 2, rangs 5 à 7, 3 lignes, 360,00 DA TTC, 0 sceau perdu, classe « remplacée après restauration » ; 2 lignes divergentes gardées comme preuve ; chaîne ok au rang 6 ; statut **« restauration »** ; résumé reçu par l'officine | OK |
| 6 | Écran (poste branché sur l'API d'essai) | rubriques « Intégrité au central » et « Restaurations constatées » à 0 px, clair et sombre, 1 536 × 890 et 1 366 × 768 ; captures regardées | OK |
| 7 | Prise d'acte depuis le poste | cause saisie, enregistrée, envoyée (lot 77 accusé) ; vérification du central : statut **« ok »**, restauration toujours listée | OK |
| 8 | Vente suivante | T-017623 (le 17622 n'est pas réattribué), rang 7 de la nouvelle suite (même rang que l'ancienne ligne perdue) : vérifiée ok au rang 7, aucune nouvelle anomalie ; écran « Aucune anomalie », tickets vérifiés jusqu'au n° 17623 | OK |

**Correction d'écran** : après la prise d'acte, le message de réussite restait affiché en changeant de rubrique et faisait dépasser
« Intégrité au central » de 52 px à 1 366 × 768 ; il est maintenant effacé au changement de rubrique (`AdminSynchronisation.razor`) :
mesuré à 0 px ensuite.

**Bases de développement** : `151` corrigé passé sur `pharmacie_dev` et `pharmacie_test` (sauvegardes `*_avant_151b_20261119_0210.dump`),
tests réussis, `database/init/schema.sql` régénéré (seule la fonction change). `023` corrigé passé sur `pharmacie_cloud_dev` ;
**la sauvegarde d'avant a échoué** (`pg_dump` sans `--enable-row-security` refusé par la RLS forcée) et le script est passé quand même :
il ne fait que remplacer des fonctions (aucune donnée touchée) ; sauvegarde prise juste après
(`pharmacie_cloud_dev_apres_023b_20261119_0211.dump`) ; suite centrale 022 à 024 réussie. Tests : Application 1 646, Architecture 243,
Cloud 78, Domain 630.

**Constats sans correction**
- **N° de tickets repris dans la fenêtre** (avant la détection) : D6 (a) n'avance le compteur qu'à la détection ; les ventes faites
  entre le redémarrage et le premier passage (30 s au moins, davantage si le central est injoignable) reprennent des n° déjà reçus par
  le central. La nature `ticket_reattribue_restauration` est prévue (022) mais **aucune vérification ne la produit** : ces doublons ne
  sont visibles qu'à travers les lignes divergentes. **[DÉCISION DU TITULAIRE : signaler les n° repris, et comment ?]**
- Données de la page Synchronisation lues une fois : passer d'une rubrique à l'autre ne relit pas l'état (« Actualiser » le fait).
- Opérations de l'ancienne époque restées dans la file (`sync_sortie`, 154 ici) : jamais envoyées (le chargement les remplace), purgées
  comme prévu en S6.

### 31.9 Synthèse finale du lot S6.5 (2026-11-19, S6.5.6)

**Ce que fait le logiciel après la restauration d'une base d'officine** (ou une bascule sur une base en retard) :

1. **Détection** (S6, § 30) : au premier échange, l'officine voit que le central a reçu plus de lots qu'elle n'en a envoyé ; elle
   suspend l'envoi, avance ses séquences et **son compteur de tickets** au-delà de ce que le central a reçu (plage de n° sautée notée,
   non comptée comme manquante), et alerte le titulaire.
2. **Nouvelle époque** (titulaire, mot de passe ressaisi) : l'officine note une **déclaration** (époques, lots, fins de chaîne des
   journaux envoyés) et l'envoie au central **avant tout lot** de la nouvelle époque, puis repart sur un chargement complet.
3. **Réception au central** : une ligne de journal chaîné reçue avec un autre contenu laisse l'original en place ; la nouvelle version est
   gardée à part (`cloud_ligne_divergente`).
4. **Vérification** (toutes les 15 min, complète chaque semaine) : elle suit les liens d'empreinte ; deux suites au même rang sont
   classées « remplacée après restauration » si les huit conditions sont réunies, « à justifier auprès de l'éditeur » si une ligne scellée
   est perdue ou si les restaurations sont trop fréquentes, sinon « chaînage rompu ». Une ancienne branche plus longue que la nouvelle
   n'est pas une rupture. L'ancienne suite n'est jamais effacée.
5. **Officine** : Administration › Synchronisation › « Restaurations constatées » ; statut « Restauration constatée » jusqu'à la
   **prise d'acte** du titulaire (cause), puis « Aucune anomalie » si rien d'autre n'est en anomalie.

**Ordre de mise en production** : scripts centraux `022`, `023`, `024` (et `002` régénéré) **avant** le script officine `151`
(`docs/mise-en-production.md` étape 10.2, `database/cloud/README.md`). Un central non mis à jour répond 404 à la déclaration :
l'officine le note et envoie ses lots sans indulgence (comportement d'avant S6.5).

**Questions de l'analyse d'origine, tranchées**

| Question (§ 31.1 à 31.5) | Réponse |
|---|---|
| Comportement réel décrit au § 31.1 | Observé à l'essai S6.4 et S6.5.5 (§ 30.7, § 31.8) : conforme, sans déclaration ; avec déclaration, classement attendu |
| Règle locale de `compteur_ticket` : peut-il être avancé ? | Oui, seulement par `sync_avancer_compteur_ticket` (saut annoncé et noté) ; continuité locale sans faux manquant (script 151) |
| `journal_audit` envoyé au central, colonnes | Non tranché dans ce lot (hors du classement des journaux chaînés) **[À VÉRIFIER]** |
| Contrôle de vraisemblance des dates à l'officine | Non tranché ; le central tolère 10 minutes d'écart d'horloge (condition 6) **[À VÉRIFIER]** |
| Durée de la vérification par branches sur un volume réel | Non mesurée (essai : quelques lignes) **[À VÉRIFIER sur une vraie officine]** |
| Présentation des lignes perdues lors d'un contrôle fiscal | **[À VÉRIFIER : avis du juriste]** |

**Points ouverts**
- ~~Décision du titulaire : n° de tickets repris avant la détection~~ : choix « A + B » le 2026-11-19, réalisé (§ 31.10).
- **Avis du comptable** : plage de n° de tickets sautée après une restauration (S6.5-D6 a).
- Page Synchronisation : l'état n'est relu qu'avec « Actualiser » (gêne mineure, § 31.8).

### 31.10 N° de tickets repris avant la détection : choix « A + B » (2026-11-19)

**Constat (§ 31.8)** : une base restaurée ne le sait pas d'elle-même ; seul le serveur central peut dire qu'il a reçu plus de lots. Les
ventes faites entre le redémarrage et la détection (30 s au moins avant) reprenaient des n° de tickets déjà reçus, sans aucune anomalie.

**Choix du titulaire** : **A** le central signale ces n° ; **B** la caisse attend le contrôle du central au démarrage, **120 s au plus**
(réglable, 0 à 600, 0 : pas d'attente), puis la vente est permise même sans Internet.

| Élément | Réalisé |
|---|---|
| B, officine `152` | `sync_parametre.demarrage_le`, `controle_demarrage_le`, `controle_demarrage_delai_s` ; `sync_noter_demarrage()` (seulement synchronisation active, adresse réglée, serveur activé), `sync_noter_controle_demarrage()`, `sync_regler_controle_demarrage()`, `encaissement_attente_secondes()` (aussi après une détection tant que l'avance des n° n'est pas faite, même plafond) ; `prochain_numero_vente()` refuse pendant l'attente (P0001 → 422, message clair, compteur inchangé). Garde de rejeu dans `112`. |
| B, API | `EnvoiSynchronisation` : démarrage noté, premier passage **aussitôt** (plus les 30 s), puis toutes les 10 s tant que l'attente dure ; ces essais rapprochés ne comptent pas pour l'attente croissante (constaté à l'essai : sinon le passage suivant partait 30 min plus tard). `EnvoyerLotsSync` note le contrôle quand le central répond « pas de retour en arrière », ou après un retour en arrière une fois le compteur et les séquences avancés ; jamais sur un central injoignable. Route `GET /api/caisse/attente-encaissement` (droit `caisse.vendre`). |
| B, écrans | Caisse : bandeau du temps restant ; Synchronisation › Réglages : « Attente au démarrage (s) » (journalisé, refus hors 0 à 600). |
| A, central `025` | `cloud_integrite_tickets_reattribues()` : pour chaque restauration constatée du journal des ventes, une vente de la nouvelle suite qui reprend le n° d'une vente de l'ancienne (reçue avant la nouvelle époque, après le rang commun) → anomalie `ticket_reattribue_restauration` (une par vente, n° dans « attendu »), rattachée à la restauration et classée comme elle ; `cloud_verifier_integrite` l'appelle, le résumé porte `tickets_reattribues` par restauration. Garde de rejeu dans `023`. **Ordre** : `025` avant `152`. |
| A, officine | `IntegriteCentrale` lit `tickets_reattribues` (borné à 1 000, textes de 1 à 40 caractères) ; Restaurations constatées : colonne « Tickets réutilisés ». |

**Essais** : copies jetables (`152` et `025` passés deux fois, rejeux de `112` et `023` sans retour en arrière, tests 151, 152, 012, 022
à 025 réussis), puis `pharmacie_dev`, `pharmacie_test` et `pharmacie_cloud_dev` (sauvegardes `*_avant_152_20261119_0236.dump` et
`pharmacie_cloud_dev_avant_025_20261119_0236.dump`, prises avec `--enable-row-security` pour le central et vérifiées avant) ;
`database/init/schema.sql` régénéré (001 à 152). Tests : Application 1 647, Architecture 243, Cloud 78.

**Essai de bout en bout** (`essai_cloud_officine.ps1`, central 5196, API 5197, bases jetables supprimées ensuite)

| N° | Étape | Constaté | Résultat |
|---|---|---|---|
| 1 | Redémarrage de l'API, central joignable | contrôle noté **0,25 s** après le démarrage ; vente aussitôt acceptée (T-017617) | OK |
| 2 | Point de restauration, 3 ventes envoyées | T-017618 à 620, lot 78 accusé | OK |
| 3 | Restauration, central injoignable, redémarrage | attente 117 s ; vente **refusée** (422, message clair) ; Caisse : bandeau « encaissement possible dans 1 min 58 au plus » | OK |
| 4 | Délai écoulé, sans central | 2 ventes acceptées : T-017618 et 619 **repris** (prévu) | OK |
| 5 | Central rétabli | détection, compteur avancé à 17620 (plage notée), nouvelle époque 2, déclaration reçue avant le premier lot, chargement terminé | OK |
| 6 | Vérification complète au central | restauration constatée (rangs 3 à 5) ; **2 anomalies `ticket_reattribue_restauration`** (T-017618, T-017619), rattachées et classées ; statut « restauration » (pas « anomalie ») | OK |
| 7 | Officine | résumé reçu : `ticketsReattribues` = T-017618, T-017619 ; colonne « Tickets réutilisés » ; vente suivante T-017621 (aucun n° repris) | OK |
| 8 | Écrans | Caisse (bandeau), Réglages, Restaurations constatées : 0 px aux deux tailles, clair et sombre (charte-graphique.md) | OK après correction des Réglages |

**Défauts trouvés et corrigés pendant l'essai** : attente croissante gonflée par les essais rapprochés du démarrage (corrigé dans
`EnvoiSynchronisation` ; rejoué le 2026-11-19 sur un essai neuf : API redémarrée central coupé, vente refusée pendant l'attente
(422), délai écoulé à 09:02:26, vente acceptée, central relancé à 09:02:38 ; dernier essai manqué 09:02:37, passage suivant 09:04:37
(**2 min**, au lieu de 30 min avant correction), lots accusés et contrôle noté) ; Réglages à 1 366 × 768 (119 px → 0).

**Limites** : pendant l'attente, seul l'encaissement est refusé (une vente déjà en panier attend) ; au-delà du délai sans Internet, les
n° repris restent possibles : ils sont alors signalés par le central (A). **[À VÉRIFIER : avis du comptable sur les n° repris et sautés.]**

## 32. Lot S7.1 : écrans de l'éditeur (réalisé en local le 2026-11-21, go du titulaire, recommandations de la conception retenues)

Détail, routes, sécurité et marche à suivre : [ecrans-central.md](ecrans-central.md). Rien n'est en ligne.

- **Projet `src/Pharma.Cloud.Web`** : Blazor en rendu serveur **statique** (formulaires classiques), processus et port distincts du serveur central ;
  `https://127.0.0.1:5094` en développement (refus de démarrer hors boucle locale) ; **jamais la clé de l'autorité** (certificat public `autorite.crt`
  seulement, pour l'épinglage du code d'activation, comme `NouveauCodeAsync` de la console).
- **Scripts centraux `030_comptes_distants.sql` et `031_ecrans_editeur.sql`** (le n° 030 libéré par un autre lot) : rôle `pharma_cloud_ecrans`
  (sans connexion, aucun droit sur les tables, fonctions `cloud_ecran_*` `SECURITY DEFINER` seulement) ; `compte_distant`, `compte_officine` (vide,
  pour S7.2), `session_distante` (empreinte du jeton), `code_second_facteur` (empreinte HMAC), `journal_acces_distant` (ajout seul, chaîné SHA-256,
  § 6.1 point 8) ; officine courante posée **par la base** dans `cloud_ecran_ouvrir` après contrôle de la session et du droit.
- **Décision n° 4 appliquée** : l'éditeur ne voit **aucune** donnée nominative ni aucun contenu (jamais `ordre_central.donnees`, cible, auteur ou
  motif ; jamais `cloud_anomalie_integrite.detail` / `attendu` / `trouve` / `cle_ligne` ; jamais `lot_recu.contenu` ; jamais un code d'activation ni
  son empreinte ; jamais le motif d'une révocation ; aucune table miroir) ; contrôlé par les tests SQL et C#.
- **Actions** (code de second facteur à chaque action, motif, ressaisie ; journaux des accès et de sécurité) : créer une officine, nouveau code
  d'activation (principal / second), révoquer un certificat, révoquer un serveur. Départ, effacement, purges, familles, autorité : **console**.
- **Comptes** : console du central `--creer-compte-editeur`, `--comptes-distants`, `--desactiver-compte`, `--deverrouiller-compte` (journalisées) ;
  BCrypt coût 12 ; mot de passe provisoire à changer ; verrouillage 5 échecs → 15 min doublées jusqu'à 24 h ; second facteur en **mode fichier**
  en développement seulement, e-mail par le SMTP du central en production, SMS sans fournisseur.
- **Reste** : mise en ligne ; S7.2b (rubrique « Accès à distance » du poste) ; mesure des écrans (écran fixe) ; voir [ecrans-central.md](ecrans-central.md) § 8.

### 32.1 Lot S7.2a : écrans du titulaire, côté serveur central (réalisé en local le 2026-11-21, go du titulaire, recommandations S7 acceptées)

Le poste ne change pas dans ce lot. Détail et routes : [ecrans-central.md](ecrans-central.md) § 3 bis.

- **Script central `032_ecrans_titulaire.sql`** : `compte_officine.droits` (`consulter`, `journal_acces`) ; `code_rattachement` (empreinte
  SHA-256 seule, un compte, une officine, 30 min, usage unique) ; `cloud_ecran_rattacher` ; lecture du titulaire `cloud_ecran_titulaire_officines`,
  `_tableau` (**agrégats seulement** : chiffre d'affaires du jour, du mois et du mois précédent, nombre de ventes, part Chifa, crédits en cours,
  alertes de stock et de péremption, synchronisation, anomalies, ordres en attente), `_journal` ; console `cloud_compte_distant_code_rattachement`.
- **Officine posée par la base** (`cloud_ecran_ouvrir`, § 6 « cloisonnement ») et RLS forcée sur les tables miroir : un titulaire ne lit
  jamais une autre officine (tests de non-fuite SQL et C#). L'éditeur n'a aucune fonction du titulaire (décision n° 4 inchangée : il ne
  voit toujours aucune donnée de vente).
- **Rien d'inventé** : une donnée non montée (table ou colonne absente, famille non envoyée, aucun chargement complet terminé comprenant la
  table) est rendue NULL et affichée « non disponible ».
- **Journal des accès consultable par le titulaire** (§ 6) : accès de ses officines, acteur « vous », « éditeur », « autre titulaire » ou
  « console », sans identifiant ni nom.
- **Console** : `--creer-compte-titulaire … --auteur`, `--code-rattachement <identifiant> <officine> --auteur` (développement, en attendant S7.2b).
