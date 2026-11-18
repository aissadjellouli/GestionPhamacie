# Reste à faire : synthèse

Point d'avancement du 2026-11-18 (après la protection de la GED). Vue d'ensemble courte ; le détail de chaque point est dans [a-faire.md](a-faire.md) et dans les
documents cités. À mettre à jour à chaque point d'avancement.

## 1. En cours

Rien. Derniers lots clos le 2026-11-17 : mutualisation des réglages M8 (13 scénarios réussis) et M9 (documentation finale,
sans le chiffrement) ; copie de la GED vers le 2e serveur et sa fréquence (script 143) ; correctif du journal des actions du 2e serveur.

## 2. Fait, à commiter

- **GED, protection des sauvegardes** (2026-11-18, « go ged ») : alerte au titulaire si aucune sauvegarde réussie sur le disque externe depuis
  7 jours (réglable de 1 à 90, script 148, Administration › Sauvegardes) ; rotation de deux disques obligatoire sans 2e serveur
  (`mise-en-production.md`, étape 11.2 bis) ; miroir du central `database/cloud/002` régénéré (il manquait aussi les tables du script 147) ; le script planifié
  `sauvegarde_pharmacie.ps1` inscrit ses passages au journal des sauvegardes ; `docs/fonctionnalites.md` devient l'inventaire complet des fonctionnalités.

## 3. Ensuite

- [ ] **Push** des commits locaux par le titulaire (branche `develop`).

## 4. Décisions attendues du titulaire

- [ ] **Compte `postgres`** : fichier `pgpass.conf` (hors dépôt) pour essayer le script 111b et la méthode `SET ROLE` sur une copie.
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
- [ ] Questions à un juriste (loi 18-07 et loi 25-11, avant la mise en service du central) : autorisation ou déclaration ANPDP, consentement ou information des patients, secret professionnel et hébergement chez l'éditeur, hébergement en Algérie obligatoire ou non, réponse à une autorité sans le titulaire et tiers de confiance (décision DC8) : [chiffrement-data.md](chiffrement-data.md) § 12.3.

## 6. Essais sur le terrain

- [ ] HTTPS : script `certificat_https.ps1` en réel, autorité commune aux serveurs, postes et téléphones.
- [ ] Copie de la GED entre deux vrais serveurs (partage SMB hors domaine, droit du compte de tâche, durée de la première copie), lecture de la fréquence par authentification Windows (SSPI, nom du compte présenté par Windows).
- [ ] Matériel (modem GSM, SMTP, imprimantes, disque de sauvegarde, iPhone).
- [ ] Chifa (double saisie, suffixe du n° d'assuré), reprise de données réelle, étiquetage du stock.

## 7. Plus tard (sur accord explicite du titulaire)

- [ ] **Synchronisation** : S5.1, S5.5, S5.2, S5.3 (nomenclature) et S5.4 (commandes en brouillon) réalisés (conception § 24 à § 27), référentiels antibiotiques suivis, organismes et centres payeurs, prestations, indications et DCI (§ 28) ; deux tables liées aux produits reclassées (§ 29) ; restent
      fin de S6 (restauration de la base d'une officine, bascule, départ), S7 (écrans du central, multi-officines).
- [ ] Découpage par officine des grandes tables du central ; multi-officines après le choix de l'hébergeur.
- [ ] Script 101b (suppression de `vente.num_facture_chifa`) : à exécuter à la mise à jour de l'officine, conflits tranchés.
- [ ] Sauvegarde chiffrée de la GED hors de l'officine (après le lot 9 et l'hébergeur).
- [ ] **Lot 9, chiffrement** : uniquement sur « go chiffrement ». Option C (ouverture à deux) conçue, décisions DC1 à DC7 acceptées le 2026-11-17 : [chiffrement-data.md](chiffrement-data.md), lots C1 à C5.
