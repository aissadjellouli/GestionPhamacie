# Reste à faire : synthèse

Point d'avancement du 2026-11-17. Vue d'ensemble courte ; le détail de chaque point est dans [a-faire.md](a-faire.md) et dans les
documents cités. À mettre à jour à chaque point d'avancement.

## 1. En cours

- [ ] **Mutualisation des réglages, lot M8** : essai de bout en bout avec un vrai serveur central (13 scénarios), sur l'essai local
      `scripts/dev/essai_cloud_officine.ps1`. Premier passage partiel ; défaut D-M8-1 corrigé (script central 011) ; règle D-M8-2
      réalisée (script officine 142). Résultats : [conception-synchronisation.md](conception-synchronisation.md) § 16 bis.12.
- [ ] **Fréquence de la copie de la GED vers le 2e serveur** : réglable dans Administration › 2e serveur, 5 minutes par défaut
      (script officine 143, `scripts/second-serveur/copier-ged.ps1`).

## 2. Fait, à commiter

- [ ] Correctif central 011 (`ajouts_locaux` seulement pour motifs et remises) et son test de validation des éléments envoyés.
- [ ] Essai local sans mot de passe du titulaire (compte temporaire dans la base d'essai seulement).
- [ ] Copie de la GED vers le 2e serveur (`copier-ged.ps1`, `partager-ged.ps1`, procédures C, F3, F4).
- [ ] Script 142 : première réception d'un modèle proposé.
- [ ] Correctif du journal des actions du 2e serveur (réglages des serveurs, seuils de réplication, export de la clé de scellement :
      erreur 500, code `role.titulaire` hors du catalogue des droits) et son test.
- [ ] Documentation : guide d'essai ([essai-cloud-officine.md](essai-cloud-officine.md), sections 3 bis, 5 bis, 6.9, 7),
      [ged-protection-a-decider.md](ged-protection-a-decider.md), conception, règles métier, manuel de mise en production.

## 3. Ensuite

- [ ] **Mutualisation, lot M9** : documentation finale ; remettre en ordre [base-de-donnees.md](base-de-donnees.md) (lignes de scripts
      placées avant le titre, scripts 127 à 140 absents).
- [ ] **Push** des commits locaux par le titulaire (branche `develop`).

## 4. Décisions attendues du titulaire

- [ ] **Bases jetables orphelines** : `pharmacie_jetable_m4_2b2cf86a`, `pharmacie_jetable_rejeu_e51efbd8` (suppression sur accord).
- [ ] **Compte `postgres`** : fichier `pgpass.conf` (hors dépôt) pour essayer le script 111b et la méthode `SET ROLE` sur une copie.
- [ ] **GED** : protection des documents (GED consultable ou non au central, sauvegarde chiffrée hors de l'officine après le lot 9,
      rotation de deux disques pour une officine à un seul serveur, alerte « pas de sauvegarde ») :
      [ged-protection-a-decider.md](ged-protection-a-decider.md).
- [ ] **Écrans de vente** : seuils d'alerte des psychotropes, liste des DCI d'antibiotiques, format de l'export pour les impôts et taux de TVA.
- [ ] **Majorations Chifa** : quels types en plus de « Garde ».
- [ ] **Démo** : page de démonstration, démo interactive ou démonstration cloud.
- [ ] **Bases de recette** : gardées pour l'instant (recette 1 à 4, `…_avant_restauration`) ; clés de l'autorité d'essai de la
      session du 16/11 gardées.
- [ ] Autres points ouverts : voir [a-faire.md](a-faire.md), « Décisions en attente du titulaire » et « Mise en production : à décider ».

## 5. Démarches du titulaire

- [ ] Hébergeur en Algérie agréé ARPCE et nom de domaine `.dz` (préalable au serveur central réel).
- [ ] Déclaration à l'ANPDP ; texte du consentement SMS ; test d'intrusion avant la mise en service du central.
- [ ] Lettre à la CNAS (format du QR Chifa, export, volets).
- [ ] Wilayas 59 à 69 à vérifier au Journal officiel.
- [ ] Exports de l'ancien logiciel, catalogues des grossistes, tableaux d'amortissement des banques, modèle de feuille CVM/MDN.
- [ ] Questions au comptable : TVA des pertes et dons, don partiel, intérêts et marge des prêts (mourabaha).

## 6. Essais sur le terrain

- [ ] HTTPS : script `certificat_https.ps1` en réel, autorité commune aux serveurs, postes et téléphones.
- [ ] Copie de la GED entre deux vrais serveurs (partage SMB hors domaine, droit du compte de tâche, durée de la première copie).
- [ ] Matériel (modem GSM, SMTP, imprimantes, disque de sauvegarde, iPhone), SQL Server jamais essayé.
- [ ] Chifa (double saisie, suffixe du n° d'assuré), reprise de données réelle, étiquetage du stock.

## 7. Plus tard (sur accord explicite du titulaire)

- [ ] **Synchronisation** : S4 (contrôle d'intégrité au central), S5 (prix, commandes, employés envoyés par le central),
      fin de S6 (restauration de la base d'une officine, bascule, départ), S7 (écrans du central, multi-officines).
- [ ] Découpage par officine des grandes tables du central ; multi-officines après le choix de l'hébergeur.
- [ ] Script 101b (suppression de `vente.num_facture_chifa`) : à exécuter à la mise à jour de l'officine, conflits tranchés.
- [ ] Sauvegarde chiffrée de la GED hors de l'officine (après le lot 9 et l'hébergeur).
- [ ] **Lot 9, chiffrement** : uniquement sur « go chiffrement ».
