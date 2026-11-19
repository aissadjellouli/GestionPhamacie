# Reste à faire : synthèse

Point d'avancement du 2026-11-19 (après le commit `48d8818` : lots S6.5.4, S6.5.5 et S6.5.6 commités en `610939a`, `c80fd28` et `48d8818`). Vue d'ensemble courte ; le détail de chaque point est dans
[a-faire.md](a-faire.md), [fonctionnalites.md](fonctionnalites.md) (inventaire complet) et dans les documents cités. À mettre à jour à chaque
point d'avancement.

## 1. En cours

Rien. Commits locaux (branche `develop`, non poussée) : jusqu'à `f404e6f` le 2026-11-18, puis `a87035b` (2026-11-19) : écrans à
1366 × 768 (Caisse, contrôle interne, emplacements, Pertes et vigilance), logo et couleurs Tracodex, thème sombre « encre », nom
Tracodex Officine / Tracodex Réseau, « Pertes et vigilance », rendu en bronze, S6.5 côté central (scripts 022, 023), route `/api/healthcheck`.
Puis `610939a` (S6.5.4), `c80fd28` (S6.5.5) et `48d8818` (S6.5.6 et n° de tickets après une restauration, choix « A + B » :
scripts officine `152` et central `025`, conception § 31.10).

## 2. Fait, à commiter

- **Conception § 31.10, essai rejoué** (2026-11-19) : `docs/conception-synchronisation.md`, essai neuf de l'attente du serveur
  central au démarrage (vente refusée pendant l'attente, puis acceptée ; nouvel essai d'envoi au bout de 2 min au lieu de 30) ; RAF.

## 3. Ensuite

