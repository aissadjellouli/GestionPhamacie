# Autres plateformes « style logiciel A » — fonctionnalités utiles pour PharmaOfficine

> Veille du 18/11/2026 sur 8 plateformes (Inde, Afrique, Égypte, France).
> Trois idées fortes pour l'Algérie : les **malades chroniques** (renouvellements préparés et rappelés), le **financement du stock** (payer le grossiste au rythme des ventes) et le **tiers payant au-delà de la CNAS** (mutuelles, assurances, conventions d'entreprises).

## Ce que font ces plateformes

| Plateforme | Pays | Ce qu'elle fait de notable |
| --- | --- | --- |
| eVitalRx | Inde | Logiciel de pharmacie en ligne : import CSV rapide, rapprochement automatique des paiements QR ou en ligne, facture et encaissement par WhatsApp, rappels automatiques de renouvellement, carnet des manquants, site et application à l'image de la pharmacie (rappels de prise, demandes de renouvellement, historique d'ordonnances, contact WhatsApp, fidélité) ; liens avec TallyPrime, Power BI, Zoho Analytics |
| Marg ERP | Inde | Rappels de paiement et notifications WhatsApp, « commande promise » (médicament manquant fourni plus tard au client) ; **fonctionne hors connexion**, contrairement à eVitalRx dont la facturation dépend d'internet |
| Maisha Meds | Kenya | Gestion de stock gratuite sur Android, **conçue pour une connexion faible et hors ligne** ; programmes de remboursement : un financeur paie le soin (paludisme…), la pharmacie le délivre à prix réduit |
| Shelf Life / Field Intelligence | Nigeria, Kenya | **Financement du stock** : la pharmacie paie au fur et à mesure qu'elle vend, 60 à 82 % moins cher qu'un prêt classique ; réassort automatique par apprentissage automatique |
| mPharma | Ghana | Place de marché + stock + vente au détail ; abonnements « Mutti » pour les malades chroniques |
| RxAll | Nigeria | Anti-contrefaçon : analyse et authentification par IA |
| Yodawy | Égypte | Tiers payant des assurances : validation en temps réel assistée par IA ; programme malades chroniques (renouvellement mensuel, pharmacien, livraison gratuite) ; part de départ : 90 %+ des ordonnances et remboursements sur papier |
| Ospharm, Pharmaland, Winpharma, Link | France | Panel de ventes de 6 000 pharmacies (Ospharm) ; prix moyens des pharmacies voisines par géolocalisation (Pharmaland) ; base produits tenue par 8 documentalistes qui lisent le Journal officiel chaque jour (Link) ; analyse de l'affluence par heure pour organiser l'équipe |

## Nouvelles idées pour PharmaOfficine (adaptées à l'Algérie)

| Idée | Vu chez | Pour nous | Priorité |
| --- | --- | --- | --- |
| **Carnet des manquants et commande promise** | eVitalRx, Marg | Produit absent demandé par un client : noté, ajouté à la commande, SMS ou WhatsApp au client à son arrivée. S'ajoute au module ruptures (M3) | P1 |
| **Programme malades chroniques** | Yodawy, mPharma, eVitalRx | Ordonnances renouvelables préparées à l'avance, rappel au patient, commande anticipée. Adapté aux maladies prises en charge à 100 % par la CNAS | P2 |
| **Facture et lien de paiement par WhatsApp, rapprochement automatique** | eVitalRx | Paiement par QR (CIB, Edahabia, BaridiMob) rapproché automatiquement de la caisse **[À VÉRIFIER : solutions disponibles]** | P2 |
| **Tiers payant multi-organismes** | Yodawy | En plus de la CNAS et de la CASNOS : mutuelles, assurances complémentaires, conventions d'entreprises ; part du patient calculée automatiquement | P2 |
| **Réassort automatique par prévision** | Shelf Life, Winpharma | Proposition de commande calculée sur l'historique, la saison et les ruptures connues ; le pharmacien valide | P2 |
| **Exports comptables et analytiques** | eVitalRx | Exports pour l'expert-comptable et les logiciels comptables utilisés en Algérie | P2 |
| **Site et application de la pharmacie** | eVitalRx | Vitrine : horaires, gardes, réservation, demande de renouvellement | P3 |
| **Financement du stock (payer à la vente)** | Shelf Life | Partenariat avec banques ou grossistes : crédit fondé sur les ventes réelles enregistrées par le logiciel **[À VÉRIFIER : cadre bancaire, finance islamique]** | P3, fort potentiel |
| **Programmes financés par un tiers** | Maisha Meds | Programmes de santé publique ou de laboratoires (dépistage, observance) remboursés à la pharmacie | P3 |
| **Comparaison avec les autres pharmacies** | Ospharm, Pharmaland | Tendances de ventes et prix moyens de la région, à partir des données anonymisées du réseau ; vendable aux laboratoires | P3 |
| **Vérification anti-contrefaçon par scan** | RxAll, Meditect | Prolongement du module DataMatrix (M1) | Avec M1 |
| **Planning de l'équipe selon l'affluence** | Outils d'analyse français | Affluence par heure calculée à partir des heures de vente | P3 |

