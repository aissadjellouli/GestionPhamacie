# Synchronisation : inventaire des tables (lot S1)

> Lot S1 de [conception-synchronisation.md](conception-synchronisation.md) (GO « go S1 », 2026-11-16). Côté officine seulement : rien n'est envoyé.
> Source de vérité : la table `sync_table` (script `126_synchronisation_sortie.sql`). Ce tableau la recopie ; le test d'architecture
> `SynchronisationInventaireTests` échoue si une table du schéma public n'est pas classée par un script, ou si ce document ne suit pas le classement.
> En base, `sync_tables_non_classees()` liste les oubliées et `sync_definir_active(true)` refuse d'activer tant qu'il en reste.

## Sens

| Sens | Effet en S1 |
|---|---|
| **montante** | Déclencheur `zz_sync_sortie` (si la synchronisation est active) : chaque création, modification ou suppression inscrit une ligne minimisée dans `sync_sortie`, dans la même transaction. Le local fait foi. |
| **bidirectionnelle** | Comme montante (état montant) ; les ordres descendants (prix, réglages, commandes en brouillon, employés et droits : décision 3) seront appliqués par l'API locale au lot S5. |
| **descendante** | Référentiel diffusé par le serveur central (lot S5) ; aucune ligne de file. Aujourd'hui livré par les scripts ou l'import de nomenclature. |
| **jamais** | Ne quitte jamais l'officine. |

## Minimisation

- **Clé stable** : clé primaire de la table (constatée dans le catalogue PostgreSQL), valeurs jointes par `|` ; identité globale = (officine de l'en-tête du lot, table, clé).
- **Colonnes exclues** : retirées de la ligne avant inscription dans la file (jamais présentes dans `sync_sortie`).
- **Sous code** : la valeur (identifiant de patient) est remplacée par un code aléatoire stable `P-` + 24 caractères hexadécimaux ; pour la table `patient`, la clé elle-même devient le code. Les porteurs du registre reçoivent un code `R-…` (colonne ajoutée `porteur_code`) ; leur nom, adresse et pièce sont retirés.
- La correspondance personne ↔ code est dans `sync_pseudonyme` (classée **jamais**) ; pour un porteur, la clé locale est l'empreinte SHA-256 du nom et de la pièce normalisés (le nom n'y est pas recopié). Aucun chiffrement (lot 9 non lancé).

**Récapitulatif** : 142 tables — 78 montantes, 22 bidirectionnelles, 17 descendantes, 25 jamais envoyées.

