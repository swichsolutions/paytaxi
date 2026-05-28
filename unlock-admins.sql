-- Reset lockout for all admin users. Dev convenience after rate-limit/lockout tests.
UPDATE "AdminUsers"
SET "LockedUntil" = NULL,
    "FailedLoginAttempts" = 0;
