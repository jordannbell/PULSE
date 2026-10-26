# ADR 0001 : SQLite pour le MVP

- **Statut** : accepté
- **Contexte** : le MVP tourne sur une seule instance, sans compte utilisateur. Il faut une base qui se lance sans installation, pour que n'importe qui puisse cloner et exécuter le projet, y compris en test d'intégration.
- **Décision** : utiliser SQLite via EF Core, avec des migrations. L'application dépend du port `IPulseStore`, pas d'EF Core.
- **Conséquences** :
  - (+) zéro installation, tests d'intégration sur une vraie base en mémoire ;
  - (+) passage à PostgreSQL = un nouvel adaptateur et une migration, sans toucher au domaine ni aux cas d'usage ;
  - (−) pas d'écritures concurrentes massives : suffisant pour un seul tenant ;
  - (−) `DateTimeOffset` est stocké au format binaire (UTC) car SQLite ne sait pas trier sa forme texte ;
  - (−) la sélection des services « à vérifier » se fait en mémoire ; à remplacer par une colonne `NextCheckAt` indexée quand le nombre de services grandit.
- **Réévaluer** : dès l'arrivée du multi-tenant ou de plusieurs instances.
