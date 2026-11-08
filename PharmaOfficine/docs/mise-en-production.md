# Mise en production et reprise de données

## Installation dans une pharmacie

1. PC serveur : PostgreSQL, base `pharmacie` (scripts `001` à `008`, sans les données de démo), API installée comme service Windows.
2. Clé JWT propre à la pharmacie, chaîne de connexion, dossier d’export Chifa.
3. Onduleur obligatoire sur le PC serveur (coupures de courant).
4. Postes : poste client avec la variable d’environnement `PHARMA_API=http://<pc-serveur>:5080/`.
5. Sauvegarde : `scripts/sauvegarde_pharmacie.ps1` planifié chaque soir, deux disques externes en rotation, test de restauration chaque mois.

## Reprise des données de l’ancien logiciel

L’ancienne base n’est jamais modifiée : on travaille sur une copie, ce qui rend le retour arrière simple.

1. **Transit** : exports bruts chargés dans le schéma `import` (un connecteur `IConnecteurSource` par logiciel concurrent).
2. **Rapprochement** : `import.rapprocher_produits` (code-barres, numéro d’enregistrement, puis nom approché à confirmer).
3. **Contrôles** : `import.controler` ; toute anomalie bloquante empêche le chargement.
4. **Chargement** : `import.charger`, en une transaction. Lot sans date de péremption = « date à vérifier ».
5. **Vérification** : `import.controle_apres_chargement` compare les totaux ; rapport signé par le pharmacien.

## Calendrier type

| Jour | Étape |
| --- | --- |
| J-15 | Répétition générale complète sur une copie |
| J-7 | Formation, paramétrage (droits, seuils, imprimantes) |
| J-1 au soir | Clôture de caisse, sauvegarde, export, chargement, contrôle des totaux |
| J | Ouverture sur le nouveau logiciel, accompagnement sur place |
| J+1 à J+30 | Ancien logiciel en lecture seule ; étiquetage progressif du stock |

Critère de retour arrière, fixé à l’avance : si les totaux ne concordent pas la veille au soir, on rouvre l’ancien logiciel et on reporte.