- [ ] **Push** des commits locaux par le titulaire (branche `develop` : 48 commits d'avance sur `origin/develop` constatés le 2026-11-19).
- [ ] **Port 5432 du poste de développement** (par le titulaire, PowerShell administrateur) : `listen_addresses = 'localhost'` dans
      `C:\Program Files\PostgreSQL\18\data\postgresql.conf` (copie `postgresql.conf.avant-localhost` gardée), puis `Restart-Service postgresql-x64-18`,
      Visual Studio arrêté avant. Constat du 2026-11-18 : écoute sur toutes les adresses, `pg_hba.conf` limité à 127.0.0.1 et ::1, aucune règle de pare-feu.
- [x] **S6.5, intégrité après restauration** : **terminé** le 2026-11-19 (S6.5.0 à S6.5.6 ; essai de bout en bout réussi). Restent
      hors lot : avis du comptable (§ 6) ; n° repris avant la détection : traité le 2026-11-19 (§ 31.10). Synthèse :
      [conception-synchronisation.md](conception-synchronisation.md) § 31.9.

## 4. Défauts d'écran constatés le 2026-11-18

- [x] Caisse en tiers payant, avec produit et bandeau de lot, à 1366 × 768 : défilait de 164 px → 0 (bloc client replié de lui-même).
- [x] Liste de recherche de la caisse à 1366 × 768 : barre horizontale → liste sur toute la largeur de la ligne.
- [x] Surveillance à 1366 × 768 : menu de gauche 37 px → 0 (tiroir et produits sensibles dans « Pertes et vigilance »).
- [x] Emplacements à 1366 × 768 : « Zone » sous la liste des rayons → les cinq champs sur une ligne.
- [x] Contrôle interne › Pertes et vigilance › Par employé à 1366 × 768 : 33 px et barre horizontale → 0 (corrigé le 2026-11-18).
- [ ] **Données d'essai dans `pharmacie_test`** (2026-11-18, soir) : ventes en attente de 23:00 (1 produit, 2 412,50 DA) et 23:05
      (1 produit, 1 930,00 DA), créées par mes essais ; vente en attente de 22:12 (3 produits, 1 417,50 DA, titulaire),
      créée par mon essai en quittant la Caisse panier rempli ; une autre de 22:13 (2 produits, 782,50 DA, motif « Autre : test »),
      pas de mon fait (poste de Visual Studio encore ouvert sur mon API ?). Abandon sur accord seulement (l'abandon crée une anomalie).
- Rappel de la règle (précision du titulaire, 2026-11-18) : seuls les **écrans principaux** ne défilent jamais ; un formulaire ouvert peut faire défiler la page.

## 5. Décisions attendues du titulaire

- [ ] **Nouvelle époque de synchronisation** : la ressaisie du mot de passe du titulaire suffit-elle, ou faut-il un code reçu par SMS ou e-mail (à créer pour le titulaire) ?
- [ ] **Archive du central** (`ligne_remplacee`) : exclure les tables recalculées au démarrage (`alerte`, `alerte_surveillance` ; 111 lignes archivées à l'essai) ?
- [ ] **Rejeu des scripts du central** : protéger aussi `cloud_descendants` (010 → 011) et `cloud_creer_ordre` (013 → 018) ?
- [ ] **Départ d'une officine** : effacer aussi la fiche, les journaux et les éléments techniques ? étendre `cloud_effacer_famille` à `ligne_remplacee` ? mode de remise de l'archive au titulaire ?
- [ ] **Patients en attente** : visible dans le menu pour un employé qui n'a que `caisse.vendre` (avant : seulement en fenêtre depuis la Caisse) ?
- [ ] **Suggestions** : libellés de trésorerie saisis à la main proposés (un libellé peut contenir un nom de client) ; libellés de versement d'organisme exclus ; plus de 10 rayons (seuls les 10 premiers proposés sans saisie) ; Seuils par produit à 1366 × 768 (environ 2,5 lignes visibles).
- [ ] **Inventaire** ([fonctionnalites.md](fonctionnalites.md)) : parapharmacie distinguée ? sorties de stock et multi-grossistes à confirmer.
- [ ] **Compte `postgres`** : fichier `pgpass.conf` (hors dépôt) pour essayer le script 111b et la méthode `SET ROLE` sur une copie.
- [ ] **Écrans de vente** : seuils d'alerte des psychotropes, liste des DCI d'antibiotiques, format de l'export pour les impôts et taux de TVA.
- [ ] **Majorations Chifa** : quels types en plus de « Garde ».
- [ ] **Démo** : page de démonstration, démo interactive ou démonstration cloud.
- [ ] **Bases** : bases de recette gardées (recette 1 à 4, `…_avant_restauration`) ; bases jetables restantes (`pharmacie_cloud_jetable_m11_1d7bbebb`,
      `pharmacie_jetable_m4_*`, `pharmacie_jetable_rejeu_*`) : suppression sur accord.
- [ ] Autres points ouverts : voir [a-faire.md](a-faire.md), « Décisions en attente du titulaire » et « Mise en production : à décider ».

## 6. Démarches du titulaire

- [ ] Hébergeur en Algérie agréé ARPCE et nom de domaine `.dz` (préalable au serveur central réel).
- [ ] Déclaration à l'ANPDP ; texte du consentement SMS ; test d'intrusion avant la mise en service du central.
- [ ] Lettre à la CNAS (format du QR Chifa, export, volets).
- [ ] Marque Tracodex : recherche d'antériorité à l'INAPI (classes 9, 42 et 5) ; avis sur le croissant du logo (emblème
      national, ordonnance 03-06) avant le dépôt ([branding/logo.md](branding/logo.md)).
- [ ] Wilayas 59 à 69 à vérifier au Journal officiel.
- [ ] Exports de l'ancien logiciel, catalogues des grossistes, tableaux d'amortissement des banques, modèle de feuille CVM/MDN.
- [ ] Questions au comptable : TVA des pertes et dons, don partiel, intérêts et marge des prêts (mourabaha) ; n° de tickets sautés après une restauration (S6.5-D6).
- [ ] Questions à un juriste (loi 18-07 et loi 25-11, avant la mise en service du central) : autorisation ou déclaration ANPDP, consentement ou information des patients, secret professionnel et hébergement chez l'éditeur, hébergement en Algérie obligatoire ou non, réponse à une autorité sans le titulaire et tiers de confiance (décision DC8) : [chiffrement-data.md](chiffrement-data.md) § 12.3 ; durée de conservation au central après le départ d'une officine (6 ans par défaut) ; obligations de conservation des ordonnances.

## 7. Essais sur le terrain

- [ ] HTTPS : script `certificat_https.ps1` en réel, autorité commune aux serveurs, postes et téléphones.
- [ ] Copie de la GED entre deux vrais serveurs (partage SMB hors domaine, droit du compte de tâche, durée de la première copie), lecture de la fréquence par authentification Windows (SSPI, nom du compte présenté par Windows).
- [ ] Matériel (modem GSM, SMTP, imprimantes, disque de sauvegarde, iPhone) ; douchette dans Achats › Commandes (Entrée sur un code scanné).
- [ ] Chifa (double saisie, suffixe du n° d'assuré), reprise de données réelle, étiquetage du stock.
- [ ] Synchronisation avec un vrai serveur central (restauration, bascule, départ en réel).

## 8. Plus tard (sur accord explicite du titulaire)


- [ ] **Synchronisation S7** : écrans du central, accès à distance du titulaire, multi-officines (après le choix de l'hébergeur).
- [ ] Découpage par officine des grandes tables du central.
- [ ] Script 101b (suppression de `vente.num_facture_chifa`) : à exécuter à la mise à jour de l'officine, conflits tranchés.
- [ ] Sauvegarde chiffrée de la GED hors de l'officine (après le lot 9 et l'hébergeur).
- [ ] **Lot 9, chiffrement** : uniquement sur « go chiffrement ». Option C (ouverture à deux) conçue, décisions DC1 à DC7 acceptées le 2026-11-17 : [chiffrement-data.md](chiffrement-data.md), lots C1 à C5.
