# Chifa et cadre réglementaire

## Position

Nous ne remplaçons pas Chifa_Officine. La CNAS fournit ce logiciel aux pharmacies conventionnées ; nous n’avons pas trouvé de procédure publique d’agrément permettant à un éditeur privé de le remplacer.

## Coexistence

- Le pharmacien sert l’assuré dans Chifa_Officine.
- Notre logiciel lit **uniquement les fichiers exportés** (factures signées) pour déstocker par lot, suivre les bordereaux et traiter les rejets.
- Aucun accès à la base interne de la CNAS.

Le connecteur est `ChifaFichiersConnecteur` (dossier configuré dans `appsettings.json`, clé `Chifa:DossierExport`). **Le format des fichiers n’est pas encore connu** : il faut un exemple anonymisé fourni par la pharmacie pilote, ou les spécifications de la CNAS. Le connecteur reste isolé derrière `IChifaConnecteur` pour évoluer sans toucher au reste.

## Volet Chifa et son QR code (constats du 11/11/2026)

Relevé sur de vrais volets (dossier `docs/EXEMPLE/aid`) : n° d'assuré (12 chiffres et suffixe « /NN »), n° de bordereau, n° de facture, organisme, une ligne par produit (quantité, montant), nombre de vignettes, montant de l'ordonnance, montant officine, vignettes collées et un QR code. Les montants sont **au centime** ; la part officine vaut 80 %, 100 % ou un taux ramené au tarif de référence.

Le QR d'un volet a été lu : `06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;` = n° de facture ; date ; n° d'assuré ; suffixe « /80 » ; deux champs de sens inconnu ; une seconde date ; puis « code produit, quantité, prix unitaire ». Le n° de bordereau et le montant officine n'y figurent pas.

**Usage dans le logiciel (script 060)** : scanné à la caisse, ce QR reprend la vente en tiers payant CNAS (assuré retrouvé, produits ajoutés avec leurs quantités, écarts de prix signalés ; depuis le script 086, écart sous le seuil aligné d'office sur le prix Chifa, au-delà choix d'un employé qui peut modifier les prix ou le plus élevé d'office, part de l'organisme toujours calculée sur le prix Chifa : voir `docs/regles-metier.md`) ; un code produit Chifa inconnu est associé une fois à un produit du catalogue, puis reconnu ; le volet (n° de facture, date, QR brut) est gardé sur la vente et ne peut être repris qu'une fois. **Format à confirmer avec l'éditeur de Chifa** : voir `docs/a-faire.md`.

**Organisme du volet (règle du titulaire, 15/11/2026)** : CNAS et CASNOS utilisent la même carte Chifa et CHIFA-Officine ; hypothèse retenue : même format de QR, qui ne contient pas l'organisme. Au scan, l'organisme proposé est celui de la fiche du patient retrouvé s'il s'agit de CNAS ou CASNOS, sinon CNAS (patient nouveau, sans organisme ou autre organisme). Le vendeur le change en un clic (CNAS / CASNOS) dans le bandeau du volet avant d'encaisser ; il est enregistré sur la vente. Le serveur refuse tout autre organisme pour une vente reprise d'un volet. Écart de prix Chifa et part de l'organisme : règles identiques pour les deux. Le QR complet reste enregistré sur la vente pour analyse ultérieure.

## Champs du QR à identifier (à vérifier)

Rien ci-dessous n'est confirmé.

