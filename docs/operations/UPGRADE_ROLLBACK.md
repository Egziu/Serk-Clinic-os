# Upgrade and rollback
Migrations are additive, ordered, checksum-verified (`DatabaseMigrator`); editing an applied migration fails startup. Roll forward only; destructive changes require owner approval and a rehearsed data-preservation plan. Rollback = restore from backup (not yet rehearsed — OPS-002/OPS-003 NOT RUN).
