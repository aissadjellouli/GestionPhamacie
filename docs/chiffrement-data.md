# Chiffrement des données nominatives avec ouverture à deux (option C)

Conception du 2026-11-17, rattachée au lot 9 (chiffrement) et à la synchronisation : [conception-synchronisation.md](conception-synchronisation.md).

> **Statut** : conception demandée par le titulaire le 2026-11-17 (« C, rédige la conception détaillée »). Ce paragraphe relève du **lot 9** :
> **aucun script, aucun code** tant que le titulaire n'a pas donné « go chiffrement ». Décisions DC1 à DC7 **acceptées** le 2026-11-17 (§ 10).
> Les éléments **[À VÉRIFIER]** ne sont pas établis ; les éléments **[DÉCISION]** attendent le titulaire.

## 1. But

Aujourd'hui (décision 1), le serveur central ne reçoit que des codes (`P-…`, `R-…`) ; la correspondance code ↔ personne est dans
`sync_pseudonyme`, qui ne quitte jamais l'officine. Si l'officine perd ses serveurs **et** ses sauvegardes, l'identité des patients et des
porteurs est perdue, et le central ne peut pas recalculer l'empreinte de `journal_registre_psychotrope`.

L'option C ajoute une **copie chiffrée** des données nominatives au central, que **personne ne peut ouvrir seul** :
il faut le **titulaire et l'éditeur ensemble**. En fonctionnement normal, rien ne change pour le central : il travaille sous code.

Ce qui **ne change pas** :
- les tables miroir du central gardent les codes et les colonnes retirées restent retirées (même contrat, mêmes écrans) ;
- aucune recherche par nom au central, **pas d'index aveugle** (empreinte du nom), qui permettrait de tester un nom connu ;
- décision 2 : ordonnances, images et GED restent dans l'officine (une sauvegarde chiffrée de la GED reste un sujet à part :
  [ged-protection-a-decider.md](ged-protection-a-decider.md), option B, qui pourra réutiliser le même mécanisme) ;
- décision 4 : le support de l'éditeur ne lit jamais seul une donnée nominative ;
- décision 8 et le cloisonnement par officine (RLS) : inchangés ;
- S4 : pour `journal_registre_psychotrope`, la vérification de routine au central reste la continuité des rangs et la conservation des
  empreintes reçues (validé le 2026-11-16) ; le recalcul complet devient **possible lors d'une ouverture** ([conception-synchronisation.md](conception-synchronisation.md) § 6).

## 2. Données chiffrées (décision DC1)

Les colonnes aujourd'hui **retirées** pour des raisons de données personnelles
([synchronisation-inventaire.md](synchronisation-inventaire.md)), et seulement elles :

| Table | Colonnes chiffrées |
|---|---|
| `patient` | nom, prenom, date_naissance, telephone, num_assure, email |
| `registre_psychotrope`, `journal_registre_psychotrope` | porteur_nom, porteur_adresse, porteur_piece |
| `vente` | remis_a_nom, pour_nom, chifa_qr |
| `client_facture` | telephone, email |
| `facture_client` | destinataire_nom, destinataire_infos |
| `facture_client_version` | contenu |
| Familles nominatives non autorisées par le réglage du central (`employes`, `banque`, `clients_factures`…) | **Toujours retirées**, pas chiffrées (DC1) |

**Jamais envoyés, même chiffrés** : `utilisateur.mot_de_passe_hash`, `pin_hash`, les codes comptoir, `parametre_notification.smtp_mot_de_passe`
et `smtp_identifiant`, la clé de scellement, la table `sync_pseudonyme` (inutile : chaque ligne chiffrée porte déjà son code).
Les textes libres restent filtrés comme aujourd'hui (script 127) : leur version d'origine **n'est pas** chiffrée ni envoyée **[DÉCISION]**.

## 3. Principe : chiffrement d'enveloppe et clé en deux parts

| Élément | Où il est | Protection |
|---|---|---|
| **Clé de données** de l'officine (KD, AES-256, 32 octets aléatoires, une par version) | Officine seulement : fichier `C:\ProgramData\PharmaOfficine\cle-donnees.dat` | DPAPI portée machine, fichier restreint, comme la clé de scellement ; **jamais en base**, jamais au journal |
| **Part éditeur** S1 (32 octets aléatoires) | Central : enveloppe chiffrée par la **clé publique de recouvrement de l'éditeur** | La clé privée correspondante n'est **jamais sur le central** (§ 5) |
| **Part titulaire** S2 = KD ⊕ S1 | Central : enveloppe chiffrée par une clé dérivée du **code de recouvrement du titulaire** | Le code n'est connu que du titulaire (papier au coffre) |

