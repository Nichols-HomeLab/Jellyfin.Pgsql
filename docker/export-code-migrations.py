#!/usr/bin/env python3
"""Emit only server code-migration history from an offline Jellyfin 12 SQLite DB.

Apply this SQL after pgloader, before starting PostgreSQL-backed Jellyfin.
Provider schema migrations must remain those created by the PostgreSQL plugin.
"""

import argparse
from datetime import datetime
from pathlib import Path
import re
import sqlite3


def quote(value):
    return "'" + value.replace("'", "''") + "'"


def export(source, server_source):
    routines = server_source / "Jellyfin.Server/Migrations"
    known = set()
    pattern = re.compile(r'\[JellyfinMigration\("([^"]+)",\s*nameof\((\w+)\)')
    for path in routines.rglob("*.cs"):
        for date, name in pattern.findall(path.read_text()):
            known.add(datetime.fromisoformat(date).strftime("%Y%m%d%H%M%S") + "_" + name)
    if not known:
        raise ValueError("Pinned Jellyfin source is missing; initialize the jellyfin submodule.")

    with sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True) as db:
        db.execute("BEGIN")
        if db.execute("PRAGMA quick_check").fetchone() != ("ok",):
            raise ValueError("SQLite integrity check failed.")
        if db.execute("PRAGMA foreign_key_check").fetchone() is not None:
            raise ValueError("SQLite contains invalid foreign keys; repair the source first.")
        history = dict(db.execute('SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory"'))
        required = "20260815063607_RemoveOrphanedUserPermissionsAndPreferences"
        if required not in history:
            raise ValueError("Upgrade the SQLite installation to Jellyfin 12 before conversion.")
        columns = {row[1] for row in db.execute('PRAGMA table_info("LinkedChildren")')}
        if not {"ParentId", "ChildId", "SortOrder", "ChildType"} <= columns:
            raise ValueError("The source does not have the Jellyfin 12 linked-child schema.")

    ids = ",\n    ".join(quote(key) for key in sorted(known))
    statements = [
        "-- Generated from the offline SQLite source; keep PostgreSQL schema history intact.",
        "BEGIN;",
        'DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" IN (\n    ' + ids + "\n);",
    ]
    for key in sorted(known & history.keys()):
        statements.append(
            'INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('
            + quote(key) + ", " + quote(history[key]) + ");"
        )
    statements.append("COMMIT;")
    return "\n".join(statements)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sqlite_database", type=Path)
    parser.add_argument("--server-source", type=Path, default=Path(__file__).resolve().parents[1] / "jellyfin")
    args = parser.parse_args()
    try:
        print(export(args.sqlite_database, args.server_source))
    except (ValueError, sqlite3.Error, OSError) as error:
        parser.exit(1, f"Cannot export migration history: {error}\n")
