# Points restants

Liste de travail tenue à jour à chaque lot. Cocher un point quand il est livré et vérifié dans le poste client.

## Prochaines demandes

- [ ] **Registre des psychotropes, suites** :
  - à vérifier : colonnes exactes des annexes 12 et 13 du décret 19-379 (modifié par le 21-196) pour les psychotropes ;
  - retrait des périmés (Alertes, « Retirer du stock ») : pas encore de sortie de stock ; le registre l'inscrira en « destruction » dès qu'elle existera ;
  - registre papier coté et paraphé : demander à la DSP de wilaya si le registre informatisé imprimé est accepté tel quel.
- [ ] **Rapports et retours** : une vente annulée sort des chiffres de son jour d’origine ; un retour client n’est pas encore déduit du chiffre d’affaires des rapports (à afficher en ligne « retours » ou à déduire : à décider).
- [ ] **Retour partiel en tiers payant** : aujourd’hui refusé (il faut annuler la vente complète) ; à revoir avec le format Chifa (régularisation de la part de l’organisme).
- [ ] **Prix dans la nomenclature** : le fichier officiel actuel ne porte ni prix ni code-barres ; le prix public reste à saisir. Barème des marges (décret 98-44) à confirmer avant tout calcul automatique.

- [ ] **Comparaison des tarifs des grossistes** : importer les tarifs de chaque grossiste et comparer les prix produit par produit (aide au choix du grossiste à la commande).
  - À fournir par le titulaire : un exemple de fichier de tarifs de chaque grossiste (Excel, PDF ou autre) pour connaître le format.
- [ ] **Parapharmacie** : distinguer les produits de parapharmacie dans le catalogue (préalable à la fidélisation parapharmacie de la V2).
  - À préciser par le titulaire : comment les reconnaître (classe du catalogue, liste fournie, autre).

## Scanner mobile : suites

- [ ] **iPhone** : la caméra passe par ZXing ; à essayer sur un vrai iPhone (et sur Firefox Android).
- [ ] Vérifier avec le vrai matériel : réglage GS1 des douchettes et terminaux laser, lecture des petits Data Matrix par la caméra.

## Questions pour la V2

- [ ] **Wasfa** : de quoi s'agit-il exactement (ordonnance préparée à l'avance ?), et qui l'envoie ?
- Autres sujets V2 déjà notés : commandes intelligentes (Ramadan, classement ABC), fidélisation parapharmacie, messagerie interne.

## À vérifier sur le terrain

- [ ] **Chifa : éviter la double saisie** (Chifa_Officine est un programme Windows installé sur chaque poste de caisse) :
  - sur un poste de caisse : noter l'heure, faire une vente tiers payant de test dans Chifa, puis chercher les fichiers modifiés à cette heure-là (dossier d'installation de Chifa, `C:\ProgramData`) ;
  - fournir le dossier, un fichier anonymisé (faux nom, faux n° d'assuré) et le nom et la version de Chifa (« À propos ») ;
  - ensuite : notre logiciel lit ces fichiers et crée la vente tiers payant chez nous (patient, produits, parts CNAS et patient, n° de facture Chifa, stock et registre), sans ressaisie ; les cas incomplets passent « à vérifier ».

- [ ] **Reprise réelle** : essayer l'assistant sur une copie de la base de l'ancien logiciel du pilote (pilote ODBC, mot de passe, séparateur des exports CSV, stock tenu par lots ou non).
- [ ] **Étiquetage du stock existant** : 17 % des boîtes sont identifiées sur la base de test. Il faut étiqueter rayon par rayon (à la fin de chaque inventaire tournant, ou dans Achats > Étiquettes internes) avant que le mode strict ne s'active.
- [ ] **Data Matrix** : part des boîtes qui en portent un dans la pharmacie (cela dépend des laboratoires et de la réglementation).
- [ ] **Modem GSM** : code PIN, opérateur, longueur réelle des SMS.
- [ ] **Compte SMTP** : serveur, port et mode TLS du fournisseur de la pharmacie.
- [ ] **Disque externe de sauvegarde** : lettre de lecteur stable et droits d'écriture du service Windows.
- [ ] **Imprimante d'étiquettes thermique** : adresse IP (variable `PHARMA_IMPRIMANTE_ETIQUETTES`).
- [ ] **DPAPI** : le service de l'API doit tourner sur le PC serveur qui a enregistré le mot de passe SMTP.

## Livré récemment

- [x] Registre des psychotropes complet (toutes entrées et sorties, numéro d'ordre sans trou, balance mensuelle, PDF et CSV) ; porteur de l'ordonnance saisi à la caisse.
- [x] Nomenclature nationale : import du fichier Excel du MIPH, création de produit depuis la nomenclature, psychotropes repérés par leur DCI.
- [x] Caisse partagée par poste, code comptoir et relève.

- [x] Assistant de reprise de données générique (ODBC) : source, correspondance proposée, rapprochement, contrôles, chargement, vérification.
- [x] Rapports : ventes, marge brute au prix réel, ventes par jour, produits les plus vendus, clôtures ; export PDF et Excel.
- [x] Prix d'achat réel par boîte (plus de moyenne) ; date différente ou lot bloqué à la réception : choix laissé à l'utilisateur.
- [x] Scanner mobile : une ligne retirée ou une réception abandonnée sur le poste remet ses scans en attente sur le mobile.
- [x] Scanner mobile : lecture caméra sur tous les mobiles grâce à ZXing (servi en local) quand le navigateur ne lit pas les Data Matrix.
- [x] Message d'alerte de stock clair (seuil appliqué, « aucune vente récente ») ; caisse : libellé « lot conseillé » ; dernier prix d'achat repris aussi pour les lots sans entrée en stock.
- [x] Champs obligatoires : astérisque rouge, champ en rouge et curseur sur le premier manquant, sur tous les formulaires du poste.
- [x] Seuil d'alerte de stock par produit, saisi par le pharmacien (Administration > Seuils), sinon automatique.
- [x] Caisse : stock restant affiché ; boîte scannée servie avec son lot et avertissement si un lot proche de la péremption doit partir avant.
- [x] Scanner mobile (lot 3) : contrôle en rayon, dates à vérifier, rangement, inventaire, scans de réception importés sur le poste ; HTTPS local (scripts/certificat_mobile.ps1).
- [x] Saisie assistée du produit (liste sous le champ, flèches et Entrée) dans Commandes et dans Réception.
- [x] Lot unique par produit, n° de lot et grossiste ; étiquette interne créée et imprimée pour chaque boîte sans Data Matrix à la réception.
- [x] Inventaire tournant (lot 2), emplacements (lot 1), notifications au titulaire et sauvegardes.
