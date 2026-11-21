// Écrans du serveur central (lot S7.1, docs/ecrans-central.md). Processus et port distincts du serveur central (5090) ;
// développement : https://127.0.0.1:5094 (refus de démarrer hors boucle locale). Secrets : variables d'environnement seulement.
Console.OutputEncoding = System.Text.Encoding.UTF8;
return await Pharma.Cloud.Web.ServeurEcrans.LancerAsync(args);
