# Notifications au titulaire et sauvegardes

Le titulaire est prévenu à distance **sans cloud ni application mobile** : par e-mail via le serveur SMTP de la pharmacie, et par SMS via un modem GSM branché sur le PC serveur. Les deux passent par une file d'attente en base, vidée par l'API toutes les 30 secondes.

## Partage des canaux

| Canal | Usage | Fonctionne sans internet |
| --- | --- | --- |
| **SMS** (modem GSM) | Urgent, court : écart de caisse au-delà du seuil, lot bloqué ou rappelé, sauvegarde échouée ou disque externe absent | Oui (réseau GSM) |
| **E-mail** (SMTP) | Détaillé : rapport quotidien (HTML), copie détaillée des alertes urgentes, messages de test | Non : mis en attente puis réessayé |

| Message | Déclencheur | E-mail | SMS |
| --- | --- | --- | --- |
| Rapport quotidien | Chaque jour à l'heure réglée (21:00 par défaut), une seule fois | Oui | Non |
| Écart de caisse | Clôture dont l'écart dépasse le seuil SMS (à défaut, le seuil de la caisse) | Oui | Oui |
| Lot bloqué | Rappel de lot (Stock, Traçabilité) | Oui | Si la case « SMS lot bloqué » est cochée |
| Sauvegarde | Échec de pg_dump ou disque externe absent | Oui | Si la case « SMS sauvegarde » est cochée |
| Écart d'inventaire | Fin d'un inventaire tournant avec des écarts au-delà des seuils (à valider par le titulaire) | Oui | Oui |
| Test | Bouton « Test » d'un destinataire | Oui | Oui |

Contenu du **rapport quotidien** : chiffre du jour (nombre de ventes, part tiers payant), annulations, écarts de caisse par employé, nouvelles alertes de péremption et nouvelles ruptures.

## Réglages (Administration, Notifications au titulaire)

**Réservé au pharmacien titulaire** (rôle système). Ni le droit `admin.parametres` ni aucun autre droit de la matrice ne suffit : lui seul consulte et modifie les réglages, renseigne les destinataires et voit la file.

| Réglage | Rôle |
| --- | --- |
| Serveur SMTP, port, TLS | Serveur d'envoi de la pharmacie. Port 465 : TLS direct ; autre port avec TLS : STARTTLS |
| Expéditeur, identifiant, mot de passe | Le mot de passe est chiffré par **DPAPI, portée machine** : seul le PC serveur qui l'a enregistré peut le relire. Il n'est jamais renvoyé aux postes ni écrit en clair. Après restauration de la base sur un autre PC, il faut le ressaisir |
| Port du modem | Ex. `COM3`. Vide : aucun SMS n'est mis en file |
| Seuil SMS d'écart de caisse | Vide : seuil d'écart des paramètres de caisse |
| Heure du rapport | 21:00 par défaut |
| SMS lot bloqué, SMS sauvegarde | Active ou non le SMS pour ces événements (l'e-mail part toujours) |

Le lien « envoyer le rapport du jour maintenant » met le rapport en file tout de suite, pour vérifier les réglages. Il n'empêche pas l'envoi automatique du soir.

### Destinataires

Seules les personnes enregistrées par le titulaire reçoivent des messages, et **seulement une fois vérifiées** :

1. Le titulaire ajoute le destinataire : nom, e-mail, mobile algérien (05, 06 ou 07, ou +213), canaux et types de messages.
2. Il clique sur « Test » ; le message de test est le seul qui part vers une adresse non vérifiée.
3. Quand la personne confirme la réception, il clique sur « Vérifié ».

Changer l'e-mail ou le numéro retire la vérification. Un destinataire désactivé ne reçoit plus rien. La base refuse elle-même (déclencheur `trg_message_destinataire`) tout message vers une adresse différente de celle enregistrée, vers un destinataire désactivé, ou vers un destinataire non vérifié (sauf le test). Chaque ajout ou modification est inscrit au journal.

## File d'attente

Table `message_sortant` : canal, destinataire, objet, corps, statut (`en_attente`, `envoye`, `echec`), tentatives, prochaine tentative, dernière erreur.

- Internet, serveur SMTP ou modem indisponible : le message reste `en_attente` et il est réessayé après **1, 5, 15 puis 60 minutes, ensuite toutes les heures**.
- Au-delà de **7 jours**, il passe au statut `echec` : une alerte d'une semaine n'a plus de sens.
- Les SMS passent avant les e-mails à chaque cycle.
- L'écran affiche les 100 derniers messages, avec la dernière erreur et l'heure du prochain essai.

## SMS par modem GSM

Commandes AT en mode texte (`AT+CMGF=1`, `AT+CSCS="GSM"`, `AT+CMGS`) sur le port série, à 115 200 bauds, un envoi à la fois. Les textes sont convertis sans accents (jeu de caractères GSM) et limités à 300 caractères (deux SMS). Il faut un modem USB ou série avec une carte SIM active, sans code PIN, ou avec le code PIN déjà saisi.

## Confidentialité

- **Aucune donnée de patient ni d'ordonnance** dans un e-mail ou un SMS : uniquement des chiffres, des noms d'employés et de postes, des noms de produits et des numéros de lot.
- Le rapport est construit à partir de données agrégées, sans aucune colonne patient. Un test vérifie par réflexion que les modèles des messages n'ont aucun champ patient, ordonnance, assuré ou médecin.
- Le motif d'un rappel de lot, saisi librement, n'est **jamais** envoyé : il pourrait citer un patient. Le message dit seulement que la liste des patients à contacter est dans le logiciel. Un test le vérifie.
- Aucun service cloud : seuls le serveur SMTP choisi par la pharmacie et le réseau GSM sont utilisés.

## Sauvegardes (Administration, Sauvegardes)

Droit `admin.sauvegardes`. Le module intégré à l'API lance `pg_dump` (format personnalisé, restaurable avec `pg_restore`) :

- chaque jour à l'heure réglée (22:00 par défaut) si la sauvegarde automatique est cochée, ou à la demande (« Sauvegarder maintenant ») ;
- vers le dossier choisi sur le disque externe, en `pharmacie_AAAAMMJJ_HHMMSS.dump`. Le fichier n'apparaît qu'une fois complet (écriture dans un `.partiel` puis renommage) ;
- en gardant les N dernières sauvegardes (14 par défaut) ;
- avec une entrée au journal `journal_sauvegarde`, non modifiable : réussie, échouée ou disque absent.

En cas de disque absent ou d'échec, le titulaire reçoit un SMS et un e-mail. `pg_dump` est cherché dans `appsettings.json` (`Sauvegardes:PgDump`), sinon dans `C:\Program Files\PostgreSQL\<version>\bin`. Le mot de passe de la base est passé à `pg_dump` par variable d'environnement, jamais sur la ligne de commande.

> Le script planifié `scripts/sauvegarde_pharmacie.ps1` (voir [mise-en-production.md](mise-en-production.md)) fait un travail proche, mais sans alerte au titulaire. Choisir l'un ou l'autre pour ne pas sauvegarder deux fois.

## Ce qui reste à vérifier avec le vrai matériel

- **Modem GSM** : vitesse du port, code PIN, accusés de l'opérateur (délai de `+CMGS`), longueur réelle des SMS chez l'opérateur algérien.
- **Compte SMTP** : serveur, port et mode TLS du fournisseur de la pharmacie ; filtrage anti-spam du rapport HTML.
- **DPAPI** : le service Windows de l'API doit tourner sur le PC serveur qui a enregistré le mot de passe.
- **Disque externe** : lettre de lecteur stable et droits d'écriture du compte du service Windows.
