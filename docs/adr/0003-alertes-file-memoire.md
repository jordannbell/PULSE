# ADR 0003 : alertes par file en mémoire, avec nouvelles tentatives

- **Statut** : accepté. Les points 5 (canaux dans la configuration) et la dernière réserve (canaux globaux) sont remplacés par l'[ADR 0004](0004-comptes-sessions-multi-tenant.md) : les canaux sont maintenant par organisation, les secrets de webhooks chiffrés en base.
- **Contexte** : une panne doit être signalée à l'administrateur sans qu'il ouvre la page de statut. L'envoi (serveur de courrier, webhook) est lent et peut échouer : il ne doit ni ralentir les contrôles, ni les faire échouer.
- **Décision** :
  1. Les contrôles **publient** un événement (`IAlertPublisher`) *après* l'enregistrement réussi. Ils n'attendent jamais l'envoi.
  2. Une file bornée en mémoire (1 000 alertes, la plus ancienne est abandonnée et journalisée en cas de débordement) relie les contrôles à un `AlertDispatcher`.
  3. Le dispatcheur envoie à chaque canal **en parallèle** ; un canal en échec est retenté (3 essais par défaut, attente de 5 s puis 10 s) sans bloquer les autres canaux ni les alertes suivantes.
  4. Une alerte n'est émise que sur un **changement d'état** (incident ouvert, incident résolu) : une panne de plusieurs heures produit deux alertes, pas une par contrôle. Le seuil d'échecs consécutifs sert d'anti-rebond.
  5. Les canaux sont configurés par **fichier de configuration ou variables d'environnement**, pas en base : aucun secret (mot de passe SMTP, clé de signature) n'est stocké ni renvoyé par l'API.
  6. Les webhooks passent par le même client protégé que les sondes (`GuardedHttp`) : pas de redirection suivie, adresses privées refusées en production. Chaque requête est signée (HMAC-SHA256 sur `horodatage.corps`).
- **Conséquences** :
  - (+) un serveur de courrier en panne ne gêne pas la surveillance ;
  - (+) pas de doublon d'alerte pendant une panne longue ;
  - (−) **les alertes en attente sont perdues si le processus s'arrête** avant l'envoi (redémarrage pendant une panne, par exemple). Le statut reste correct sur la page publique ;
  - (−) un seul administrateur et des canaux globaux : pas de canaux par service ni par organisation.
- **Réévaluer** : avec le multi-tenant. La suite logique est une table « boîte d'envoi » (outbox) écrite dans la même transaction que l'incident, ce qui rend la livraison « au moins une fois » même après un arrêt brutal, et des canaux stockés par organisation.
