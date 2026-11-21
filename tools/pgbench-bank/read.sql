-- The bank point read of CamusDB.Workload (Operations/ReadOperation.cs): one autocommit, read-only statement
-- for a row of the whole table, returning the four columns it validates. It runs at the session default
-- (READ COMMITTED), a single-statement snapshot read that takes no SIREAD lock, as CamusDB's read-only
-- autocommit takes no write lock.
\set r random(0, :nrows - 1)
SELECT id, balance, version, payload FROM workload_accounts WHERE id = lpad(:r::text, 24, '0');
