# Feuille de route

Mise à jour le 2026-11-18, alignée sur l'inventaire [fonctionnalites.md](fonctionnalites.md) : l'état de chaque fonction y est
tenu à jour ; cette page ne garde que les grandes étapes. Points à décider et démarches : [reste-a-faire-synthese.md](reste-a-faire-synthese.md).

## Informations à obtenir de la pharmacie pilote

- [ ] Délais de reprise des grossistes avant péremption, délais de livraison (le réglage par grossiste existe : Alertes et seuils)
- [ ] Modèle de l'imprimante d'étiquettes (ZPL prévu, à essayer) et du scanner (2D ou laser)
- [ ] Nom et version du logiciel actuel, possibilités d'export (l'assistant de reprise ODBC existe)
- [ ] Exemple anonymisé de facture signée exportée par Chifa_Officine (format inconnu : import bloqué)

## Version 1 (pilote)

- [x] Caisse : panier, encaissement, part CNAS et part patient, crédit, ticket imprimé par Windows
- [ ] Ticket en impression directe ESC/POS et tiroir-caisse : **bloqué** (modèle d'imprimante et matériel à définir)
- [x] Scan boîte par boîte et lecture Data Matrix en caisse et à la réception
- [x] Clôture de caisse en comptage aveugle, validations par code
- [x] Réception avec étiquettes (planche PDF, ZPL à essayer sur la vraie imprimante), écran Stock
- [x] Écrans d'administration des seuils et des droits
- [x] Tests automatisés : tests .NET (domaine, application, architecture, central) et tests SQL de chaque script (`database/tests`)
- [x] Reprise de données (super admin) : assistant ODBC ; première répétition avec le vrai logiciel du pilote à faire sur le terrain

## Fait depuis

- [x] Bordereaux Chifa et rejets, rapports, achats, retours clients et fournisseurs
- [x] Alertes SMS et rapport quotidien par e-mail au titulaire (à essayer avec le vrai modem et le vrai compte SMTP)
- [x] Sauvegardes : disque externe, copie de secours, 2e serveur, alerte sans sauvegarde sur disque depuis N jours
- [x] Synchronisation avec le serveur central : lots S1 à S5, mutualisation des réglages (M1 à M9)

## Ensuite

- [ ] Connecteur Chifa : import des factures signées, bordereaux et rejets (dès que le format est connu)
- [ ] Traductions arabe et anglais, interface de droite à gauche
- [ ] Installateur et mises à jour automatiques des postes
- [ ] Accès au pilotage hors de la pharmacie par VPN (à choisir avec le prestataire)
- [ ] Synchronisation : fin du lot S6 (restauration d'une officine depuis le central, bascule, départ), lot S7 (écrans du central, multi-officines), après le choix de l'hébergeur
- [ ] Lot 9, chiffrement (C1 à C6) : uniquement sur « go chiffrement »
- [ ] Essais sur le terrain : HTTPS réel, matériel (modem GSM, SMTP, imprimantes, disque de sauvegarde, iPhone), copie de la GED entre deux vrais serveurs, Chifa, reprise réelle