- KD = S1 ⊕ S2 (partage 2 parmi 2) : **une part seule ne révèle rien** de la clé, quelle que soit la puissance de calcul.
- Le central conserve les deux enveloppes, mais ne peut ouvrir ni l'une (pas de clé privée de l'éditeur) ni l'autre (pas de code du titulaire).
- **Vol de la base centrale ou de ses sauvegardes** : rien de lisible. **Central piraté** : rien de lisible non plus, tant que la clé privée
  de l'éditeur reste hors ligne. **Éditeur seul** ou **titulaire seul** : rien de lisible.
- L'officine, elle, a KD en clair (DPAPI) : ce n'est pas un recul, elle détient déjà les données en clair.

**Algorithmes** (fonctions intégrées à .NET, sans paquet externe, comme l'export `.pocle`) :
- données : **AES-256-GCM**, nonce aléatoire de 12 octets par bloc chiffré, étiquette de 16 octets ;
- part titulaire : code → **PBKDF2-SHA256** (600 000 tours, sel aléatoire de 16 octets) → clé AES-256-GCM qui chiffre S2 ;
- part éditeur : **RSA-OAEP-SHA256, clé de 3072 bits** (ou ECDH P-256 + HKDF + AES-GCM) **[À VÉRIFIER au lot C1 : choix final selon le coffre de clés retenu]** ;
- chaque enveloppe et chaque bloc chiffré commence par un octet de **version de format**, pour pouvoir changer d'algorithme plus tard.

## 4. Chiffrement à l'envoi

- **Où** : à la **constitution des lots** dans l'API locale, à l'endroit où les colonnes sont aujourd'hui retirées (`colonnes_retirees`)
  **[À VÉRIFIER au lot C3 : point exact dans le code]**. Jamais dans un déclencheur PostgreSQL : la clé n'entre jamais dans la base.
- **Format** : par ligne, un seul bloc chiffré contenant les colonnes retirées de la ligne (objet JSON `{colonne: valeur}`), et non une
  valeur par colonne : moins de place, un seul nonce, un ensemble cohérent.
- **Données associées authentifiées** (AAD) : `officine_id`, nom de la table, clé de la ligne (code), version de clé, version de format.
  Un bloc copié vers une autre ligne, une autre table ou une autre officine est **refusé** au déchiffrement.
- **Contrat** : champ facultatif `chiffre` sur chaque ligne du lot ; nouvelle version du contrat (`sync-v2`) **[À VÉRIFIER : numérotation actuelle]**.
  Une officine en retard d'une version ([conception-synchronisation.md](conception-synchronisation.md) § 0) envoie sans bloc chiffré : accepté, la ligne reste seulement sous code.
- **Lignes déjà au central** : elles reçoivent leur bloc par un **chargement complet** (lot S6, « Recharger toutes les données »), sans
  nouveau mécanisme.
- **Coût** : chiffrement en mémoire, seulement à la constitution des lots, jamais sur le chemin d'une vente. Volume supplémentaire
  **[À VÉRIFIER : à mesurer sur une vraie base]**.

## 5. Garde des secrets

**Code de recouvrement du titulaire** (décision DC3) :
- **généré par l'application** (128 bits aléatoires, affiché en 8 groupes de 4 caractères sans lettres ambiguës, avec contrôle de
  frappe), **imprimé une seule fois**, à ranger au coffre ; plutôt qu'un mot de passe choisi, souvent trop faible ;
- jamais en base, jamais au journal, jamais envoyé : seule l'enveloppe S2 part ;
- écran réservé au titulaire (`ExigerTitulaire`), même règle que l'export de la clé de scellement ;
- **changement de code** : l'officine (qui a KD) refait l'enveloppe S2 et l'envoie ; le central **efface** l'ancienne enveloppe
  (seule suppression permise dans ces tables, journalisée), sinon l'ancien code ouvrirait encore.

**Clé privée de recouvrement de l'éditeur** (décision DC4) :
- créée **hors ligne** sur un poste dédié, jamais connecté au central ni à Internet ; ou dans le coffre de clés matériel de l'hébergeur
  **[À VÉRIFIER : offre de l'hébergeur]** ;
- elle-même chiffrée et **scindée entre deux personnes de l'éditeur** (aucune ne peut l'utiliser seule) ; **deux copies** en deux lieux,
  car sa perte rendrait **toutes** les officines irrécupérables ;
