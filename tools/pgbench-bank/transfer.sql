-- The bank transfer of CamusDB.Workload (Operations/TransferOperation.cs, sequential wire shape) for pgbench.
-- 'from' is a row of this client's strided shard (index % nclients == client_id), 'to' any other row; the two
-- legs run in ascending row order, each a SELECT of balance and version followed by an UPDATE that writes the
-- balance read plus the delta and bumps version: six round trips (BEGIN, SELECT, UPDATE, SELECT, UPDATE, COMMIT).
-- A serialization failure or deadlock re-runs the whole script with the same random values (--max-tries).
\set from :client_id + :nclients * random(0, (:nrows - 1 - :client_id) / :nclients)
\set to random(0, :nrows - 2)
\set to CASE WHEN :to >= :from THEN :to + 1 ELSE :to END
\set lo least(:from, :to)
\set hi greatest(:from, :to)
\set lodelta CASE WHEN :lo = :from THEN -1 ELSE 1 END
\set hidelta 0 - :lodelta
BEGIN ISOLATION LEVEL SERIALIZABLE;
SELECT balance AS lo_balance, version AS lo_version FROM workload_accounts WHERE id = lpad(:lo::text, 24, '0') \gset
UPDATE workload_accounts SET balance = :lo_balance::bigint + :lodelta::bigint, version = version + 1 WHERE id = lpad(:lo::text, 24, '0');
SELECT balance AS hi_balance, version AS hi_version FROM workload_accounts WHERE id = lpad(:hi::text, 24, '0') \gset
UPDATE workload_accounts SET balance = :hi_balance::bigint + :hidelta::bigint, version = version + 1 WHERE id = lpad(:hi::text, 24, '0');
COMMIT;
