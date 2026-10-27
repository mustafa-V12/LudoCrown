-- Ludo Crown — database roles (run once by a database administrator, NOT by the app).
-- Replace the passwords with values from your secret store before running, e.g.:
--   psql "$ADMIN_DATABASE_URL" -v ON_ERROR_STOP=1 -v migrator_pw='...' -v app_pw='...' -f db/roles.sql
--
-- ludocrown_migrator : owns the schema, used only to apply migrations (CI/CD or manual).
-- ludocrown_app      : used by the running server. Can read/write rows, cannot change the schema.

CREATE ROLE ludocrown_migrator LOGIN PASSWORD :'migrator_pw';
CREATE ROLE ludocrown_app LOGIN PASSWORD :'app_pw';

SELECT format('GRANT CONNECT ON DATABASE %I TO ludocrown_app', current_database()) \gexec
GRANT USAGE, CREATE ON SCHEMA public TO ludocrown_migrator;
GRANT USAGE ON SCHEMA public TO ludocrown_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Tables created later by the migrator are automatically usable (DML only) by the app role.
ALTER DEFAULT PRIVILEGES FOR ROLE ludocrown_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ludocrown_app;
ALTER DEFAULT PRIVILEGES FOR ROLE ludocrown_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO ludocrown_app;