- la **clé publique** est distribuée dans un certificat signé par l'autorité du central ; l'officine vérifie la signature et l'empreinte
  avant de chiffrer S1, et l'empreinte est affichée au titulaire.

## 6. Ouverture à deux

**Principe** : la clé n'est **jamais reconstituée au central**. Elle l'est **dans un serveur d'officine** (officine neuve après sinistre,
ou serveur de l'officine pour un contrôle), et les données en clair n'existent que là.

| Étape | Qui | Ce qui se passe |
|---|---|---|
| 1. Demande | Titulaire (ou éditeur, sur demande écrite du titulaire) | Demande au central : officine, motif (§ 7), référence (n° de dossier, de réquisition) ; ligne au **journal des ouvertures** (ajout seul, chaîné) ; **alerte immédiate** au titulaire (SMS et courriel) |
| 2. Validation éditeur | Les deux personnes qui détiennent la clé privée, sur le poste hors ligne | Ouvrent S1 et la **rechiffrent pour le certificat du serveur d'officine destinataire** (sa clé publique) : S1 ne circule jamais en clair ; validation inscrite au journal |
| 3. Transfert | Central → serveur d'officine | Enveloppe S2, S1 rechiffrée, blocs chiffrés de l'officine |
| 4. Validation titulaire | Titulaire, devant le serveur d'officine | Saisit son code de recouvrement → S2 ; le serveur calcule KD = S1 ⊕ S2, la range en DPAPI, déchiffre localement ; résultat (nombre de lignes, empreinte) remonté au journal |
| 5. Clôture | Automatique | La S1 rechiffrée n'est utilisable que par ce serveur ; ligne de clôture au journal ; le titulaire peut ensuite **changer de code** (§ 5) |

- Une demande non validée par les deux parties dans les 30 jours (DC7) est close, journalisée, sans effet.
- Le journal des ouvertures est consultable par le titulaire (écrans S7), comme le journal des accès ([conception-synchronisation.md](conception-synchronisation.md) § 6).
- **Registre des psychotropes** : après une ouverture, le serveur d'officine recalcule les empreintes de `journal_registre_psychotrope`
  à partir des données déchiffrées et les compare à celles conservées au central (preuve de non-altération).

## 7. Cas d'ouverture (décision DC2)

1. **Reconstruction d'une officine** après perte des serveurs et des sauvegardes (lié à la fin du lot S6, restauration) ;
2. **Contrôle ou réquisition** d'une autorité, en présence du titulaire ; si le titulaire refuse ou ne peut pas être joint, la conduite
   à tenir par l'éditeur est une question juridique **[À VÉRIFIER]** — par construction, l'éditeur **ne peut pas** ouvrir seul ;
3. **Départ d'une officine** : remise d'un export complet au titulaire, si l'officine n'a plus ses données.

Aucune autre ouverture : en particulier, jamais pour le support technique ni pour une statistique.

## 8. Cas particuliers

| Cas | Traitement proposé |
|---|---|
| **2e serveur** | Même KD (même officine) : l'export `.pocle` de la clé de scellement est étendu à KD (même fichier, même mot de passe, même import `--importer-cle`) |
| **Sauvegarde locale de l'officine** | KD n'est pas dans la base : le fichier `.pocle` est sa seule copie hors serveur ; à défaut, KD se retrouve par une ouverture à deux |
| **Restauration d'une sauvegarde locale** | Sans effet sur KD (hors base) ; nouvelle époque comme prévu en S6 |
| **Code du titulaire perdu** | Officine en service : nouveau code, nouvelle enveloppe S2 (§ 5). Officine perdue **et** code perdu : identités **irrécupérables**, par construction (à dire clairement au titulaire à la remise du code) |
| **Titulaire indisponible** (décès, incapacité) ou **vente de l'officine** | DC5 : **suppléant** facultatif désigné par le titulaire (second code, seconde enveloppe S2) ; procédure légale en son absence **[À VÉRIFIER]** ; à la vente, nouvelle KD et nouveau code pour le repreneur |
| **Rotation de KD** | DC6 : sur incident et à chaque changement de titulaire ; nouvelle version ; les nouvelles lignes utilisent la nouvelle clé ; les anciennes sont rechiffrées par un chargement complet, puis les anciennes enveloppes effacées |
| **Effacement au départ** | **Effacement cryptographique** : supprimer les deux enveloppes rend tous les blocs illisibles, y compris dans les sauvegardes du central ; après les durées légales de conservation **[À VÉRIFIER : 6 ans fiscaux, registre]**, avec procès-verbal |
| **Officine en retard d'une version** | Envoie sans bloc chiffré ; aucun refus |

