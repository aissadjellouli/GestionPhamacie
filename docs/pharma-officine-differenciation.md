# Logiciel de gestion d'officine (Algérie) — Axes de différenciation

> Document de cadrage produit à destination de Claude Code.
> Stack : .NET (API) / Angular (front) / PostgreSQL et SQL Server.
> Le développement est **déjà commencé** : ce document décrit la cible, pas un point de départ.
> Rédigé le 15/11/2026 à partir d'une veille marché et réglementaire (sources en fin de document).

---

## 0. Instructions pour Claude Code

1. **Ne rien coder tout de suite.** Commence par lire le code existant, puis produis `docs/analyse-ecarts.md` :
   - pour chaque module ci-dessous : *déjà fait / partiel / absent*, avec les fichiers concernés ;
   - les impacts sur le modèle de données existant (entités, migrations) ;
   - les risques techniques et les dépendances entre modules.
2. Propose ensuite un **plan de livraison par lots** respectant l'ordre de priorité de la section 3, et fais-le valider.
3. Chaque module est un chantier back + front + BDD : utilise `/equipe` pour les modules P1 et P2.
4. Les points marqués **[À VÉRIFIER]** sont des hypothèses réglementaires : ne pas les coder en dur, les rendre **paramétrables**, et les lister dans le rapport.
5. Les interfaces avec des systèmes nationaux qui n'existent pas encore (sérialisation, répertoire des ordonnances, plateforme officines) doivent passer par une **couche d'abstraction (port/adaptateur)** avec une implémentation factice, pour brancher l'API réelle plus tard sans toucher au métier.

---

## 1. Contexte marché

- Concurrents installés, modèle **licence à vie** : Chifa Plus (~53 000 DA, 2 500+ licences revendiquées, ~30 % des officines), XpertPharm (70 000 / 160 000 / 230 000 DA + 15–35 000 DA par poste), PharmaPro (45 000 DA), Profficine, Shabakett, Inabex, Cirta Officine.
- Fonctions de base déjà couvertes par tous : vente comptoir, Chifa (CNAS/CASNOS), conventions militaires, stock, péremptions, achats, crédits clients, codes-barres.
- **Conclusion : ne pas se battre sur le prix ni sur les fonctions de base.** Se différencier sur la conformité à venir, la protection du pharmacien, la gestion des ruptures et l'effet réseau.

### Positionnement cible

> « Le logiciel d'officine conforme 2027 — sérialisation, psychotropes, fiscal — qui vous aide à trouver les médicaments en rupture. »

---

## 2. Modules différenciants

### M1 — Sérialisation DataMatrix (« prêt dès le premier jour »)

**Pourquoi** : le ministère de l'Industrie pharmaceutique (MIPH) déploie la sérialisation. Les programmes d'importation 2027 doivent intégrer des produits porteurs d'un code 2D **DataMatrix**. Sont concernés les **médicaments remboursables** et ceux **soumis à prescription obligatoire**, soit l'essentiel du chiffre d'une officine. Une base nationale centralisée enregistre, vérifie et suit les numéros de série.

**Fonctions** :
- Lecture du DataMatrix (douchette 2D) à la **réception** et à la **dispensation** ; parsing GS1 : GTIN (01), n° de série (21), lot (10), date de péremption (17).
- Stock tenu **à la boîte** pour les produits sérialisés (en plus du stock par lot pour les autres).
- Vérification d'authenticité et **désactivation (décommissionnement) à la vente** via la base nationale → interface `ISerialisationGateway` (implémentation factice en attendant l'API officielle).
- Mode dégradé hors ligne : file d'attente des vérifications, resynchronisation au retour du réseau.
- Alertes : boîte déjà vendue, inconnue, rappelée ou expirée ; retours fournisseur traçables.
- Gestion des rappels de lots (pharmacovigilance) : retrouver en un clic les boîtes concernées en stock et vendues.

