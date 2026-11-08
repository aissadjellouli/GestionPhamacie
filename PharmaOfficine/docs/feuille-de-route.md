# Feuille de route

## Informations à obtenir de la pharmacie pilote

- [ ] Délais de reprise des grossistes avant péremption, délais de livraison
- [ ] Modèle de l’imprimante d’étiquettes (ZPL ou TSPL) et du scanner (2D ou laser)
- [ ] Nom et version du logiciel actuel, possibilités d’export
- [ ] Exemple anonymisé de facture signée exportée par Chifa_Officine

## Version 1 (pilote)

- [ ] Caisse : panier, encaissement, part CNAS et part patient, crédit, ticket ESC/POS
- [ ] Scan boîte par boîte (`vendre_boite`) et lecture Data Matrix en caisse
- [ ] Clôture de caisse en comptage aveugle, demande du code titulaire
- [ ] Réception avec impression des étiquettes, écran Stock
- [ ] Écran d’administration des seuils et des droits
- [ ] Tests d’intégration sur `pharmacie_test` (alertes, FEFO, traçabilité, reprise)
- [ ] Connecteur de reprise pour le logiciel du pilote, première répétition

## Ensuite

- [ ] Connecteur Chifa (dès que le format est connu)
- [ ] Bordereaux et rejets, rapports, achats et retours
- [ ] Traductions arabe et anglais (fichiers `.resx`), interface de droite à gauche
- [ ] Installateur (Inno Setup) et mises à jour automatiques (Velopack)
- [ ] Alertes SMS et rapport e-mail au titulaire, puis accès à distance par VPN
