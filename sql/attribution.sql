-- provider_snapshot: one row per charge from two GET reads about an hour apart,
-- keeping only charges whose state was the same in both reads. A charge the provider
-- returned and the platform does not have shows up with local_status NULL.
WITH mismatch AS (
  SELECT p.provider_id, c.status AS local_status, p.status AS remote_status,
         CASE
           WHEN p.deleted IS TRUE THEN 'PAYMENT_DELETED'
           WHEN p.status IN ('RECEIVED', 'RECEIVED_IN_CASH') THEN 'PAYMENT_RECEIVED'
           WHEN p.status = 'CONFIRMED' THEN 'PAYMENT_CONFIRMED'
           WHEN p.status = 'OVERDUE' THEN 'PAYMENT_OVERDUE'
           WHEN p.status = 'REFUNDED' THEN 'PAYMENT_REFUNDED'
         END AS expected_event        -- NULL for statuses we do not attribute
  FROM provider_snapshot p
  LEFT JOIN charges c ON c.provider_id = p.provider_id
  -- Only the provider has the charge: it counts when it is settled there and its customer is one
  -- we know. A charge whose customer has no row in our customers table is left out: nothing here verifies
  -- whether the platform should have it, so it is not counted as a divergence.
  WHERE (c.provider_id IS NULL
         AND p.status IN ('CONFIRMED', 'RECEIVED', 'RECEIVED_IN_CASH')
         AND EXISTS (SELECT 1 FROM customers k WHERE k.provider_id = p.customer_id))
     OR (COALESCE(c.last_changed_at, c.created_at) < p.first_read_at - interval '60 minutes'
         AND ((p.deleted IS TRUE AND c.status IN ('PENDING', 'OVERDUE'))
              OR (p.deleted IS NOT TRUE AND p.status <> c.status)))
)
SELECT m.provider_id, m.local_status, m.remote_status,
       CASE
         WHEN m.local_status IS NULL OR m.expected_event IS NULL
           THEN 'unattributed'
         WHEN NOT EXISTS (SELECT 1 FROM inbox_events i
                          WHERE i.entity_key = m.provider_id
                            AND i.event_type = m.expected_event)
           THEN '1 not delivered (unproven)'
         WHEN EXISTS (SELECT 1 FROM inbox_events i
                      WHERE i.entity_key = m.provider_id
                        AND i.event_type = m.expected_event
                        AND i.status = 'PROCESSED')
           THEN '3 processed without effect'
         WHEN NOT EXISTS (SELECT 1 FROM inbox_events i
                          WHERE i.entity_key = m.provider_id
                            AND i.event_type = m.expected_event
                            AND i.status IS DISTINCT FROM 'FAILED')
           THEN '2 failed, never retried'
         ELSE 'unattributed'          -- some expected-type event is neither PROCESSED nor FAILED: pending, ignored or NULL
       END AS layer
FROM mismatch m
ORDER BY layer, m.provider_id;
