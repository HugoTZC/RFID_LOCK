BEGIN;
-- The current enrollment UI does not collect an employee number.
ALTER TABLE rfid_lock.badges ALTER COLUMN employee_number DROP NOT NULL;
COMMIT;