**Critères d'acceptation** :
- [ ] Un scan DataMatrix renseigne automatiquement produit, lot, péremption et n° de série.
- [ ] Une même boîte (GTIN + série) ne peut pas être vendue deux fois.
- [ ] La vente reste possible hors ligne, et les vérifications sont rejouées ensuite.
- [ ] Les normes de référence sont paramétrables : normes IANOR, basées sur GS1 **[À VÉRIFIER]**.

### M2 — Psychotropes et stupéfiants (« vous êtes couvert en cas de contrôle »)

**Pourquoi** : la loi 23-05 prévoit un **répertoire national numérique des ordonnances** pour les stupéfiants et psychotropes. Le module officine est annoncé prêt, l'intégration côté médecins est bloquée. Des pharmaciens ont été poursuivis pour des irrégularités de délivrance. La plateforme nationale des officines prévoit le recensement des quantités d'antibiotiques et de psychotropes consommées.

**Fonctions** :
- **Registre des psychotropes infalsifiable** : écritures en ajout seul, chaînage par hachage (chaque ligne contient le hash de la précédente), aucune suppression ni modification, corrections par écriture d'annulation.
- Saisie à la délivrance : patient (identité, n° de pièce), prescripteur, n° et date d'ordonnance, quantités, durée.
- Contrôles automatiques et alertes :
  - même patient servi plusieurs fois sur une période ;
  - ordonnance déjà utilisée (même n° / même prescripteur) ;
  - quantités anormales par rapport à la posologie ou à la durée ;
  - prescripteur inhabituel ou hors zone.
- Inventaire des psychotropes avec écarts justifiés.
- Exports prêts pour une **inspection** (PDF et Excel, par période, produit, patient, prescripteur).
- Connecteur `IRepertoireOrdonnancesGateway` (factice) pour le répertoire national dès son ouverture.
- Suivi des **antibiotiques** (quantités délivrées) pour le reporting national.

**Critères d'acceptation** :
- [ ] Toute tentative de modification d'une ligne du registre est impossible et tracée.
- [ ] Une rupture de la chaîne de hachage est détectée et signalée.
- [ ] Les alertes sont paramétrables (seuils, périodes) et chaque alerte forcée garde l'utilisateur et le motif.
- [ ] La liste des molécules et des classes est paramétrable **[À VÉRIFIER]** : nomenclature officielle.

### M3 — Ruptures de stock et réseau (« trouvez le médicament »)

**Pourquoi** : les ruptures sont le premier problème quotidien. On compte environ 600 grossistes-répartiteurs, avec qui les pharmaciens ont du mal à travailler. 82 % des produits vendus en officine sont fabriqués en Algérie, ce qui rend souvent possible une substitution locale.

**Fonctions** :
- **Commandes multi-grossistes** depuis un écran unique : catalogue et disponibilité par grossiste (import fichier, email, puis API si disponible), comparaison prix et délais, envoi du bon de commande.
- **Historique et alertes de rupture** : produits en rupture chez les grossistes suivis, notification au retour en stock, file des patients en attente d'un produit (rappel par SMS ou WhatsApp **[À VÉRIFIER]** : consentement et règles de communication).
- **Substitution** : proposition d'équivalents (même DCI, dosage, forme), en mettant en avant les génériques fabriqués localement ; liste blanche paramétrable par le pharmacien.
- **Réseau inter-officines** (abonnés uniquement) : vue agrégée et opt-in des disponibilités chez les confrères proches, demande d'échange ou de dépannage, suivi des échanges.
- *(Phase ultérieure)* application patient « où trouver mon médicament » qui oriente vers les officines du réseau. **[À VÉRIFIER]** auprès de l'Ordre des pharmaciens (CNOP) : publicité et concurrence entre officines.

**Critères d'acceptation** :
- [ ] Une officine ne partage ses disponibilités que si elle l'a explicitement activé (opt-in), et seulement sous forme agrégée (disponible / non), jamais ses quantités exactes ni ses prix.
- [ ] Une commande peut être répartie entre plusieurs grossistes.
- [ ] La substitution propose uniquement des produits de même DCI, dosage et forme.