## 9. Schéma (aucun script écrit)

**Central** (script central à numéroter après `011`, réversible) :
- `donnee_chiffree` (`officine_id`, `nom_table`, `cle_ligne`, `cle_version`, `format`, `bloc bytea`, `recu_le`) : table **à part**, pour ne pas
  toucher aux tables miroir (« mêmes tables et colonnes que la base locale ») ; dernier état seulement pour les tables qui changent
  (`patient`, `vente`), **ajout seul** pour les journaux (`journal_registre_psychotrope`) ; RLS et `FORCE ROW LEVEL SECURITY` comme les autres tables ;
- `cle_enveloppe` (`officine_id`, `cle_version`, `partie` `editeur` / `titulaire` / `suppleant`, `enveloppe bytea`, `empreinte_cle_publique`, `cree_le`) ;
  seules suppressions permises : remplacement d'une enveloppe titulaire et effacement au départ, par une fonction dédiée journalisée ;
- `ouverture_demande` et son journal (ajout seul, chaîné) ; aucune colonne en clair.

**Officine** (script officine à numéroter après `143`, idempotent, scripts d'initialisation mis à jour dans la même modification) :
- `chiffrement_cle` (`version`, `empreinte`, `creee_le`, `etat`, `enveloppes_envoyees_le`) : **aucun matériel de clé** en base ;
- `sync_table` : liste des colonnes chiffrées par table (comme `colonnes_texte`).

## 10. Décisions du titulaire (2026-11-17, « ok recommandations »)

Les sept recommandations sont acceptées telles quelles.

| N° | Sujet | Décision |
|---|---|---|
| DC1 | Données chiffrées | Les colonnes du § 2 ; les familles nominatives non autorisées (`employes`, `banque`…) restent **retirées**, pas chiffrées |
| DC2 | Cas d'ouverture | Les trois du § 7, aucun autre |
| DC3 | Secret du titulaire | Code de recouvrement **généré et imprimé** plutôt qu'un mot de passe choisi |
| DC4 | Garde de la clé privée de l'éditeur | Hors ligne, scindée entre deux personnes, deux copies en deux lieux |
| DC5 | Titulaire indisponible | Un **suppléant** désigné par le titulaire (seconde enveloppe), facultatif |
| DC6 | Rotation de KD | Sur incident et à chaque changement de titulaire |
| DC7 | Délai d'une demande d'ouverture | 30 jours, puis close sans effet |

**[À VÉRIFIER] réglementaires** : une copie chiffrée dont l'éditeur détient une part reste vraisemblablement une **donnée personnelle de santé**
au sens de la loi 18-07 (à faire confirmer) : déclaration ANPDP, rôle de l'éditeur (sous-traitant), exigences d'hébergement des données
de santé ; obligations face à une réquisition ; durées de conservation et effacement.

## 11. Découpage en lots (dans le lot 9, rien n'est lancé)

| Lot | Contenu | Dépend de |
|---|---|---|
| C1 | Paire de clés de recouvrement de l'éditeur, outil hors ligne (création, ouverture, rechiffrement de S1), procédure de garde ; script central (tables du § 9) | « go chiffrement », DC4 |
| C2 | Officine : création de KD (DPAPI), extension de l'export `.pocle`, écran du code de recouvrement (titulaire), envoi des enveloppes ; script officine | C1, DC3, DC5 |
| C3 | Chiffrement à la constitution des lots, réception et contrôle au central, `sync-v2`, rattrapage par chargement complet | C2, DC1 |
| C4 | Ouverture à deux (demande, validations, journal, alertes, reconstitution dans un serveur d'officine), recalcul des empreintes du registre | C3, fin de S6 (restauration), DC2, DC7 |
| C5 | Changement de code, rotation de KD, effacement cryptographique au départ | C4, DC6 |

**Essais prévus** : tests unitaires (format, refus d'un bloc déplacé grâce aux données associées, parts S1 / S2, mauvais code) ; essai de bout
en bout sur copies jetables (central et officine) ; **preuve que la base centrale ne contient aucune donnée lisible** (recherche des valeurs
en clair connues dans un export complet du central) ; essai d'une reconstruction complète ; revue de sécurité obligatoire et test
d'intrusion avant mise en service.