- **Champ « 24 » (5e champ)** : piste, le code du centre payeur ou de l'agence CNAS. Les logiciels d'officine demandent le centre payeur (CNAS ou CASNOS) avant de facturer (manuel CTI Pharm : https://ctib.dz/adm/gfichiers/upload/files/MesDoc/Manuel_cti_pharm.pdf) ; chaque agence a son centre payeur qui reçoit les bordereaux (CNAS Batna : https://sorpbatna.wordpress.com/wp-content/uploads/2017/11/batna_chifa.pdf). Mais le centre payeur fait 5 chiffres dans les déclarations employeurs, ce qui ne correspond pas à « 24 ».
- **Champ « 2 » (6e champ)** : pistes, le code de la caisse (CNAS / CASNOS), un type de prestation ou une version de format.
- **Seconde date (7e champ, ex. 09/11/2026)** : hypothèse retenue le 15/11/2026, **date de l'ordonnance** (antérieure à la date de la facture) ; reprise d'office comme date de l'ordonnance de la vente. **À confirmer.**
  *Hypothèse fragilisée (modèle Wasfa, 2026-11-15, D-1)* : cette date pourrait aussi être la **date des soins**. Elle n'est **pas** rangée d'office en date des soins (`vente.chifa_date_soins`, script 100) : la date des soins se saisit au volet (Finances › Chifa › « Infos Chifa ») ou au rattachement ; au rattachement, la 2e date du QR est seulement proposée (« Reprendre la 2e date du QR »), marquée « à vérifier sur le volet ». À confirmer sur un volet réel avant toute reprise automatique.
- **Prix unitaire d'une ligne et SHP** : le QR ne donne qu'un prix par boîte (aucun champ SHP séparé) ; 3 × 156,48 = 469,44 = « Montant Ord » du volet. Sources non officielles : le PPA s'entend SHP inclus, SHP de 2,50 DA (classe I), 1,50 DA (classe II), 0 hors liste. Décision du titulaire (15/11/2026) : on garde la comparaison avec notre prix SHP compris ; l'écart possible (≤ 2,50 DA par boîte) reste sous le seuil d'écart Chifa (5 DA) et est aligné d'office, donc sans effet. **À confirmer** sur une vignette réelle (PPA + SHP = prix du volet ?) ou auprès de la CNAS.
- **Suffixe du n° d'assuré (« /00 », « /80 »…)** : sens non confirmé (assuré / ayant droit, ou clé de contrôle). Ne rien en déduire ; comparer les cartes Chifa d'un assuré et d'un membre de sa famille. Depuis le 15/11/2026 (script 102, modèle Wasfa qui affiche un « Rang » de 2 chiffres), le suffixe est affiché « rang NN » à côté du n° d'assuré (fonction `rang_assure`) : **sens du suffixe = rang de l'ayant droit, à confirmer sur cartes réelles** ; rien d'autre n'en est déduit.
- **Hypothèse « même QR pour CNAS et CASNOS »** : également à vérifier.
- **Méthode de vérification** : comparer un volet CASNOS, deux volets CNAS d'agences différentes et deux volets du même assuré ; regarder le centre payeur affiché par CHIFA-Officine ou Chifa Plus ; demander le format à la CNAS.
- **Suite** : quand l'organisme sera identifiable dans le QR, le choix de l'organisme pourra devenir automatique.

## Démarche officielle à mener

- Créer la société (registre de commerce, NIF).
- Écrire à la CNAS (direction des systèmes d’information) pour obtenir les formats d’échange ou un cadre de partenariat.
- S’appuyer sur des pharmacies pilotes et sur le syndicat des pharmaciens d’officine.

## Autres obligations

- **Data Matrix** : le ministère prépare la sérialisation des boîtes. Le logiciel lit déjà le format GS1. Calendrier d’obligation à suivre ; prévoir des scanners 2D.
- **Psychotropes** : registre numérique non modifiable, délivrance réservée aux rôles autorisés, patient obligatoire.
- **Données de santé** : données conservées dans la pharmacie, accès par rôle, journal d’audit ; respect de la loi 18-07 à intégrer au contrat avec chaque pharmacie.

## Circuit de travail souhaité par le titulaire (2026-11-15)

1. **La caisse d'abord** : le vendeur scanne les boîtes et l'ordonnance dans Pharma Officine (stock, lots, prix, patient).
2. **Transmission à CHIFA-Officine, si c'est possible** : seule la liste des produits (codes Chifa, quantités) est transmise à CHIFA-Officine, pour éviter la recherche manuelle des produits dans Chifa. **L'assuré est identifié par CHIFA-Officine, qui lit la carte Chifa** (précision du titulaire, 2026-11-15) : notre logiciel ne transmet pas l'identité de l'assuré ; il la récupère ensuite par le QR du volet. **Dépend du format d'échange de CHIFA-Officine, inconnu** : demandé à la CNAS (docs/taches/2026-11-15-recette-complete/demande-cnas.md). Aucune solution par simulation du clavier ou modification de CHIFA-Officine.
3. **Chifa édite la facture** : lecture de la carte, signature, impression du volet (rôle de la CNAS, inchangé).
4. **Le logiciel scanne le volet** : le QR est rapproché de la vente déjà faite à la caisse (produits, quantités, prix Chifa, écarts signalés), au lieu de créer une nouvelle vente. **Réalisé le 2026-11-15** (script 095) : la vente est encaissée avec la case « Facturée par Chifa » sans n° (elle attend son volet) ; au scan du QR, panier vide, les ventes Chifa en attente des 7 derniers jours (même assuré d'abord) sont proposées, comparées ligne à ligne (différences en rouge, assuré compris) ; « Rattacher » enregistre le volet sur la vente sans la modifier ; différences : droit « modifier les prix » et motif obligatoire, liste « Volets avec écart » (Finances › Chifa, avec « Ventes en attente de volet »). Le circuit « Chifa d'abord » reste disponible (aucune candidate, ou « Nouvelle vente depuis le volet »). Règles détaillées : `docs/regles-metier.md` (section « Volet Chifa repris par son QR code »). **Décisions du titulaire du 2026-11-15** (script 097) : une vente « Facturée par Chifa » sans n° de facture exige un motif (liste réglable « Vente Chifa sans n° de facture » : pas de carte Chifa, CHIFA-Officine indisponible, volet à éditer plus tard, autre), visible dans « Ventes en attente de volet » ; aucune expiration : au-delà du délai réglable (Administration › Caisse, 7 jours par défaut) la vente est « en retard » et reste rattachable à la main ; sans vente du même assuré, celles de la même carte (ayants droit) sont proposées d'abord ; un écart de part organisme est régularisé (créance ramenée à la part Chifa ; manque à la charge du patient ou perte pharmacie au choix d'un ayant droit ; trop-payé du patient rendu, dette d'abord), visible dans « Volets avec écart ».