### M4 — Suppression du frein au changement

**Pourquoi** : le vrai frein à l'achat est la peur de perdre ses données et de s'arrêter de travailler, pas le prix.

**Fonctions** :
- **Assistants d'import** depuis les logiciels concurrents (Chifa Plus, XpertPharm, PharmaPro, Profficine) : produits, stock, lots, fournisseurs, clients, crédits, historique Chifa. Formats source : exports Excel ou CSV, ou lecture de leur base **[À VÉRIFIER]** au cas par cas. Rapport d'import avec anomalies.
- **Mode hors ligne d'abord** : la vente au comptoir fonctionne sans internet (base locale) ; synchronisation cloud en tâche de fond ; gestion des conflits.
- **Multi-postes et multi-officines** : un titulaire suit plusieurs officines.
- **Application mobile du titulaire** (pilotage à distance) : chiffre d'affaires du jour, ventes en cours, alertes (ruptures, psychotropes, péremptions), activité par employé.

### M5 — Pilotage financier

- Marge par famille, produit et laboratoire ; évolution dans le temps.
- Pertes sur les produits **périmés** (valeur), produits proches de la péremption avec suggestion de retour ou d'échange.
- **Stock dormant** : produits sans vente depuis N jours, valeur immobilisée.
- **Rejets Chifa** : liste, motif, montant, statut de correction et de renvoi ; taux de rejet par motif.
- Suggestions de commande basées sur les ventes, la saisonnalité et les ruptures connues.

### M6 — Conformité fiscale (caisse certifiée)

**Pourquoi** : la loi de finances 2026 (article 51 bis du code de la TVA, en vigueur au **1ᵉʳ janvier 2027**) exige, pour les ventes sans facture enregistrées via un logiciel ou une caisse, un **engagement de l'éditeur** ou un certificat sur l'intégrité, la sécurité, la conservation et l'archivage des données. La conservation des données informatisées est de 6 ans.

**[À VÉRIFIER]** : application exacte aux officines. Les médicaments sont-ils hors TVA ? La parapharmacie est-elle soumise à la TVA ? Faire valider par un fiscaliste avant d'en faire un argument commercial.

**Fonctions (à concevoir dès maintenant, peu coûteuses si faites tôt)** :
- Journal des ventes **inaltérable** (même principe de chaînage par hachage que M2), numérotation continue des tickets, clôtures journalières et mensuelles signées.
- Archivage 6 ans, export d'audit au format demandé par la DGI **[À VÉRIFIER]** : le format exact dépend des textes d'application.
- Distinction des produits soumis ou non à la TVA, et récapitulatifs.

---

## 3. Priorités de livraison

| Priorité | Module | Raison |
|---|---|---|
| **P1** | M4 (import des concurrents + hors ligne) | Condition pour signer les premiers clients |
| **P1** | M3 (multi-grossistes + alertes ruptures + substitution) | Gain visible dès le premier jour |
| **P1** | M6 (journal inaltérable) | Peu cher si fait tôt ; très cher à rajouter ensuite |
| **P2** | M2 (registre psychotropes + alertes) | Argument fort « protection du pharmacien » |
| **P2** | M1 (DataMatrix : lecture, stock à la boîte, mode factice) | Échéance 2027, avance concurrentielle |
| **P3** | M5 (pilotage financier) | Différenciation sur la valeur |
| **P3** | M3 réseau inter-officines, puis application patient | Effet réseau, nécessite une base clients |

---

## 4. Principes techniques transverses

