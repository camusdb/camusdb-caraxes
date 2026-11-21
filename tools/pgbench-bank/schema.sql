-- The bank dataset of CamusDB.Workload (Workload/Dataset.cs) for PostgreSQL: one table, 2,000 rows, a 24-character
-- id (CamusDB's OID renders as 24 hex characters), a 256-character payload and the secondary index on owner. Every
-- balance starts at 1,000, so SUM(balance) = 2,000,000 must hold after any run, and SUM(version) counts two per
-- committed transfer.
DROP TABLE IF EXISTS workload_accounts;
CREATE TABLE workload_accounts (
    id      text   PRIMARY KEY,
    owner   bigint NOT NULL,
    balance bigint NOT NULL,
    version bigint NOT NULL,
    payload text   NOT NULL
);
CREATE INDEX workload_accounts_owner ON workload_accounts (owner);
INSERT INTO workload_accounts (id, owner, balance, version, payload)
SELECT lpad(i::text, 24, '0'), (hashint8(i) & 1048575), 1000, 0, substr(repeat(md5(i::text), 8), 1, 256)
FROM generate_series(0, 1999) AS i;
VACUUM ANALYZE workload_accounts;
