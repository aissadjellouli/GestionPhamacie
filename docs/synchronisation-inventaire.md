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

**Récapitulatif** : 149 tables — 81 montantes, 22 bidirectionnelles, 18 descendantes, 28 jamais envoyées (`sync_ordre_resultat`, montante, et `sync_ordre_recu`, jamais, par le script 145, lots S5.1 et S5.5 ; `compteur_ticket` passée de jamais à montante par le script 144, lot S4 ; `reglage_mutualise`, montante, par le script 141 ; `sync_journal_descendant` par le script 136 ; `sync_chargement` ajoutée par le script 129, lot S6 ; `sync_famille_colonne` par le script 130 ; `reglage_central`, descendante, par le script 131).

## Ce que reçoit le serveur central : vue d'ensemble (2026-11-17)

**En pratique aujourd'hui : rien.** La synchronisation est désactivée par défaut (`sync_parametre.active = false` : aucun déclencheur
posé, rien en file) ; l'hébergeur n'est pas choisi, il n'existe pas de vrai serveur central ; les essais de bout en bout ont été faits sur
des copies jetables, supprimées (`pharmacie_dev` et `pharmacie_test` : synchronisation inactive, 0 lot).

**Une fois activée** (détail table par table plus bas ; colonnes non nominatives restantes de chaque table : voir le schéma) :

| Famille | Exemples de tables | Ce qui part |
|---|---|---|
| Ventes et paiements | `vente`, `ligne_vente`, `paiement`, `annulation_vente`, `retour_client` | Montants, produits, dates, employé par son identifiant ; retirés : `remis_a_nom`, `pour_nom`, `chifa_qr` |
| Journal fiscal et intégrité | `journal_vente`, `cloture_fiscale`, `sceau_integrite`, `verification_integrite` | Tel quel : rangs, empreintes, sceaux |
| Patients | `patient`, `credit_*`, `demande_patient` | **Sous code `P-…`** ; retirés : nom, prénom, date de naissance, téléphone, n° d'assuré, courriel |
| Registre des psychotropes | `registre_psychotrope`, `journal_registre_psychotrope`, `forcage_alerte_psychotrope` | Porteur **sous code `R-…`** ; retirés : nom, adresse, pièce |
| Chifa | `bordereau`, `bordereau_vente`, `regularisation_part_organisme` | Montants et états, sous code |
| Stock et traçabilité | `lot`, `boite`, `boite_evenement`, `mouvement_stock`, `inventaire` | Tel quel |
| Caisse et trésorerie | `session_caisse`, `comptage_caisse`, `mouvement_tresorerie`, `pret_bancaire` | Montants ; banque et références **non par défaut** (famille `banque`) |
| Achats et fournisseurs | `reception`, `commande_fournisseur`, `fournisseur`, `retour_fournisseur` | Raison sociale, soldes ; coordonnées **oui par défaut** (famille `fournisseurs`) |
| Factures clients | `facture_client`, `client_facture` | Raison sociale et identifiants d'entreprise ; retirés : destinataire, contenu ; adresse **non par défaut** (famille `clients_factures`) |
| Réglages | `parametre_*`, `motif_reglable`, `remise_automatique`, `produit` | Tel quel |
| Employés et droits | `utilisateur`, `role`, `role_permission` | Identifiant et rôle ; nom et identifiant de connexion **non par défaut** (famille `employes`) ; **jamais** : mots de passe, PIN, codes comptoir |
| Pharmacie | `pharmacie` | Nom, NIF, adresse… **oui par défaut** (famille `pharmacie`) |

