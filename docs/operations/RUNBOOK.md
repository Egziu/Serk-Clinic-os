# Runbook (draft — not yet rehearsed)

| Situation | Check | Action |
|---|---|---|
| UI says "session ended" for everyone | `/health/ready`; DB connectivity | Restart host after DB recovers; sessions persist in DB |
| `/health/ready` unhealthy | PostgreSQL reachable as `serk_app`? | Fix DB/network; host needs no restart |
| Outbox backlog | `SELECT status, count(*) FROM platform.outbox_messages GROUP BY 1` | Investigate `DeadLettered` rows (`last_error` holds exception type only); requeue by setting `status='Pending', attempts=0` after fixing cause |
| Document download returns `document_integrity_failure` | `audit.security_events` type `document.integrity_failure` | Treat as possible tampering: preserve object store + DB, start incident response |
| Suspected compromised staff account | — | Clinic admin: `POST /api/v1/admin/staff/{userId}/revoke-sessions`; deactivate user (DB/admin tool TBD); review `audit.audit_events` by actor |
| Print problem | User sees "Print dialog opened" | System never records paper as printed; reprint from final document (audited) |

Start/stop: run `Serk.Clinic.Host.dll migrate` to apply migrations, then start the host. Logs are JSON on stdout without PHI.
