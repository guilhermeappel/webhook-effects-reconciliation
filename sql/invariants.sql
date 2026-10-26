-- An ACTIVE contract must not have ended: its end date is today or later, and it has one.
-- A contract with no end date is a violation too, because nothing would ever end it.
-- "Today" is the date in Sao Paulo, where the training centers are, not the date of the session's time zone.
-- Run on a schedule; alert when > 0.
SELECT count(*) AS violations
FROM contracts
WHERE status = 'ACTIVE'
  AND (end_date IS NULL OR end_date < (now() AT TIME ZONE 'America/Sao_Paulo')::date);
