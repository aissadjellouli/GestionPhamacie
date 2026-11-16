# Sécurité et droits

## Comptes

- Un compte par employé, jamais de compte partagé. Mot de passe haché en bcrypt ; code court (PIN) prévu pour la caisse.
- Connexion : `POST /api/auth/connexion` renvoie un jeton JWT (12 h par défaut).
- Chaque connexion réussie (mot de passe ou code comptoir) est tracée avec le poste (table `connexion_employe`, script 058) : la surveillance signale un employé actif sur deux postes à la fois.
- La clé `Jwt:Cle` de `appsettings.json` est une valeur de développement : la remplacer en production par une clé aléatoire de 64 caractères au moins, propre à chaque pharmacie.

## Droits

- 34 permissions (table `permission`, constantes `Pharma.Domain.Securite.Permissions`, `database/init/seed_referentiel.sql`) ; la liste complète, avec les droits de départ de chaque rôle, est dans `docs/regles-metier.md` (Sécurité et droits, Droits par défaut). Ajouts : « stock.donner » (échantillon, gratuité, don ; script 056), « achats.valider_ecart » (écart de réception à l'aveugle ; script 057) et « caisse.remises_auto » (remises automatiques ; scripts 071-072).
- **Droits réglés par personne** (script 071) : 3 des 34 droits ne se règlent plus par rôle mais sur chaque employé, par le titulaire seul (fiche de l'employé, Utilisateurs et droits) : « caisse.remise » (remise maximale en %, 0 = pas de remise), « stock.donner » (peut faire un don) et « caisse.remises_auto » (peut gérer les remises automatiques). Ils sont absents de la matrice des rôles. Remise au-delà du maximum : la fenêtre « Faire autoriser » ne propose que les employés dont le maximum couvre la remise demandée. Le droit « rapports.marges » montre aussi la valeur d'achat et la TVA des pertes et dons, et la marge après pertes et dons.
- Rôles de départ : titulaire (tout, non modifiable), pharmacien assistant, vendeur, stagiaire. Le titulaire ajuste les droits depuis l’administration.
- Chaque point d’accès déclare sa permission : `.ExigerPermission(Permissions.StockConsulter)`. La vérification se fait en base à chaque appel, donc un droit retiré s’applique immédiatement.
- Certaines actions sont **réservées au titulaire** quel que soit le réglage des droits : `.ExigerTitulaire()` côté API et `ExigerTitulaireAsync` dans les cas d'usage (rôle système). C'est le cas des notifications : réglages, destinataires, messages de test et file d'attente ; et du réglage de la TVA à reverser sur les pertes et dons (Rapports, script 061, inscrit au journal `tva_sorties`). Le poste reçoit le marqueur `role.titulaire` dans la liste des droits pour afficher ces écrans.
- Action sensible non autorisée : le poste demande le code d'un employé qui a le droit ; le journal enregistre qui a agi et qui a autorisé, et l'opération (annulation ancienne, retour) garde ce collègue comme validateur. Jamais par code : la délivrance d'un psychotrope (un employé habilité reprend la vente), et, sur une connexion par code comptoir, un droit hors du comptoir.
- Mot de passe provisoire : aucun droit, ni par mot de passe ni par code comptoir, tant qu'il n'est pas changé. Les routes sans droit particulier (`ExigerConnexion`) refusent aussi le provisoire et le super admin, sauf ce qui leur reste permis.
- Connexions révoquées (script 063) : mot de passe changé ou réinitialisé, compte désactivé, code comptoir effacé ou base restaurée → les connexions déjà ouvertes sont refusées à l'appel suivant (l'API relit l'état du compte à chaque appel).
- Réseau : chiffrer les échanges en HTTPS à la mise en production (étape 3.5 bis de [mise-en-production.md](mise-en-production.md)).

## Garanties en base

- Le titulaire garde tous ses droits, et il reste toujours au moins un titulaire actif.
- `journal_audit`, `registre_psychotrope` et `boite_evenement` ne peuvent être ni modifiés ni supprimés.
- PostgreSQL n’accepte que les connexions locales de l’API (à régler dans `pg_hba.conf` à l’installation).

## Journaux sans données personnelles

Règle (2026-11-16) : les fichiers journaux du serveur (`C:\ProgramData\PharmaOfficine\Journaux\pharma-api-*.log`) et du poste (`%LOCALAPPDATA%\PharmaOfficine\erreurs.log`) ne contiennent aucune donnée personnelle : ni nom ou prénom de patient, de médecin ou d'employé, ni n° d'assuré, n° de pièce, téléphone, e-mail, adresse, ni texte libre saisi (motifs, observations). Le journal d'audit en base (`journal_audit`) n'est pas concerné.

- **À la source** : on journalise des identifiants techniques (id, code d'action, type d'alerte, code d'erreur), jamais le texte. Refus métier : chemin, type d'exception, champ et statut, jamais le message (qui part au poste seulement). Refus de la base : code SQL, contrainte, table, sans le texte de la base. Requête mal formée : nom du paramètre seulement. Code super admin : action et canal (SMS ou e-mail), jamais le destinataire.
- **Filet de sécurité** : `Pharma.Shared.MasqueurJournal` est appliqué à chaque ligne écrite (message rendu et pile d'exception complète) par `JournalFichierProvider` (serveur) et par `App.NoterErreur` (poste). Il masque : e-mail → `a***@d***.dz` (1er caractère, 1er caractère du domaine, extension) ; téléphone mobile algérien 05/06/07 ou +213/00213, séparateurs espace, point ou tiret admis → `06******12` ; nombre de 9 à 12 chiffres avec suffixe `/NN` facultatif (n° d'assuré) → `6907******42/80`. Les noms et textes libres ne sont pas reconnaissables : seule la règle « à la source » les protège.
- Contrôlé par `tests/Pharma.Architecture.Tests/JournauxSansDonneesPersonnellesTests.cs`.