- **Textes libres** (motifs, commentaires, `journal_audit.details`…) : filtrés avant envoi (`[COURRIEL]`, `[TÉL]`, `[N°]`, nom d'un patient →
  son code `P-…`, nom d'un porteur → `[NOM]`), ou non envoyés du tout (`sync_parametre.textes_libres = 'retires'`) ; limite : un nom mal
  orthographié ou inconnu de la base n'est pas repéré.
- **Jamais envoyés** : `sync_pseudonyme` (correspondance code ↔ personne), clé de scellement, ordonnances et GED, médecins (sauf option
  `sync_parametre.envoyer_medecins`), mots de passe et identifiants SMTP.
- **Familles nominatives** (`employes`, `pharmacie`, `fournisseurs`, `banque`, `clients_factures`, `courriel_commandes`) : réglées au
  serveur central par officine, le réglage descend, l'officine retire elle-même les colonnes avant envoi (§ suivant).
- **Cadre légal** : des données sous code restent des données personnelles (loi 18-07) : [chiffrement-data.md](chiffrement-data.md) § 12.

## Familles de données nominatives (script 130, 2026-11-16)

Réglées **au serveur central** (case à cocher globale + surcharge par officine, script central `004`) ; le réglage **descend** par la synchronisation dans la table locale `reglage_central` (script 131, voie descendante : réponse au dépôt d'un lot et échange léger à chaque passage), seule source lue par l'API locale (aucun appel au serveur central pour l'obtenir ; central en panne : dernière version reçue), affichée en lecture seule dans « Admin › Synchronisation › Réglages » ; l'officine **retire avant envoi** les colonnes d'une famille non autorisée (à la constitution des lots : une ligne déjà en file est filtrée elle aussi ; un lot non accusé est reformé). Réglage jamais reçu : **aucune** famille envoyée. Le serveur central efface à la réception toute colonne d'une famille non autorisée (double sécurité). Liste commune : `sync_famille_colonne` (officine), `cloud_famille_colonne` (central), `Pharma.Shared.FamillesNominativesSync` (test `FamillesNominativesTests`).

| Famille | Colonnes | Défaut (titulaire) | Ce qui part si décochée |
|---|---|---|---|
| `employes` | `utilisateur.nom_complet`, `utilisateur.identifiant` | **Non** | Le code (`utilisateur.id`, identifiant local opaque, déjà la clé de la ligne au central), le rôle (`role_id`) et les autres colonnes non nominatives. Les autres tables ne désignent le personnel que par cet id (`utilisateur_id`, `traitee_par`…). |
| `pharmacie` | `pharmacie.titulaire`, `.adresse`, `.telephone`, `parametre_notification.smtp_expediteur` ; **script 133** : `pharmacie.nom`, `.nif`, `.nis`, `.rc`, `.article_imposition` | Oui | N° Chifa (`chifa_officine`), langue. |
| `fournisseurs` | `fournisseur.telephone`, `fournisseur.email`, `fournisseur.adresse` | Oui | Raison sociale, `nif`, `rc`, soldes et délais. |
| `courriel_commandes` | `commande_fournisseur.envoyee_email_a` | Oui | La date d'envoi (`envoyee_email_le`). |
| `banque` (scripts 133 et 135) | `compte_tresorerie.numero_compte`, `.banque`, `pret_bancaire.preteur`, `.reference`, `reglement_fournisseur.banque`, `.reference`, `paiement_effet.reference`, `remise_cheques.reference` | **Non** | Nom du compte, montants, dates. |
| `clients_factures` (script 133) | `client_facture.adresse` | **Non** | Raison sociale, `nif`, `rc`, `nis`, `article_imposition` du client (identifiants d'entreprise, pas des coordonnées). |

Décision du titulaire (2026-11-16, « ok proposition familles ») : `parametre_formulaire_sms.texte` (modèle de message) reste **toujours envoyé**. Décision « ok banque » (2026-11-16) : la famille `banque` couvre aussi la banque et les références des règlements, effets, remises de chèques et prêts (script 135) ; vérifié : aucune n'est dans la clé de sa table (clé = `id`) ni utilisée par le serveur central pour une jointure ou un contrôle. `correspondance_echeancier_preteur.preteur` **n'est pas** ajoutée : c'est la clé de sa ligne (la retirer rendrait la ligne non identifiable au serveur central). Restent hors familles : identifiants fiscaux des fournisseurs et des clients facturés, `client_facture.raison_sociale`. `utilisateur.telephone` et `utilisateur.email` sont déjà exclus (script 126). Textes libres : filtrés (script 127).

**Points [À VÉRIFIER] / [DÉCISION]** relevés pendant l'inventaire :
- Champs de texte libre envoyés (motifs, commentaires, messages d'alerte, `chifa_ecart_detail`…) : **décision du titulaire (script 127)** : listés dans `sync_table.colonnes_texte` et **filtrés** avant envoi (courriel → `[COURRIEL]`, téléphone → `[TÉL]`, suite de 9 chiffres et plus → `[N°]`, nom ou prénom d'un patient → son code `P-…`, ou `[NOM]` s'il est porté par plusieurs patients, nom d'un porteur du registre → `[NOM]`) ; option `sync_parametre.textes_libres = 'retires'` pour ne pas les envoyer du tout (valeur nulle). Limite : un nom inconnu de la base ou mal orthographié n'est pas repéré. `journal_audit.details`, `forcage_alerte_psychotrope.messages`, `facture_client_version.contenu` et `demande_patient.sms_erreur` sont déjà exclus par prudence.
- `journal_registre_psychotrope` est chaîné : les colonnes du porteur étant retirées, le serveur central **ne pourra pas recalculer** l'empreinte de ces lignes (§ 5 de la conception) ; il pourra contrôler la continuité des rangs et conserver les empreintes reçues, la vérification complète restant dans l'officine. **Validé par le titulaire le 2026-11-16 ; réalisé au lot S4 (2026-11-17, conception § 22).**
- `medecin` classée jamais par défaut ; **option du titulaire** `sync_parametre.envoyer_medecins` (script 127, défaut non) : activée, nom, spécialité et wilaya sont envoyés (téléphone, adresse, courriel, n° d'ordre et code prestataire retirés) ; les ordonnances restent dans l'officine ; les médecins déjà enregistrés partent avec le prochain chargement complet (lot S6, « Recharger toutes les données ») ; `catalogue_grossiste` montante (volume à mesurer) ; référentiels descendants : diffusion à concevoir au lot S5.
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
| `compteur_ticket` | montante | id | - | - | Compteur des tickets : continuité « T- » vérifiée au serveur central (lot S4, script 144 ; jamais envoyée avant) |
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
| `sync_ordre_resultat` | montante | ordre_id | - | - | Résultat des ordres du serveur central (état, motif filtré, date), script 145 |
| `utilisation_financement` | montante | id | - | - | |
| `vente` | montante | id | remis_a_nom, pour_nom, chifa_qr | patient_id, remis_a_patient_id, pour_patient_id | |
| `vente_chifa_facture_conflit` | montante | vente_id | - | - | |
| `verification_integrite` | montante | id | - | - | Avec la fin de chaîne constatée (`dernier_rang`, `dernier_hash`, script 144) : vérification croisée au serveur central |
| `verification_serialisation` | montante | id | - | - | |
| `versement_bordereau` | montante | id | - | - | |
| `reglage_mutualise` | montante | nom_table | modele | - | État de la mutualisation des réglages par table (mode, version appliquée, rejet) ; la valeur du modèle reçue ne repart pas ; texte libre `motif_rejet` filtré, script 141 |
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
| `utilisateur` | bidirectionnelle | id | mot_de_passe_hash, pin_hash, code_comptoir_essais, code_comptoir_bloque_jusqu, telephone, email, jetons_revoques_le | - | Jamais de mot de passe ni de code comptoir ; nom et identifiant : famille `employes` (défaut non) |
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
| `reglage_central` | descendante | cle | - | - | Réglages décidés au serveur central (familles nominatives), voie descendante, script 131 |
| `alerte_non_envoyee` | jamais | id | - | - | Technique, local |
| `chaine_integrite` | jamais | journal | - | - | Registre technique local |
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
| `sync_chargement` | jamais | numero | - | - | Synchronisation elle-même (chargements complets, script 129) |
| `sync_famille_colonne` | jamais | famille, nom_table, colonne | - | - | Synchronisation elle-même (familles nominatives, script 130) |
| `sync_journal_descendant` | jamais | id | - | - | Synchronisation elle-même (journal de la voie descendante, script 136) |
| `sync_lot` | jamais | epoque, numero | - | - | Synchronisation elle-même |
| `sync_ordre_recu` | jamais | id | - | - | Ordres reçus du serveur central (données d'employés possibles), script 145 |
| `sync_parametre` | jamais | id | - | - | Synchronisation elle-même |
| `sync_pseudonyme` | jamais | nature, cle_locale | - | - | Correspondance personne <-> code : ne quitte jamais l'officine |
| `sync_sortie` | jamais | id | - | - | Synchronisation elle-même |
| `sync_table` | jamais | nom_table | - | - | Synchronisation elle-même |
