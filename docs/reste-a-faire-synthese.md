# Reste à faire : synthèse

Point d'avancement du 2026-11-18 (fin de journée, après le commit `f404e6f` ; défauts d'écran à 1366 × 768 corrigés le soir, non commités). Vue d'ensemble courte ; le détail de chaque point est dans
[a-faire.md](a-faire.md), [fonctionnalites.md](fonctionnalites.md) (inventaire complet) et dans les documents cités. À mettre à jour à chaque
point d'avancement.

## 1. En cours

Rien. Lots clos et commités le 2026-11-18 (branche `develop`, non poussée) :
- `200439b` : GED, alerte « aucune sauvegarde sur disque externe depuis N jours » (script 148), rotation de deux disques ; inventaire complet des fonctionnalités.
- `71ca1a6` : documents d'analyse sans nom d'autre logiciel ; feuille de route alignée.
- `968fce9` : synchronisation S6 (restauration, bascule, départ d'une officine ; scripts officine 149, central 020 et 021 ; essai de bout en bout S6.4 ; corrections après revue) ; analyse S6.5 ; domaine Patients (fichier des patients, patients en attente) ; menu vertical en teinte « grès ».
- `f404e6f` : suggestions pendant la frappe dans tout le logiciel (composant commun, liste blanche, script 150) ; message de nomenclature corrigé.

## 2. Fait, à commiter

- Défauts d'écran à 1366 × 768 du 2026-11-18 (les 4 du § 4), corrigés et mesurés le soir même (0 px, clair et sombre, 1366 × 768
  et 1536 × 890 ; architecture 243/243) : Caisse (repli automatique du bloc client, en-tête sur une rangée, paiement resserré),
  propositions de la caisse sur toute la largeur, menu du contrôle interne, fiche d'un emplacement. Détail : charte-graphique.md
  (« Défauts à 1 366 × 768 corrigés »), regles-metier.md « Caisse en trois parties ». Fichiers : `Caisse.razor`,
  `NavControleInterne.razor`, `AdminEmplacements.razor`, `app.css`, `poste.js`, docs.
- **Nouveau logo Tracodex** (gélule), adopté par le titulaire le 2026-11-18 : en-tête, connexion, icône de la fenêtre et du
  `.exe` (tailles 16 à 256), application mobile, titre des PDF ; sources et règles dans `docs/branding/` (rattaché au `.sln` et
  au `.slnx`) ; charte-graphique.md § Logo. Non indexé (les corrections d'écran ci-dessus sont indexées à part).
- **Vert de l'interface = vert du logo** (2026-11-18) : variables de `app.css` (clair et sombre), application mobile, PDF, e-mails,
  écran du client ; contrastes vérifiés (≥ 5 pour le texte), captures avant et après ; charte-graphique.md « Fonds ». Non indexé.
- **Thème sombre sur l'« encre » du logo** (2026-11-18) : fonds, bordures et textes bleu-vert au lieu de gris-vert (poste et
  mobile), contrastes égaux ou meilleurs, captures ; charte-graphique.md « Fonds ». Non indexé.
- **Corrections du 2026-11-18 au soir** (non indexées) : champ patient ou médecin de la Caisse qui sautait pendant la frappe
  (listes de propositions écartées du calcul des cadres, `poste.js` ; « Nouveau médecin » à droite du libellé) ; bandeau « produit
  redevenu disponible » sans « 0 patient » ; Pertes et vigilance › Par employé à 0 px à 1366 × 768 (`.tableau-serre`) ; nom
  « PharmaOfficine » puis, sur choix du titulaire, **« Tracodex Officine »** (logiciel local) et **« Tracodex Réseau »** (serveur central) partout (sauf dossier `Documents\Pharma Officine`, identifiants de pilote de la reprise et scripts SQL déjà
  exécutés). Onglet Jour par jour aussi à 0 px. Rendu de monnaie de la Caisse en bronze (pendant la saisie et après
  l'encaissement).

## 3. Ensuite

- [ ] **Push** des commits locaux par le titulaire (branche `develop` : 44 commits d'avance sur `origin/develop` constatés le 2026-11-18 au soir).
- [ ] **Port 5432 du poste de développement** (par le titulaire, PowerShell administrateur) : `listen_addresses = 'localhost'` dans
      `C:\Program Files\PostgreSQL\18\data\postgresql.conf` (copie `postgresql.conf.avant-localhost` gardée), puis `Restart-Service postgresql-x64-18`,
      Visual Studio arrêté avant. Constat du 2026-11-18 : écoute sur toutes les adresses, `pg_hba.conf` limité à 127.0.0.1 et ::1, aucune règle de pare-feu.
- [ ] **S6.5, intégrité après restauration** (« go S6.5 » du 2026-11-18) : **central fait** (S6.5.1 à S6.5.3 : scripts 022 et 023,
      route `POST /api/v1/epoque`, console `--restaurations-integrite`, export de départ ; passés sur `pharmacie_cloud_dev`, non commités).
      **Reste** : S6.5.4 officine (script 151, envoi de la déclaration, compteur de tickets, prise d'acte, écran), S6.5.5 essai de bout en
      bout, S6.5.6 documentation : [conception-synchronisation.md](conception-synchronisation.md) § 31.6.

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