## À retenir

1. **Le hors ligne est un vrai avantage concurrentiel** : Marg l'oppose à eVitalRx, Maisha Meds en a fait sa base pour les zones mal connectées. Cela confirme le choix fait face à logiciel A, 100 % en ligne.
2. **Les malades chroniques sont les clients les plus fidèles** : trois plateformes sur huit en ont fait un produit à part.
3. **Le financement du stock** est le modèle le plus original : il demande un partenaire financier, mais les données de ventes du logiciel en sont la condition.
4. **La base produits demande une personne dédiée** (exemple de Link) : à prévoir dans l'organisation (voir section 4 ter du fichier `pharma-officine-differenciation.md`).

## Sources

- eVitalRx (Slashdot) : https://slashdot.org/software/p/eVitalRx
- eVitalRx (GetApp, intégrations) : https://www.getapp.ca/software/2063645/evitalrx
- eVitalRx, application en marque blanche : https://www.evitalrx.in/products/byob/
- Marg ERP vs eVitalRx : https://margcompusoft.com/m/compare-pharmacy-software-marg-erp-vs-evitalrx/
- Maisha Meds : https://techtrendske.co.ke/2022/06/01/kenyan-e-health-startup-maisha-meds-hits-1000-active-facilities-expands-to-nigeria/
- Maisha Meds, programmes de remboursement : https://thebenchmark.com.ng/?p=4493
- Shelf Life, payer à la vente : https://businessday.ng/bd-weekender/article/finance-for-pharmacies-shouldnt-come-at-a-cost-to-patients/
- Shelf Life, coût vs prêts : https://techpoint.africa/startups/investors/page/2/
- Shelf Life, réassort automatique : https://www.prosperafrica.gov/blog/a-well-timed-investment-how-investing-in-tech-driven-healthcare-smes-is-boosting-africas-covid-19-response/
- mPharma, RxAll (panorama) : https://kolonell.com/en/blog/pharmacy-saas-africa-2026
- Yodawy (Dealroom) : https://app.dealroom.co/companies/yodawy
- Yodawy (TechCrunch) : https://blog.kowatek.com/?p=14038
- Ospharm : https://www.lequotidiendupharmacien.fr/formation/specialites-medicales/lepidemie-de-grippe-est-passee-par-la
- Pharmaland : https://www.lequotidiendupharmacien.fr/gestion-de-lofficine/e-sante/les-challenges-du-nouveau-pharmaland
- Link : https://www.lequotidiendupharmacien.fr/exercice-pro/link-la-plateforme-digitale-utilisee-par-11-000-pharmacies
- Analyse d'affluence : https://www.lequotidiendupharmacien.fr/gestion-de-lofficine/comptabilite/performances-de-lofficine-plus-quune-affaire-de-chiffres
