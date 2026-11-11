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

**Usage dans le logiciel (script 060)** : scanné à la caisse, ce QR reprend la vente en tiers payant CNAS (assuré retrouvé, produits ajoutés avec leurs quantités, écarts de prix signalés) ; un code produit Chifa inconnu est associé une fois à un produit du catalogue, puis reconnu ; le volet (n° de facture, date, QR brut) est gardé sur la vente et ne peut être repris qu'une fois. **Format à confirmer avec l'éditeur de Chifa** : voir `docs/a-faire.md`.

## Démarche officielle à mener

- Créer la société (registre de commerce, NIF).
- Écrire à la CNAS (direction des systèmes d’information) pour obtenir les formats d’échange ou un cadre de partenariat.
- S’appuyer sur des pharmacies pilotes et sur le syndicat des pharmaciens d’officine.

## Autres obligations

- **Data Matrix** : le ministère prépare la sérialisation des boîtes. Le logiciel lit déjà le format GS1. Calendrier d’obligation à suivre ; prévoir des scanners 2D.
- **Psychotropes** : registre numérique non modifiable, délivrance réservée aux rôles autorisés, patient obligatoire.
- **Données de santé** : données conservées dans la pharmacie, accès par rôle, journal d’audit ; respect de la loi 18-07 à intégrer au contrat avec chaque pharmacie.