**Points [À VÉRIFIER] / [DÉCISION]** relevés pendant l'inventaire :
- Champs de texte libre envoyés (motifs, commentaires, messages d'alerte, `chifa_ecart_detail`…) : **décision du titulaire (script 127)** : listés dans `sync_table.colonnes_texte` et **filtrés** avant envoi (courriel → `[COURRIEL]`, téléphone → `[TÉL]`, suite de 9 chiffres et plus → `[N°]`, nom ou prénom d'un patient → son code `P-…`, ou `[NOM]` s'il est porté par plusieurs patients, nom d'un porteur du registre → `[NOM]`) ; option `sync_parametre.textes_libres = 'retires'` pour ne pas les envoyer du tout (valeur nulle). Limite : un nom inconnu de la base ou mal orthographié n'est pas repéré. `journal_audit.details`, `forcage_alerte_psychotrope.messages`, `facture_client_version.contenu` et `demande_patient.sms_erreur` sont déjà exclus par prudence.
- `journal_registre_psychotrope` est chaîné : les colonnes du porteur étant retirées, le serveur central **ne pourra pas recalculer** l'empreinte de ces lignes (§ 5 de la conception) ; il pourra contrôler la continuité des rangs et conserver les empreintes reçues, la vérification complète restant dans l'officine. **Validé par le titulaire le 2026-11-16.**
- `medecin` classée jamais par défaut ; **option du titulaire** `sync_parametre.envoyer_medecins` (script 127, défaut non) : activée, nom, spécialité et wilaya sont envoyés (téléphone, adresse, courriel, n° d'ordre et code prestataire retirés) ; les ordonnances restent dans l'officine ; les médecins déjà enregistrés ne partent qu'à leur prochaine modification (chargement initial : lot S2) ; `catalogue_grossiste` montante (volume à mesurer) ; référentiels descendants : diffusion à concevoir au lot S5.
- Le schéma `import` (tables de reprise) n'est pas synchronisé.

## Inventaire

| Table | Sens | Clé stable | Colonnes exclues | Sous code | Remarque |
|---|---|---|---|---|---|
| `alerte` | montante | id | - | - | |
| `alerte_surveillance` | montante | id | - | - | |
| `annulation_registre` | montante | id | - | - | |
| `annulation_vente` | montante | id | - | - | |
| `avoir_mouvement` | montante | id | - | patient_id | |
| `boite` | montante | id | - | - | |
| `boite_evenement` | montante | id | - | patient_id | |
| `bordereau` | montante | id | - | - | |
| `bordereau_vente` | montante | id | - | - | |
| `campagne_inventaire` | montante | id | - | - | |
| `campagne_inventaire_ligne` | montante | campagne_id, lot_id | - | - | |
| `catalogue_grossiste` | montante | fournisseur_id, produit_id | - | - | |
| `client_facture` | montante | id | telephone, email | - | |
| `cloture_fiscale` | montante | id | - | - | |
| `comptage_caisse` | montante | session_id, coupure | - | - | |
| `comptage_intermediaire` | montante | id | - | - | |
| `comptage_reception` | montante | id | - | - | |
| `comptage_reception_ligne` | montante | id | - | - | |
| `compte_carte_periode` | montante | id | - | - | |
| `compte_tresorerie` | montante | id | - | - | |
| `connexion_employe` | montante | id | - | - | |
| `correction_lot` | montante | id | - | - | |
| `correspondance_echeancier` | montante | compte_id | - | - | |
| `correspondance_echeancier_preteur` | montante | preteur | - | - | |
| `credit_imputation` | montante | id | - | - | |
| `credit_mouvement` | montante | id | - | patient_id | |
| `credit_ouverture` | montante | patient_id | - | patient_id | |
| `demande_patient` | montante | id | sms_erreur | patient_id | |
| `ecart_caisse_vu` | montante | session_id | - | - | |
| `echeance_pret` | montante | id | - | - | |
| `emplacement` | montante | id | - | - | |
| `evenement_caisse` | montante | id | - | - | |
| `facture_client` | montante | id | destinataire_nom, destinataire_infos | patient_id | |
| `facture_client_ligne` | montante | id | - | - | |
| `facture_client_version` | montante | facture_id, version | contenu | - | Contenu figé retiré (nom du destinataire) |
| `forcage_alerte_psychotrope` | montante | id | messages | patient_id | |
| `fournisseur` | montante | id | - | - | |
| `fournisseur_mouvement` | montante | id | - | - | |
| `inventaire` | montante | id | - | - | |
| `journal_audit` | montante | id | details | - | Détails libres retirés (peuvent contenir des données personnelles) |
| `journal_registre_psychotrope` | montante | id | porteur_nom, porteur_adresse, porteur_piece | patient_id, porteur_code | Journal chaîné : colonnes du porteur retirées, le serveur central ne peut pas recalculer l'empreinte (lot S4) |
| `journal_sauvegarde` | montante | id | - | - | |
| `journal_vente` | montante | id | - | - | Journal chaîné, envoyé avec rang et empreinte |
| `ligne_inventaire` | montante | id | - | - | |
| `ligne_retour_fournisseur` | montante | id | - | - | |
| `ligne_vente` | montante | id | - | - | |
| `lot` | montante | id | - | - | |
| `mouvement_stock` | montante | id | - | - | |
| `mouvement_tiroir` | montante | id | - | - | |
| `mouvement_tresorerie` | montante | id | - | - | |
| `paiement` | montante | id | - | patient_id | |
| `paiement_echeance_pret` | montante | id | - | - | |
| `paiement_effet` | montante | id | - | - | |
| `patient` | montante | id | nom, prenom, date_naissance, telephone, num_assure, email | id, assure_principal_id | Pseudonyme : le code remplace l'identifiant |
| `pharmacie` | montante | id | - | - | |
| `pret_bancaire` | montante | id | - | - | |
| `reception` | montante | id | - | - | |
| `reception_ligne` | montante | id | - | - | |
| `registre_psychotrope` | montante | id | porteur_nom, porteur_adresse, porteur_piece | patient_id, porteur_code | |
| `reglement_facture` | montante | reglement_id, reception_id | - | - | |
| `reglement_facture_client` | montante | mouvement_id, facture_id | - | - | |
| `reglement_fournisseur` | montante | id | - | - | |
| `regularisation_part_organisme` | montante | id | - | - | |
| `remboursement_titulaire` | montante | id | - | - | |
| `remise_cheque_paiement` | montante | remise_id, paiement_id | - | - | |
| `remise_cheques` | montante | id | - | - | |
| `retour_client` | montante | id | - | - | |
| `retour_client_ligne` | montante | id | - | - | |
| `retour_fournisseur` | montante | id | - | - | |
| `rupture_grossiste` | montante | id | - | - | |
| `sceau_integrite` | montante | id | - | - | Sceaux recopiés tels quels ; la clé ne part jamais |
| `session_caisse` | montante | id | - | - | |
| `utilisation_financement` | montante | id | - | - | |
| `vente` | montante | id | remis_a_nom, pour_nom, chifa_qr | patient_id, remis_a_patient_id, pour_patient_id | |
| `vente_chifa_facture_conflit` | montante | vente_id | - | - | |
| `verification_integrite` | montante | id | - | - | |
| `verification_serialisation` | montante | id | - | - | |
| `versement_bordereau` | montante | id | - | - | |
| `commande_fournisseur` | bidirectionnelle | id | - | - | Commande en brouillon préparée à distance |
| `ligne_commande` | bidirectionnelle | id | - | - | |
| `motif_reglable` | bidirectionnelle | id | - | - | |
| `parametre_alerte` | bidirectionnelle | id | - | - | |
| `parametre_caisse` | bidirectionnelle | id | - | - | |
| `parametre_fiscal` | bidirectionnelle | id | - | - | |
| `parametre_formulaire_sms` | bidirectionnelle | id | - | - | |
| `parametre_inventaire` | bidirectionnelle | id | - | - | |
| `parametre_notification` | bidirectionnelle | id | smtp_identifiant, smtp_mot_de_passe | - | Jamais le compte de messagerie |
| `parametre_rapport` | bidirectionnelle | id | - | - | |
| `parametre_sauvegarde` | bidirectionnelle | id | - | - | |
| `parametre_serialisation` | bidirectionnelle | id | - | - | |
| `parametre_substitution` | bidirectionnelle | id | - | - | |
| `parametre_surveillance` | bidirectionnelle | id | - | - | |
| `parametre_tracabilite` | bidirectionnelle | id | - | - | |
| `produit` | bidirectionnelle | id | - | - | Prix et marges |
| `regle_alerte_psychotrope` | bidirectionnelle | code | - | - | |
| `remise_automatique` | bidirectionnelle | id | - | - | |
| `role` | bidirectionnelle | id | - | - | |
| `role_permission` | bidirectionnelle | role_id, permission_code | - | - | |
| `substitution_liste_blanche` | bidirectionnelle | produit_id, remplacant_id | - | - | |
| `utilisateur` | bidirectionnelle | id | mot_de_passe_hash, pin_hash, code_comptoir_essais, code_comptoir_bloque_jusqu, telephone, email, jetons_revoques_le | - | Jamais de mot de passe ni de code comptoir |
| `categorie_produit` | descendante | code | - | - | |
| `centre_payeur` | descendante | id | - | - | |
| `chifa_code_produit` | descendante | code | - | - | |
| `classe_suivie` | descendante | code | - | - | |
| `classe_suivie_dci` | descendante | classe_code, dci_id | - | - | |
| `dci` | descendante | id | - | - | |
| `indication` | descendante | id | - | - | |
| `laboratoire` | descendante | id | - | - | |
| `nomenclature_produit` | descendante | id | - | - | |
| `nomenclature_version` | descendante | id | - | - | |
| `organisme` | descendante | id | - | - | |
| `parametre_gs1_ai` | descendante | code | - | - | |
| `produit_indication` | descendante | produit_id, indication_id | - | - | |
| `substance_surveillee` | descendante | motif | - | - | |
| `type_prestation` | descendante | id | - | - | |
| `wilaya` | descendante | code | - | - | |
| `zone_psychotrope_wilaya` | descendante | wilaya | - | - | |
| `alerte_non_envoyee` | jamais | id | - | - | Technique, local |
| `chaine_integrite` | jamais | journal | - | - | Registre technique local |
| `compteur_ticket` | jamais | id | - | - | Compteur local |
| `destinataire_notification` | jamais | id | - | - | Coordonnées personnelles |
| `facture_compteur` | jamais | type, annee | - | - | Compteur local |
| `historique_chifa` | jamais | id | - | - | Reprise : nom et n° d'assuré en clair |
| `import_catalogue_grossiste` | jamais | id | - | - | Journal d'import local |
| `integrite_essai` | jamais | id | - | - | Table d'essai interne |
| `medecin` | jamais | id | - | - | Coordonnées des prescripteurs : aucun usage central identifié |
| `message_sortant` | jamais | id | - | - | Contenu et adresse des SMS et courriels |
| `ordonnance` | jamais | id | - | - | Ordonnances et images scannées restent dans l'officine (décision 2) |
| `panier_attente` | jamais | id | - | - | Transitoire |
| `parametre_integrite` | jamais | id | - | - | Technique, local |
| `permission` | jamais | code | - | - | Livrée par les scripts du logiciel |
| `profil_catalogue_grossiste` | jamais | fournisseur_id | - | - | Réglage d'import local |
| `reception_attente` | jamais | id | - | - | Transitoire |
| `scan_reception` | jamais | id | - | - | Transitoire |
| `serialisation_reponse_factice` | jamais | id | - | - | Essai |
| `serveur_officine` | jamais | nom | - | - | Réglages propres à chaque serveur |
| `surveillance_replication` | jamais | id | - | - | Réplication locale |
| `sync_lot` | jamais | epoque, numero | - | - | Synchronisation elle-même |
| `sync_parametre` | jamais | id | - | - | Synchronisation elle-même |
| `sync_pseudonyme` | jamais | nature, cle_locale | - | - | Correspondance personne <-> code : ne quitte jamais l'officine |
| `sync_sortie` | jamais | id | - | - | Synchronisation elle-même |
| `sync_table` | jamais | nom_table | - | - | Synchronisation elle-même |
