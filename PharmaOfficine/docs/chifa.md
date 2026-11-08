# Chifa et cadre réglementaire

## Position

Nous ne remplaçons pas Chifa_Officine. La CNAS fournit ce logiciel aux pharmacies conventionnées ; nous n’avons pas trouvé de procédure publique d’agrément permettant à un éditeur privé de le remplacer.

## Coexistence

- Le pharmacien sert l’assuré dans Chifa_Officine.
- Notre logiciel lit **uniquement les fichiers exportés** (factures signées) pour déstocker par lot, suivre les bordereaux et traiter les rejets.
- Aucun accès à la base interne de la CNAS.

Le connecteur est `ChifaFichiersConnecteur` (dossier configuré dans `appsettings.json`, clé `Chifa:DossierExport`). **Le format des fichiers n’est pas encore connu** : il faut un exemple anonymisé fourni par la pharmacie pilote, ou les spécifications de la CNAS. Le connecteur reste isolé derrière `IChifaConnecteur` pour évoluer sans toucher au reste.

## Démarche officielle à mener

- Créer la société (registre de commerce, NIF).
- Écrire à la CNAS (direction des systèmes d’information) pour obtenir les formats d’échange ou un cadre de partenariat.
- S’appuyer sur des pharmacies pilotes et sur le syndicat des pharmaciens d’officine.

## Autres obligations

- **Data Matrix** : le ministère prépare la sérialisation des boîtes. Le logiciel lit déjà le format GS1. Calendrier d’obligation à suivre ; prévoir des scanners 2D.
- **Psychotropes** : registre numérique non modifiable, délivrance réservée aux rôles autorisés, patient obligatoire.
- **Données de santé** : données conservées dans la pharmacie, accès par rôle, journal d’audit ; respect de la loi 18-07 à intégrer au contrat avec chaque pharmacie.