- **Couche d'abstraction pour chaque système externe** : `ISerialisationGateway`, `IRepertoireOrdonnancesGateway`, `IPlateformeOfficinesGateway`, `IGrossisteConnector`, `IChifaConnector` ; implémentations factices et tests de contrat.
- **Journaux inaltérables** (psychotropes, ventes) : table en ajout seul, colonnes `hash` et `hash_precedent`, vérification d'intégrité planifiée, aucun droit UPDATE/DELETE pour le compte applicatif.
- **Double compatibilité PostgreSQL / SQL Server** pour toutes les migrations.
- **Hors ligne** : base locale au poste de comptoir, synchronisation idempotente, horodatage UTC, identifiants GUID.
- **Paramétrage réglementaire** (listes de molécules, seuils, taux, formats d'export) en données, pas en code.
- **Données de santé** : chiffrement des données patients, journal des accès, minimisation des données collectées ; conformité à la loi 18-07 sur la protection des données personnelles **[À VÉRIFIER]**.
- **Interface** en arabe et en français, avec gestion de l'écriture de droite à gauche (RTL).

---

## 5. Commercialisation (pour information, hors périmètre technique)

- **Cibles prioritaires** : les nouvelles officines, sans logiciel à remplacer. Un décret en préparation prévoit de traiter via une plateforme numérique les dossiers des pharmaciens en liste d'attente, ce qui pourrait entraîner une vague d'ouvertures.
- **Partenaire naturel** : le SNAPO (syndicat des pharmaciens d'officine), qui milite pour l'ordonnance numérique et la traçabilité.
- **Offre** : reprise des données, installation et formation sur place offertes ; abonnement pour le cloud, le réseau et les mises à jour réglementaires (à arbitrer face aux licences à vie des concurrents).

---

## 6. Points à vérifier avant de figer les spécifications

1. Format et calendrier de l'API de la base nationale de sérialisation (MIPH) ; normes IANOR applicables.
2. Date d'ouverture et interface du répertoire national des ordonnances (loi 23-05, article 5 bis 8).
3. Périmètre et API de la plateforme nationale de gestion des officines (ministère de la Santé).
4. Application de l'article 51 bis (caisse certifiée) aux officines ; textes d'application DGI et format d'export.
5. Règles de l'Ordre (CNOP) sur une application patient et sur le partage de disponibilités entre officines.
6. Protection des données de santé (loi 18-07, autorité ANPDP) : déclaration ou autorisation nécessaire pour les registres patients.

---

## Sources

- MIPH, guide DataMatrix : https://www.miph.gov.dz/fr/wp-content/uploads/2026/01/guide-datamatrix-draft-novembre-2025.pdf
- MIPH, guide d'importation (sérialisation) : https://www.miph.gov.dz/fr/wp-content/uploads/2026/04/guide-importation-finale.pdf
- MIPH, stratégie de sérialisation : https://www.miph.gov.dz/fr/wp-content/uploads/2026/04/la-strategie-de-la-serialisation.pdf
- Loi 23-05, répertoire des ordonnances : https://www.algerie360.com/fraude-aux-ordonnances-ce-que-les-pharmaciens-reclament-au-ministere-de-la-sante/
- Ordonnance numérique, état d'avancement : https://afriqueitnews.com/tech-media/pharmaciens-algeriens-reclament-ordonnances-numeriques-fraude/
- Plateforme nationale des officines : https://news.radioalgerie.dz/fr/node/44295
- Ruptures et grossistes : https://www.algerie360.com/ruptures-recurrentes-des-produits-de-soins-le-president-du-syndicat-des-pharmaciens-met-en-cause-lagence-et-la-cellule-de-veille-du-medicament/
- Production nationale (82 %) : https://news.radioalgerie.dz/fr/node/83166
- Loi de finances 2026, article 51 bis : https://www.upgrowth.dz/loi-de-finances-2026-articles-cles-algerie
- Projet de décret, exercice de la pharmacie : https://news.radioalgerie.dz/fr/node/77727
- Tarifs concurrents : https://chifa.plus/ · http://xpertsoft-dz.com/tarifs.php · https://pharmapro.tkawen.com/
- Part de marché (étude université de Sétif) : https://repository.univ-setif.dz/bitstreams/ac2e4408-7e60-4162-8684-3356523302aa/download
