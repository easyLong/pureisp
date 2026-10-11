-- Presentation view only. expires_at in the original table stays UTC.
CREATE OR REPLACE VIEW pureisp_license_keys_beijing AS
SELECT id, hint, label, enabled,
       DATE_ADD(expires_at, INTERVAL 8 HOUR) AS expires_at_beijing,
       expires_at AS expires_at_utc,
       version
FROM pureisp_license_keys;
