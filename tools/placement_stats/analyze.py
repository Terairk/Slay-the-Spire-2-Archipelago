#!/usr/bin/env python3
"""Query placement-stat CSV shards (or a compacted Parquet file) with DuckDB."""

from __future__ import annotations

import argparse
import os
import sys
import uuid
from pathlib import Path
from typing import Any, Sequence


def _duckdb() -> Any:
    try:
        import duckdb
    except ImportError:
        raise SystemExit(
            "DuckDB is required for analysis. Run this script with "
            "`uv run --with duckdb python analyze.py ...`."
        ) from None
    return duckdb


def _sql_string(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def _source_expression(source: str) -> str:
    path = Path(source).expanduser()
    if path.is_dir():
        pattern = path / "runs" / "*.csv"
        if not any((path / "runs").glob("*.csv")):
            raise SystemExit(f"No CSV shards found under {path / 'runs'}")
        source = str(pattern)

    lower = source.lower()
    if lower.endswith(".parquet") or ".parquet" in lower and any(char in source for char in "*?["):
        return f"read_parquet({_sql_string(source)}, union_by_name=true)"
    return (
        f"read_csv_auto({_sql_string(source)}, header=true, "
        "union_by_name=true, filename=true)"
    )


def _filters(args: argparse.Namespace, name_column: str, player_column: str) -> tuple[str, list[Any]]:
    clauses = [f"{name_column} = ?"]
    parameters: list[Any] = [getattr(args, "item", None) or getattr(args, "location")]
    player = getattr(args, "item_player", None)
    if player is None:
        player = getattr(args, "location_player", None)
    if player is not None:
        clauses.append(f"{player_column} = ?")
        parameters.append(player)
    return " AND ".join(clauses), parameters


def _print_rows(cursor: Any) -> None:
    columns = [description[0] for description in cursor.description]
    print("\t".join(columns))
    for row in cursor.fetchall():
        print("\t".join("" if value is None else str(value) for value in row))


def _run_query(source: str, sql: str, parameters: Sequence[Any] = ()) -> None:
    duckdb = _duckdb()
    connection = duckdb.connect()
    connection.execute(f"CREATE VIEW placements AS SELECT * FROM {_source_expression(source)}")
    _print_rows(connection.execute(sql, parameters))


def _item_locations(args: argparse.Namespace) -> None:
    where, parameters = _filters(args, "item_name", "item_player")
    parameters.append(args.limit)
    _run_query(
        args.data,
        f"""
        SELECT
            location_name,
            location_player,
            region,
            count(*) AS placements,
            round(100.0 * count(*) / sum(count(*)) OVER (), 3) AS percent
        FROM placements
        WHERE {where}
        GROUP BY location_name, location_player, region
        ORDER BY placements DESC, location_player, location_name
        LIMIT ?
        """,
        parameters,
    )


def _location_items(args: argparse.Namespace) -> None:
    where, parameters = _filters(args, "location_name", "location_player")
    parameters.append(args.limit)
    _run_query(
        args.data,
        f"""
        SELECT
            item_name,
            item_player,
            count(*) AS placements,
            round(100.0 * count(*) / sum(count(*)) OVER (), 3) AS percent
        FROM placements
        WHERE {where}
        GROUP BY item_name, item_player
        ORDER BY placements DESC, item_player, item_name
        LIMIT ?
        """,
        parameters,
    )


def _item_spheres(args: argparse.Namespace) -> None:
    where, parameters = _filters(args, "item_name", "item_player")
    _run_query(
        args.data,
        f"""
        SELECT
            CASE WHEN reachable THEN cast(sphere AS varchar) ELSE 'unreachable' END AS sphere,
            count(*) AS placements,
            count(DISTINCT generation_id) AS seeds,
            round(100.0 * count(*) / sum(count(*)) OVER (), 3) AS percent_of_placements
        FROM placements
        WHERE {where}
        GROUP BY reachable, sphere
        ORDER BY reachable DESC, sphere NULLS LAST
        """,
        parameters,
    )


def _custom_sql(args: argparse.Namespace) -> None:
    _run_query(args.data, args.query)


def _compact(args: argparse.Namespace) -> None:
    output = Path(args.output).expanduser().resolve()
    if output.exists() and not args.force:
        raise SystemExit(f"Refusing to overwrite {output}; pass --force to replace it")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.{uuid.uuid4().hex}.tmp")
    duckdb = _duckdb()
    connection = duckdb.connect()
    try:
        connection.execute(
            f"COPY (SELECT * FROM {_source_expression(args.data)}) "
            f"TO {_sql_string(str(temporary))} (FORMAT PARQUET, COMPRESSION ZSTD)"
        )
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    print(output)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    item_locations = subparsers.add_parser("item-locations", help="Most common locations for an item")
    item_locations.add_argument("data")
    item_locations.add_argument("--item", required=True)
    item_locations.add_argument("--item-player", type=int)
    item_locations.add_argument("--limit", type=int, default=25)
    item_locations.set_defaults(handler=_item_locations)

    location_items = subparsers.add_parser("location-items", help="Most common items at a location")
    location_items.add_argument("data")
    location_items.add_argument("--location", required=True)
    location_items.add_argument("--location-player", type=int)
    location_items.add_argument("--limit", type=int, default=25)
    location_items.set_defaults(handler=_location_items)

    item_spheres = subparsers.add_parser("item-spheres", help="Logical sphere distribution for an item")
    item_spheres.add_argument("data")
    item_spheres.add_argument("--item", required=True)
    item_spheres.add_argument("--item-player", type=int)
    item_spheres.set_defaults(handler=_item_spheres)

    custom_sql = subparsers.add_parser("sql", help="Run custom DuckDB SQL against the placements view")
    custom_sql.add_argument("data")
    custom_sql.add_argument("query")
    custom_sql.set_defaults(handler=_custom_sql)

    compact = subparsers.add_parser("compact", help="Stream CSV shards into one compressed Parquet file")
    compact.add_argument("data")
    compact.add_argument("output")
    compact.add_argument("--force", action="store_true")
    compact.set_defaults(handler=_compact)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    args.handler(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
