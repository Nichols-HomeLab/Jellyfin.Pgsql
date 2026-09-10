"""Checks that the SQLite import cannot replace PostgreSQL schema history."""

import importlib.util
from pathlib import Path
import sqlite3
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("history", ROOT / "docker/export-code-migrations.py")
history = importlib.util.module_from_spec(spec)
spec.loader.exec_module(history)


class ConversionHistoryTests(unittest.TestCase):
    def test_replaces_only_code_history_with_actual_source_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            routines = root / "Jellyfin.Server/Migrations"
            routines.mkdir(parents=True)
            (routines / "routines.cs").write_text(
                '[JellyfinMigration("2026-01-01T12:00:00", nameof(Done))]\n'
                '[JellyfinMigration("2026-02-01T12:00:00", nameof(Pending))]\n'
            )
            source = root / "jellyfin.db"
            with sqlite3.connect(source) as db:
                db.executescript('''
                    CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);
                    CREATE TABLE LinkedChildren (ParentId TEXT, ChildId TEXT, SortOrder INTEGER, ChildType INTEGER);
                    INSERT INTO "__EFMigrationsHistory" VALUES
                        ('20260815063607_RemoveOrphanedUserPermissionsAndPreferences', '10.0.11'),
                        ('20260101120000_Done', '12.0.0.0');
                ''')
            sql = history.export(source, root)
            with sqlite3.connect(":memory:") as target:
                target.executescript('''
                    CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);
                    INSERT INTO "__EFMigrationsHistory" VALUES
                        ('20260910022712_UpgradeJellyfin12', '10.0.11'),
                        ('20260201120000_Pending', '12.0.0.0');
                ''')
                target.executescript(sql)
                self.assertEqual(
                    {"20260910022712_UpgradeJellyfin12", "20260101120000_Done"},
                    {row[0] for row in target.execute('SELECT MigrationId FROM "__EFMigrationsHistory"')},
                )
                target.executescript(sql)  # Reapplying the metadata import is idempotent.
            with sqlite3.connect(source) as db:
                db.execute('DELETE FROM "__EFMigrationsHistory" WHERE ProductVersion = ?', ("10.0.11",))
            with self.assertRaisesRegex(ValueError, "Upgrade the SQLite"):
                history.export(source, root)


if __name__ == "__main__":
    unittest.main()
